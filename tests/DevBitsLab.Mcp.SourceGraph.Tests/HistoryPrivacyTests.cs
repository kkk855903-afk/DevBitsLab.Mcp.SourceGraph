using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Server;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class HistoryPrivacyTests
{
    private const string Canary = "MEDINTEROP_PHI_CANARY_7E91";

    private static readonly string[] _excludedRelativePaths =
    {
        Path.Join("bin", "secret.cs"),
        Path.Join("obj", "secret.cs"),
        Path.Join(".vs", "secret.cs"),
        Path.Join("Debug", "secret.cs"),
        Path.Join("Release", "secret.cs"),
        Path.Join("Images", "secret.cs"),
        Path.Join("PatientData", "secret.cs"),
        Path.Join("Database", "secret.cs"),
        Path.Join("Logs", "secret.cs"),
        Path.Join("src", "scan.dcm"),
        Path.Join("src", "portrait.jpg"),
        Path.Join("src", "portrait.jpeg"),
        Path.Join("src", "capture.png"),
    };

    [Fact]
    public async Task BlameAsync_excludedPathsNeverStartGit()
    {
        var root = CreateTempDirectory();
        try
        {
            var starts = 0;
            var runner = new GitBlameRunner(
                logger: null,
                timeout: TimeSpan.FromSeconds(1),
                startProcess: _ =>
                {
                    starts++;
                    return null;
                });

            foreach (var relativePath in _excludedRelativePaths)
            {
                var path = Path.Join(root, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, Canary);

                var result = await runner.BlameAsync(path, root);

                result.IsSuccess.Should().BeFalse();
                result.FailureReason.Should().Be("path excluded by privacy policy");
            }

            var outsidePath = Path.Join(
                Path.GetDirectoryName(root)!,
                "outside-history-root-" + Guid.NewGuid().ToString("N"),
                "secret.cs");
            var outsideResult = await runner.BlameAsync(outsidePath, root);
            outsideResult.FailureReason.Should().Be("path excluded by privacy policy");

            (await runner.IsGitWorkingTreeAsync(Path.Join(root, "PatientData"))).Should().BeFalse();
            starts.Should().Be(0, "privacy filtering must happen before Process.Start");
        }
        finally
        {
            DeleteDirectoryBestEffort(root);
        }
    }

    [SkippableFact]
    public async Task BlameAsync_outOfRepositoryDirectoryLinkNeverStartsGit()
    {
        var root = CreateTempDirectory();
        var outside = CreateTempDirectory();
        try
        {
            var linkedDirectory = Path.Join(root, "src", "External");
            Directory.CreateDirectory(Path.GetDirectoryName(linkedDirectory)!);
            Skip.IfNot(
                PhysicalPathTestSupport.TryCreateDirectoryLink(linkedDirectory, outside),
                "This environment does not permit symbolic-link or junction creation.");
            var linkedPath = Path.Join(linkedDirectory, "Secret.cs");
            await File.WriteAllTextAsync(Path.Join(outside, "Secret.cs"), Canary);

            var starts = 0;
            var runner = new GitBlameRunner(
                logger: null,
                timeout: TimeSpan.FromSeconds(1),
                startProcess: _ =>
                {
                    starts++;
                    return null;
                });

            var result = await runner.BlameAsync(linkedPath, root);

            result.IsSuccess.Should().BeFalse();
            result.FailureReason.Should().Be("path excluded by privacy policy");
            starts.Should().Be(0);
        }
        finally
        {
            DeleteDirectoryBestEffort(root);
            DeleteDirectoryBestEffort(outside);
        }
    }

    [Fact]
    public async Task IncrementalHistoryRequests_forExcludedPathsNeverReachBlameRunner()
    {
        var root = CreateTempDirectory();
        var store = new SqliteGraphStore(Path.Join(root, "history.db"));
        try
        {
            await store.EnsureSchemaAsync();
            var queue = new HistoryQueue();
            var runner = new RecordingGitBlameRunner();
            var service = new HistoryHostedService(
                queue,
                store,
                runner,
                new HistoryOptions(Disabled: false)
                {
                    RepositoryRoot = root,
                    ExcludePatterns = ["**/generated/**"],
                },
                Microsoft.Extensions.Logging.Abstractions.NullLogger<HistoryHostedService>.Instance);

            var excludedPaths = _excludedRelativePaths
                .Select(relativePath => Path.Join(root, relativePath))
                .Append(Path.Join(
                    Path.GetDirectoryName(root)!,
                    "outside-history-root-" + Guid.NewGuid().ToString("N"),
                    "secret.cs"))
                .Append(Path.Join(root, "src", "Generated", "secret.cs"))
                .ToList();
            var symbolIds = new List<long>(excludedPaths.Count);
            for (var i = 0; i < excludedPaths.Count; i++)
            {
                var path = excludedPaths[i];
                var sha = Enumerable.Repeat((byte)(i + 1), 32).ToArray();
                var fileId = await store.UpsertFileAsync(path, sha, DateTimeOffset.UtcNow);
                var symbolId = await SeedSymbolAsync(store, fileId, $"Excluded{i}", $"Canary.Excluded{i}");
                symbolIds.Add(symbolId);
                await queue.Writer.WriteAsync(new HistoryRequest(fileId, path, sha, "default"));
            }

            queue.Writer.TryComplete();
            await service.ExecuteAsyncForOneShot(CancellationToken.None);

            runner.CallCount.Should().Be(0,
                "the shared queue consumer must reject cold and changed-file requests before invoking git");
            foreach (var symbolId in symbolIds)
            {
                (await store.GetSymbolHistoryAsync(symbolId)).Should().BeNull();
            }
        }
        finally
        {
            await store.DisposeAsync();
            DeleteDirectoryBestEffort(root);
        }
    }

    [SkippableFact]
    public async Task PublicHistoryTools_filterExcludedCachedRowsAndCanaries()
    {
        var root = CreateTempDirectory();
        var outside = CreateTempDirectory();
        var store = new SqliteGraphStore(Path.Join(root, "history-tools.db"));
        ScopeHost? host = null;
        try
        {
            await store.EnsureSchemaAsync();
            var linkedDirectory = Path.Join(root, "src", "ExternalAlias");
            Directory.CreateDirectory(Path.GetDirectoryName(linkedDirectory)!);
            Skip.IfNot(
                PhysicalPathTestSupport.TryCreateDirectoryLink(linkedDirectory, outside),
                "This environment does not permit symbolic-link or junction creation.");

            var excludedFileId = await store.UpsertFileAsync(
                Path.Join(root, "PatientData", $"{Canary}.cs"),
                new byte[32],
                DateTimeOffset.UtcNow);
            var excludedSymbolId = await SeedSymbolAsync(
                store,
                excludedFileId,
                "SharedOperation",
                $"Sensitive.{Canary}.SharedOperation");
            await store.UpsertSymbolHistoryAsync(new SymbolHistory(
                excludedSymbolId,
                $"deadbeef{Canary}",
                $"author-{Canary}",
                DateTimeOffset.UtcNow.AddHours(-1),
                3,
                new byte[32]));

            var allowedFileId = await store.UpsertFileAsync(
                Path.Join(root, "src", "Allowed.cs"),
                new byte[32],
                DateTimeOffset.UtcNow);
            var allowedSymbolId = await SeedSymbolAsync(
                store,
                allowedFileId,
                "AllowedOperation",
                "Safe.AllowedOperation");
            await store.UpsertSymbolHistoryAsync(new SymbolHistory(
                allowedSymbolId,
                "0123456789abcdef",
                "safe-author",
                DateTimeOffset.UtcNow.AddHours(-2),
                2,
                new byte[32]));

            var scopeExcludedFileId = await store.UpsertFileAsync(
                Path.Join(root, "src", "Generated", $"{Canary}.cs"),
                new byte[32],
                DateTimeOffset.UtcNow);
            var scopeExcludedSymbolId = await SeedSymbolAsync(
                store,
                scopeExcludedFileId,
                "SharedOperation",
                $"Generated.{Canary}.SharedOperation");
            await store.UpsertSymbolHistoryAsync(new SymbolHistory(
                scopeExcludedSymbolId,
                $"cafebabe{Canary}",
                $"generated-author-{Canary}",
                DateTimeOffset.UtcNow.AddMinutes(-30),
                4,
                new byte[32]));

            var physicallyExcludedFileId = await store.UpsertFileAsync(
                Path.Join(linkedDirectory, $"{Canary}.cs"),
                new byte[32],
                DateTimeOffset.UtcNow);
            var physicallyExcludedSymbolId = await SeedSymbolAsync(
                store,
                physicallyExcludedFileId,
                "SharedOperation",
                $"External.{Canary}.SharedOperation");
            await store.UpsertSymbolHistoryAsync(new SymbolHistory(
                physicallyExcludedSymbolId,
                $"feedface{Canary}",
                $"external-author-{Canary}",
                DateTimeOffset.UtcNow.AddMinutes(-15),
                5,
                new byte[32]));

            var scope = new Scope(
                "default",
                "default",
                root,
                new ScopeProjectSet.Solutions(
                    Array.Empty<string>(),
                    ["**/generated/**"]),
                Isolated: false,
                LastIndexedAt: DateTimeOffset.UtcNow);
            var indexer = new RoslynIndexer(store);
            host = new ScopeHost(scope, store, store.CreateEmbeddingsStore(384), indexer, "");
            host.MarkReady();
            var router = new ScopeRouter();
            router.Register(host);
            router.SetDefaultScope("default");
            var options = new HistoryOptions(Disabled: false);

            var whoAuthored = await HistoryTools.WhoAuthoredAsync(router, options, "SharedOperation");
            VisibleWireText(whoAuthored).Should().NotContain(Canary);

            var recentChanges = await HistoryTools.RecentChangesAsync(router, options, days: 7, limit: 20);
            var recentWire = VisibleWireText(recentChanges);
            recentWire.Should().Contain("Safe.AllowedOperation");
            recentWire.Should().NotContain(Canary);
            recentChanges.StructuredContent.Should().NotBeNull();
            recentChanges.StructuredContent!.Value.GetProperty("changes").GetArrayLength().Should().Be(1);
        }
        finally
        {
            if (host is not null)
            {
                await host.DisposeAsync();
            }
            else
            {
                await store.DisposeAsync();
            }
            DeleteDirectoryBestEffort(root);
            DeleteDirectoryBestEffort(outside);
        }
    }

    private static async Task<long> SeedSymbolAsync(
        SqliteGraphStore store,
        long fileId,
        string name,
        string fqn) =>
        await store.UpsertSymbolAsync(
            $"csharp:M:{fqn}",
            new Symbol(
                Id: 0,
                Name: name,
                Fqn: fqn,
                Kind: "method",
                FileId: fileId,
                StartLine: 1,
                StartCol: 1,
                EndLine: 3,
                EndCol: 1,
                Signature: $"void {name}()",
                ContainerId: null,
                Accessibility: 6));

    private static string VisibleWireText(CallToolResult result)
    {
        var blocks = result.Content?.Select(block => block switch
        {
            TextContentBlock text => text.Text,
            ResourceLinkBlock link => string.Join(
                " ",
                link.Uri,
                link.Name,
                link.Title,
                link.Description),
            _ => string.Empty,
        }) ?? Array.Empty<string>();
        var structured = result.StructuredContent?.GetRawText() ?? string.Empty;
        return string.Join(Environment.NewLine, blocks.Append(structured));
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-history-privacy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectoryBestEffort(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: SQLite sidecars can remain briefly open on Windows.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort: CI ACLs can deny temp cleanup.
        }
    }

    private sealed class RecordingGitBlameRunner : GitBlameRunner
    {
        public int CallCount { get; private set; }

        public override Task<GitBlameResult> BlameAsync(
            string path,
            CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(GitBlameResult.Ok(new[]
            {
                new BlameLine("0123456789abcdef", Canary, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            }));
        }

        public override Task<GitBlameResult> BlameAsync(
            string path,
            string repositoryRoot,
            CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(GitBlameResult.Ok(new[]
            {
                new BlameLine("0123456789abcdef", Canary, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            }));
        }
    }
}
