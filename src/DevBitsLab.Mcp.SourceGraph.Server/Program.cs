using System.Text.Json;
using System.Text.Json.Nodes;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Embeddings;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Server;
using DevBitsLab.Mcp.SourceGraph.Server.Cli;
using DevBitsLab.Mcp.SourceGraph.Server.Observability;
using DevBitsLab.Mcp.SourceGraph.Server.Plugins;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using DevBitsLab.Mcp.SourceGraph.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

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
    "init" => await InitCli.RunAsync(cli).ConfigureAwait(false),
    "doctor" => await DoctorCli.RunAsync(cli).ConfigureAwait(false),
    "demo" => await DemoCli.RunAsync(cli).ConfigureAwait(false),
    "init-scopes" => await ScopesCli.RunInitAsync(cli).ConfigureAwait(false),
    "scopes" => await ScopesCli.RunSubcommandAsync(cli).ConfigureAwait(false),
    "plugins" => await PluginsCli.RunSubcommandAsync(cli).ConfigureAwait(false),
    "vocabulary" => await VocabularyCli.RunSubcommandAsync(cli).ConfigureAwait(false),
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
    // Cap min-level to Warning during `serve`. The MCP transport speaks JSON-RPC over stdout;
    // stderr is the only safe channel for unstructured logs but it's still observed by every
    // client (see ServerHarness.StderrLines). Info-level lifecycle chatter from
    // Microsoft.Hosting.Lifetime + StdioServerTransport during initialize would otherwise leak
    // there. Warnings/errors still flow through because those genuinely indicate problems an
    // operator should see. Other CLI verbs (index, stats, scopes, plugins, vocabulary) build
    // their own logger and keep info-level output for terminal users.
    builder.Logging.SetMinimumLevel(LogLevel.Warning);

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
    ToolMetrics.Configure(Path.Join(dbDir, "usage.jsonl"));

    var embeddingsEnabled = !cli.NoEmbeddings;
    // Always know the active model identity so the vec0 table can be sized consistently
    // even when --no-embeddings is set: the table still exists (empty), and re-enabling
    // embeddings later will populate it without a schema rebuild.
    var modelInfo = new EmbeddingModelInfo(cli.Model ?? DefaultEmbeddingModel.ModelId, DefaultEmbeddingModel.Dimension);

    builder.Services.AddSingleton(modelInfo);
    builder.Services.AddSingleton<ModelStore>(sp => new ModelStore(sp.GetRequiredService<ILogger<ModelStore>>()));

    // The embedding generator is a process-wide singleton (the ONNX session is expensive to
    // construct and is thread-safe). The per-scope sink and drain task are owned by each
    // ScopeHost in LiveIndexService, so the symbol-id <-> per-scope-store routing stays
    // unambiguous. See ScopeHost.EmbeddingsService for the per-scope ownership.
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
    }
    else
    {
        builder.Services.AddSingleton<ICodeEmbeddingGenerator>(_ => new DisabledEmbeddingGenerator(modelInfo));
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

    // Tunables for the query_graph tool: CLI flag → env var → built-in default. Singleton so the
    // tool method can resolve it via DI without re-parsing every call.
    builder.Services.AddSingleton(GraphQueryOptions.Resolve(cli.QueryTimeoutSeconds, cli.QueryRowLimit));
    // Repo root surfaced for tools (query_graph / describe_schema) that need it to locate
    // per-scope DB files via ScopeLayout.
    builder.Services.AddSingleton(new RepoRootInfo(repoRoot));

    // The scope-config watcher is started by LiveIndexService when WatchConfig is true. We skip
    // it when --solution is set: --solution is a runtime override that bypasses .sourcegraph.json
    // entirely (Synthesise above), so an edit to the JSON shouldn't override the user's explicit
    // CLI argument. Without --solution, the watcher's deletion-revert path uses the same empty
    // discoveredSolutions Synthesise was called with at startup, which is the symmetry we want.
    var watchConfig = string.IsNullOrEmpty(cli.SolutionPath);
    var discoveredSolutions = string.IsNullOrEmpty(cli.SolutionPath)
        ? Array.Empty<string>()
        : new[] { Path.GetFullPath(cli.SolutionPath) };
    // Test harnesses pass SOURCEGRAPH_SCOPE_REPLACE_GRACE_MS=50 to avoid waiting the full
    // production 5s grace window during a live-modify integration test. Production users never
    // set it; the default (5000) is the right shape for real workloads.
    var graceMs = 5000;
    var graceEnv = Environment.GetEnvironmentVariable("SOURCEGRAPH_SCOPE_REPLACE_GRACE_MS");
    if (!string.IsNullOrEmpty(graceEnv) && int.TryParse(graceEnv, out var parsedGrace) && parsedGrace > 0)
    {
        graceMs = parsedGrace;
    }
    builder.Services.AddSingleton(new LiveIndexConfig(
        Scopes: scopeConfig.Scopes,
        RepoRoot: repoRoot,
        DiscoveredSolutions: discoveredSolutions,
        StartupPlugins: scopeConfig.Plugins,
        DefaultScope: scopeConfig.DefaultScope,
        WatchConfig: watchConfig,
        ScopeReplaceGraceMs: graceMs));
    builder.Services.AddHostedService<HistoryHostedService>(sp => new HistoryHostedService(
        sp.GetRequiredService<HistoryQueue>(),
        sp.GetRequiredService<ScopeRouter>(),
        sp.GetRequiredService<GitBlameRunner>(),
        sp.GetRequiredService<HistoryOptions>(),
        sp.GetRequiredService<ILogger<HistoryHostedService>>()));
    builder.Services.AddHostedService<LiveIndexService>();

    // Plugin host: discover plugins via .sourcegraph.json's plugins[] array. With NO entry the
    // host is a no-op and single-solution v0.5.0 behaviour is preserved exactly. The built-in
    // RoslynLanguageIndexer is registered into LanguageIndexerRegistry below — no plugin entry
    // required.
    var pluginHost = new PluginHost(repoRoot, scopeConfig.Plugins, StartupLogging.CreateLogger<PluginHost>());
    try
    {
        await pluginHost.LoadAllAsync().ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        await Console.Error.WriteLineAsync($"[sourcegraph-mcp] plugin host startup failed: {ex.Message}").ConfigureAwait(false);
    }
    builder.Services.AddSingleton(pluginHost);

    // The language-indexer registry is shared across scopes. The built-in C# pathway is not
    // routed through here for actual indexing — `LiveIndexService` keeps the workspace-aware
    // bulk path for back-compat — but registering it here makes the seam visible to `plugins
    // list` and to the analyzer pipeline.
    var langRegistry = new LanguageIndexerRegistry();
    var rejectedExt = langRegistry.Register(new BuiltInRoslynLanguageIndexerStub());
    if (rejectedExt.Count > 0)
    {
        await Console.Error.WriteLineAsync($"[sourcegraph-mcp] built-in indexer extension(s) rejected: {string.Join(", ", rejectedExt)}").ConfigureAwait(false);
    }
    // Built-in XAML indexer ships with the server (in-tree). Registered here alongside the C#
    // stub so `LanguageIndexerRegistry` reports the .xaml claim and so the dispatcher routes
    // .xaml files to the indexer at scope startup. The matching XamlLanguageProjectFactory is
    // registered in the project-factory registry below.
    var xamlRejected = langRegistry.Register(new DevBitsLab.Mcp.SourceGraph.Indexing.Xaml.XamlLanguageIndexer());
    if (xamlRejected.Count > 0)
    {
        await Console.Error.WriteLineAsync($"[sourcegraph-mcp] XAML indexer extension(s) rejected: {string.Join(", ", xamlRejected)}").ConfigureAwait(false);
    }
    foreach (var record in pluginHost.Plugins.Where(p => p.Status == PluginStatus.Loaded))
    {
        foreach (var li in record.LanguageIndexers)
        {
            var rejected = langRegistry.Register(li, record);
            if (rejected.Count > 0)
            {
                record.Status = PluginStatus.Failed;
                record.StatusMessage = $"Refused to claim already-registered file extensions: {string.Join(", ", rejected)}.";
            }
        }
    }
    builder.Services.AddSingleton(langRegistry);

    // Per-scope ILanguageProjectFactory registry. The XAML factory ships in-tree; plugin-supplied
    // factories are added below from the loaded plugin records. The MSBuild factory is wired
    // per-scope inside LiveIndexService once the Roslyn workspace is open (it requires the live
    // workspace handle, which doesn't exist yet at this point in the startup flow).
    var projectFactoryRegistry = new LanguageProjectFactoryRegistry();
    projectFactoryRegistry.Register(new DevBitsLab.Mcp.SourceGraph.Indexing.Xaml.XamlLanguageProjectFactory());
    foreach (var record in pluginHost.Plugins.Where(p => p.Status == PluginStatus.Loaded))
    {
        foreach (var lpf in record.LanguageProjectFactories)
        {
            projectFactoryRegistry.Register(lpf, record);
        }
    }
    builder.Services.AddSingleton(projectFactoryRegistry);

    builder.Services.AddSingleton<AnalyzerPipeline>();
    builder.Services.AddSingleton<LanguageIndexerDispatcher>();

    // Server-published usage instructions: tells the connected model to prefer source-graph
    // tools over Grep+Read for symbol-level questions, and to call usage_stats at end of turn
    // to verify the graph was actually queried. Suppressed by --no-instructions or
    // SOURCEGRAPH_NO_INSTRUCTIONS=1 — clients that don't honour the field already ignore it.
    //
    // Phase F: alongside the instructions string we publish the open-language vocabulary
    // (edge_kinds, symbol_kinds, annotation_flavors) under
    // `capabilities.experimental["sourcegraph.vocabulary"]`. Computed up-front by reading the
    // distinct-kind columns of every scope DB and unioning with the SDK's constants. Same
    // suppression knob as the instructions string so an operator can disable both with one flag.
    var noInstructions = ServerInstructions.ShouldSuppress(
        cli.NoInstructions,
        Environment.GetEnvironmentVariable(ServerInstructions.EnvVarName));

    // Brand-mark suppression: same shape as --no-instructions. Set once, read by the
    // LeafFormatter chokepoint inside ToolMetrics on every tool call (see Decision 6 in
    // openspec/changes/add-leaf-brand-mark/design.md).
    LeafFormatter.Suppressed = ServerInstructions.ShouldSuppress(
        cli.NoLeaf,
        Environment.GetEnvironmentVariable(LeafFormatter.EnvVarName));
    if (!noInstructions)
    {
        // Use Program as the logger category — ServerVocabulary is static so it can't be a
        // generic type argument; the category name is just for log filtering.
        var vocabLogger = StartupLogging.CreateLogger<Program>();
        var vocabulary = await ServerVocabulary
            .ComputeAsync(scopeConfig.Scopes, vocabLogger)
            .ConfigureAwait(false);
        builder.Services.Configure<McpServerOptions>(o =>
        {
            o.ServerInstructions = ServerInstructions.ResolvePublished();
            // Capabilities.Experimental is the MCP spec's extension point for non-standard
            // server capabilities; we slot the vocabulary in under a namespaced key so unrelated
            // clients ignore it. The wire shape is `{ "edge_kinds": [...union...],
            // "symbol_kinds": [...], ..., "scopes": { "<id>": { "edge_kinds": [...], ... } } }`.
            // The top-level lists are the server-wide union across every scope; the `scopes` map
            // carries the per-scope breakdown for clients that need to validate a tool argument
            // against a specific scope's vocabulary.
            //
            // We build the value as a `JsonObject` rather than an anonymous type because the MCP
            // SDK's source-generated `McpJsonUtilities+JsonContext` rejects anonymous-type
            // `JsonTypeInfo` lookups at serialise time (NotSupportedException). `JsonObject`
            // derives from `JsonNode` which the SDK's context handles natively as opaque JSON, so
            // the serialiser writes it through unchanged. The emitted JSON is byte-for-byte
            // identical to what the anonymous-type path produced.
            o.Capabilities ??= new ModelContextProtocol.Protocol.ServerCapabilities();
            o.Capabilities.Experimental ??= new Dictionary<string, object>(StringComparer.Ordinal);

            var perScope = new JsonObject();
            foreach (var kv in vocabulary.Scopes)
            {
                perScope[kv.Key] = new JsonObject
                {
                    ["edge_kinds"] = JsonSerializer.SerializeToNode(kv.Value.EdgeKinds),
                    ["symbol_kinds"] = JsonSerializer.SerializeToNode(kv.Value.SymbolKinds),
                    ["annotation_flavors"] = JsonSerializer.SerializeToNode(kv.Value.AnnotationFlavors),
                };
            }
            var vocabNode = new JsonObject
            {
                ["edge_kinds"] = JsonSerializer.SerializeToNode(vocabulary.EdgeKinds),
                ["symbol_kinds"] = JsonSerializer.SerializeToNode(vocabulary.SymbolKinds),
                ["annotation_flavors"] = JsonSerializer.SerializeToNode(vocabulary.AnnotationFlavors),
                ["scopes"] = perScope,
            };
            o.Capabilities.Experimental[ServerVocabulary.CapabilityKey] = vocabNode;
        });
    }

    var mcpBuilder = builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithToolsFromAssembly()
        .WithResourcesFromAssembly();

    // Tool plugins: register every tool the loaded plugins want to expose, prefixed by their
    // declared `Prefix`. Collisions (same final name from two plugins, or shadowing a built-in)
    // mark the plugin failed but the host stays up.
    var claimedNames = new HashSet<string>(StringComparer.Ordinal);
    foreach (var record in pluginHost.Plugins.Where(p => p.Status == PluginStatus.Loaded))
    {
        foreach (var tp in record.ToolPlugins)
        {
            try
            {
                if (string.IsNullOrEmpty(tp.Prefix))
                {
                    record.Status = PluginStatus.Failed;
                    record.StatusMessage = "IMcpToolPlugin.Prefix is empty; non-empty prefix is required.";
                    continue;
                }
                var registry = new ToolRegistry(tp.Prefix, claimedNames, record);
                await tp.RegisterAsync(registry).ConfigureAwait(false);
                if (registry.RegisteredTools.Count > 0)
                {
                    mcpBuilder.WithTools(registry.RegisteredTools);
                }
            }
            catch (Exception ex)
            {
                record.Status = PluginStatus.Failed;
                record.StatusMessage = $"IMcpToolPlugin.RegisterAsync threw: {ex.Message}";
            }
        }
    }

    var host = builder.Build();

    // Walk the registered McpServerTool collection and rewrite ProtocolTool.Description to append
    // `Use when: <trigger>` for every method carrying [ToolTrigger]. Done after Build() so we see
    // both built-in tools (registered via WithToolsFromAssembly) and any tools added by plugins.
    ToolDescriptionFormatter.ApplyTriggersFromAttributes(host.Services.GetServices<McpServerTool>());

    // Stamp the brand mark on every built-in tool's catalog identity (Title + Description). Same
    // post-build mutation pattern as ApplyTriggersFromAttributes; reads LeafFormatter.Suppressed
    // (set above ~line 255) to short-circuit when --no-leaf is in effect. Plugin tools are skipped
    // structurally — their declaring type lacks [McpServerToolType]. See
    // openspec/changes/add-leaf-to-tool-identity/design.md for the chokepoint design.
    ToolIdentityFormatter.ApplyBrandMark(host.Services.GetServices<McpServerTool>());

    await host.RunAsync().ConfigureAwait(false);
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

    // Plugin host + analyzer pipeline. The one-shot `index` path mirrors `serve`'s behaviour
    // here so a CI / smoke-test invocation produces the same per-scope graph the live server
    // would. Skipped silently when no plugins are declared in `.sourcegraph.json`, preserving
    // the v0.5.0 zero-config single-solution path.
    var repoRootForIndex = Path.GetDirectoryName(solutionFull) ?? cli.ResolvedRepoRoot();
    DevBitsLab.Mcp.SourceGraph.Storage.ScopeConfig? indexScopeConfig = null;
    try
    {
        indexScopeConfig = DevBitsLab.Mcp.SourceGraph.Storage.ScopeConfigLoader.Load(repoRootForIndex);
    }
    catch (DevBitsLab.Mcp.SourceGraph.Storage.ScopeConfigException ex)
    {
        await Console.Error.WriteLineAsync($"[sourcegraph-mcp] {ex.Message}").ConfigureAwait(false);
    }
    // Plugin host always runs on the one-shot CLI path so the in-tree XAML indexer dispatches
    // .xaml files alongside the C# bulk index. Skipping the plugin host loop is fine when no
    // plugins are declared (LoadAllAsync is a no-op then) — but the langRegistry below still
    // gets the built-in C# stub + XAML indexer so the dispatcher pass actually runs.
    {
        var pluginHost = new PluginHost(repoRootForIndex, indexScopeConfig?.Plugins ?? Array.Empty<PluginRef>(), loggerFactory.CreateLogger<PluginHost>());
        try
        {
            await pluginHost.LoadAllAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[sourcegraph-mcp] plugin host startup failed: {ex.Message}").ConfigureAwait(false);
        }

        // Build the language indexer registry — same shape as the serve path.
        var langRegistry = new LanguageIndexerRegistry();
        langRegistry.Register(new BuiltInRoslynLanguageIndexerStub());
        langRegistry.Register(new DevBitsLab.Mcp.SourceGraph.Indexing.Xaml.XamlLanguageIndexer());
        foreach (var record in pluginHost.Plugins.Where(p => p.Status == PluginStatus.Loaded))
        {
            foreach (var li in record.LanguageIndexers)
            {
                langRegistry.Register(li, record);
            }
        }

        var projectFactoryRegistry = new LanguageProjectFactoryRegistry();
        projectFactoryRegistry.Register(new DevBitsLab.Mcp.SourceGraph.Indexing.Xaml.XamlLanguageProjectFactory());
        foreach (var record in pluginHost.Plugins.Where(p => p.Status == PluginStatus.Loaded))
        {
            foreach (var lpf in record.LanguageProjectFactories) projectFactoryRegistry.Register(lpf, record);
        }
        if (indexer.Workspace is { } ws)
        {
            projectFactoryRegistry.Register(new MSBuildLanguageProjectFactory(ws));
        }

        // Dispatch non-C# files (XAML + plugin-supplied) for the one-shot path. The C# bulk index
        // above already covered .cs. We use the dispatcher's test-friendly overload here rather
        // than constructing a ScopeHost: ScopeHost owns indexer/embeddings/store lifetimes via
        // its DisposeAsync, but the one-shot CLI path keeps `await using var indexer = new ...`
        // and `await using var store = new ...` for those — wrapping the host would either
        // double-dispose them or leak when the dispatcher throws. Building the project map
        // inline against the existing store sidesteps both problems.
        var dispatcher = new LanguageIndexerDispatcher(langRegistry, projectFactoryRegistry, loggerFactory.CreateLogger<LanguageIndexerDispatcher>());
        var oneShotProjectMap = new Dictionary<string, ILanguageProject>(StringComparer.OrdinalIgnoreCase);
        foreach (var factory in projectFactoryRegistry.All())
        {
            IReadOnlyList<ILanguageProject> projects;
            try { projects = await factory.DiscoverAsync(repoRootForIndex, default).ConfigureAwait(false); }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"[sourcegraph-mcp] factory `{factory.GetType().FullName}` failed DiscoverAsync: {ex.Message}").ConfigureAwait(false);
                continue;
            }
            foreach (var p in projects)
            {
                foreach (var path in p.FilePaths)
                {
                    if (!oneShotProjectMap.ContainsKey(path)) oneShotProjectMap[path] = p;
                }
            }
        }
        var dispatched = await dispatcher.DispatchAllForTestAsync(store, "default", repoRootForIndex, oneShotProjectMap).ConfigureAwait(false);
        if (dispatched > 0)
        {
            Console.WriteLine($"non-C# dispatch: indexed {dispatched} files");
        }

        if (indexScopeConfig is { Plugins.Count: > 0 })
        {
            var analyzerPipeline = new AnalyzerPipeline(pluginHost, loggerFactory.CreateLogger<AnalyzerPipeline>());
            if (analyzerPipeline.HasAnalyzers)
            {
                await DispatchAnalyzersForOneShotAsync(store, "default", repoRootForIndex, analyzerPipeline, loggerFactory.CreateLogger<Program>()).ConfigureAwait(false);
            }
            var loadedCount = pluginHost.Plugins.Count(p => p.Status == PluginStatus.Loaded);
            var failedCount = pluginHost.Plugins.Count(p => p.Status == PluginStatus.Failed);
            Console.WriteLine($"plugins: {loadedCount} loaded, {failedCount} failed");
        }
    }

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
/// One-shot analyzer-dispatch helper for the `index` CLI path. Mirrors what
/// <see cref="LiveIndexService"/> does per scope: reads every indexed file's symbol+annotation
/// rows out of the store, packages them as <see cref="Sdk.IndexEvent"/>s, and runs every loaded
/// analyzer against each file. Failures are isolated by the pipeline; the function returns
/// normally even if some analyzers fail.
/// </summary>
static async Task DispatchAnalyzersForOneShotAsync(
    DevBitsLab.Mcp.SourceGraph.Storage.IGraphStore store,
    string scopeId,
    string repoRoot,
    AnalyzerPipeline analyzerPipeline,
    ILogger<Program> logger)
{
    var files = await store.GetAllFilesAsync().ConfigureAwait(false);
    var symbolKeys = await store.GetAllSymbolKeysAsync().ConfigureAwait(false);

    var symbolIdByKey = new Dictionary<string, long>(StringComparer.Ordinal);
    var keysByFileId = new Dictionary<long, List<string>>();
    foreach (var k in symbolKeys)
    {
        symbolIdByKey[k.CanonicalKey] = k.Id;
        if (!keysByFileId.TryGetValue(k.FileId, out var list))
        {
            list = new List<string>();
            keysByFileId[k.FileId] = list;
        }
        list.Add(k.CanonicalKey);
    }

    foreach (var file in files)
    {
        byte[] contents;
        try
        {
            if (!File.Exists(file.Path)) continue;
            contents = await File.ReadAllBytesAsync(file.Path).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "Skipping {Path} for analyzer dispatch (read failed)", file.Path);
            continue;
        }

        var events = new List<IndexEvent>();
        if (keysByFileId.TryGetValue(file.Id, out var keys))
        {
            foreach (var key in keys)
            {
                var sym = await store.GetSymbolByIdAsync(symbolIdByKey[key]).ConfigureAwait(false);
                if (sym is null) continue;
                events.Add(new IndexEvent.SymbolDeclared(
                    canonicalKey: key,
                    name: sym.Name,
                    fqn: sym.Fqn,
                    kind: sym.Kind,
                    startLine: sym.StartLine,
                    startColumn: sym.StartCol,
                    endLine: sym.EndLine,
                    endColumn: sym.EndCol,
                    signature: sym.Signature,
                    modifiers: sym.Modifiers,
                    accessibility: sym.Accessibility,
                    xmlSummary: sym.XmlSummary));

                var anns = await store.GetAnnotationsForSymbolAsync(sym.Id).ConfigureAwait(false);
                foreach (var a in anns)
                {
                    events.Add(new IndexEvent.AnnotationAttached(
                        symbolCanonicalKey: key,
                        annotationName: a.Name,
                        flavor: a.Flavor,
                        fullName: a.FullName,
                        argsJson: a.ArgsJson));
                }
            }
        }

        await analyzerPipeline.RunAsync(
            store, file.Id, file.Path, contents, scopeId, repoRoot,
            events, symbolIdByKey).ConfigureAwait(false);
    }
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
            ? Path.GetDirectoryName(Path.IsPathRooted(s.Items[0]) ? s.Items[0] : Path.Join(scope.Root, s.Items[0]))
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
