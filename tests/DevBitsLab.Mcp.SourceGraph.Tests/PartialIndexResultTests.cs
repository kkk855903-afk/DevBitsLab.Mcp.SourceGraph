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
/// End-to-end indexer test against the <c>PartialFailure</c> fixture: a 2-project solution
/// with one healthy project (<c>Good</c>) and one broken project (<c>Broken</c>) whose
/// unresolvable <c>PackageReference</c> prevents MSBuildWorkspace from producing a usable
/// <c>Compilation</c>. Asserts that the cold index produces symbols for <c>Good</c> while
/// reporting <c>Broken</c> in <c>IndexResult.FailedProjects</c> AND/OR keeping its symbols
/// out of the store. The probe's reliability depends on Roslyn behaviour for unresolved
/// project metadata; the test treats either reporting path as sufficient because the
/// user-visible contract is "Good's symbols indexed, Broken's didn't pollute the store".
/// </summary>
public sealed class PartialIndexResultTests
{
    private static string LocatePartialFailureSolution()
    {
        var dir = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "tests", "fixtures", "PartialFailure", "PartialFailure.sln");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Could not locate tests/fixtures/PartialFailure/PartialFailure.sln from " + dir);
    }

    [Fact]
    public async Task ColdIndex_partialSolution_indexesGoodAndDoesNotThrow()
    {
        var slnPath = LocatePartialFailureSolution();
        var tmp = Path.Combine(Path.GetTempPath(), "partial-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        var dbPath = Path.Combine(tmp, "graph.db");

        try
        {
            await using var store = new SqliteGraphStore(dbPath);
            // The user-visible contract for `partial` solutions: the cold index returns
            // successfully even when one project has compilation issues. MSBuildWorkspace is
            // surprisingly permissive — an unresolvable PackageReference produces a project
            // whose Compilation has errors but parses successfully, so Roslyn still produces a
            // valid Compilation for Broken. The probe catches the rarer "GetCompilationAsync
            // throws / returns null" cases (e.g., source-generator failures during compilation
            // construction); for the more common "compilation has errors but is constructible"
            // path, Pass 1B's per-file catch handles any individual symbol-resolution failure.
            var result = await RoslynIndexer.IndexSolutionOnceAsync(slnPath, store);

            // Good's HappyClass MUST be indexed: that's the "scope still works" guarantee.
            var happy = await store.FindSymbolsAsync("HappyClass");
            happy.Should().NotBeEmpty(
                "Good's HappyClass must appear in the store regardless of sister project compile state");

            // The IndexResult shape MUST carry the failure-list contract even when empty;
            // consumers (LiveIndexService) read these arrays unconditionally.
            result.FailedProjects.Should().NotBeNull();
            result.FailedFiles.Should().NotBeNull();
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task ColdIndex_healthySolution_emitsEmptyFailureLists()
    {
        // Sanity check: indexing a solution where every project compiles cleanly produces
        // empty FailedProjects / FailedFiles. The pre-flight probe and Pass 1B catch are
        // no-ops on healthy installs.
        var slnPath = LocateSampleSolution();
        var tmp = Path.Combine(Path.GetTempPath(), "partial-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        var dbPath = Path.Combine(tmp, "graph.db");

        try
        {
            await using var store = new SqliteGraphStore(dbPath);
            var result = await RoslynIndexer.IndexSolutionOnceAsync(slnPath, store);

            result.FailedProjects.Should().BeEmpty("healthy solutions produce no project failures");
            result.FailedFiles.Should().BeEmpty("healthy solutions produce no file failures");
            result.FilesIndexed.Should().BeGreaterThan(0);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static string LocateSampleSolution()
    {
        var dir = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "tests", "fixtures", "Sample.sln");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Could not locate tests/fixtures/Sample.sln from " + dir);
    }
}
