using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using DevBitsLab.Mcp.SourceGraph.Indexing.Xaml.Parser;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Storage;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CoreEvidenceConfidence =
    DevBitsLab.Mcp.SourceGraph.Core.EvidenceConfidence;
using CoreSourceLocation =
    DevBitsLab.Mcp.SourceGraph.Core.SourceLocation;

namespace DevBitsLab.Mcp.SourceGraph.Indexing.Xaml;

/// <summary>
/// Builds exact code-behind member → named XAML element edges from WPF-generated field symbols.
/// The projection deliberately requires Roslyn symbol equality: matching an identifier's text to
/// <c>x:Name</c> is never sufficient.
/// </summary>
internal static class XamlCodeBehindProjection
{
    internal const string Producer = "xaml-code-behind-v1";
    internal const string EdgeKind = "code-behind-uses-element";

    internal static async Task<XamlCodeBehindProjectionResult> BuildAsync(
        XamlLanguageProject project,
        string repoRoot,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);

        var roslynProjects = project.GetRoslynProjects();
        if (roslynProjects is null || roslynProjects.Count == 0)
        {
            return XamlCodeBehindProjectionResult.Unavailable;
        }

        var declarations = await ReadElementDeclarationsAsync(
                project.FilePaths,
                repoRoot,
                ct)
            .ConfigureAwait(false);
        if (declarations is null)
        {
            return XamlCodeBehindProjectionResult.Unavailable;
        }

        var facts = new List<ProducerEdgeEvidenceFact>();
        var occurrenceKeys = new HashSet<string>(StringComparer.Ordinal);
        var producingFilePaths = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var roslynProject in roslynProjects)
        {
            ct.ThrowIfCancellationRequested();
            var check = await XamlLanguageProject.CheckProjectSemanticStateAsync(
                    roslynProject,
                    ct)
                .ConfigureAwait(false);
            if (check.Compilation is not { } compilation)
            {
                return XamlCodeBehindProjectionResult.Unavailable;
            }

            var targetsByField = ResolveUniqueGeneratedFields(
                compilation,
                declarations);
            if (targetsByField.Count == 0)
            {
                return declarations.Count == 0
                    ? new XamlCodeBehindProjectionResult(
                        IsComplete: true,
                        [],
                        [])
                    : XamlCodeBehindProjectionResult.Unavailable;
            }

            foreach (var tree in compilation.SyntaxTrees)
            {
                ct.ThrowIfCancellationRequested();
                if (!IsUserSourceTree(repoRoot, tree.FilePath))
                {
                    continue;
                }
                producingFilePaths.Add(Path.GetFullPath(tree.FilePath));

                var root = await tree.GetRootAsync(ct).ConfigureAwait(false);
                var model = compilation.GetSemanticModel(tree);
                foreach (var identifier in root.DescendantNodes()
                             .OfType<IdentifierNameSyntax>())
                {
                    ct.ThrowIfCancellationRequested();
                    var referenced = model.GetSymbolInfo(identifier, ct).Symbol;
                    if (referenced is null
                        || !targetsByField.TryGetValue(
                            referenced.OriginalDefinition,
                            out var target))
                    {
                        continue;
                    }

                    var source = FindIndexableEnclosingSymbol(
                        model,
                        identifier.SpanStart,
                        ct);
                    var sourceKey = source is null
                        ? null
                        : XamlSemanticResolver.CanonicalKey(source);
                    if (sourceKey is null)
                    {
                        continue;
                    }

                    var lineSpan = identifier.GetLocation().GetLineSpan();
                    var start = lineSpan.StartLinePosition;
                    var end = lineSpan.EndLinePosition;
                    var fullPath = Path.GetFullPath(tree.FilePath);
                    var occurrenceKey = string.Join(
                        "\n",
                        sourceKey,
                        target.CanonicalKey,
                        fullPath,
                        start.Line,
                        start.Character,
                        end.Line,
                        end.Character);
                    if (!occurrenceKeys.Add(occurrenceKey))
                    {
                        continue;
                    }

                    var metadata = new Dictionary<string, string>(
                        StringComparer.Ordinal)
                    {
                        ["element-name"] = target.Name,
                        ["xaml-file"] = target.RelativePath,
                        ["generated-field"] = referenced.Name,
                    };
                    facts.Add(new ProducerEdgeEvidenceFact(
                        sourceKey,
                        target.CanonicalKey,
                        EdgeKind,
                        metadata,
                        new FileEvidenceFact(
                            new CoreSourceLocation(
                                fullPath,
                                start.Line + 1,
                                start.Character + 1,
                                end.Line + 1,
                                end.Character + 1),
                            CoreEvidenceConfidence.Exact,
                            Producer,
                            metadata)));
                }
            }
        }

        return new XamlCodeBehindProjectionResult(
            IsComplete: true,
            facts,
            producingFilePaths
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    private static async Task<IReadOnlyList<XamlElementDeclaration>?>
        ReadElementDeclarationsAsync(
            IReadOnlyCollection<string> xamlPaths,
            string repoRoot,
            CancellationToken ct)
    {
        var declarations = new List<XamlElementDeclaration>();
        foreach (var xamlPath in xamlPaths)
        {
            ct.ThrowIfCancellationRequested();
            XamlDocument document;
            try
            {
                document = XamlReader.Parse(
                    await File.ReadAllBytesAsync(xamlPath, ct)
                        .ConfigureAwait(false));
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
            catch (XmlException)
            {
                return null;
            }

            var xClass = document.Root
                .FindAttribute(XamlReader.XamlNamespace, "Class")
                ?.Value
                ?.Trim();
            if (string.IsNullOrWhiteSpace(xClass))
            {
                continue;
            }

            var relativePath = ToRepoRelative(repoRoot, xamlPath);
            XamlReader.Walk(document.Root, (element, _) =>
            {
                var name = ResolveName(element);
                if (name is null)
                {
                    return;
                }
                declarations.Add(new XamlElementDeclaration(
                    xClass,
                    name,
                    relativePath,
                    $"xaml:element:{relativePath}#{name}"));
            });
        }
        return declarations;
    }

    private static Dictionary<ISymbol, XamlElementDeclaration>
        ResolveUniqueGeneratedFields(
            Compilation compilation,
            IReadOnlyList<XamlElementDeclaration> declarations)
    {
        var candidates = new Dictionary<ISymbol, List<XamlElementDeclaration>>(
            SymbolEqualityComparer.Default);
        foreach (var declaration in declarations)
        {
            var type = compilation.GetTypeByMetadataName(declaration.XClass);
            if (type is null)
            {
                continue;
            }

            var fields = type.GetMembers(declaration.Name)
                .OfType<IFieldSymbol>()
                .Where(IsGeneratedXamlField)
                .ToArray();
            if (fields.Length != 1)
            {
                continue;
            }

            var field = fields[0].OriginalDefinition;
            if (!candidates.TryGetValue(field, out var targets))
            {
                targets = [];
                candidates[field] = targets;
            }
            targets.Add(declaration);
        }

        return candidates
            .Where(pair => pair.Value
                .Select(target => target.CanonicalKey)
                .Distinct(StringComparer.Ordinal)
                .Count() == 1)
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value[0],
            SymbolEqualityComparer.Default);
    }

    private static bool IsGeneratedXamlField(IFieldSymbol field) =>
        field.Locations.Any(location =>
        {
            var fileName = Path.GetFileName(
                location.SourceTree?.FilePath ?? string.Empty);
            return fileName.EndsWith(
                    ".g.cs",
                    StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(
                    ".g.i.cs",
                    StringComparison.OrdinalIgnoreCase);
        });

    private static ISymbol? FindIndexableEnclosingSymbol(
        SemanticModel model,
        int position,
        CancellationToken ct)
    {
        for (var symbol = model.GetEnclosingSymbol(position, ct);
             symbol is not null;
             symbol = symbol.ContainingSymbol)
        {
            if (symbol is IMethodSymbol
                {
                    MethodKind: MethodKind.Ordinary
                        or MethodKind.Constructor
                        or MethodKind.StaticConstructor
                        or MethodKind.UserDefinedOperator
                        or MethodKind.Conversion
                        or MethodKind.ExplicitInterfaceImplementation,
                }
                or IPropertySymbol
                or IFieldSymbol
                or IEventSymbol)
            {
                return symbol;
            }
        }
        return null;
    }

    private static bool IsUserSourceTree(string repoRoot, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || !Path.IsPathFullyQualified(path))
        {
            return false;
        }
        var fileName = Path.GetFileName(path);
        if (fileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(
                ".generated.cs",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var relative = Path.GetRelativePath(
            Path.GetFullPath(repoRoot),
            Path.GetFullPath(path));
        return !Path.IsPathRooted(relative)
            && !relative.Equals("..", StringComparison.Ordinal)
            && !relative.StartsWith(
                ".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal);
    }

    private static string? ResolveName(XamlElement element)
    {
        var xName = element.FindAttribute(XamlReader.XamlNamespace, "Name");
        if (xName is not null && !string.IsNullOrWhiteSpace(xName.Value))
        {
            return xName.Value.Trim();
        }
        var name = element.FindAttributeByLocalName("Name");
        return name is not null
            && string.IsNullOrEmpty(name.Prefix)
            && !string.IsNullOrWhiteSpace(name.Value)
                ? name.Value.Trim()
                : null;
    }

    private static string ToRepoRelative(string repoRoot, string path) =>
        Path.GetRelativePath(
                Path.GetFullPath(repoRoot),
                Path.GetFullPath(path))
            .Replace('\\', '/');

    private sealed record XamlElementDeclaration(
        string XClass,
        string Name,
        string RelativePath,
        string CanonicalKey);
}

internal sealed record XamlCodeBehindProjectionResult(
    bool IsComplete,
    IReadOnlyList<ProducerEdgeEvidenceFact> Edges,
    IReadOnlyList<string> ProducingFilePaths)
{
    internal static XamlCodeBehindProjectionResult Unavailable { get; } =
        new(false, [], []);
}
