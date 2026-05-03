using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Embeddings;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Server;
using DevBitsLab.Mcp.SourceGraph.Server.Cli;
using DevBitsLab.Mcp.SourceGraph.Server.Observability;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using DevBitsLab.Mcp.SourceGraph.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

CommandLine cli;
try
{
    cli = CommandLine.Parse(args);
}
catch (ArgumentException ex)
{
    await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
    await Console.Error.WriteLineAsync().ConfigureAwait(false);
    await Console.Error.WriteLineAsync(CommandLine.HelpText).ConfigureAwait(false);
    return 2;
}

if (cli.ShowHelp)
{
    Console.WriteLine(CommandLine.HelpText);
    return 0;
}

return cli.Subcommand switch
{
    "serve" => await RunServeAsync(cli).ConfigureAwait(false),
    "index" => await RunIndexAsync(cli).ConfigureAwait(false),
    "stats" => await RunStatsAsync(cli).ConfigureAwait(false),
    "clear" => await RunClearAsync(cli).ConfigureAwait(false),
    "init-scopes" => await ScopesCli.RunInitAsync(cli).ConfigureAwait(false),
    "scopes" => await ScopesCli.RunSubcommandAsync(cli).ConfigureAwait(false),
    _ => Unknown(cli.Subcommand),
};

static int Unknown(string sub)
{
    Console.Error.WriteLine($"Unknown subcommand: {sub}");
    Console.Error.WriteLine();
    Console.Error.WriteLine(CommandLine.HelpText);
    return 2;
}

static async Task<int> RunServeAsync(CommandLine cli)
{
    var builder = Host.CreateApplicationBuilder();
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

    var repoRoot = cli.ResolvedRepoRoot();

    // One-shot migration of the legacy `.sourcegraph/graph.db` to `.sourcegraph/scopes/default.db`.
    // Runs before scope wiring so the synthesised default scope picks up the moved DB.
    try
    {
        if (ScopeLayout.MigrateLegacyDb(repoRoot))
        {
            Console.Error.WriteLine($"[sourcegraph-mcp] migrated legacy {ScopeLayout.LegacyDbName} to scopes/default.db");
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLineAsync($"[sourcegraph-mcp] legacy DB migration skipped: {ex.Message}").GetAwaiter().GetResult();
    }

    // Resolve the scope configuration. With --solution we synthesise a single scope so existing
    // single-solution users keep working without a .sourcegraph.json. Without --solution we read
    // .sourcegraph.json or fall back to a synthesised single-scope rooted at the repo.
    ScopeConfig scopeConfig;
    try
    {
        if (!string.IsNullOrEmpty(cli.SolutionPath))
        {
            // --solution overrides .sourcegraph.json: register an implicit single-scope `default`.
            scopeConfig = ScopeConfigLoader.Synthesise(repoRoot, new[] { Path.GetFullPath(cli.SolutionPath) });
        }
        else
        {
            scopeConfig = ScopeConfigLoader.Load(repoRoot);
            // When no .sourcegraph.json and no --solution, the synthesised default has no
            // solutions list. That's fine — the indexer simply won't open a workspace and queries
            // run against whatever's already in the DB.
        }
    }
    catch (ScopeConfigException ex)
    {
        await Console.Error.WriteLineAsync($"[sourcegraph-mcp] {ex.Message}").ConfigureAwait(false);
        return 2;
    }

    var dbDir = ScopeLayout.SourcegraphDir(repoRoot);
    Directory.CreateDirectory(dbDir);
    ToolMetrics.Configure(Path.Combine(dbDir, "usage.jsonl"));

    var embeddingsEnabled = !cli.NoEmbeddings;
    // Always know the active model identity so the vec0 table can be sized consistently
    // even when --no-embeddings is set: the table still exists (empty), and re-enabling
    // embeddings later will populate it without a schema rebuild.
    var modelInfo = new EmbeddingModelInfo(cli.Model ?? DefaultEmbeddingModel.ModelId, DefaultEmbeddingModel.Dimension);

    builder.Services.AddSingleton(modelInfo);
    builder.Services.AddSingleton<ModelStore>(sp => new ModelStore(sp.GetRequiredService<ILogger<ModelStore>>()));

    if (embeddingsEnabled)
    {
        builder.Services.AddSingleton<ICodeEmbeddingGenerator>(sp =>
        {
            var ms = sp.GetRequiredService<ModelStore>();
            var info = sp.GetRequiredService<EmbeddingModelInfo>();
            var onnx = ms.FilePath(info.ModelId, "model.onnx");
            var tok = ms.FilePath(info.ModelId, "tokenizer.json");
            return new JinaCodeEmbeddingGenerator(onnx, tok, info, logger: sp.GetRequiredService<ILogger<JinaCodeEmbeddingGenerator>>());
        });
        var sink = new ChannelEmbeddingsRequestSink();
        builder.Services.AddSingleton(sink);
        builder.Services.AddSingleton<IEmbeddingsRequestSink>(sink);
        builder.Services.AddHostedService<EmbeddingsHostedService>();
    }
    else
    {
        builder.Services.AddSingleton<ICodeEmbeddingGenerator>(_ => new DisabledEmbeddingGenerator(modelInfo));
        builder.Services.AddSingleton<IEmbeddingsRequestSink>(new NoOpEmbeddingsRequestSink());
    }

    // Scope registry (lives in `_meta.db`). Wired up first so list_scopes can reflect the
    // pre-scope-host status while initial indexing is still running.
    builder.Services.AddSingleton<IScopeRegistry>(sp =>
        new SqliteScopeRegistry(ScopeLayout.MetaDbPath(repoRoot),
            sp.GetRequiredService<ILogger<SqliteScopeRegistry>>()));

    var router = new ScopeRouter();
    router.SetDefaultScope(scopeConfig.DefaultScope);
    builder.Services.AddSingleton(router);

    // History (git-blame) pipeline. Now multi-scope-aware via the router.
    builder.Services.AddSingleton<HistoryQueue>();
    builder.Services.AddSingleton(sp => new GitBlameRunner(sp.GetService<ILogger<GitBlameRunner>>()));

    var historyDisabled = await ResolveHistoryDisabledForScopesAsync(cli, scopeConfig).ConfigureAwait(false);
    builder.Services.AddSingleton(new HistoryOptions(historyDisabled));

    builder.Services.AddSingleton(new LiveIndexConfig(scopeConfig.Scopes));
    builder.Services.AddHostedService<HistoryHostedService>(sp => new HistoryHostedService(
        sp.GetRequiredService<HistoryQueue>(),
        sp.GetRequiredService<ScopeRouter>(),
        sp.GetRequiredService<GitBlameRunner>(),
        sp.GetRequiredService<HistoryOptions>(),
        sp.GetRequiredService<ILogger<HistoryHostedService>>()));
    builder.Services.AddHostedService<LiveIndexService>();

    builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithToolsFromAssembly()
        .WithResourcesFromAssembly();

    await builder.Build().RunAsync().ConfigureAwait(false);
    return 0;
}

static async Task<int> RunIndexAsync(CommandLine cli)
{
    if (string.IsNullOrEmpty(cli.SolutionPath))
    {
        await Console.Error.WriteLineAsync("error: index requires a solution path").ConfigureAwait(false);
        return 2;
    }
    var solutionFull = Path.GetFullPath(cli.SolutionPath);
    if (!File.Exists(solutionFull))
    {
        await Console.Error.WriteLineAsync($"error: solution not found: {solutionFull}").ConfigureAwait(false);
        return 2;
    }

    using var loggerFactory = LoggerFactory.Create(b =>
    {
        b.SetMinimumLevel(LogLevel.Information);
        b.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        });
    });

    // Migrate the legacy graph.db on the one-shot path too, so later `serve` runs see the moved
    // file. Only when the user pointed at a solution (we know the repo root then).
    var solutionDir = Path.GetDirectoryName(solutionFull) ?? "";
    if (!string.IsNullOrEmpty(solutionDir))
    {
        try { ScopeLayout.MigrateLegacyDb(solutionDir); } catch { /* best-effort */ }
    }

    await using var store = new SqliteGraphStore(cli.ResolvedDbPath(), loggerFactory.CreateLogger<SqliteGraphStore>());

    // Load the vec0 extension regardless of --no-embeddings so the schema can stand up an
    // empty `symbol_embeddings` table. --no-embeddings only disables the *pipeline*; the
    // table is created so re-enabling embeddings on a later run doesn't require a schema
    // rebuild.
    var modelInfo = new EmbeddingModelInfo(cli.Model ?? DefaultEmbeddingModel.ModelId, DefaultEmbeddingModel.Dimension);
    store.TryLoadVectorExtension(modelInfo.Dimension);
    await store.EnsureSchemaAsync().ConfigureAwait(false);

    // For one-shot index runs we register the embedding pipeline only when the user opts in.
    IEmbeddingsRequestSink sink = new NoOpEmbeddingsRequestSink();
    Task? embedDrain = null;
    ChannelEmbeddingsRequestSink? channelSink = null;
    EmbeddingsHostedService? embedService = null;
    JinaCodeEmbeddingGenerator? generator = null;
    var embeddingsEnabled = !cli.NoEmbeddings;
    if (embeddingsEnabled && store.VectorExtensionLoaded)
    {
        channelSink = new ChannelEmbeddingsRequestSink();
        sink = channelSink;
        var ms = new ModelStore(loggerFactory.CreateLogger<ModelStore>());
        generator = new JinaCodeEmbeddingGenerator(
            ms.FilePath(modelInfo.ModelId, "model.onnx"),
            ms.FilePath(modelInfo.ModelId, "tokenizer.json"),
            modelInfo,
            logger: loggerFactory.CreateLogger<JinaCodeEmbeddingGenerator>());
        var embStore = store.CreateEmbeddingsStore(modelInfo.Dimension, loggerFactory.CreateLogger<SqliteEmbeddingsStore>());
        embedService = new EmbeddingsHostedService(channelSink, generator, embStore, loggerFactory.CreateLogger<EmbeddingsHostedService>());
        embedDrain = embedService.StartAsync(CancellationToken.None);
    }

    var historyDisabled = await ResolveHistoryDisabledAsync(cli, solutionFull).ConfigureAwait(false);
    var historyQueue = new HistoryQueue();
    var blamer = new GitBlameRunner(loggerFactory.CreateLogger<GitBlameRunner>());
    var historyTask = historyDisabled
        ? Task.CompletedTask
        : RunHistoryPipelineAsync(historyQueue, store, blamer, loggerFactory.CreateLogger<HistoryHostedService>());

    await using var indexer = new RoslynIndexer(store, loggerFactory.CreateLogger<RoslynIndexer>(), sink);
    if (!historyDisabled)
    {
        indexer.OnFileIndexed = (fileId, path, sha) =>
            historyQueue.Writer.WriteAsync(new HistoryRequest(fileId, path, sha, "default")).AsTask();
    }
    await indexer.OpenAsync(solutionFull).ConfigureAwait(false);
    var result = await indexer.IndexAllAsync().ConfigureAwait(false);

    historyQueue.Writer.Complete();
    await historyTask.ConfigureAwait(false);

    if (embedService is not null)
    {
        channelSink!.Complete();
        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await embedService.StopAsync(stopCts.Token).ConfigureAwait(false); } catch { /* best-effort drain */ }
        generator?.Dispose();
        if (embedDrain is not null) try { await embedDrain.ConfigureAwait(false); } catch { }
    }

    Console.WriteLine($"indexed {result.FilesIndexed} files, {result.SymbolsIndexed} symbols, {result.ReferencesIndexed} refs in {result.Elapsed.TotalSeconds:F2}s");
    Console.WriteLine($"database: {cli.ResolvedDbPath()}");
    if (historyDisabled) Console.WriteLine("history: disabled (--no-history or git unavailable)");
    return 0;
}

/// <summary>
/// Run the history pipeline as a single async loop without the BackgroundService scaffolding.
/// </summary>
static async Task RunHistoryPipelineAsync(
    HistoryQueue queue,
    DevBitsLab.Mcp.SourceGraph.Storage.IGraphStore store,
    GitBlameRunner blamer,
    ILogger<HistoryHostedService> logger)
{
    var svc = new HistoryHostedService(
        queue, store, blamer, new HistoryOptions(false), logger);
    var runner = svc.ExecuteAsyncForOneShot(CancellationToken.None);
    await runner.ConfigureAwait(false);
}

/// <summary>
/// Decide whether the git-blame pipeline should run for the multi-scope `serve` path. Disabled
/// when <c>--no-history</c> is set, or when none of the configured scopes resolves to a git
/// working tree.
/// </summary>
static async Task<bool> ResolveHistoryDisabledForScopesAsync(CommandLine cli, ScopeConfig config)
{
    if (cli.NoHistory) return true;
    foreach (var scope in config.Scopes)
    {
        var solutionDir = scope.ProjectSet is ScopeProjectSet.Solutions s && s.Items.Count > 0
            ? Path.GetDirectoryName(Path.IsPathRooted(s.Items[0]) ? s.Items[0] : Path.Combine(scope.Root, s.Items[0]))
            : scope.Root;
        if (string.IsNullOrEmpty(solutionDir)) continue;
        var probe = new GitBlameRunner();
        if (await probe.IsGitWorkingTreeAsync(solutionDir).ConfigureAwait(false))
        {
            return false;
        }
    }
    return true;
}

/// <summary>
/// Single-solution overload used by the one-shot <c>index</c> command.
/// </summary>
static async Task<bool> ResolveHistoryDisabledAsync(CommandLine cli, string solutionFull)
{
    if (cli.NoHistory) return true;
    if (string.IsNullOrEmpty(solutionFull)) return true;
    var solutionDir = Path.GetDirectoryName(solutionFull);
    if (string.IsNullOrEmpty(solutionDir)) return true;
    var probe = new GitBlameRunner();
    return !await probe.IsGitWorkingTreeAsync(solutionDir).ConfigureAwait(false);
}

static async Task<int> RunStatsAsync(CommandLine cli)
{
    var dbPath = cli.ResolvedDbPath();
    if (!File.Exists(dbPath))
    {
        await Console.Error.WriteLineAsync($"error: no graph database at {dbPath}").ConfigureAwait(false);
        return 1;
    }
    await using var store = new SqliteGraphStore(dbPath);
    await store.EnsureSchemaAsync().ConfigureAwait(false);
    var s = await store.GetStatsAsync().ConfigureAwait(false);
    Console.WriteLine($"database: {dbPath}");
    Console.WriteLine($"  files       {s.FileCount}");
    Console.WriteLine($"  symbols     {s.SymbolCount}");
    Console.WriteLine($"  references  {s.ReferenceCount}");
    Console.WriteLine($"  edges       {s.EdgeCount}");
    return 0;
}

static async Task<int> RunClearAsync(CommandLine cli)
{
    var dbPath = cli.ResolvedDbPath();
    if (File.Exists(dbPath)) File.Delete(dbPath);
    await using var store = new SqliteGraphStore(dbPath);
    await store.EnsureSchemaAsync().ConfigureAwait(false);
    Console.WriteLine($"cleared {dbPath}");
    return 0;
}
