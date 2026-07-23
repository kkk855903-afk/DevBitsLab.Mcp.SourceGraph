using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Coverage for the <c>verify_scope</c> tool. Builds a synthetic <see cref="ScopeHost"/> against a
/// temp DB seeded with a known set of files / symbols so the four documented behaviours (happy
/// path, degraded short-circuit, drift detection, integrity-check failure) can be exercised
/// without standing up a Roslyn workspace.
/// </summary>
public sealed class VerifyScopeToolTests : IAsyncLifetime
{
    private string _tempDir = string.Empty;
    private string _dbPath = string.Empty;
    private SqliteGraphStore? _store;
    private ScopeHost? _host;
    private ScopeRouter? _router;
    private readonly List<string> _externalDirectories = [];

    public async Task InitializeAsync()
    {
        _tempDir = Path.Join(Path.GetTempPath(), "verify-scope-tool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Join(_tempDir, "graph.db");
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

        _host = new ScopeHost(scope, _store, embeddingsStore, indexer, "");
        _host.MarkReady();
        _host.LastIndexedAt = DateTimeOffset.UtcNow;
        _router = new ScopeRouter();
        _router.Register(_host);
        _router.SetDefaultScope("default");
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
        else if (_store is not null) await _store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        foreach (var path in _externalDirectories)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task HappyPath_returnsOk_withNonZeroCounts_andNoDrift()
    {
        // Plant a file on disk + index it (file path + content_sha256 + a symbol).
        var srcPath = Path.Join(_tempDir, "Foo.cs");
        var srcBytes = Encoding.UTF8.GetBytes("class Foo { void Bar() {} }");
        await File.WriteAllBytesAsync(srcPath, srcBytes);
        var sha = SHA256.HashData(srcBytes);
        var fileId = await _store!.UpsertFileAsync(srcPath, sha, DateTimeOffset.UtcNow);
        await _store.UpsertSymbolAsync("S:csharp:K1", new Symbol(
            Id: 0, Name: "Foo", Fqn: "Foo", Kind: "class", FileId: fileId,
            StartLine: 1, StartCol: 1, EndLine: 1, EndCol: 30, Signature: "class Foo",
            ContainerId: null, Modifiers: null, Accessibility: (int)Accessibility.Public, XmlSummary: null));

        var result = await ScopeTools.VerifyScopeAsync(_router!, "default");
        var dto = JsonSerializer.Deserialize(result.StructuredContent!.Value.GetRawText(), ToolOutputJsonContext.Default.VerifyScopeResult)!;

        dto.Scopes.Should().HaveCount(1);
        var row = dto.Scopes[0];
        row.Scope.Should().Be("default");
        row.Status.Should().Be("ok");
        row.RowCounts!.Symbols.Should().Be(1);
        row.RowCounts.Files.Should().Be(1);
        row.IntegrityCheck.Should().Be("ok");
        row.DriftSample!.Sampled.Should().Be(1);
        row.DriftSample.Changed.Should().Be(0);
        row.DriftSample.ChangedPaths.Should().BeEmpty();
        row.SchemaVersion.Should().Be(Schema.Version);
        row.ViewsSchemaVersion.Should().Be(Views.SchemaVersion);
    }

    [Fact]
    public async Task DegradedScope_returnsStatusMessage_andOmitsRowCounts()
    {
        _host!.Status = "degraded";
        _host.StatusMessage = "scope DB file missing — call repair_scope or restart";

        var result = await ScopeTools.VerifyScopeAsync(_router!, "default");
        var dto = JsonSerializer.Deserialize(result.StructuredContent!.Value.GetRawText(), ToolOutputJsonContext.Default.VerifyScopeResult)!;

        var row = dto.Scopes.Single();
        row.Status.Should().Be("degraded");
        row.StatusMessage.Should().Be("scope DB file missing — call repair_scope or restart");
        row.RowCounts.Should().BeNull();
        row.IntegrityCheck.Should().BeNull();
        row.DriftSample.Should().BeNull();
    }

    [Fact]
    public async Task Drift_isDetected_whenOnDiskShaDiffersFromDb()
    {
        // Index file with one SHA, then mutate on-disk bytes so the on-disk SHA differs.
        var srcPath = Path.Join(_tempDir, "Drift.cs");
        var originalBytes = Encoding.UTF8.GetBytes("class Drift { /* v1 */ }");
        await File.WriteAllBytesAsync(srcPath, originalBytes);
        var originalSha = SHA256.HashData(originalBytes);
        await _store!.UpsertFileAsync(srcPath, originalSha, DateTimeOffset.UtcNow);

        var mutated = Encoding.UTF8.GetBytes("class Drift { /* v2 — different content */ }");
        await File.WriteAllBytesAsync(srcPath, mutated);

        var result = await ScopeTools.VerifyScopeAsync(_router!, "default");
        var dto = JsonSerializer.Deserialize(result.StructuredContent!.Value.GetRawText(), ToolOutputJsonContext.Default.VerifyScopeResult)!;

        var row = dto.Scopes.Single();
        row.DriftSample!.Changed.Should().Be(1);
        row.DriftSample.ChangedPaths.Should().ContainSingle().Which.Should().Be(srcPath);
    }

    [SkippableFact]
    public async Task Drift_sample_neverReadsFileThroughOutOfRepositoryDirectoryLink()
    {
        var outside = Path.Join(
            Path.GetTempPath(),
            "verify-scope-outside-" + Guid.NewGuid().ToString("N"));
        _externalDirectories.Add(outside);
        Directory.CreateDirectory(outside);
        var outsidePath = Path.Join(outside, "Outside.cs");
        var originalBytes = Encoding.UTF8.GetBytes("class Outside { /* v1 */ }");
        await File.WriteAllBytesAsync(outsidePath, originalBytes);

        var link = Path.Join(_tempDir, "External");
        Skip.IfNot(
            PhysicalPathTestSupport.TryCreateDirectoryLink(link, outside),
            "This environment does not permit symbolic-link or junction creation.");
        var linkedPath = Path.Join(link, "Outside.cs");
        await _store!.UpsertFileAsync(
            linkedPath,
            SHA256.HashData(originalBytes),
            DateTimeOffset.UtcNow);

        await File.WriteAllTextAsync(outsidePath, "class Outside { /* v2 */ }");

        var result = await ScopeTools.VerifyScopeAsync(_router!, "default");
        var dto = JsonSerializer.Deserialize(
            result.StructuredContent!.Value.GetRawText(),
            ToolOutputJsonContext.Default.VerifyScopeResult)!;

        var row = dto.Scopes.Single();
        row.DriftSample!.Sampled.Should().Be(0);
        row.DriftSample.Changed.Should().Be(0);
        row.DriftSample.ChangedPaths.Should().BeEmpty();
    }

    [Fact]
    public async Task IntegrityCheck_returnsNonOk_onCorruptedDb()
    {
        // Plant some real data, then close the connection and stomp a deeper page.
        var srcPath = Path.Join(_tempDir, "Foo.cs");
        var srcBytes = Encoding.UTF8.GetBytes("class Foo {}");
        await File.WriteAllBytesAsync(srcPath, srcBytes);
        var sha = SHA256.HashData(srcBytes);
        var fileId = await _store!.UpsertFileAsync(srcPath, sha, DateTimeOffset.UtcNow);
        for (var i = 0; i < 50; i++)
        {
            await _store.UpsertSymbolAsync($"S:csharp:K{i}", new Symbol(
                Id: 0, Name: $"Foo{i}", Fqn: $"Foo{i}", Kind: "class", FileId: fileId,
                StartLine: i + 1, StartCol: 1, EndLine: i + 1, EndCol: 1, Signature: $"class Foo{i}",
                ContainerId: null, Modifiers: null, Accessibility: (int)Accessibility.Public, XmlSummary: null));
        }

        // Detach the host so we can reopen the store after corrupting it.
        await _host!.DisposeAsync();
        _host = null;
        _store = null;
        SqliteConnection.ClearAllPools();

        // Stomp deep enough that the file still opens but page checksums fail.
        var rnd = new Random(42);
        using (var stream = new FileStream(_dbPath, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Seek(8192, SeekOrigin.Begin);
            var bytes = new byte[800];
            rnd.NextBytes(bytes);
            await stream.WriteAsync(bytes);
        }

        // Reopen and rebuild the host.
        _store = new SqliteGraphStore(_dbPath);
        var scope = new Scope("default", "default", _tempDir,
            new ScopeProjectSet.Solutions(Array.Empty<string>(), Array.Empty<string>()),
            false, DateTimeOffset.UtcNow);
        var embeddingsStore = _store.CreateEmbeddingsStore(384);
        var indexer = new RoslynIndexer(_store);
        _host = new ScopeHost(scope, _store, embeddingsStore, indexer, "");
        _host.MarkReady();
        _router = new ScopeRouter();
        _router.Register(_host);
        _router.SetDefaultScope("default");

        var result = await ScopeTools.VerifyScopeAsync(_router, "default");
        var dto = JsonSerializer.Deserialize(result.StructuredContent!.Value.GetRawText(), ToolOutputJsonContext.Default.VerifyScopeResult)!;
        var row = dto.Scopes.Single();
        row.IntegrityCheck.Should().NotBe("ok");
    }
}
