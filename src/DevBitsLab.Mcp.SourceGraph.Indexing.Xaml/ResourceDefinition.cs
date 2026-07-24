using DevBitsLab.Mcp.SourceGraph.Indexing.Xaml.Parser;

namespace DevBitsLab.Mcp.SourceGraph.Indexing.Xaml;

/// <summary>
/// One <c>x:Key</c>-bearing resource discovered during the project's resource-cascade walk. Carries
/// enough position info that the indexer can emit a resolved <c>uses-resource</c> edge pointing
/// back at the declaration site.
/// </summary>
public sealed class ResourceDefinition
{
    public ResourceDefinition(
        string key,
        string filePath,
        int line,
        int column,
        string elementName,
        string? canonicalDiscriminator = null)
    {
        Key = key;
        FilePath = filePath;
        Line = line;
        Column = column;
        ElementName = elementName;
        CanonicalDiscriminator = canonicalDiscriminator;
    }

    /// <summary>The resource <c>x:Key</c> as written.</summary>
    public string Key { get; }

    /// <summary>Absolute path to the file the resource was declared in.</summary>
    public string FilePath { get; }

    /// <summary>1-based line of the declaring element.</summary>
    public int Line { get; }

    /// <summary>1-based column of the declaring element.</summary>
    public int Column { get; }

    /// <summary>Local name of the element that carries the <c>x:Key</c> (e.g. <c>SolidColorBrush</c>, <c>Style</c>).</summary>
    public string ElementName { get; }

    /// <summary>
    /// Stable declaration discriminator used only when another declaration in the same document
    /// would otherwise produce the same canonical key.
    /// </summary>
    public string? CanonicalDiscriminator { get; }

    /// <summary>
    /// Returns the canonical key of the real XAML declaration represented by this definition.
    /// The path portion is repository-relative and uses forward slashes so it matches the
    /// symbol declaration emitted when the declaring document is indexed.
    /// </summary>
    public string ToCanonicalKey(string repoRoot)
    {
        var relativePath = Path.GetRelativePath(
                Path.GetFullPath(repoRoot),
                Path.GetFullPath(FilePath))
            .Replace('\\', '/');
        var schemeRest = XamlResourceCanonicalKey.ClassifyScheme(ElementName);
        var discriminator = CanonicalDiscriminator is null
            ? string.Empty
            : "@" + CanonicalDiscriminator;
        return $"xaml:{schemeRest}:{relativePath}#{Key}{discriminator}";
    }
}

/// <summary>
/// Computes the canonical-key discriminator shared by declaration emission and resolved resource
/// targets. Unique declarations retain the legacy key; only same-document collisions receive a
/// line/column suffix.
/// </summary>
internal static class XamlResourceCanonicalKey
{
    public static IReadOnlyDictionary<XamlElement, string> FindDeclarationDiscriminators(
        XamlElement root)
    {
        var declarationsByIdentity =
            new Dictionary<string, List<(XamlElement Declaration, XamlElement ScopeOwner)>>(
                StringComparer.Ordinal);
        XamlReader.Walk(root, (element, ancestors) =>
        {
            var key = element.FindAttribute(
                XamlReader.XamlNamespace,
                "Key");
            if (key is null
                || (!IsInResourceScope(ancestors)
                    && !IsStyleOrTemplate(element)))
            {
                return;
            }

            var identity = ClassifyScheme(element.LocalName)
                           + "\0"
                           + key.Value;
            if (!declarationsByIdentity.TryGetValue(
                    identity,
                    out var declarations))
            {
                declarations =
                    new List<(XamlElement Declaration, XamlElement ScopeOwner)>();
                declarationsByIdentity[identity] = declarations;
            }
            declarations.Add((
                element,
                FindDeclarationScopeOwner(root, ancestors)));
        });

        var discriminators = new Dictionary<XamlElement, string>();
        foreach (var declarations in declarationsByIdentity.Values)
        {
            if (declarations.Count < 2) continue;

            // When one declaration belongs to an outer scope that contains every other
            // colliding scope, preserve its legacy canonical key and discriminate only the
            // nested private declarations. Same-scope duplicates and sibling local scopes
            // have no such compatible declaration, so every declaration is discriminated.
            var compatibleDeclaration = declarations
                .Where(candidate => declarations.All(other =>
                    ReferenceEquals(
                        candidate.Declaration,
                        other.Declaration)
                    || (!ReferenceEquals(
                            candidate.ScopeOwner,
                            other.ScopeOwner)
                        && IsAncestorOrSelf(
                            candidate.ScopeOwner,
                            other.ScopeOwner))))
                .Select(candidate => candidate.Declaration)
                .SingleOrDefault();
            foreach (var (declaration, _) in declarations)
            {
                if (ReferenceEquals(
                        declaration,
                        compatibleDeclaration))
                {
                    continue;
                }
                discriminators[declaration] = CreateDiscriminator(declaration);
            }
        }
        return discriminators;
    }

    public static string ClassifyScheme(string elementName) =>
        elementName switch
        {
            "Style" => "style",
            "DataTemplate" or "ControlTemplate" or "ItemsPanelTemplate" or "ControlTheme" =>
                "template",
            _ => "resource",
        };

    private static string CreateDiscriminator(XamlElement declaration) =>
        $"L{declaration.Line}C{declaration.Column}";

    private static XamlElement FindDeclarationScopeOwner(
        XamlElement root,
        IReadOnlyList<XamlElement> ancestors)
    {
        for (var i = ancestors.Count - 1; i >= 0; i--)
        {
            var ancestor = ancestors[i];
            if (ancestor.LocalName.EndsWith(
                    ".Resources",
                    StringComparison.Ordinal))
            {
                return ancestor.Parent ?? root;
            }
            if (string.Equals(
                    ancestor.LocalName,
                    "ResourceDictionary",
                    StringComparison.Ordinal)
                && (ancestor.Parent is null
                    || ancestor.FindAttribute(
                        XamlReader.XamlNamespace,
                        "Key") is not null))
            {
                return ancestor;
            }
        }
        return root;
    }

    private static bool IsAncestorOrSelf(
        XamlElement ancestor,
        XamlElement descendant)
    {
        for (var current = descendant;
             current is not null;
             current = current.Parent)
        {
            if (ReferenceEquals(ancestor, current))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsInResourceScope(IReadOnlyList<XamlElement> ancestors)
    {
        foreach (var ancestor in ancestors)
        {
            if (ancestor.LocalName.EndsWith(
                    ".Resources",
                    StringComparison.Ordinal)
                || string.Equals(
                    ancestor.LocalName,
                    "ResourceDictionary",
                    StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsStyleOrTemplate(XamlElement element) =>
        element.LocalName is
            "Style"
            or "DataTemplate"
            or "ControlTemplate"
            or "ItemsPanelTemplate"
            or "ControlTheme";
}
