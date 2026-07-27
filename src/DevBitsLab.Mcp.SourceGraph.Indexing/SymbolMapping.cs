using System.Text;
using System.Xml;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using Microsoft.CodeAnalysis;

namespace DevBitsLab.Mcp.SourceGraph.Indexing;

internal static class SymbolMapping
{
    private static readonly SymbolDisplayFormat FqnFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions:
            SymbolDisplayMemberOptions.IncludeContainingType |
            SymbolDisplayMemberOptions.IncludeParameters |
            SymbolDisplayMemberOptions.IncludeType |
            SymbolDisplayMemberOptions.IncludeRef,
        parameterOptions:
            SymbolDisplayParameterOptions.IncludeType |
            SymbolDisplayParameterOptions.IncludeName |
            SymbolDisplayParameterOptions.IncludeParamsRefOut,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private static readonly SymbolDisplayFormat SignatureFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeTypeConstraints,
        memberOptions:
            SymbolDisplayMemberOptions.IncludeParameters |
            SymbolDisplayMemberOptions.IncludeType |
            SymbolDisplayMemberOptions.IncludeModifiers |
            SymbolDisplayMemberOptions.IncludeAccessibility,
        parameterOptions:
            SymbolDisplayParameterOptions.IncludeType |
            SymbolDisplayParameterOptions.IncludeName |
            SymbolDisplayParameterOptions.IncludeDefaultValue |
            SymbolDisplayParameterOptions.IncludeParamsRefOut,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    public static string Fqn(ISymbol symbol) => symbol.ToDisplayString(FqnFormat);
    public static string Signature(ISymbol symbol) => symbol.ToDisplayString(SignatureFormat);

    /// <summary>
    /// Scheme prefix attached to every canonical key emitted by the C# Roslyn indexer.
    /// The SDK's <see cref="DevBitsLab.Mcp.SourceGraph.Sdk.Validation.CanonicalKeyValidator"/>
    /// rejects emissions outside the reserved set, so every read and write must include the prefix.
    /// </summary>
    public const string CanonicalKeyScheme = "csharp:";

    /// <summary>
    /// URI-prefixed canonical key for <paramref name="symbol"/>. Returns <c>null</c> when Roslyn
    /// has neither a doc-id nor a display string for the symbol (e.g. degenerate error symbols).
    /// </summary>
    public static string? CanonicalKey(ISymbol symbol)
    {
        var raw = symbol.OriginalDefinition.GetDocumentationCommentId() ?? symbol.OriginalDefinition.ToDisplayString(FqnFormat);
        return raw is null ? null : CanonicalKeyScheme + raw;
    }

    /// <summary>
    /// Kebab-case symbol-kind identifier for <paramref name="symbol"/>; values come from
    /// <see cref="SymbolKinds"/>. The unmatched case folds onto <see cref="SymbolKinds.Other"/>.
    /// </summary>
    public static string ToCoreKind(ISymbol symbol) => symbol switch
    {
        INamespaceSymbol => SymbolKinds.Namespace,
        ITypeSymbol t => t.TypeKind switch
        {
            TypeKind.Class => SymbolKinds.Class,
            TypeKind.Struct => SymbolKinds.Struct,
            TypeKind.Interface => SymbolKinds.Interface,
            TypeKind.Enum => SymbolKinds.Enum,
            TypeKind.Delegate => SymbolKinds.Delegate,
            TypeKind.TypeParameter => SymbolKinds.TypeParameter,
            _ => SymbolKinds.Class,
        },
        IMethodSymbol m => m.MethodKind == MethodKind.Constructor ? SymbolKinds.Constructor : SymbolKinds.Method,
        IPropertySymbol => SymbolKinds.Property,
        IFieldSymbol f when f.ContainingType?.TypeKind == TypeKind.Enum => SymbolKinds.EnumMember,
        IFieldSymbol => SymbolKinds.Field,
        IEventSymbol => SymbolKinds.Event,
        IParameterSymbol => SymbolKinds.Parameter,
        ILocalSymbol => SymbolKinds.Local,
        _ => SymbolKinds.Other,
    };

    public static bool IsIndexable(ISymbol symbol) => symbol switch
    {
        INamespaceSymbol ns => !ns.IsGlobalNamespace,
        ITypeSymbol t => t.TypeKind is TypeKind.Class or TypeKind.Struct or TypeKind.Interface or TypeKind.Enum or TypeKind.Delegate,
        IMethodSymbol m => m.MethodKind is MethodKind.Ordinary
            or MethodKind.Constructor
            or MethodKind.UserDefinedOperator
            or MethodKind.Conversion
            or MethodKind.ExplicitInterfaceImplementation,
        IPropertySymbol => true,
        IFieldSymbol => true,
        IEventSymbol => true,
        _ => false,
    };

    /// <summary>
    /// Comma-joined token string of every Roslyn modifier in canonical order:
    /// <c>static, async, virtual, abstract, sealed, override, extern, readonly, partial</c>.
    /// Returns <c>null</c> when no modifiers apply (so SQLite stores NULL, not the empty string).
    /// Accessibility is encoded separately and intentionally excluded here.
    /// </summary>
    public static string? Modifiers(ISymbol symbol)
    {
        var sb = new StringBuilder();
        void Add(string token)
        {
            if (sb.Length > 0) sb.Append(',');
            sb.Append(token);
        }

        // Suppress modifiers Roslyn reports as "true" for symbols whose source form doesn't
        // actually carry that keyword:
        //   - Namespaces always report IsStatic=true.
        //   - Interface declarations report IsAbstract=true even without the keyword.
        //   - Methods/properties/events declared inside an interface report IsAbstract=true
        //     (the abstract-ness is implicit in C# 8+ default-method-less interface members).
        // These would otherwise pollute every interface row with "abstract" noise.
        var isNamespace = symbol is INamespaceSymbol;
        var isInterface = symbol is INamedTypeSymbol nti && nti.TypeKind == TypeKind.Interface;
        var inInterface = symbol.ContainingType?.TypeKind == TypeKind.Interface;
        var suppressStatic = isNamespace || isInterface;
        var suppressAbstract = isNamespace || isInterface || inInterface;

        if (symbol.IsStatic && !suppressStatic) Add("static");
        if (symbol is IMethodSymbol m && m.IsAsync) Add("async");
        if (symbol.IsVirtual) Add("virtual");
        if (symbol.IsAbstract && !suppressAbstract) Add("abstract");
        if (symbol.IsSealed && !isNamespace) Add("sealed");
        if (symbol.IsOverride) Add("override");
        if (symbol.IsExtern) Add("extern");

        // IsReadOnly is meaningful on fields and on methods/structs (readonly struct, readonly method).
        var isReadOnly = symbol switch
        {
            IFieldSymbol f => f.IsReadOnly,
            IMethodSymbol mr => mr.IsReadOnly,
            INamedTypeSymbol nt => nt.IsReadOnly,
            _ => false,
        };
        if (isReadOnly) Add("readonly");

        // Partial declarations: types, methods (PartialDefinitionPart on methods).
        var isPartial = symbol switch
        {
            IMethodSymbol mp => mp.PartialDefinitionPart is not null || mp.PartialImplementationPart is not null,
            INamedTypeSymbol nt => nt.DeclaringSyntaxReferences.Length > 1, // best-effort; multi-declaration ⇒ partial
            _ => false,
        };
        if (isPartial) Add("partial");

        return sb.Length == 0 ? null : sb.ToString();
    }

    /// <summary>Integer value of <see cref="ISymbol.DeclaredAccessibility"/>; safe to round-trip via cast.</summary>
    public static int Accessibility(ISymbol symbol) => (int)symbol.DeclaredAccessibility;

    /// <summary>
    /// Returns the first <paramref name="maxLines"/> lines of the declaring syntax for symbols
    /// that have a body (methods/properties/types). Returns <c>null</c> for symbols without
    /// a syntactic body or when none of <paramref name="symbol"/>'s declarations have a tree.
    /// Used to populate the synthesised text the embedding pipeline hashes and embeds.
    /// </summary>
    public static string? BodyExcerpt(ISymbol symbol, int maxLines = 40)
    {
        foreach (var sref in symbol.DeclaringSyntaxReferences)
        {
            var node = sref.GetSyntax();
            if (node is null) continue;
            var raw = node.ToString();
            if (string.IsNullOrEmpty(raw)) continue;
            // First N lines, joined with \n, no trailing whitespace.
            var lines = raw.Split('\n');
            var take = Math.Min(maxLines, lines.Length);
            var sb = new StringBuilder();
            for (var i = 0; i < take; i++)
            {
                sb.Append(lines[i].TrimEnd('\r'));
                if (i + 1 < take) sb.Append('\n');
            }
            return sb.ToString();
        }
        return null;
    }

    /// <summary>
    /// Parses the symbol's <c>&lt;summary&gt;</c> XML doc text, preserving inline tags
    /// (<c>&lt;see&gt;</c>, <c>&lt;paramref&gt;</c>, …) as plain text. Resolves
    /// <c>&lt;inheritdoc/&gt;</c> by walking up the override chain (or the first declared
    /// interface implementation) until a non-inherited summary is found, capped at a small depth
    /// to defend against pathological circular doc graphs. Returns <c>null</c> when no summary is
    /// available (so SQLite stores NULL).
    /// </summary>
    public static string? XmlSummary(ISymbol symbol)
    {
        try
        {
            return XmlSummaryCore(symbol, depth: 0);
        }
        catch
        {
            // Roslyn occasionally throws on degenerate XML or circular inheritdoc; treat as
            // "no summary" rather than failing the whole pass.
            return null;
        }
    }

    private const int InheritDocMaxDepth = 4;

    private static string? XmlSummaryCore(ISymbol symbol, int depth)
    {
        if (depth > InheritDocMaxDepth) return null;
        var raw = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(raw)) return null;

        // Wrap so a doc with no single root parses cleanly (Roslyn typically returns <member ...>
        // already, but defensive).
        var (summaryText, hasInheritDoc) = ParseSummary(raw);
        if (!string.IsNullOrEmpty(summaryText)) return summaryText;

        if (hasInheritDoc)
        {
            foreach (var parent in InheritDocParents(symbol))
            {
                var inherited = XmlSummaryCore(parent, depth + 1);
                if (!string.IsNullOrEmpty(inherited)) return inherited;
            }
        }
        return null;
    }

    private static IEnumerable<ISymbol> InheritDocParents(ISymbol symbol)
    {
        // Methods/properties/events: walk overrides first, then explicit/implicit interface impls.
        switch (symbol)
        {
            case IMethodSymbol m:
                if (m.OverriddenMethod is { } om) yield return om;
                foreach (var iface in m.ContainingType?.AllInterfaces ?? default)
                {
                    foreach (var iMember in iface.GetMembers().OfType<IMethodSymbol>())
                    {
                        var impl = m.ContainingType?.FindImplementationForInterfaceMember(iMember);
                        if (SymbolEqualityComparer.Default.Equals(impl, m)) yield return iMember;
                    }
                }
                break;
            case IPropertySymbol p:
                if (p.OverriddenProperty is { } op) yield return op;
                foreach (var iface in p.ContainingType?.AllInterfaces ?? default)
                {
                    foreach (var iMember in iface.GetMembers().OfType<IPropertySymbol>())
                    {
                        var impl = p.ContainingType?.FindImplementationForInterfaceMember(iMember);
                        if (SymbolEqualityComparer.Default.Equals(impl, p)) yield return iMember;
                    }
                }
                break;
            case IEventSymbol e:
                if (e.OverriddenEvent is { } oe) yield return oe;
                break;
            case INamedTypeSymbol nt:
                if (nt.BaseType is { } bt && bt.SpecialType != SpecialType.System_Object) yield return bt;
                foreach (var iface in nt.Interfaces) yield return iface;
                break;
        }
    }

    private static (string? Summary, bool HasInheritDoc) ParseSummary(string xml)
    {
        try
        {
            // Roslyn's GetDocumentationCommentXml returns a fragment like:
            //   <member name="...">\n  <summary>...</summary>\n</member>
            // Wrap in a synthetic root in case the first child isn't <member>.
            var doc = new XmlDocument();
            doc.LoadXml($"<doc>{xml}</doc>");

            var inheritDoc = doc.SelectSingleNode("//inheritdoc");
            var summary = doc.SelectSingleNode("//summary");
            if (summary is null) return (null, inheritDoc is not null);

            var text = NormalizeWhitespace(InnerTextWithCrefs(summary));
            return (string.IsNullOrEmpty(text) ? null : text, inheritDoc is not null);
        }
        catch (XmlException)
        {
            return (null, false);
        }
    }

    private static string InnerTextWithCrefs(XmlNode node)
    {
        var sb = new StringBuilder();
        foreach (XmlNode child in node.ChildNodes)
        {
            switch (child.NodeType)
            {
                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                case XmlNodeType.Whitespace:
                case XmlNodeType.SignificantWhitespace:
                    sb.Append(child.Value);
                    break;
                case XmlNodeType.Element:
                    // <see cref="..."/>, <paramref name="..."/>, <typeparamref name="..."/>:
                    // surface the cref/name as plain text, e.g. "see X.Y" -> "X.Y".
                    var cref = child.Attributes?["cref"]?.Value;
                    var name = child.Attributes?["name"]?.Value;
                    var tag = cref ?? name;
                    if (!string.IsNullOrEmpty(tag))
                    {
                        // Strip the Roslyn doc-id prefix ("T:", "M:", ...) for readability.
                        if (tag.Length > 2 && tag[1] == ':') tag = tag[2..];
                        sb.Append(tag);
                    }
                    else
                    {
                        sb.Append(InnerTextWithCrefs(child));
                    }
                    break;
            }
        }
        return sb.ToString();
    }

    private static string NormalizeWhitespace(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var sb = new StringBuilder(s.Length);
        var inWs = false;
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!inWs && sb.Length > 0) sb.Append(' ');
                inWs = true;
            }
            else
            {
                sb.Append(c);
                inWs = false;
            }
        }
        return sb.ToString().Trim();
    }

}
