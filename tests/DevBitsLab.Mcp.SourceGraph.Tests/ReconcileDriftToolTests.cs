using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Coverage for <see cref="DriftReconciler.ComputeAsync"/>: walks the source tree, queries the DB,
/// and computes the symmetric difference (changed + added + removed + unchanged). The
/// <c>ApplyAsync</c> side requires a real Roslyn workspace and is exercised by integration tests;
/// the compute path here gives the contract its assertion floor.
/// </summary>
public sealed class ReconcileDriftToolTests : IAsyncLifetime
{
    private string _root = string.Empty;
    private string _dbPath = string.Empty;
    private SqliteGraphStore? _store;
    private ScopeHost? _host;

    public async Task InitializeAsync()
    {
        _root = Path.Join(Path.GetTempPath(), "reconcile-drift-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _dbPath = Path.Join(_root, ".sourcegraph", "scopes", "default.db");
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);

        _store = new SqliteGraphStore(_dbPath);
        await _store.EnsureSchemaAsync();

        var scope = new Scope(
            Id: "default",
            Name: "default",
            Root: _root,
            ProjectSet: new ScopeProjectSet.Solutions(
                Array.Empty<string>(),
                ["**/generated/**"]),
            Isolated: false,
            LastIndexedAt: DateTimeOffset.UtcNow);

        var embeddings = _store.CreateEmbeddingsStore(384);
        var indexer = new RoslynIndexer(_store);
        _host = new ScopeHost(scope, _store, embeddings, indexer, "");
        _host.MarkReady();
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
        else if (_store is not null) await _store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task ComputeAsync_returns_correct_diffCounts_forKnownDrift()
    {
        // Plant 4 files in DB: A B C D. After "drift": A is edited, B/C unchanged, D deleted, E added.
        var srcDir = Path.Join(_root, "src");
        Directory.CreateDirectory(srcDir);
        var aPath = Path.Join(srcDir, "A.cs");
        var bPath = Path.Join(srcDir, "B.cs");
        var cPath = Path.Join(srcDir, "C.cs");
        var dPath = Path.Join(srcDir, "D.cs");
        var ePath = Path.Join(srcDir, "E.cs");

        await PlantFile(aPath, "class A_v1 {}");
        await PlantFile(bPath, "class B {}");
        await PlantFile(cPath, "class C {}");
        await PlantFile(dPath, "class D {}");

        // Index all four into the DB by upserting their bytes' SHA-256.
        await UpsertFile(aPath);
        await UpsertFile(bPath);
        await UpsertFile(cPath);
        await UpsertFile(dPath);

        // Now mutate disk: A's content changes, D vanishes, E appears.
        await PlantFile(aPath, "class A_v2 {} // edited");
        File.Delete(dPath);
        await PlantFile(ePath, "class E {} // added");

        var diff = await DriftReconciler.ComputeAsync(_host!, maxFiles: 1000, CancellationToken.None);

        diff.Scanned.Should().Be(4); // A + B + C + E
        diff.Changed.Should().ContainSingle().Which.Should().Be(aPath);
        diff.Added.Should().ContainSingle().Which.Should().Be(ePath);
        diff.Removed.Should().ContainSingle().Which.Should().Be(dPath);
        diff.Unchanged.Should().Be(2); // B + C
        diff.Partial.Should().BeFalse();
    }

    [Fact]
    public async Task ComputeAsync_respects_maxFiles_andSetsPartialFlag()
    {
        var srcDir = Path.Join(_root, "src");
        Directory.CreateDirectory(srcDir);
        for (var i = 0; i < 5; i++)
        {
            await PlantFile(Path.Join(srcDir, $"F{i}.cs"), $"class F{i} {{}}");
        }

        var diff = await DriftReconciler.ComputeAsync(_host!, maxFiles: 3, CancellationToken.None);
        diff.Scanned.Should().Be(3);
        diff.Partial.Should().BeTrue();
    }

    [Fact]
    public async Task ComputeAsync_partialWalk_doesNotMisclassifyDbPathsAsRemoved()
    {
        // Regression: before the fix, ComputeAsync would mark any DB path absent from the
        // (capped) `disk` set as `removed` — leading ApplyAsync to wipe still-existing files
        // when the walk hit max_files. Plant 5 files, index all 5, walk with cap=3 → the 2
        // unwalked files must NOT show up as removed.
        var srcDir = Path.Join(_root, "src");
        Directory.CreateDirectory(srcDir);
        for (var i = 0; i < 5; i++)
        {
            var path = Path.Join(srcDir, $"F{i}.cs");
            await PlantFile(path, $"class F{i} {{}}");
            await UpsertFile(path);
        }

        var diff = await DriftReconciler.ComputeAsync(_host!, maxFiles: 3, CancellationToken.None);
        diff.Partial.Should().BeTrue();
        diff.Removed.Should().BeEmpty(
            "removal detection requires a complete walk; partial walks must not infer deletions");
    }

    [Fact]
    public async Task ComputeAsync_emptyDirectory_yieldsNoDrift()
    {
        var diff = await DriftReconciler.ComputeAsync(_host!, maxFiles: 100, CancellationToken.None);
        diff.Scanned.Should().Be(0);
        diff.Changed.Should().BeEmpty();
        diff.Added.Should().BeEmpty();
        diff.Removed.Should().BeEmpty();
        diff.Unchanged.Should().Be(0);
    }

    [Fact]
    public async Task ComputeAsync_doesNotDiscoverScopeExcludedFiles()
    {
        var allowed = Path.Join(_root, "src", "Allowed.cs");
        var excluded = Path.Join(_root, "src", "Generated", "Hidden.cs");
        await PlantFile(allowed, "class Allowed {}");
        await PlantFile(excluded, "class Hidden {}");

        var diff = await DriftReconciler.ComputeAsync(
            _host!,
            maxFiles: 100,
            CancellationToken.None);

        diff.Scanned.Should().Be(1);
        diff.Added.Should().Equal(allowed);
        diff.Added.Should().NotContain(excluded);
    }

    [Fact]
    public async Task ComputeAsync_purgesRowsIndexedBeforeExcludeWasAdded()
    {
        var excluded = Path.Join(_root, "src", "Generated", "StaleSecret.cs");
        await PlantFile(excluded, "class StaleSecret {}");
        var fileId = await UpsertFile(excluded);
        await _store!.UpsertSymbolAsync(
            "csharp:T:StaleSecret",
            new Symbol(
                0,
                "StaleSecret",
                "StaleSecret",
                "class",
                fileId,
                1,
                1,
                1,
                21,
                "class StaleSecret",
                null));

        (await _store.FindSymbolsAsync("StaleSecret")).Should().ContainSingle(
            "the row models data indexed before the generated/** exclude was introduced");

        var diff = await DriftReconciler.ComputeAsync(
            _host!,
            maxFiles: 100,
            CancellationToken.None);

        diff.Scanned.Should().Be(0);
        diff.Removed.Should().BeEmpty(
            "excluded rows are purged before ordinary drift comparison");
        (await _store.FindSymbolsAsync("StaleSecret")).Should().BeEmpty();
        (await _store.GetAllFilesAsync()).Should().NotContain(file => file.Path == excluded);
    }

    [Fact]
    public async Task ExcludedFilePurger_preservesAllowedSourceGeneratedBuildOutput()
    {
        var generated = Path.Join(_root, "obj", "CompilerOutput.g.cs");
        await PlantFile(generated, "class CompilerOutput {}");
        var bytes = await File.ReadAllBytesAsync(generated);
        var fileId = await _store!.UpsertFileAsync(
            generated,
            SHA256.HashData(bytes),
            DateTimeOffset.UtcNow,
            isGenerated: true);
        await _store.UpsertSymbolAsync(
            "csharp:T:CompilerOutput",
            new Symbol(
                0,
                "CompilerOutput",
                "CompilerOutput",
                "class",
                fileId,
                1,
                1,
                1,
                24,
                "class CompilerOutput",
                null));

        (await ExcludedFilePurger.PurgeAsync(_host!, CancellationToken.None)).Should().Be(0);
        (await _store.FindSymbolsAsync("CompilerOutput")).Should().ContainSingle();
        (await _store.GetAllFilesAsync()).Should().Contain(file => file.Path == generated);
    }

    private static async Task PlantFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
    }

    private async Task<long> UpsertFile(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        var sha = SHA256.HashData(bytes);
        return await _store!.UpsertFileAsync(path, sha, DateTimeOffset.UtcNow);
    }
}
