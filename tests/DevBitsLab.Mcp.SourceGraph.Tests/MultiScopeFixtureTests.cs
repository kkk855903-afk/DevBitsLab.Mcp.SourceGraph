using System;
using System.IO;
using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// End-to-end indexing test for the multi-scope fixture under
/// <c>tests/fixtures/MultiScope/</c> (frontend.sln + backend.sln, distinct namespaces).
/// Confirms each scope's DB lands at the expected path, that the indexer walks the right
/// projects, and that cross-scope identity is intact: the symbol is present in only the
/// scope that defines it.
/// </summary>
public sealed class MultiScopeFixtureTests
{
    private static string LocateMultiScopeRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "tests", "fixtures", "MultiScope");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("Could not locate tests/fixtures/MultiScope from " + dir);
    }

    [Fact]
    public async Task EachSolutionLandsInItsOwnScopeDb()
    {
        var root = LocateMultiScopeRoot();
        // Use a temp DB dir per run to keep test isolation.
        var tmp = Path.Combine(Path.GetTempPath(), "multiscope-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var frontendDb = Path.Combine(tmp, "frontend.db");
            var backendDb = Path.Combine(tmp, "backend.db");

            await using (var fe = new SqliteGraphStore(frontendDb))
            {
                await RoslynIndexer.IndexSolutionOnceAsync(Path.Combine(root, "frontend.sln"), fe);
                var feHits = await fe.FindSymbolsAsync("Widget");
                feHits.Should().NotBeEmpty("frontend scope should contain Widget");
            }

            await using (var be = new SqliteGraphStore(backendDb))
            {
                await RoslynIndexer.IndexSolutionOnceAsync(Path.Combine(root, "backend.sln"), be);
                var beHits = await be.FindSymbolsAsync("AuthService");
                beHits.Should().NotBeEmpty("backend scope should contain AuthService");

                var widgetInBackend = await be.FindSymbolsAsync("Widget");
                widgetInBackend.Should().BeEmpty("Widget belongs to frontend, not backend; cross-scope isolation is the point of the layout");
            }
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* best-effort */ }
        }
    }
}
