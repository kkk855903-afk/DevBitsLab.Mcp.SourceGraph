using System;
using System.IO;
using System.Linq;
using System.Text.Json;
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
/// Coverage for <see cref="CorruptionGuard"/>: detection of SQLite-level corruption errors,
/// the verify-and-mark-degraded dispatch logic, and the false-alarm path. The reactive flow
/// is invoked by <see cref="ScopedExecution"/> on every wrapped tool call; tests here exercise
/// the helper directly so the contract is pinned independent of dispatch shape.
///
/// Joins the <c>HealLogState</c> collection so the process-static heal-log path doesn't race
/// with other heal-aware tests.
/// </summary>
[Collection("HealLogState")]
public sealed class CorruptionGuardTests : IAsyncLifetime
{
    private string _tempDir = string.Empty;
    private string _dbPath = string.Empty;
    private string _healPath = string.Empty;
    private string? _previousHealPath;
    private SqliteGraphStore? _store;
    private ScopeHost? _host;
    private SqliteScopeRegistry? _registry;

    public async Task InitializeAsync()
    {
        _tempDir = Path.Join(Path.GetTempPath(), "corruption-guard-tests-" + Guid.NewGuid().ToString("N"));
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
        var emb = _store.CreateEmbeddingsStore(384);
        var indexer = new RoslynIndexer(_store);
        _host = new ScopeHost(scope, _store, emb, indexer, "");
        _host.MarkReady();

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
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void IsCorruptionError_recognises_sqlError11_andSqlError26()
    {
        var ex11 = new SqliteException("corrupt page", 11);
        var ex26 = new SqliteException("not a db", 26);
        var ex5 = new SqliteException("busy", 5);
        var ioEx = new IOException("not sqlite");

        CorruptionGuard.IsCorruptionError(ex11).Should().BeTrue();
        CorruptionGuard.IsCorruptionError(ex26).Should().BeTrue();
        CorruptionGuard.IsCorruptionError(ex5).Should().BeFalse();
        CorruptionGuard.IsCorruptionError(ioEx).Should().BeFalse();
    }

    [Fact]
    public async Task VerifyAndMark_falseAlarm_emitsCleanHeal_andDoesNotMarkDegraded()
    {
        // Healthy DB; integrity_check returns "ok"; we feed a synthetic corruption exception.
        var fakeCorruption = new SqliteException("phantom corruption error", 11);

        await CorruptionGuard.VerifyAndMarkAsync(_host!, _registry!, fakeCorruption, NullLogger.Instance, CancellationToken.None);

        _host!.Status.Should().Be("ok"); // not flipped
        var post = await _registry!.GetAsync("default");
        (post?.Status ?? "").Should().NotBe("degraded");

        var line = HealLineForScope("default");
        line.GetProperty("kind").GetString().Should().Be("corruption-suspected-but-clean");
        line.GetProperty("ok").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task VerifyAndMark_confirmedCorruption_emitsHeal_andMarksScopeDegraded()
    {
        // Plant data + corrupt DB so integrity_check actually fails.
        var fileId = await _store!.UpsertFileAsync("/tmp/A.cs", new byte[] { 1, 2, 3 }, DateTimeOffset.UtcNow);
        for (var i = 0; i < 50; i++)
        {
            await _store.UpsertSymbolAsync($"S:csharp:K{i}", new Symbol(
                Id: 0, Name: $"Foo{i}", Fqn: $"X.Foo{i}", Kind: "class", FileId: fileId,
                StartLine: i + 1, StartCol: 1, EndLine: i + 1, EndCol: 1, Signature: $"class Foo{i}",
                ContainerId: null, Modifiers: null, Accessibility: (int)Accessibility.Public, XmlSummary: null));
        }
        await _host!.DisposeAsync();
        _host = null;
        _store = null;
        SqliteConnection.ClearAllPools();

        // Stomp deeper than page 1 so the file still opens but page checksums fail.
        var rnd = new Random(42);
        using (var stream = new FileStream(_dbPath, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Seek(8192, SeekOrigin.Begin);
            var bytes = new byte[800];
            rnd.NextBytes(bytes);
            await stream.WriteAsync(bytes);
        }

        // Reopen + rebuild the host; integrity_check from inside VerifyAndMark will fail.
        _store = new SqliteGraphStore(_dbPath);
        var scope = new Scope("default", "default", _tempDir,
            new ScopeProjectSet.Solutions(Array.Empty<string>(), Array.Empty<string>()),
            false, DateTimeOffset.UtcNow);
        var emb = _store.CreateEmbeddingsStore(384);
        var indexer = new RoslynIndexer(_store);
        _host = new ScopeHost(scope, _store, emb, indexer, "");
        _host.MarkReady();

        var fakeCorruption = new SqliteException("page checksum mismatch", 11);

        await CorruptionGuard.VerifyAndMarkAsync(_host, _registry!, fakeCorruption, NullLogger.Instance, CancellationToken.None);

        _host.Status.Should().Be("degraded");
        _host.StatusMessage.Should().Contain("corruption detected");
        _host.StatusMessage.Should().Contain("repair_scope mode=rebuild");

        var post = await _registry!.GetAsync("default");
        post!.Status.Should().Be("degraded");
        post.StatusMessage.Should().Contain("corruption detected");

        var line = HealLineForScope("default");
        line.GetProperty("kind").GetString().Should().Be("corruption-detected");
        line.GetProperty("ok").GetBoolean().Should().BeTrue();
    }

    private JsonElement HealLineForScope(string scopeId)
    {
        if (!File.Exists(_healPath)) throw new InvalidOperationException("heals.jsonl does not exist");
        foreach (var raw in File.ReadAllLines(_healPath))
        {
            var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.GetProperty("scope").GetString() == scopeId)
            {
                return doc.RootElement.Clone();
            }
        }
        throw new InvalidOperationException($"no heals.jsonl line with scope = `{scopeId}`");
    }
}
