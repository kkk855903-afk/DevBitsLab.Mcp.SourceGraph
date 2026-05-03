using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Server;
using DevBitsLab.Mcp.SourceGraph.Server.Cli;
using DevBitsLab.Mcp.SourceGraph.Server.Observability;
using DevBitsLab.Mcp.SourceGraph.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    builder.Services.AddSingleton<IGraphStore>(sp =>
        new SqliteGraphStore(dbPath, sp.GetRequiredService<ILogger<SqliteGraphStore>>()));
    builder.Services.AddSingleton<HistoryQueue>();
    builder.Services.AddSingleton(sp => new GitBlameRunner(sp.GetService<ILogger<GitBlameRunner>>()));

    var solutionFull = string.IsNullOrEmpty(cli.SolutionPath) ? null : Path.GetFullPath(cli.SolutionPath);
    var historyDisabled = await ResolveHistoryDisabledAsync(cli, solutionFull).ConfigureAwait(false);
    builder.Services.AddSingleton(new HistoryOptions(historyDisabled));

    builder.Services.AddSingleton(sp =>
    {
        var indexer = new RoslynIndexer(sp.GetRequiredService<IGraphStore>(), sp.GetRequiredService<ILogger<RoslynIndexer>>());
        if (!historyDisabled)
        {
            var queue = sp.GetRequiredService<HistoryQueue>();
            indexer.OnFileIndexed = (fileId, path, sha) =>
                queue.Writer.WriteAsync(new HistoryRequest(fileId, path, sha)).AsTask();
        }
        return indexer;
    });
    builder.Services.AddSingleton(new LiveIndexOptions(SolutionPath: solutionFull));
    builder.Services.AddHostedService<HistoryHostedService>();
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
    await store.EnsureSchemaAsync().ConfigureAwait(false);

    var historyDisabled = await ResolveHistoryDisabledAsync(cli, solutionFull).ConfigureAwait(false);
    var queue = new HistoryQueue();
    var blamer = new GitBlameRunner(loggerFactory.CreateLogger<GitBlameRunner>());

    // For one-shot index runs we don't bother with the BackgroundService scaffolding — drive the
    // history pipeline as a single Task that we await for natural completion when the channel
    // closes. StopAsync cancels the stoppingToken which would race-cancel mid-drain.
    var historyTask = historyDisabled
        ? Task.CompletedTask
        : RunHistoryPipelineAsync(queue, store, blamer, loggerFactory.CreateLogger<HistoryHostedService>());

    await using var indexer = new RoslynIndexer(store, loggerFactory.CreateLogger<RoslynIndexer>());
    if (!historyDisabled)
    {
        indexer.OnFileIndexed = (fileId, path, sha) =>
            queue.Writer.WriteAsync(new HistoryRequest(fileId, path, sha)).AsTask();
    }
    await indexer.OpenAsync(solutionFull).ConfigureAwait(false);
    var result = await indexer.IndexAllAsync().ConfigureAwait(false);

    // Drain the channel so the history pipeline finishes before we exit.
    queue.Writer.Complete();
    await historyTask.ConfigureAwait(false);

    Console.WriteLine($"indexed {result.FilesIndexed} files, {result.SymbolsIndexed} symbols, {result.ReferencesIndexed} refs in {result.Elapsed.TotalSeconds:F2}s");
    Console.WriteLine($"database: {cli.ResolvedDbPath()}");
    if (historyDisabled) Console.WriteLine("history: disabled (--no-history or git unavailable)");
    return 0;
}

/// <summary>
/// Run the history pipeline as a single async loop without the BackgroundService scaffolding.
/// Used by the one-shot <c>index</c> command where we want the pipeline to drain naturally on
/// channel close without StopAsync's stoppingToken cancellation racing the drain.
/// </summary>
static async Task RunHistoryPipelineAsync(
    HistoryQueue queue,
    DevBitsLab.Mcp.SourceGraph.Storage.IGraphStore store,
    GitBlameRunner blamer,
    ILogger<HistoryHostedService> logger)
{
    // Drive the same processing as the hosted service but via an explicit task we own. This
    // avoids the BackgroundService.StopAsync race where the stopping token cancels the loop
    // before it drains. The consumer exits naturally once queue.Writer.Complete() is called.
    var svc = new HistoryHostedService(
        queue, store, blamer, new HistoryOptions(false), logger);
    var runner = svc.ExecuteAsyncForOneShot(CancellationToken.None);
    await runner.ConfigureAwait(false);
}

/// <summary>
/// Decide whether the git-blame pipeline should run. Disabled when <c>--no-history</c> is set,
/// when no solution is loaded (we can't determine a working tree), or when the solution's
/// directory isn't a git working tree (probed once via <c>git rev-parse --is-inside-work-tree</c>).
/// </summary>
static async Task<bool> ResolveHistoryDisabledAsync(CommandLine cli, string? solutionFull)
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
