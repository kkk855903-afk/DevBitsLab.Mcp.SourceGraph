using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Indexing.Xaml.Parser;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using Microsoft.CodeAnalysis;

namespace DevBitsLab.Mcp.SourceGraph.Indexing.Xaml;

/// <summary>
/// Resolves XAML binding and event names against the Roslyn compilation already opened and
/// privacy-sanitized by the host. Resolution is deliberately fail-closed: unsupported binding
/// syntax, ambiguous members, missing types, and missing compilations produce no semantic target.
/// </summary>
internal sealed class XamlSemanticResolver
{
    private readonly Compilation _compilation;
    private readonly Dictionary<XamlElement, XamlBindingContext?> _bindingContexts = new();
    private readonly Dictionary<XamlElement, IReadOnlyList<XamlViewModelAssociation>>
        _viewModelAssociations = new();

    private XamlSemanticResolver(Compilation compilation, XamlDocument document)
    {
        _compilation = compilation;
        BuildBindingContexts(document.Root, inherited: null);
    }

    public static async Task<XamlSemanticResolver?> CreateAsync(
        XamlLanguageProject? project,
        XamlDocument document,
        CancellationToken ct)
    {
        if (project is null) return null;
        var compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false);
        return compilation is null ? null : new XamlSemanticResolver(compilation, document);
    }

    public XamlBindingTarget? ResolveBinding(
        XamlElement element,
        string path,
        bool requireCommand)
    {
        if (!_bindingContexts.TryGetValue(element, out var context) || context is null)
        {
            return null;
        }

        var property = ResolvePropertyPath(context.Type, path);
        if (property is null) return null;
        if (requireCommand && !IsCommandType(property.Type)) return null;

        var propertyKey = CanonicalKey(property);
        var dataTypeKey = CanonicalKey(context.Type);
        if (propertyKey is null || dataTypeKey is null) return null;

        return new XamlBindingTarget(
            property,
            propertyKey,
            dataTypeKey,
            context.Confidence,
            context.Source);
    }

    public IReadOnlyList<XamlViewModelAssociation> GetViewModelAssociations(
        XamlElement element) =>
        _viewModelAssociations.TryGetValue(element, out var associations)
            ? associations
            : Array.Empty<XamlViewModelAssociation>();

    public IMethodSymbol? ResolveEventHandler(string? xClass, string handlerName)
    {
        if (string.IsNullOrWhiteSpace(xClass) || string.IsNullOrWhiteSpace(handlerName))
        {
            return null;
        }

        var type = _compilation.GetTypeByMetadataName(xClass.Trim());
        if (type is null) return null;

        var candidates = new List<IMethodSymbol>();
        for (var current = type; current is not null; current = current.BaseType)
        {
            foreach (var method in current.GetMembers(handlerName).OfType<IMethodSymbol>())
            {
                if (!IsCompatibleEventHandler(method)) continue;
                if (!_compilation.IsSymbolAccessibleWithin(method, type)) continue;
                if (candidates.Any(existing =>
                    SymbolEqualityComparer.Default.Equals(
                        existing.OriginalDefinition,
                        method.OriginalDefinition)))
                {
                    continue;
                }
                candidates.Add(method);
            }
        }

        return candidates.Count == 1 ? candidates[0] : null;
    }

    private bool IsCompatibleEventHandler(IMethodSymbol method)
    {
        if (method.MethodKind != MethodKind.Ordinary
            || method.IsStatic
            || method.Arity != 0
            || !method.ReturnsVoid
            || method.Parameters.Length != 2
            || method.Parameters[0].RefKind != RefKind.None
            || method.Parameters[1].RefKind != RefKind.None
            || method.Parameters[0].Type.SpecialType != SpecialType.System_Object)
        {
            return false;
        }

        for (var current = method.Parameters[1].Type as INamedTypeSymbol;
             current is not null;
             current = current.BaseType)
        {
            if (string.Equals(current.MetadataName, "EventArgs", StringComparison.Ordinal)
                && string.Equals(
                    current.ContainingNamespace?.ToDisplayString(),
                    "System",
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static string? CanonicalKey(ISymbol symbol)
    {
        var documentationId = symbol.OriginalDefinition.GetDocumentationCommentId();
        return string.IsNullOrEmpty(documentationId) ? null : "csharp:" + documentationId;
    }

    private void BuildBindingContexts(
        XamlElement element,
        XamlBindingContext? inherited)
    {
        var effective = inherited;
        var associations = new List<XamlViewModelAssociation>(capacity: 2);

        var dataContextAttribute = element.Attributes.FirstOrDefault(a =>
            string.IsNullOrEmpty(a.Prefix)
            && string.Equals(a.LocalName, "DataContext", StringComparison.Ordinal));
        if (dataContextAttribute is not null)
        {
            effective = ResolveDataContextAttribute(dataContextAttribute, inherited);
            AddAssociation(
                associations,
                effective,
                "data-context-attribute",
                dataContextAttribute.Line,
                dataContextAttribute.Column,
                QualifiedAttributeNameLength(dataContextAttribute));
        }

        var dataContextElements = element.Children
            .Where(IsDataContextPropertyElement)
            .ToArray();
        if (dataContextElements.Length > 0)
        {
            effective = dataContextElements.Length == 1
                ? ResolveDataContextPropertyElement(dataContextElements[0])
                : null;
            if (dataContextElements.Length == 1)
            {
                var propertyElement = dataContextElements[0];
                AddAssociation(
                    associations,
                    effective,
                    "data-context-element",
                    propertyElement.Line,
                    propertyElement.Column,
                    propertyElement.LocalName.Length);
            }
        }

        var dataType = element.FindAttribute(XamlReader.XamlNamespace, "DataType");
        if (dataType is not null)
        {
            var declaredType = ResolveTypeToken(element, dataType.Value);
            effective = declaredType is null
                ? null
                : new XamlBindingContext(
                    declaredType,
                    EvidenceConfidence.Exact,
                    "x-data-type");
            AddAssociation(
                associations,
                effective,
                "x-data-type",
                dataType.Line,
                dataType.Column,
                QualifiedAttributeNameLength(dataType));
        }

        _bindingContexts[element] = effective;
        _viewModelAssociations[element] = associations;
        foreach (var child in element.Children)
        {
            BuildBindingContexts(child, effective);
        }
    }

    private static void AddAssociation(
        ICollection<XamlViewModelAssociation> associations,
        XamlBindingContext? context,
        string source,
        int line,
        int column,
        int length)
    {
        if (context is null) return;
        var targetCanonicalKey = CanonicalKey(context.Type);
        if (targetCanonicalKey is null) return;
        associations.Add(new XamlViewModelAssociation(
            targetCanonicalKey,
            context.Confidence,
            source,
            line,
            column,
            Math.Max(1, length)));
    }

    private static int QualifiedAttributeNameLength(XamlAttribute attribute) =>
        attribute.LocalName.Length
        + (string.IsNullOrEmpty(attribute.Prefix)
            ? 0
            : attribute.Prefix.Length + 1);

    private XamlBindingContext? ResolveDataContextAttribute(
        XamlAttribute attribute,
        XamlBindingContext? inherited)
    {
        if (!MarkupExtensionParser.TryParse(attribute.Value, out var extension)
            || extension.IsLiteral
            || !string.Equals(extension.Name, "Binding", StringComparison.Ordinal)
            || extension.NamedArgs.ContainsKey("ElementName")
            || extension.NamedArgs.ContainsKey("RelativeSource")
            || extension.NamedArgs.ContainsKey("Source"))
        {
            return null;
        }

        var path = ResolveBindingPath(extension);
        if (string.IsNullOrWhiteSpace(path))
        {
            return inherited;
        }
        if (inherited is null) return null;

        var property = ResolvePropertyPath(inherited.Type, path);
        var type = property is null ? null : AsNamedType(property.Type);
        return type is null
            ? null
            : new XamlBindingContext(
                type,
                EvidenceConfidence.Semantic,
                "data-context-binding");
    }

    private XamlBindingContext? ResolveDataContextPropertyElement(XamlElement propertyElement)
    {
        if (propertyElement.Children.Count != 1) return null;
        var type = ResolveElementType(propertyElement.Children[0]);
        return type is null
            ? null
            : new XamlBindingContext(
                type,
                EvidenceConfidence.Exact,
                "data-context");
    }

    private INamedTypeSymbol? ResolveTypeToken(XamlElement element, string rawValue)
    {
        var token = rawValue.Trim();
        if (MarkupExtensionParser.TryParse(token, out var extension) && !extension.IsLiteral)
        {
            if (extension.Name is not ("x:Type" or "Type")) return null;
            token = extension.PositionalArgs.Count > 0
                && extension.PositionalArgs[0].IsLiteral
                    ? extension.PositionalArgs[0].Literal ?? string.Empty
                    : extension.NamedArgs.TryGetValue("TypeName", out var named)
                        && named.IsLiteral
                            ? named.Literal ?? string.Empty
                            : string.Empty;
        }

        token = token.Trim();
        if (token.Length == 0) return null;

        var colon = token.IndexOf(':');
        if (colon < 0)
        {
            return token.IndexOf('.') >= 0
                ? _compilation.GetTypeByMetadataName(token)
                : null;
        }

        var prefix = token.Substring(0, colon);
        var localName = token.Substring(colon + 1);
        if (localName.Length == 0) return null;
        var namespaceUri = ResolveNamespaceUri(element, prefix);
        return namespaceUri is null
            ? null
            : ResolveClrType(namespaceUri, localName);
    }

    private INamedTypeSymbol? ResolveElementType(XamlElement element) =>
        ResolveClrType(element.Namespace, element.LocalName);

    private INamedTypeSymbol? ResolveClrType(string namespaceUri, string localName)
    {
        string namespaceName;
        string? assemblyName = null;
        if (namespaceUri.StartsWith("clr-namespace:", StringComparison.Ordinal))
        {
            var body = namespaceUri.Substring("clr-namespace:".Length);
            var separator = body.IndexOf(';');
            namespaceName = separator < 0 ? body : body.Substring(0, separator);
            if (separator >= 0)
            {
                foreach (var option in body.Substring(separator + 1).Split(';'))
                {
                    const string assemblyPrefix = "assembly=";
                    if (option.StartsWith(assemblyPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        assemblyName = option.Substring(assemblyPrefix.Length).Trim();
                        break;
                    }
                }
            }
        }
        else if (namespaceUri.StartsWith("using:", StringComparison.Ordinal))
        {
            namespaceName = namespaceUri.Substring("using:".Length);
        }
        else
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(namespaceName)
            || string.IsNullOrWhiteSpace(localName))
        {
            return null;
        }

        var metadataName = namespaceName.Trim() + "." + localName.Trim();
        if (string.IsNullOrEmpty(assemblyName))
        {
            return _compilation.GetTypeByMetadataName(metadataName);
        }

        if (string.Equals(
            _compilation.AssemblyName,
            assemblyName,
            StringComparison.OrdinalIgnoreCase))
        {
            return _compilation.Assembly.GetTypeByMetadataName(metadataName);
        }

        foreach (var referencedAssembly in _compilation.SourceModule.ReferencedAssemblySymbols)
        {
            if (string.Equals(
                referencedAssembly.Name,
                assemblyName,
                StringComparison.OrdinalIgnoreCase))
            {
                return referencedAssembly.GetTypeByMetadataName(metadataName);
            }
        }

        return null;
    }

    private static string? ResolveNamespaceUri(XamlElement element, string prefix)
    {
        for (var current = element; current is not null; current = current.Parent)
        {
            foreach (var attribute in current.Attributes)
            {
                if (prefix.Length == 0
                    && attribute.Prefix.Length == 0
                    && string.Equals(attribute.LocalName, "xmlns", StringComparison.Ordinal))
                {
                    return attribute.Value;
                }
                if (string.Equals(attribute.Prefix, "xmlns", StringComparison.Ordinal)
                    && string.Equals(attribute.LocalName, prefix, StringComparison.Ordinal))
                {
                    return attribute.Value;
                }
            }
        }

        return null;
    }

    private IPropertySymbol? ResolvePropertyPath(
        INamedTypeSymbol rootType,
        string path)
    {
        var segments = path.Split('.');
        if (segments.Length == 0) return null;

        ITypeSymbol currentType = rootType;
        IPropertySymbol? property = null;
        foreach (var rawSegment in segments)
        {
            var segment = rawSegment.Trim();
            if (!IsIdentifier(segment)) return null;

            property = FindProperty(currentType, segment);
            if (property is null) return null;
            currentType = property.Type;
        }

        return property;
    }

    private static IPropertySymbol? FindProperty(ITypeSymbol type, string name)
    {
        var current = AsNamedType(type);
        while (current is not null)
        {
            var matches = current.GetMembers(name)
                .OfType<IPropertySymbol>()
                .Where(p =>
                    !p.IsStatic
                    && p.Parameters.Length == 0
                    && p.DeclaredAccessibility == Accessibility.Public)
                .ToArray();
            if (matches.Length > 1) return null;
            if (matches.Length == 1) return matches[0];
            current = current.BaseType;
        }

        return null;
    }

    private bool IsCommandType(ITypeSymbol type)
    {
        var command = _compilation.GetTypeByMetadataName("System.Windows.Input.ICommand");
        var named = AsNamedType(type);
        if (command is null || named is null) return false;
        if (SymbolEqualityComparer.Default.Equals(named, command)) return true;
        return named.AllInterfaces.Any(i =>
            SymbolEqualityComparer.Default.Equals(i, command));
    }

    private static INamedTypeSymbol? AsNamedType(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named) return null;
        if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
            && named.TypeArguments.Length == 1)
        {
            return named.TypeArguments[0] as INamedTypeSymbol;
        }
        return named;
    }

    private static string? ResolveBindingPath(MarkupExtensionValue extension)
    {
        if (extension.NamedArgs.TryGetValue("Path", out var named))
        {
            return named.IsLiteral ? named.Literal : null;
        }
        return extension.PositionalArgs.Count > 0
            && extension.PositionalArgs[0].IsLiteral
                ? extension.PositionalArgs[0].Literal
                : null;
    }

    private static bool IsDataContextPropertyElement(XamlElement element) =>
        element.LocalName.EndsWith(".DataContext", StringComparison.Ordinal);

    private static bool IsIdentifier(string value)
    {
        if (value.Length == 0) return false;
        if (value[0] != '_' && !char.IsLetter(value[0])) return false;
        for (var i = 1; i < value.Length; i++)
        {
            if (value[i] != '_' && !char.IsLetterOrDigit(value[i])) return false;
        }
        return true;
    }
}

internal sealed record XamlBindingContext(
    INamedTypeSymbol Type,
    EvidenceConfidence Confidence,
    string Source);

internal sealed record XamlViewModelAssociation(
    string TargetCanonicalKey,
    EvidenceConfidence Confidence,
    string Source,
    int Line,
    int Column,
    int Length);

internal sealed record XamlBindingTarget(
    IPropertySymbol Property,
    string PropertyCanonicalKey,
    string DataTypeCanonicalKey,
    EvidenceConfidence Confidence,
    string ContextSource);
