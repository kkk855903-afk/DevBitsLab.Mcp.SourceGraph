using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using DevBitsLab.Mcp.SourceGraph.Indexing.Xaml.Parser;
using DevBitsLab.Mcp.SourceGraph.Sdk;

namespace DevBitsLab.Mcp.SourceGraph.Indexing.Xaml;

/// <summary>
/// <see cref="ILanguageProjectFactory"/> for the in-tree XAML indexer. Walks every
/// <c>.csproj</c> under the repo root, locates the <c>.xaml</c> files belonging to each project
/// (via <c>&lt;Page&gt;</c> / <c>&lt;ApplicationDefinition&gt;</c> / <c>&lt;EmbeddedResource&gt;</c>
/// / <c>&lt;Resource&gt;</c> items, falling back to a directory scan when the items aren't
/// present), and builds a per-project resource cache from <c>App.xaml</c>'s
/// <c>Application.Resources</c> plus a theme <c>Generic.xaml</c> if present under <c>Themes/</c>.
///
/// <para><b>v1 simplification — no MergedDictionaries Source-URI dereferencing.</b> The cache
/// includes any <c>x:Key</c>-bearing element nested inside <c>App.xaml</c>'s own
/// <c>MergedDictionaries</c> declarations, but the <c>Source=</c> URIs that point at separate
/// <c>.xaml</c> files are NOT followed: their resources are not pulled into the cache.
/// References to those keys fall through to the unresolved sentinel
/// (<c>xaml:resource:&lt;file&gt;#__unresolved:&lt;key&gt;</c>) rather than confidently resolving
/// to the wrong target. The cross-project cascade is the design.md open question; v1 ships the
/// honest answer and revisits if usage data shows it hurts.</para>
/// </summary>
public sealed class XamlLanguageProjectFactory : ILanguageProjectFactory
{
    /// <inheritdoc />
    public IReadOnlyCollection<string> ProjectMarkers { get; } = new[] { "*.csproj", "*.xaml" };

    /// <inheritdoc />
    public Task<IReadOnlyList<ILanguageProject>> DiscoverAsync(string repoRoot, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(repoRoot)) throw new ArgumentException("repoRoot must be non-empty", nameof(repoRoot));
        if (!Directory.Exists(repoRoot)) return Task.FromResult<IReadOnlyList<ILanguageProject>>(Array.Empty<ILanguageProject>());

        var projects = new List<ILanguageProject>();
        // Use an explicit directory walker so a single unreadable folder doesn't abort discovery,
        // and so we skip the same noise dirs the dispatcher does. `Directory.EnumerateFiles(...,
        // SearchOption.AllDirectories)` is lazy and can throw mid-iteration when it hits an
        // inaccessible directory; the explicit stack walk below contains the failure to that one
        // subtree.
        foreach (var csproj in EnumerateCsprojFiles(repoRoot))
        {
            ct.ThrowIfCancellationRequested();
            var project = TryBuildProject(csproj);
            if (project is not null) projects.Add(project);
        }

        return Task.FromResult<IReadOnlyList<ILanguageProject>>(projects);
    }

    /// <summary>
    /// Walk <paramref name="root"/> for <c>.csproj</c> files. Skips <c>bin/</c>, <c>obj/</c>,
    /// <c>node_modules/</c>, <c>.git/</c>, and <c>.sourcegraph/</c> wholesale (matching the
    /// dispatcher's own walker) so build outputs and vendored sources don't pollute project
    /// discovery. Each directory's enumeration is wrapped so an inaccessible subtree
    /// degrades to "skipped" rather than aborting the whole pass.
    /// </summary>
    private static IEnumerable<string> EnumerateCsprojFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(dir); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (string.Equals(name, "bin", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(name, "obj", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(name, ".sourcegraph", StringComparison.OrdinalIgnoreCase)) continue;
                stack.Push(child);
            }

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*.csproj"); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (var f in files) yield return f;
        }
    }

    private static XamlLanguageProject? TryBuildProject(string csprojPath)
    {
        try
        {
            var projectDir = Path.GetDirectoryName(Path.GetFullPath(csprojPath))!;
            var xamlFiles = EnumerateXamlFiles(csprojPath, projectDir);
            if (xamlFiles.Count == 0) return null;

            var resourceCache = BuildResourceCache(xamlFiles);
            return new XamlLanguageProject(Path.GetFullPath(csprojPath), xamlFiles, resourceCache);
        }
        catch (IOException) { return null; }
        catch (XmlException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Find the <c>.xaml</c> files that belong to the project. Reads the csproj XML for explicit
    /// <c>&lt;Page&gt;</c> / <c>&lt;ApplicationDefinition&gt;</c> items first; if either yields
    /// nothing, falls back to a recursive directory scan from the project's directory. Falls back
    /// quietly when the csproj uses globbed includes that the indexer can't statically expand —
    /// the directory scan covers those cases.
    /// </summary>
    private static IReadOnlyList<string> EnumerateXamlFiles(string csprojPath, string projectDir)
    {
        var fromItems = ReadXamlItemsFromCsproj(csprojPath, projectDir);
        if (fromItems.Count > 0) return fromItems;

        // Fallback: walk the project directory for *.xaml. Skip bin/ and obj/ so we don't pick up
        // the markup-compiler's intermediate copies.
        var results = new List<string>();
        foreach (var path in Directory.EnumerateFiles(projectDir, "*.xaml", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(projectDir, path).Replace('\\', '/');
            if (relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            results.Add(Path.GetFullPath(path));
        }
        return results;
    }

    private static IReadOnlyList<string> ReadXamlItemsFromCsproj(string csprojPath, string projectDir)
    {
        var results = new List<string>();
        try
        {
            using var stream = File.OpenRead(csprojPath);
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                IgnoreComments = true,
                IgnoreWhitespace = true,
                IgnoreProcessingInstructions = true,
                CloseInput = false,
            };
            using var reader = XmlReader.Create(stream, settings);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (!IsXamlItemElement(reader.LocalName)) continue;
                var include = reader.GetAttribute("Include") ?? reader.GetAttribute("Update");
                if (string.IsNullOrEmpty(include)) continue;
                if (include.IndexOfAny(new[] { '*', '?' }) >= 0) continue; // globbed; defer to fs walk
                if (!include.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) continue;
                var relative = include.Replace('\\', '/');
                var absolute = Path.IsPathRooted(relative) ? relative : Path.GetFullPath(Path.Combine(projectDir, relative));
                results.Add(absolute);
            }
        }
        catch (XmlException) { return Array.Empty<string>(); }
        catch (IOException) { return Array.Empty<string>(); }
        return results;
    }

    private static bool IsXamlItemElement(string localName) =>
        // <Page> + <ApplicationDefinition> are the WPF/WinUI markup-compiler item types; many
        // projects also include resource dictionaries via <Resource> or <EmbeddedResource> when
        // the file isn't compiled at build time but still belongs to the project. Match all
        // four so explicit item includes are honoured before the directory-scan fallback runs.
        string.Equals(localName, "Page", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(localName, "ApplicationDefinition", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(localName, "EmbeddedResource", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(localName, "Resource", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Build the per-project resource cache from the actual cascade roots — the project's
    /// <c>App.xaml</c> (its <c>Application.Resources</c>, including any <c>x:Key</c>s declared
    /// inside the merged-dictionary collection itself) and a <c>Themes/Generic.xaml</c> if
    /// present. View-local resources (<c>&lt;Window.Resources&gt;</c>, etc.) are NOT added — they
    /// are not globally visible at runtime, so adding them would emit false-positive
    /// <c>uses-resource</c> / <c>applies-style</c> edges from elements that can't actually see
    /// them. v1 does NOT follow <c>MergedDictionaries</c> <c>Source=</c> URIs (the open
    /// cross-project cascade question lives in design.md); references to keys declared in a
    /// dereferenced dictionary fall through to "unresolved" rather than confidently wrong.
    /// </summary>
    private static Dictionary<string, ResourceDefinition> BuildResourceCache(IReadOnlyList<string> xamlFiles)
    {
        var cache = new Dictionary<string, ResourceDefinition>(StringComparer.Ordinal);

        // Restrict to App.xaml + Themes/Generic.xaml — the two cascade roots WPF / WinUI /
        // Avalonia / Uno actually treat as project-globally-visible. App.xaml first so its keys
        // win on collision with a same-named theme key.
        var roots = new List<string>(2);
        foreach (var path in xamlFiles)
        {
            var fileName = Path.GetFileName(path);
            if (string.Equals(fileName, "App.xaml", StringComparison.OrdinalIgnoreCase))
            {
                roots.Insert(0, path);
            }
            else if (string.Equals(fileName, "Generic.xaml", StringComparison.OrdinalIgnoreCase))
            {
                // Only count it if it lives under a Themes/ folder — the WPF themed-controls
                // convention. A loose Generic.xaml elsewhere isn't auto-cascaded.
                var parentDir = Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty);
                if (string.Equals(parentDir, "Themes", StringComparison.OrdinalIgnoreCase))
                {
                    roots.Add(path);
                }
            }
        }

        foreach (var path in roots)
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            XamlDocument doc;
            try { doc = XamlReader.Parse(bytes); }
            catch (XmlException) { continue; }

            XamlReader.Walk(doc.Root, (element, _) =>
            {
                var keyAttr = element.FindAttribute(XamlReader.XamlNamespace, "Key");
                if (keyAttr is null) return;
                if (cache.ContainsKey(keyAttr.Value)) return; // App.xaml wins, see ordering above.
                cache[keyAttr.Value] = new ResourceDefinition(
                    key: keyAttr.Value,
                    filePath: path,
                    line: element.Line,
                    column: element.Column,
                    elementName: element.LocalName);
            });
        }
        return cache;
    }
}
