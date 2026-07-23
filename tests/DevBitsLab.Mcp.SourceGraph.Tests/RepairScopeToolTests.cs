using System;
using System.IO;
using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Server.Observability;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Coverage for <see cref="MinimalRepair.RunAsync"/>: integrity check first (refusing if non-`ok`),
/// embeddings prune, retry-wrapped reload + index. Tests run against a real <see cref="ScopeHost"/>
/// + <see cref="SqliteScopeRegistry"/> in a temp directory; the heavy rebuild path
/// (<see cref="LiveIndexService.RebuildScopeAsync"/>) is exercised by integration tests. Joins the
/// <c>HealLogState</c> collection so the process-static path doesn't race with other heal-aware
/// tests.
/// </summary>
[Collection("HealLogState")]
public sealed class RepairScopeToolTests : IAsyncLifetime
{
    private string _tempDir = string.Empty;
    private string _dbPath = string.Empty;
    private SqliteGraphStore? _store;
    private ScopeHost? _host;
    private SqliteScopeRegistry? _registry;
    private string _healPath = string.Empty;
    private string? _previousHealPath;

    private static readonly IReadOnlyList<TimeSpan> TinyBackoffs =
        new[] { TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10) };

    public async Task InitializeAsync()
    {
        _tempDir = Path.Join(Path.GetTempPath(), "repair-scope-tool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Join(_tempDir, "graph.db");

        _previousHealPath = HealLog.LogPath;
        _healPath = Path.Join(_tempDir, "heals.jsonl");
        HealLog.Configure(_healPath);

        _store = new SqliteGraphStore(_dbPath);
        await _store.EnsureSchemaAsync();

        var scope = new Scope(
            Id: "default",
            Name: "default",
            Root: _tempDir,
            ProjectSet: new ScopeProjectSet.Solutions(Array.Empty<string>(), Array.Empty<string>()),
            Isolated: false,
            LastIndexedAt: DateTimeOffset.UtcNow);

        var embeddingsStore = _store.CreateEmbeddingsStore(384);
        var indexer = new RoslynIndexer(_store);
        // SolutionPath = "" so the reload-and-index branch is skipped (no workspace to reload).
        // The integrity-check + prune branches still run end-to-end.
        _host = new ScopeHost(scope, _store, embeddingsStore, indexer, "");
        _host.MarkReady();
        _host.LastIndexedAt = DateTimeOffset.UtcNow;

        _registry = new SqliteScopeRegistry(Path.Join(_tempDir, "_meta.db"), NullLogger<SqliteScopeRegistry>.Instance);
        await _registry.EnsureSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
        else if (_store is not null) await _store.DisposeAsync();
        if (_registry is not null) await _registry.DisposeAsync();
        SqliteConnection.ClearAllPools();
        HealLog.Configure(string.IsNullOrEmpty(_previousHealPath) ? null : _previousHealPath);
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Minimal_onHealthyScope_returnsOk_withZeroPruneCount()
    {
        var result = await MinimalRepair.RunAsync(_host!, _registry!, TinyBackoffs, NullLogger.Instance, CancellationToken.None);

        result.Refused.Should().BeFalse();
        result.IntegrityCheck.Should().Be("ok");
        result.PrunedEmbeddings.Should().Be(0);
        result.Reindexed.Should().BeFalse(); // no SolutionPath
        result.Message.Should().Contain("ok");
        result.Message.Should().Contain("0 orphan embeddings");
    }

    [Fact]
    public async Task Minimal_onCorruptDb_refuses_andHintsAtRebuildMode()
    {
        // Plant data + dispose so we can corrupt the file, then reopen.
        var fileId = await _store!.UpsertFileAsync("/tmp/A.cs", new byte[] { 1, 2, 3 }, DateTimeOffset.UtcNow);
        for (var i = 0; i < 50; i++)
        {
            await _store.UpsertSymbolAsync($"S:csharp:K{i}", new Symbol(
                Id: 0, Name: $"Foo{i}", Fqn: $"Foo{i}", Kind: "class", FileId: fileId,
                StartLine: i + 1, StartCol: 1, EndLine: i + 1, EndCol: 1, Signature: $"class Foo{i}",
                ContainerId: null, Modifiers: null, Accessibility: (int)Accessibility.Public, XmlSummary: null));
        }
        await _host!.DisposeAsync();
        _host = null;
        _store = null;
        SqliteConnection.ClearAllPools();

        var rnd = new Random(42);
        using (var stream = new FileStream(_dbPath, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Seek(8192, SeekOrigin.Begin);
            var bytes = new byte[800];
            rnd.NextBytes(bytes);
            await stream.WriteAsync(bytes);
        }

        // Reopen with a fresh ScopeHost.
        _store = new SqliteGraphStore(_dbPath);
        var scope = new Scope("default", "default", _tempDir,
            new ScopeProjectSet.Solutions(Array.Empty<string>(), Array.Empty<string>()),
            false, DateTimeOffset.UtcNow);
        var embeddingsStore = _store.CreateEmbeddingsStore(384);
        var indexer = new RoslynIndexer(_store);
        _host = new ScopeHost(scope, _store, embeddingsStore, indexer, "");
        _host.MarkReady();

        var result = await MinimalRepair.RunAsync(_host, _registry!, TinyBackoffs, NullLogger.Instance, CancellationToken.None);

        result.Refused.Should().BeTrue();
        result.IntegrityCheck.Should().NotBe("ok");
        result.Message.Should().Contain("repair_scope mode=rebuild");
        result.PrunedEmbeddings.Should().Be(0);
        result.Reindexed.Should().BeFalse();
    }
}
