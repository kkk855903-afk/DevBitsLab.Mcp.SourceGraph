using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Indexing.Xaml.Parser;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace DevBitsLab.Mcp.SourceGraph.Indexing.Xaml;

/// <summary>
/// Resolves XAML binding and event names against the Roslyn compilation already opened and
/// privacy-sanitized by the host. Resolution is deliberately fail-closed: unsupported binding
/// syntax, ambiguous members, missing types, and missing compilations produce no semantic target.
/// </summary>
internal sealed class XamlSemanticResolver
{
    private readonly Compilation _compilation;
    private readonly bool _compilationIsComplete;
    private readonly bool _semanticResolutionIsSafe;
    private readonly bool _directBindingMembersOnly;
    private readonly Dictionary<XamlElement, XamlBindingContextResolution> _bindingContexts = new();
    private readonly Dictionary<XamlElement, IReadOnlyList<XamlViewModelAssociation>>
        _viewModelAssociations = new();

    private XamlSemanticResolver(
        Compilation compilation,
        bool compilationIsComplete,
        bool semanticResolutionIsSafe,
        bool directBindingMembersOnly,
        XamlDocument document)
    {
        _compilation = compilation;
        _compilationIsComplete = compilationIsComplete;
        _semanticResolutionIsSafe = semanticResolutionIsSafe;
        _directBindingMembersOnly = directBindingMembersOnly;
        BuildBindingContexts(
            document.Root,
            ResolveCodeBehindDataContext(document));
    }

    public static async Task<XamlSemanticResolver?> CreateAsync(
        XamlLanguageProject? project,
        XamlDocument document,
        CancellationToken ct)
    {
        if (project is null) return null;
        var compilationState = await project
            .GetCompilationStateAsync(ct)
            .ConfigureAwait(false);
        return compilationState is null
            ? null
            : new XamlSemanticResolver(
                compilationState.Compilation,
                compilationState.IsComplete,
                compilationState.CanResolve,
                compilationState.DirectBindingMembersOnly,
                document);
    }

    public XamlBindingResolution ResolveBinding(
        XamlElement element,
        string path,
        bool requireCommand)
    {
        if (!IsSimplePropertyPath(path))
        {
            return XamlBindingResolution.Unresolved(
                XamlResolutionStatus.Unsupported,
                "binding-path-syntax-not-supported");
        }

        if (!_semanticResolutionIsSafe)
        {
            return XamlBindingResolution.Unresolved(
                XamlResolutionStatus.Incomplete,
                "semantic-input-incomplete");
        }

        if (!_bindingContexts.TryGetValue(element, out var contextResolution)
            || contextResolution.Context is null
            || contextResolution.Outcome.Status != XamlResolutionStatus.Resolved)
        {
            return new XamlBindingResolution(
                contextResolution?.Outcome
                ?? new XamlResolutionOutcome(
                    XamlResolutionStatus.Unknown,
                    "no-known-data-context"),
                Target: null,
                contextResolution?.Candidates ?? Array.Empty<string>());
        }

        var context = contextResolution.Context;
        var propertyResolution = ResolvePropertyPath(
            context.Type,
            path,
            _directBindingMembersOnly);
        if (propertyResolution.Property is null)
        {
            if (propertyResolution.Outcome.Status == XamlResolutionStatus.Missing
                && !_compilationIsComplete)
            {
                return XamlBindingResolution.Unresolved(
                    XamlResolutionStatus.Incomplete,
                    "compilation-has-errors");
            }
            return new XamlBindingResolution(
                propertyResolution.Outcome,
                Target: null,
                propertyResolution.Candidates);
        }

        var property = propertyResolution.Property;
        if (requireCommand && !IsCommandType(property.Type))
        {
            return XamlBindingResolution.Unresolved(
                XamlResolutionStatus.Unsupported,
                "resolved-member-is-not-icommand");
        }

        var propertyKey = CanonicalKey(property);
        var dataTypeKey = CanonicalKey(context.Type);
        if (propertyKey is null || dataTypeKey is null)
        {
            return XamlBindingResolution.Unresolved(
                XamlResolutionStatus.Unknown,
                "canonical-symbol-identity-unavailable");
        }

        return new XamlBindingResolution(
            new XamlResolutionOutcome(
                XamlResolutionStatus.Resolved,
                "unique-semantic-property"),
            new XamlBindingTarget(
                property,
                propertyKey,
                dataTypeKey,
                EvidenceConfidence.Semantic,
                context.Source),
            Array.Empty<string>());
    }

    public IReadOnlyList<XamlViewModelAssociation> GetViewModelAssociations(
        XamlElement element) =>
        _semanticResolutionIsSafe
        && _viewModelAssociations.TryGetValue(element, out var associations)
            ? associations
            : Array.Empty<XamlViewModelAssociation>();

    public IMethodSymbol? ResolveEventHandler(string? xClass, string handlerName)
    {
        if (!_semanticResolutionIsSafe || _directBindingMembersOnly)
        {
            return null;
        }
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
        XamlBindingContextResolution inherited)
    {
        var effective = inherited;
        var associations = new List<XamlViewModelAssociation>(capacity: 2);
        if (element.Parent is null
            && inherited.Context is not null
            && inherited.Outcome.Status == XamlResolutionStatus.Resolved)
        {
            AddAssociation(
                associations,
                inherited,
                inherited.Context.Source,
                element.Line,
                element.Column,
                element.LocalName.Length);
        }

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
                : XamlBindingContextResolution.Ambiguous(
                    "multiple-data-context-property-elements");
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
            effective = declaredType.Type is null
                ? new XamlBindingContextResolution(
                    declaredType.Outcome,
                    Context: null,
                    declaredType.Candidates)
                : XamlBindingContextResolution.Resolved(
                    declaredType.Type,
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

    private XamlBindingContextResolution ResolveCodeBehindDataContext(
        XamlDocument document)
    {
        var xClass = document.Root.FindAttribute(
            XamlReader.XamlNamespace,
            "Class")?.Value.Trim();
        if (string.IsNullOrEmpty(xClass))
        {
            return XamlBindingContextResolution.Unknown(
                "no-known-data-context");
        }

        var codeBehindType = _compilation.GetTypeByMetadataName(xClass);
        if (codeBehindType is null)
        {
            return XamlBindingContextResolution.Unknown(
                "x-class-type-not-found");
        }

        var candidates = new List<INamedTypeSymbol>();
        foreach (var constructor in codeBehindType.InstanceConstructors)
        {
            foreach (var syntaxReference in constructor.DeclaringSyntaxReferences)
            {
                if (syntaxReference.GetSyntax() is not
                    ConstructorDeclarationSyntax syntax)
                {
                    continue;
                }

                var model = _compilation.GetSemanticModel(syntax.SyntaxTree);
                foreach (var assignment in syntax
                             .DescendantNodes()
                             .OfType<AssignmentExpressionSyntax>())
                {
                    if (model.GetOperation(assignment) is not
                        ISimpleAssignmentOperation operation
                        || operation.Target is not
                            IPropertyReferenceOperation propertyReference
                        || !string.Equals(
                            propertyReference.Property.Name,
                            "DataContext",
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var type = AssignedNamedType(operation.Value);
                    if (type is null
                        || candidates.Any(candidate =>
                            SymbolEqualityComparer.Default.Equals(
                                candidate,
                                type)))
                    {
                        continue;
                    }
                    candidates.Add(type);
                }
            }
        }

        return candidates.Count switch
        {
            0 => XamlBindingContextResolution.Unknown(
                "no-known-data-context"),
            1 => XamlBindingContextResolution.Resolved(
                candidates[0],
                "code-behind-data-context"),
            _ => new XamlBindingContextResolution(
                new XamlResolutionOutcome(
                    XamlResolutionStatus.Ambiguous,
                    "multiple-code-behind-data-context-types"),
                Context: null,
                candidates
                    .Select(candidate => candidate.ToDisplayString())
                    .OrderBy(candidate => candidate, StringComparer.Ordinal)
                    .ToArray()),
        };
    }

    private static INamedTypeSymbol? AssignedNamedType(IOperation value)
    {
        while (value is IConversionOperation
               {
                   IsImplicit: true,
                   Operand: { } operand,
               })
        {
            value = operand;
        }

        var type = value switch
        {
            IFieldReferenceOperation field => field.Field.Type,
            ILocalReferenceOperation local => local.Local.Type,
            IParameterReferenceOperation parameter => parameter.Parameter.Type,
            IPropertyReferenceOperation property => property.Property.Type,
            _ => value.Type,
        };
        return type is null ? null : AsNamedType(type);
    }

    private static void AddAssociation(
        ICollection<XamlViewModelAssociation> associations,
        XamlBindingContextResolution contextResolution,
        string source,
        int line,
        int column,
        int length)
    {
        var context = contextResolution.Context;
        if (context is null
            || contextResolution.Outcome.Status != XamlResolutionStatus.Resolved)
        {
            return;
        }
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

    private XamlBindingContextResolution ResolveDataContextAttribute(
        XamlAttribute attribute,
        XamlBindingContextResolution inherited)
    {
        if (!MarkupExtensionParser.TryParse(attribute.Value, out var extension)
            || extension.IsLiteral
            || !string.Equals(extension.Name, "Binding", StringComparison.Ordinal)
            || extension.NamedArgs.ContainsKey("ElementName")
            || extension.NamedArgs.ContainsKey("RelativeSource")
            || extension.NamedArgs.ContainsKey("Source"))
        {
            return XamlBindingContextResolution.Unknown(
                "data-context-expression-not-statically-supported");
        }

        var path = ResolveBindingPath(extension);
        if (string.IsNullOrWhiteSpace(path))
        {
            return inherited;
        }
        if (inherited.Context is null
            || inherited.Outcome.Status != XamlResolutionStatus.Resolved)
        {
            return inherited;
        }

        var property = ResolvePropertyPath(
            inherited.Context.Type,
            path,
            _directBindingMembersOnly);
        if (property.Property is null)
        {
            var outcome = property.Outcome.Status == XamlResolutionStatus.Missing
                ? new XamlResolutionOutcome(
                    _compilationIsComplete
                        ? XamlResolutionStatus.Unknown
                        : XamlResolutionStatus.Incomplete,
                    _compilationIsComplete
                        ? "data-context-binding-target-unknown"
                        : "compilation-has-errors")
                : property.Outcome;
            return new XamlBindingContextResolution(
                outcome,
                Context: null,
                property.Candidates);
        }

        var type = AsNamedType(property.Property.Type);
        return type is null
            ? new XamlBindingContextResolution(
                new XamlResolutionOutcome(
                    XamlResolutionStatus.Unsupported,
                    "data-context-binding-type-not-supported"),
                Context: null,
                Array.Empty<string>())
            : XamlBindingContextResolution.Resolved(
                type,
                "data-context-binding");
    }

    private XamlBindingContextResolution ResolveDataContextPropertyElement(
        XamlElement propertyElement)
    {
        if (propertyElement.Children.Count == 0)
        {
            return XamlBindingContextResolution.Unknown(
                "data-context-property-element-is-empty");
        }
        if (propertyElement.Children.Count != 1)
        {
            return XamlBindingContextResolution.Ambiguous(
                "multiple-data-context-values");
        }
        var type = ResolveElementType(propertyElement.Children[0]);
        return type.Type is null
            ? new XamlBindingContextResolution(
                type.Outcome,
                Context: null,
                type.Candidates)
            : XamlBindingContextResolution.Resolved(
                type.Type,
                "data-context");
    }

    private XamlTypeResolution ResolveTypeToken(XamlElement element, string rawValue)
    {
        var token = rawValue.Trim();
        if (MarkupExtensionParser.TryParse(token, out var extension) && !extension.IsLiteral)
        {
            if (extension.Name is not ("x:Type" or "Type"))
            {
                return XamlTypeResolution.Unresolved(
                    XamlResolutionStatus.Unsupported,
                    "data-type-expression-not-supported");
            }
            token = extension.PositionalArgs.Count > 0
                && extension.PositionalArgs[0].IsLiteral
                    ? extension.PositionalArgs[0].Literal ?? string.Empty
                    : extension.NamedArgs.TryGetValue("TypeName", out var named)
                        && named.IsLiteral
                            ? named.Literal ?? string.Empty
                            : string.Empty;
        }

        token = token.Trim();
        if (token.Length == 0)
        {
            return XamlTypeResolution.Unresolved(
                XamlResolutionStatus.Unknown,
                "data-type-token-is-empty");
        }

        var colon = token.IndexOf(':');
        if (colon < 0)
        {
            return token.IndexOf('.') >= 0
                ? ResolveMetadataType(token, assemblyName: null)
                : XamlTypeResolution.Unresolved(
                    XamlResolutionStatus.Unknown,
                    "data-type-namespace-prefix-unavailable");
        }

        var prefix = token.Substring(0, colon);
        var localName = token.Substring(colon + 1);
        if (localName.Length == 0)
        {
            return XamlTypeResolution.Unresolved(
                XamlResolutionStatus.Unknown,
                "data-type-local-name-is-empty");
        }
        var namespaceUri = ResolveNamespaceUri(element, prefix);
        return namespaceUri is null
            ? XamlTypeResolution.Unresolved(
                XamlResolutionStatus.Unknown,
                "data-type-namespace-prefix-unresolved")
            : ResolveClrType(namespaceUri, localName);
    }

    private XamlTypeResolution ResolveElementType(XamlElement element) =>
        ResolveClrType(element.Namespace, element.LocalName);

    private XamlTypeResolution ResolveClrType(string namespaceUri, string localName)
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
            return XamlTypeResolution.Unresolved(
                XamlResolutionStatus.Unsupported,
                "xaml-namespace-is-not-clr-addressable");
        }

        if (string.IsNullOrWhiteSpace(namespaceName)
            || string.IsNullOrWhiteSpace(localName))
        {
            return XamlTypeResolution.Unresolved(
                XamlResolutionStatus.Unknown,
                "clr-type-name-is-incomplete");
        }

        var metadataName = namespaceName.Trim() + "." + localName.Trim();
        return ResolveMetadataType(metadataName, assemblyName);
    }

    private XamlTypeResolution ResolveMetadataType(
        string metadataName,
        string? assemblyName)
    {
        var candidates = new List<INamedTypeSymbol>();
        var localType = _compilation.Assembly.GetTypeByMetadataName(metadataName);
        if (string.IsNullOrEmpty(assemblyName) && localType is not null)
        {
            return XamlTypeResolution.Resolved(localType);
        }

        AddCandidate(_compilation.Assembly);
        foreach (var referencedAssembly in _compilation.SourceModule.ReferencedAssemblySymbols)
        {
            AddCandidate(referencedAssembly);
        }

        if (candidates.Count == 1)
        {
            return XamlTypeResolution.Resolved(candidates[0]);
        }

        var candidateKeys = candidates
            .Select(candidate =>
                CanonicalKey(candidate)
                ?? candidate.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
            .OrderBy(candidate => candidate, StringComparer.Ordinal)
            .ToArray();
        if (candidates.Count > 1)
        {
            return new XamlTypeResolution(
                new XamlResolutionOutcome(
                    XamlResolutionStatus.Ambiguous,
                    "multiple-clr-types-match-data-context"),
                Type: null,
                candidateKeys);
        }

        return XamlTypeResolution.Unresolved(
            _compilationIsComplete
                ? XamlResolutionStatus.Unknown
                : XamlResolutionStatus.Incomplete,
            _compilationIsComplete
                ? "data-context-type-not-found"
                : "compilation-has-errors");

        void AddCandidate(IAssemblySymbol assembly)
        {
            if (!string.IsNullOrEmpty(assemblyName)
                && !string.Equals(
                    assembly.Name,
                    assemblyName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var candidate = assembly.GetTypeByMetadataName(metadataName);
            if (candidate is null
                || candidates.Any(existing =>
                    SymbolEqualityComparer.Default.Equals(existing, candidate)))
            {
                return;
            }
            candidates.Add(candidate);
        }
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

    private XamlPropertyResolution ResolvePropertyPath(
        INamedTypeSymbol rootType,
        string path,
        bool directMembersOnly)
    {
        var segments = path.Split('.');

        ITypeSymbol currentType = rootType;
        XamlPropertyResolution? resolution = null;
        foreach (var rawSegment in segments)
        {
            var segment = rawSegment.Trim();
            resolution = FindProperty(
                currentType,
                segment,
                directMembersOnly);
            if (resolution.Property is null) return resolution;
            currentType = resolution.Property.Type;
        }

        return resolution
            ?? XamlPropertyResolution.Unresolved(
                XamlResolutionStatus.Unsupported,
                "binding-path-is-empty");
    }

    private static XamlPropertyResolution FindProperty(
        ITypeSymbol type,
        string name,
        bool directMembersOnly)
    {
        var current = AsNamedType(type);
        if (current is null)
        {
            return XamlPropertyResolution.Unresolved(
                XamlResolutionStatus.Unsupported,
                "binding-path-enters-non-object-type");
        }
        while (current is not null)
        {
            var matches = BindableProperties(current, name);
            if (matches.Length > 1)
            {
                return Ambiguous(matches);
            }
            if (matches.Length == 1)
            {
                return XamlPropertyResolution.Resolved(matches[0]);
            }
            if (directMembersOnly)
            {
                break;
            }

            if (current.TypeKind == TypeKind.Interface)
            {
                var inheritedMatches = new List<IPropertySymbol>();
                foreach (var inheritedInterface in current.AllInterfaces)
                {
                    foreach (var property in BindableProperties(
                                 inheritedInterface,
                                 name))
                    {
                        if (inheritedMatches.Any(existing =>
                                SymbolEqualityComparer.Default.Equals(
                                    existing.OriginalDefinition,
                                    property.OriginalDefinition)))
                        {
                            continue;
                        }
                        inheritedMatches.Add(property);
                    }
                }

                if (inheritedMatches.Count > 1)
                {
                    return Ambiguous(inheritedMatches);
                }
                if (inheritedMatches.Count == 1)
                {
                    return XamlPropertyResolution.Resolved(inheritedMatches[0]);
                }
                break;
            }

            current = current.BaseType;
        }

        return XamlPropertyResolution.Unresolved(
            XamlResolutionStatus.Missing,
            "property-not-found");

        static IPropertySymbol[] BindableProperties(
            INamedTypeSymbol declaringType,
            string propertyName) =>
            declaringType.GetMembers(propertyName)
                .OfType<IPropertySymbol>()
                .Where(property =>
                    !property.IsStatic
                    && property.Parameters.Length == 0
                    && property.DeclaredAccessibility == Accessibility.Public)
                .ToArray();

        static XamlPropertyResolution Ambiguous(
            IEnumerable<IPropertySymbol> properties) =>
            new(
                new XamlResolutionOutcome(
                    XamlResolutionStatus.Ambiguous,
                    "multiple-properties-match-binding-segment"),
                Property: null,
                properties
                    .Select(property =>
                        CanonicalKey(property)
                        ?? property.ToDisplayString(
                            SymbolDisplayFormat.FullyQualifiedFormat))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(candidate => candidate, StringComparer.Ordinal)
                    .ToArray());
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

    private static bool IsSimplePropertyPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var segments = path.Split('.');
        return segments.Length > 0
            && segments.All(segment => IsIdentifier(segment.Trim()));
    }

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

internal sealed record XamlBindingContextResolution(
    XamlResolutionOutcome Outcome,
    XamlBindingContext? Context,
    IReadOnlyList<string> Candidates)
{
    public static XamlBindingContextResolution Resolved(
        INamedTypeSymbol type,
        string source) =>
        new(
            new XamlResolutionOutcome(
                XamlResolutionStatus.Resolved,
                "unique-data-context-type"),
            new XamlBindingContext(
                type,
                EvidenceConfidence.Semantic,
                source),
            Array.Empty<string>());

    public static XamlBindingContextResolution Unknown(string reason) =>
        new(
            new XamlResolutionOutcome(XamlResolutionStatus.Unknown, reason),
            Context: null,
            Array.Empty<string>());

    public static XamlBindingContextResolution Ambiguous(string reason) =>
        new(
            new XamlResolutionOutcome(XamlResolutionStatus.Ambiguous, reason),
            Context: null,
            Array.Empty<string>());
}

internal sealed record XamlTypeResolution(
    XamlResolutionOutcome Outcome,
    INamedTypeSymbol? Type,
    IReadOnlyList<string> Candidates)
{
    public static XamlTypeResolution Resolved(INamedTypeSymbol type) =>
        new(
            new XamlResolutionOutcome(
                XamlResolutionStatus.Resolved,
                "unique-clr-type"),
            type,
            Array.Empty<string>());

    public static XamlTypeResolution Unresolved(
        XamlResolutionStatus status,
        string reason) =>
        new(
            new XamlResolutionOutcome(status, reason),
            Type: null,
            Array.Empty<string>());
}

internal sealed record XamlPropertyResolution(
    XamlResolutionOutcome Outcome,
    IPropertySymbol? Property,
    IReadOnlyList<string> Candidates)
{
    public static XamlPropertyResolution Resolved(IPropertySymbol property) =>
        new(
            new XamlResolutionOutcome(
                XamlResolutionStatus.Resolved,
                "unique-property"),
            property,
            Array.Empty<string>());

    public static XamlPropertyResolution Unresolved(
        XamlResolutionStatus status,
        string reason) =>
        new(
            new XamlResolutionOutcome(status, reason),
            Property: null,
            Array.Empty<string>());
}

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

internal sealed record XamlBindingResolution(
    XamlResolutionOutcome Outcome,
    XamlBindingTarget? Target,
    IReadOnlyList<string> Candidates)
{
    public static XamlBindingResolution Unresolved(
        XamlResolutionStatus status,
        string reason) =>
        new(
            new XamlResolutionOutcome(status, reason),
            Target: null,
            Array.Empty<string>());
}
