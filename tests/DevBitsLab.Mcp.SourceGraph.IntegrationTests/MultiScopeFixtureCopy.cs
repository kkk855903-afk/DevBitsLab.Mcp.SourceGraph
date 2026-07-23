using System;
using System.IO;
using System.Linq;

namespace DevBitsLab.Mcp.SourceGraph.IntegrationTests;

/// <summary>
/// Copy <c>tests/fixtures/MultiScope/</c> into a per-test temp directory so the test can mutate
/// <c>.sourcegraph.json</c> in isolation without polluting the source-controlled fixture. Skips
/// any <c>bin</c>/<c>obj</c>/<c>.sourcegraph</c> subtrees so tests start with a clean slate.
/// Disposing returns the temp dir back to the OS.
/// </summary>
internal sealed class MultiScopeFixtureCopy : IDisposable
{
    public string Root { get; }

    private MultiScopeFixtureCopy(string root) { Root = root; }

    public static MultiScopeFixtureCopy Create()
    {
        var sourceRoot = ServerHarness.LocateFixture("MultiScope");
        var tempRoot = Path.Join(Path.GetTempPath(), "live-scope-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        CopyTree(sourceRoot, tempRoot);
        return new MultiScopeFixtureCopy(tempRoot);
    }

    public string ConfigPath => Path.Join(Root, ".sourcegraph.json");

    public string ScopeDbPath(string scopeId) =>
        Path.Join(Root, ".sourcegraph", "scopes", scopeId + ".db");

    public void WriteConfig(string json) => File.WriteAllText(ConfigPath, json);

    public void DeleteConfig() => File.Delete(ConfigPath);

    private static void CopyTree(string source, string destination)
    {
        var dirRels = Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories)
            .Select(d => Path.GetRelativePath(source, d))
            .Where(rel => !ShouldSkip(rel));
        foreach (var rel in dirRels)
        {
            Directory.CreateDirectory(Path.Join(destination, rel));
        }
        var fileRels = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(source, f))
            .Where(rel => !ShouldSkip(rel));
        foreach (var rel in fileRels)
        {
            var target = Path.Join(destination, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Join(source, rel), target, overwrite: true);
        }
    }

    private static bool ShouldSkip(string relativePath)
    {
        var s = relativePath.Replace('\\', '/');
        // Match three positions for each excluded segment so a top-level `bin` (or other exact-
        // name entry that EnumerateDirectories may yield before its children) is also skipped:
        //   - exact equality (the relative path is exactly the segment name, e.g. "bin"),
        //   - `<seg>/...` (top-level entry with children, e.g. "bin/Debug/..."),
        //   - `.../<seg>/...` (nested entry, e.g. "src/foo/bin/Debug/...").
        return s.Equals("bin", StringComparison.Ordinal)
            || s.Equals("obj", StringComparison.Ordinal)
            || s.Equals(".sourcegraph", StringComparison.Ordinal)
            || s.StartsWith("bin/", StringComparison.Ordinal)
            || s.StartsWith("obj/", StringComparison.Ordinal)
            || s.StartsWith(".sourcegraph/", StringComparison.Ordinal)
            || s.Contains("/bin/", StringComparison.Ordinal)
            || s.Contains("/obj/", StringComparison.Ordinal)
            || s.Contains("/.sourcegraph/", StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort cleanup */ }
    }
}
