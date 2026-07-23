using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Verifies the open-language-contract requirement that every canonical key emitted by the
/// built-in C# Roslyn indexer is scheme-prefixed with <c>"csharp:"</c>. After cold-indexing the
/// fixture solution into a temp DB, walk every <see cref="SymbolKeyRow"/> and assert its
/// <c>CanonicalKey</c> starts with the reserved prefix — a regression in the indexer's mapper
/// would surface here as missing or stripped prefixes.
/// </summary>
public sealed class CSharpKeyPrefixTests : IAsyncLifetime
{
    private string _dbPath = string.Empty;
    private SqliteGraphStore? _store;

    public async Task InitializeAsync()
    {
        var slnPath = LocateSolution();
        // Path.Join over Path.Combine — Combine silently drops earlier args when a later one
        // looks absolute; Join always concatenates with a separator regardless. Safer for the
        // test fixture path roots that get composed from inputs we don't fully control.
        var tmp = Path.Join(Path.GetTempPath(), "sourcegraph-keyprefix-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        _dbPath = Path.Join(tmp, "graph.db");
        _store = new SqliteGraphStore(_dbPath);
        await RoslynIndexer.IndexSolutionOnceAsync(slnPath, _store);
    }

    public async Task DisposeAsync()
    {
        if (_store is not null) await _store.DisposeAsync();
        try
        {
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
        }
        catch (IOException) { /* best-effort cleanup; another handle may still hold the file */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup; readonly bit or ACL drift */ }
    }

    private static string LocateSolution()
    {
        var dir = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
        {
            var candidate = Path.Join(d.FullName, "tests", "fixtures", "Sample.sln");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Could not locate tests/fixtures/Sample.sln from " + dir);
    }

    [Fact]
    public async Task EveryCanonicalKey_startsWithCsharpScheme()
    {
        var rows = await _store!.GetAllSymbolKeysAsync();
        rows.Should().NotBeEmpty("the fixture solution declares Sample.Domain types and methods");
        rows.Should().AllSatisfy(r =>
            r.CanonicalKey.Should().StartWith("csharp:",
                "open-language-contract requires every C# indexer emission to be scheme-prefixed"));
    }
}
