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
/// Reconciliation: edit a copy of the fixture to remove the obsolete-usage warning,
/// re-index, and assert the CS0618 diagnostic disappears from the table.
/// </summary>
public sealed class DiagnosticsReconcileTests
{
    [Fact]
    public async Task EditFile_removeObsoleteUse_diagnosticDisappears()
    {
        var sourceRoot = LocateSourceFixtureRoot();
        var workRoot = Path.Combine(Path.GetTempPath(), "sourcegraph-reconcile-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(workRoot);
            CopyDirectory(sourceRoot, workRoot);
            var slnPath = Path.Combine(workRoot, "Sample.sln");
            var dbDir = Path.Combine(workRoot, "graph");
            Directory.CreateDirectory(dbDir);
            var dbPath = Path.Combine(dbDir, "graph.db");

            await using var store = new SqliteGraphStore(dbPath);

            // Cold index #1: obsolete-use site present, expect CS0618.
            await using (var indexer = new RoslynIndexer(store))
            {
                await indexer.OpenAsync(slnPath);
                await indexer.IndexAllAsync();

                var before = await store.FindDiagnosticsAsync(severity: null, code: "CS0618", symbolId: null);
                before.Should().NotBeEmpty("the seed Program.cs uses Calculator.LegacyAdd, an [Obsolete] member");
            }

            // Edit the file to remove the LegacyAdd use sites.
            var programPath = Path.Combine(workRoot, "Sample.App", "Program.cs");
            var newContent = """
                using Sample.Domain;

                var calc = new Calculator();
                var sum = calc.Add(2, 3);
                var product = calc.Multiply(4, 5);

                IGreeter greeter = new Greeter("Hello");
                Console.WriteLine(greeter.Greet("World"));
                Console.WriteLine($"sum={sum} product={product}");
                """;
            await File.WriteAllTextAsync(programPath, newContent);

            // Index again; the indexer's SHA gate detects the change and re-runs everything
            // for Program.cs. Diagnostics for that file get wiped and re-inserted.
            await using (var indexer = new RoslynIndexer(store))
            {
                await indexer.OpenAsync(slnPath);
                await indexer.IndexAllAsync();

                var after = await store.FindDiagnosticsAsync(severity: null, code: "CS0618", symbolId: null);
                after.Should().BeEmpty("editing Program.cs removed every LegacyAdd call site, so CS0618 should not fire");
            }
        }
        finally
        {
            try { Directory.Delete(workRoot, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static string LocateSourceFixtureRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "tests", "fixtures", "Sample.sln");
            if (File.Exists(candidate)) return Path.GetDirectoryName(candidate)!;
        }
        throw new FileNotFoundException("Could not locate tests/fixtures/Sample.sln from " + dir);
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, dir);
            // Skip build outputs.
            if (rel.Contains("bin") || rel.Contains("obj") || rel.StartsWith(".sourcegraph") || rel.StartsWith("graph"))
                continue;
            Directory.CreateDirectory(Path.Combine(dest, rel));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            if (rel.Contains("bin") || rel.Contains("obj") || rel.StartsWith(".sourcegraph") || rel.StartsWith("graph"))
                continue;
            var target = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
