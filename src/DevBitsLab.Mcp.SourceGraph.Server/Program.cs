using DevBitsLab.Mcp.SourceGraph.Embeddings;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Server;
using DevBitsLab.Mcp.SourceGraph.Server.Cli;
using DevBitsLab.Mcp.SourceGraph.Server.Observability;
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

    var dbPath = cli.ResolvedDbPath();
    var dbDir = Path.GetDirectoryName(dbPath) ?? Path.GetTempPath();
    ToolMetrics.Configure(Path.Combine(dbDir, "usage.jsonl"));

    var embeddingsEnabled = !cli.NoEmbeddings;
    // Always know the active model identity so the vec0 table can be sized consistently
    // even when --no-embeddings is set: the table still exists (empty), and re-enabling
    // embeddings later will populate it without a schema rebuild.
    var modelInfo = new EmbeddingModelInfo(cli.Model ?? DefaultEmbeddingModel.ModelId, DefaultEmbeddingModel.Dimension);

    builder.Services.AddSingleton<SqliteGraphStore>(sp =>
    {
        var store = new SqliteGraphStore(dbPath, sp.GetRequiredService<ILogger<SqliteGraphStore>>());
        // Load the extension regardless of --no-embeddings so the schema can stand up the
        // empty vec0 table — agents can ask semantic_search later without re-running migrate.
        store.TryLoadVectorExtension(modelInfo.Dimension);
        return store;
    });
    builder.Services.AddSingleton<IGraphStore>(sp => sp.GetRequiredService<SqliteGraphStore>());
    builder.Services.AddSingleton<IEmbeddingsStore>(sp =>
    {
        var store = sp.GetRequiredService<SqliteGraphStore>();
        return store.CreateEmbeddingsStore(modelInfo.Dimension, sp.GetRequiredService<ILogger<SqliteEmbeddingsStore>>());
    });

    // Always register the model identity so the semantic_search tool has a single source of
    // truth for the active dimension. When --no-embeddings is set we skip the generator / sink /
    // hosted-service wiring, but still register a disabled-only stand-in for ICodeEmbeddingGenerator
    // so the tool DI resolution stays uniform.
    builder.Services.AddSingleton(modelInfo);
    builder.Services.AddSingleton<ModelStore>(sp => new ModelStore(sp.GetRequiredService<ILogger<ModelStore>>()));

    if (embeddingsEnabled)
    {
        // Resolve model paths and build the generator. If the cache is empty we DO NOT block
        // startup waiting for a download; the worker logs a warning and stays idle until the
        // user runs the bootstrap (or downloads the files manually). Letting startup proceed
        // is the right trade-off for the "model not yet downloaded" graceful-disable scenario.
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

    builder.Services.AddSingleton(sp =>
        new RoslynIndexer(sp.GetRequiredService<IGraphStore>(), sp.GetRequiredService<ILogger<RoslynIndexer>>(), sp.GetRequiredService<IEmbeddingsRequestSink>()));
    builder.Services.AddSingleton(new LiveIndexOptions(
        SolutionPath: string.IsNullOrEmpty(cli.SolutionPath) ? null : Path.GetFullPath(cli.SolutionPath)));
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

    await using var store = new SqliteGraphStore(cli.ResolvedDbPath(), loggerFactory.CreateLogger<SqliteGraphStore>());

    // Load the vec0 extension regardless of --no-embeddings so the schema can stand up an
    // empty `symbol_embeddings` table. --no-embeddings only disables the *pipeline*; the
    // table is created so re-enabling embeddings on a later run doesn't require a schema
    // rebuild.
    var modelInfo = new EmbeddingModelInfo(cli.Model ?? DefaultEmbeddingModel.ModelId, DefaultEmbeddingModel.Dimension);
    store.TryLoadVectorExtension(modelInfo.Dimension);

    // For one-shot index runs we register the embedding pipeline only when the user opts in.
    // The hosted-service path (serve) does the same gating; here we follow the simpler shape
    // because there's no host to manage.
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
        await store.EnsureSchemaAsync().ConfigureAwait(false);
        var embStore = store.CreateEmbeddingsStore(modelInfo.Dimension, loggerFactory.CreateLogger<SqliteEmbeddingsStore>());
        embedService = new EmbeddingsHostedService(channelSink, generator, embStore, loggerFactory.CreateLogger<EmbeddingsHostedService>());
        embedDrain = embedService.StartAsync(CancellationToken.None);
    }

    var result = await RoslynIndexer.IndexSolutionOnceAsync(solutionFull, store, loggerFactory.CreateLogger<RoslynIndexer>(), sink).ConfigureAwait(false);

    if (embedService is not null)
    {
        // Give the worker a chance to drain the queue before exiting.
        channelSink!.Complete();
        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await embedService.StopAsync(stopCts.Token).ConfigureAwait(false); } catch { /* best-effort drain */ }
        generator?.Dispose();
        if (embedDrain is not null) try { await embedDrain.ConfigureAwait(false); } catch { }
    }

    Console.WriteLine($"indexed {result.FilesIndexed} files, {result.SymbolsIndexed} symbols, {result.ReferencesIndexed} refs in {result.Elapsed.TotalSeconds:F2}s");
    Console.WriteLine($"database: {cli.ResolvedDbPath()}");
    return 0;
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
