using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using DevBitsLab.Mcp.SourceGraph.Indexing.Xaml.Dialects;
using DevBitsLab.Mcp.SourceGraph.Indexing.Xaml.Parser;
using DevBitsLab.Mcp.SourceGraph.Sdk;

namespace DevBitsLab.Mcp.SourceGraph.Indexing.Xaml;

/// <summary>
/// Built-in XAML <see cref="ILanguageIndexer"/>. Registered for the <c>.xaml</c> extension; emits
/// the five XAML symbol kinds, the nine cross-language edge kinds (<c>code-behind</c>,
/// <c>binds-to</c>, <c>binds-path</c>, <c>binds-element</c>, <c>handles-event</c>,
/// <c>uses-resource</c>, <c>instantiates-type</c>, <c>merges</c>, <c>applies-style</c>), and the
/// <c>xaml-attached-property</c> annotation flavor documented in
/// <c>openspec/changes/xaml-language-indexer/specs/extensibility/spec.md</c>. The indexer is
/// profile-aware: it detects the framework profile per file via
/// <see cref="XamlProfileDetector"/> and dispatches markup-extension handling through the
/// matching <see cref="IXamlDialect"/>. The per-project resource cascade lives on the
/// <see cref="XamlLanguageProject"/> instance the host attaches via
/// <see cref="IndexContext.Project"/>. Missing and ambiguous resource keys emit a structured
/// <c>xaml-resource-finding</c> annotation on the consuming element and no edge; the indexer never
/// manufactures an unresolved target symbol.
/// </summary>
public sealed class XamlLanguageIndexer : ILanguageIndexer
{
    private const string KindXamlView = "xaml-view";
    private const string KindXamlElement = "xaml-element";
    private const string KindXamlResource = "xaml-resource";
    private const string KindXamlStyle = "xaml-style";
    private const string KindXamlTemplate = "xaml-template";

    private const string EdgeCodeBehind = "code-behind";
    private const string EdgeBindsPath = "binds-path";
    private const string EdgeBindsElement = "binds-element";
    private const string EdgeHandlesEvent = "handles-event";
    private const string EdgeUsesResource = "uses-resource";
    private const string EdgeInstantiatesType = "instantiates-type";
    private const string EdgeMerges = "merges";
    private const string EdgeAppliesStyle = "applies-style";

    private const string FlavorAttachedProperty = "xaml-attached-property";

    /// <inheritdoc />
    public IReadOnlyCollection<string> FileExtensions { get; } = new[] { ".xaml" };

    /// <inheritdoc />
    public async Task<IReadOnlyList<IndexEvent>> IndexAsync(IndexContext ctx, CancellationToken ct)
    {
        if (ctx is null) throw new ArgumentNullException(nameof(ctx));

        XamlDocument doc;
        try
        {
            doc = XamlReader.Parse(ctx.Contents);
        }
        catch (XmlException)
        {
            // Malformed XAML — return no events rather than throwing so a single bad file doesn't
            // fail the entire indexing pass.
            return Array.Empty<IndexEvent>();
        }

        var dialect = XamlProfileDetector.Detect(doc.RootNamespaceMappings);
        var project = ctx.Project as XamlLanguageProject;
        var semanticResolver = await XamlSemanticResolver.CreateAsync(
            project,
            doc,
            ct).ConfigureAwait(false);
        var emission = new EmissionContext(ctx, doc, dialect, semanticResolver);

        // Pass 1: collect named elements and this document's resource declarations. The second
        // pass can then resolve both ElementName bindings and local resource references without
        // depending on declaration order.
        XamlReader.Walk(doc.Root, (element, ancestors) =>
        {
            emission.RegisterNamedElement(element);
            emission.RegisterResource(element, ancestors);
        });

        // Pass 2: emit all symbol declarations, edges, and annotations.
        XamlReader.Walk(doc.Root, (element, ancestors) => emission.Emit(element, ancestors));

        return emission.Events;
    }

    /// <summary>
    /// Per-file emission state. Bundles the XAML view's canonical key, the named-element map, and
    /// the resource cache (when the host attached a <see cref="XamlLanguageProject"/>) so the
    /// per-element walker can build well-formed canonical keys without re-querying the host.
    /// </summary>
    private sealed class EmissionContext
    {
        private readonly IndexContext _ctx;
        private readonly XamlDocument _doc;
        private readonly IXamlDialect _dialect;
        private readonly XamlLanguageProject? _project;
        private readonly XamlSemanticResolver? _semanticResolver;
        private readonly string _relativePath;
        private readonly XamlAttribute? _xClassAttribute;
        private readonly string? _xClass;
        private readonly string _viewKey;
        private readonly Dictionary<string, string> _namedElementKeysByName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<ResourceDefinition>> _localResources =
            new(StringComparer.Ordinal);
        private int _anonymousCounter;

        public List<IndexEvent> Events { get; } = new();

        public EmissionContext(
            IndexContext ctx,
            XamlDocument doc,
            IXamlDialect dialect,
            XamlSemanticResolver? semanticResolver)
        {
            _ctx = ctx;
            _doc = doc;
            _dialect = dialect;
            _project = ctx.Project as XamlLanguageProject;
            _semanticResolver = semanticResolver;
            _relativePath = ToRepoRelative(ctx.RepoRoot, ctx.FilePath);
            _xClassAttribute = doc.Root.FindAttribute(XamlReader.XamlNamespace, "Class");
            _xClass = _xClassAttribute?.Value?.Trim();
            _viewKey = $"xaml:view:{_relativePath}";
        }

        public void RegisterNamedElement(XamlElement element)
        {
            var name = ResolveNameAttribute(element);
            if (name is null) return;
            var key = $"xaml:element:{_relativePath}#{name}";
            _namedElementKeysByName[name] = key;
        }

        public void RegisterResource(
            XamlElement element,
            IReadOnlyList<XamlElement> ancestors)
        {
            var keyAttribute = element.FindAttribute(
                XamlReader.XamlNamespace,
                "Key");
            if (keyAttribute is null
                || (!AnyAncestorIsResources(ancestors)
                    && !IsStyleOrTemplate(element)))
            {
                return;
            }

            if (!_localResources.TryGetValue(keyAttribute.Value, out var candidates))
            {
                candidates = new List<ResourceDefinition>();
                _localResources[keyAttribute.Value] = candidates;
            }
            candidates.Add(new ResourceDefinition(
                keyAttribute.Value,
                _ctx.FilePath,
                element.Line,
                element.Column,
                element.LocalName));
        }

        public void Emit(XamlElement element, IReadOnlyList<XamlElement> ancestors)
        {
            // Root element is the xaml-view; everything else is an xaml-element / xaml-resource /
            // xaml-style / xaml-template depending on what attributes it carries.
            var isRoot = ancestors.Count == 0;
            string elementKey;
            if (isRoot)
            {
                EmitView(element);
                elementKey = _viewKey;
            }
            else
            {
                elementKey = EmitNonRoot(element, ancestors);
            }

            EmitViewModelAssociationEdges(element, elementKey);

            // Attached properties become annotations on the element symbol.
            EmitAttachedPropertyAnnotations(element, elementKey);

            // Walk every binding/event/style attribute on the element and emit the matching edge.
            EmitAttributeEdges(element, elementKey);

            // ResourceDictionary.MergedDictionaries → emit `merges` edges from the parent
            // resource-dictionary to each Source uri (recorded as a literal target since we do
            // not resolve cross-project URIs in v1).
            EmitMergedDictionaryEdges(element, elementKey);
        }

        private void EmitView(XamlElement root)
        {
            var name = string.IsNullOrEmpty(_xClass)
                ? root.LocalName
                : ShortName(_xClass!);
            // Prefer the relative path as the FQN so the view is discoverable both by
            // file path search ("Views/MainWindow.xaml") and by class shortname ("MainWindow").
            // The class FQN is still recoverable from the code-behind edge target.
            var fqn = _relativePath;

            Events.Add(new IndexEvent.SymbolDeclared(
                canonicalKey: _viewKey,
                name: name,
                fqn: fqn,
                kind: KindXamlView,
                startLine: root.Line,
                startColumn: root.Column,
                endLine: root.Line,
                endColumn: root.Column,
                signature: null,
                containerCanonicalKey: null,
                modifiers: null,
                accessibility: 6,
                xmlSummary: null));

            // code-behind edge: xaml-view → csharp:T:<x:Class>
            if (!string.IsNullOrEmpty(_xClass) && _xClassAttribute is not null)
            {
                var typeKey = CanonicalKeys.ForType(_xClass!);
                Events.Add(new IndexEvent.EdgeEmitted(
                    sourceCanonicalKey: _viewKey,
                    targetCanonicalKey: typeKey,
                    edgeKindName: EdgeCodeBehind)
                {
                    Evidence = CreateAttributeEvidence(
                        _xClassAttribute,
                        EvidenceConfidence.Exact,
                        metadata: null),
                });
            }
        }

        private string EmitNonRoot(XamlElement element, IReadOnlyList<XamlElement> ancestors)
        {
            var keyAttr = element.FindAttribute(XamlReader.XamlNamespace, "Key");
            var hasName = ResolveNameAttribute(element) is not null;
            var inResources = AnyAncestorIsResources(ancestors);

            string canonicalKey;
            string kind;
            if (keyAttr is not null && (inResources || IsStyleOrTemplate(element)))
            {
                kind = ClassifyKeyedKind(element);
                canonicalKey = $"xaml:{kind.Substring("xaml-".Length)}:{_relativePath}#{keyAttr.Value}";
            }
            else if (hasName)
            {
                var name = ResolveNameAttribute(element)!;
                kind = KindXamlElement;
                canonicalKey = $"xaml:element:{_relativePath}#{name}";
            }
            else
            {
                // Unnamed element — only emit a symbol if it carries an attribute that needs
                // its own anchor (binding, event, style application, OR attached property).
                // `<Button Grid.Row="2"/>` has no x:Name and no binding but the
                // `xaml-attached-property` annotation contract requires a host symbol to attach
                // to, so attached properties also justify synthesizing one.
                if (!ElementNeedsSymbol(element)
                    && (_semanticResolver?.GetViewModelAssociations(element).Count ?? 0) == 0)
                {
                    // Use the parent's view key as the surrogate so subsequent emissions still
                    // have something to attach to (they'll skip when this returns the view).
                    return _viewKey;
                }
                kind = KindXamlElement;
                _anonymousCounter++;
                canonicalKey = $"xaml:element:{_relativePath}#__anon_{_anonymousCounter}";
            }

            var displayName = element.LocalName;
            var fqn = canonicalKey.Substring(canonicalKey.IndexOf(':') + 1);
            Events.Add(new IndexEvent.SymbolDeclared(
                canonicalKey: canonicalKey,
                name: displayName,
                fqn: fqn,
                kind: kind,
                startLine: element.Line,
                startColumn: element.Column,
                endLine: element.Line,
                endColumn: element.Column,
                signature: null,
                containerCanonicalKey: _viewKey,
                modifiers: null,
                accessibility: 6,
                xmlSummary: null));

            // x:Class on non-root elements is unusual; the xaml-view emission above already
            // handles the root. For named elements that map to a CLR type, emit
            // `instantiates-type` to record the dependency.
            EmitInstantiatesTypeEdge(element);

            return canonicalKey;
        }

        private void EmitViewModelAssociationEdges(XamlElement element, string hostKey)
        {
            if (_semanticResolver is null) return;
            if (hostKey == _viewKey && element != _doc.Root) return;

            foreach (var association in _semanticResolver.GetViewModelAssociations(element))
            {
                var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["association"] = "data-context",
                    ["source"] = association.Source,
                };
                Events.Add(new IndexEvent.EdgeEmitted(
                    sourceCanonicalKey: hostKey,
                    targetCanonicalKey: association.TargetCanonicalKey,
                    edgeKindName: EdgeKinds.BindsTo,
                    metadata: metadata)
                {
                    Evidence = new EdgeEvidence(
                        new SourceLocation(
                            _ctx.FilePath,
                            association.Line,
                            association.Column,
                            association.Line,
                            association.Column + association.Length),
                        association.Confidence,
                        "xaml-semantic",
                        metadata),
                });
            }
        }

        private void EmitAttachedPropertyAnnotations(XamlElement element, string hostKey)
        {
            // Skip annotations when we don't have a real symbol to attach to (the placeholder
            // viewKey is not a meaningful host for a Grid.Row annotation).
            if (hostKey == _viewKey && element != _doc.Root) return;

            foreach (var attr in element.Attributes)
            {
                if (XamlAttributeClassifier.Classify(attr) != XamlAttributeKind.AttachedProperty) continue;
                Events.Add(new IndexEvent.AnnotationAttached(
                    symbolCanonicalKey: hostKey,
                    annotationName: attr.LocalName,
                    flavor: FlavorAttachedProperty,
                    fullName: attr.LocalName,
                    argsJson: BuildSingleArgJson(attr.Value)));
            }
        }

        private void EmitAttributeEdges(XamlElement element, string hostKey)
        {
            // Skip edges when the host is the placeholder view key — they would attach to an
            // overly broad source symbol and pollute the graph.
            if (hostKey == _viewKey && element != _doc.Root) return;

            foreach (var attr in element.Attributes)
            {
                var classification = XamlAttributeClassifier.Classify(attr);
                if (classification == XamlAttributeKind.NamespaceDeclaration) continue;
                if (classification == XamlAttributeKind.XamlDirective) continue;
                if (classification == XamlAttributeKind.AttachedProperty) continue;
                if (classification == XamlAttributeKind.MarkupExtension)
                {
                    EmitMarkupExtensionEdges(element, attr, hostKey);
                }
                else
                {
                    // Ordinary string-typed attribute. Heuristic: anything ending in a routed-event
                    // suffix that maps cleanly to an OnX-style handler is treated as a
                    // handles-event edge; we use the convention "PascalCase + handler in
                    // codebehind" rather than re-deriving from the WPF event metadata.
                    EmitPossibleEventHandlerEdge(attr, hostKey);
                }
            }

            // Style="{StaticResource X}" already emits via the markup-extension path; the literal
            // form Style="Resource" would also be a styled-by edge, but the canonical XAML idiom
            // is to use a markup extension so we skip the literal case here.
        }

        private void EmitMergedDictionaryEdges(XamlElement element, string hostKey)
        {
            // The canonical XAML idiom for merged dictionaries is the property-element form
            // `<ResourceDictionary.MergedDictionaries>` (or
            // `<Application.Resources>` containing `<ResourceDictionary.MergedDictionaries>`,
            // or Avalonia's `<ResourceDictionary.MergedDictionaries>` shape). Match either the
            // bare local name or the `*.MergedDictionaries` property-element form so every
            // dialect's idiomatic shape lands. Source collection itself is keyed off the parent
            // <ResourceDictionary>, not this property-element wrapper, so we walk grandchildren
            // when we're on the property element and direct children when we're on the bare form.
            var isMergedRoot = string.Equals(element.LocalName, "MergedDictionaries", StringComparison.Ordinal);
            var isMergedProperty = element.LocalName.EndsWith(".MergedDictionaries", StringComparison.Ordinal);
            if (!isMergedRoot && !isMergedProperty) return;

            foreach (var child in element.Children)
            {
                var sourceAttr = child.FindAttributeByLocalName("Source");
                if (sourceAttr is null || string.IsNullOrEmpty(sourceAttr.Value)) continue;
                // We don't resolve the Source URI to a real resource dictionary in v1; emit with
                // an unresolved sentinel so the edge is still discoverable by `list_callees`
                // queries on the parent dictionary. The source is the host symbol of the
                // property-element / merged-dictionaries collection itself. The placeholder
                // symbol declaration below is required: GraphStoreEmitter skips edges whose
                // endpoints aren't in the canonical-key map, so without it the `merges` edge
                // would silently disappear at flush time.
                var targetKey = $"xaml:resource:{_relativePath}#__merged:{sourceAttr.Value}";
                Events.Add(new IndexEvent.SymbolDeclared(
                    canonicalKey: targetKey,
                    name: sourceAttr.Value,
                    fqn: targetKey.Substring(targetKey.IndexOf(':') + 1),
                    kind: KindXamlResource,
                    startLine: sourceAttr.Line,
                    startColumn: sourceAttr.Column,
                    endLine: sourceAttr.Line,
                    endColumn: sourceAttr.Column,
                    containerCanonicalKey: _viewKey));
                Events.Add(new IndexEvent.EdgeEmitted(
                    sourceCanonicalKey: hostKey,
                    targetCanonicalKey: targetKey,
                    edgeKindName: EdgeMerges,
                    metadata: new Dictionary<string, string> { ["source"] = sourceAttr.Value }));
            }
        }

        private void EmitMarkupExtensionEdges(
            XamlElement element,
            XamlAttribute attr,
            string hostKey)
        {
            if (!MarkupExtensionParser.TryParse(attr.Value, out var ext)) return;
            if (ext.IsLiteral) return;

            switch (ext.Name)
            {
                case "Binding":
                case "x:Bind":
                    EmitBindingEdge(element, attr, hostKey, ext);
                    break;
            }

            // Resource extensions may be the top-level value or nested inside another extension
            // (for example Binding.Converter). Walk the parsed tree once so both shapes preserve
            // the same exact attribute evidence.
            EmitResourceExtensions(attr, hostKey, ext);
        }

        private void EmitBindingEdge(
            XamlElement element,
            XamlAttribute attr,
            string hostKey,
            MarkupExtensionValue ext)
        {
            var path = ResolveBindingPath(ext);
            var mode = ResolveNamedAsLiteral(ext, "Mode");
            var converter = ResolveNamedAsLiteral(ext, "Converter");
            var converterParameter = ResolveNamedAsLiteral(ext, "ConverterParameter");
            var elementName = ResolveNamedAsLiteral(ext, "ElementName");
            var relativeSource = ResolveNamedAsLiteral(ext, "RelativeSource");
            var fallback = ResolveNamedAsLiteral(ext, "FallbackValue");
            var stringFormat = ResolveNamedAsLiteral(ext, "StringFormat");
            var updateSourceTrigger = ResolveNamedAsLiteral(ext, "UpdateSourceTrigger");

            var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(path)) metadata[PayloadKeys.Path] = path!;
            if (!string.IsNullOrEmpty(mode)) metadata[PayloadKeys.Mode] = NormaliseMode(mode!);
            if (!string.IsNullOrEmpty(converter)) metadata[PayloadKeys.Converter] = converter!;
            if (!string.IsNullOrEmpty(converterParameter)) metadata[PayloadKeys.ConverterParameter] = converterParameter!;
            if (!string.IsNullOrEmpty(elementName)) metadata[PayloadKeys.ElementName] = elementName!;
            if (!string.IsNullOrEmpty(relativeSource)) metadata[PayloadKeys.RelativeSource] = relativeSource!;
            if (!string.IsNullOrEmpty(fallback)) metadata[PayloadKeys.FallbackValue] = fallback!;
            if (!string.IsNullOrEmpty(stringFormat)) metadata[PayloadKeys.StringFormat] = stringFormat!;
            if (!string.IsNullOrEmpty(updateSourceTrigger)) metadata[PayloadKeys.UpdateSourceTrigger] = updateSourceTrigger!;

            if (!string.IsNullOrEmpty(elementName) && _namedElementKeysByName.TryGetValue(elementName!, out var resolvedTarget))
            {
                var elementMeta = new Dictionary<string, string>(StringComparer.Ordinal);
                if (!string.IsNullOrEmpty(path)) elementMeta[PayloadKeys.Path] = path!;
                Events.Add(new IndexEvent.EdgeEmitted(
                    sourceCanonicalKey: hostKey,
                    targetCanonicalKey: resolvedTarget,
                    edgeKindName: EdgeBindsElement,
                    metadata: elementMeta.Count == 0 ? null : elementMeta)
                {
                    Evidence = CreateAttributeEvidence(
                        attr,
                        EvidenceConfidence.Exact,
                        elementMeta),
                });
            }

            // ElementName / RelativeSource / explicit Source bindings do not evaluate against the
            // inherited DataContext. Until those source shapes have their own CLR-type resolver,
            // retaining only the exact binds-element fact is safer than claiming a property edge
            // against the wrong object.
            if (ext.NamedArgs.ContainsKey("ElementName")
                || ext.NamedArgs.ContainsKey("RelativeSource")
                || ext.NamedArgs.ContainsKey("Source")
                || !string.Equals(ext.Name, "Binding", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var semanticTarget = _semanticResolver?.ResolveBinding(
                element,
                path!,
                requireCommand: string.Equals(
                    attr.LocalName,
                    "Command",
                    StringComparison.Ordinal));
            if (semanticTarget is null) return;

            metadata[PayloadKeys.DataType] = semanticTarget.DataTypeCanonicalKey;
            metadata["context-source"] = semanticTarget.ContextSource;
            if (string.Equals(attr.LocalName, "Command", StringComparison.Ordinal))
            {
                metadata["command"] = semanticTarget.Property.Name;
            }

            Events.Add(new IndexEvent.EdgeEmitted(
                sourceCanonicalKey: hostKey,
                targetCanonicalKey: semanticTarget.PropertyCanonicalKey,
                edgeKindName: EdgeBindsPath,
                metadata: metadata.Count == 0 ? null : metadata)
            {
                Evidence = CreateAttributeEvidence(
                    attr,
                    semanticTarget.Confidence,
                    metadata),
            });
        }

        private void EmitResourceExtensions(
            XamlAttribute attr,
            string hostKey,
            MarkupExtensionValue extension)
        {
            if (extension.Name is "StaticResource" or "DynamicResource" or "ThemeResource")
            {
                EmitUsesResourceEdge(attr, hostKey, extension);
            }
            foreach (var positional in extension.PositionalArgs)
            {
                if (!positional.IsLiteral)
                {
                    EmitResourceExtensions(attr, hostKey, positional);
                }
            }
            foreach (var named in extension.NamedArgs.Values)
            {
                if (!named.IsLiteral)
                {
                    EmitResourceExtensions(attr, hostKey, named);
                }
            }
        }

        private void EmitUsesResourceEdge(
            XamlAttribute attr,
            string hostKey,
            MarkupExtensionValue ext)
        {
            var key = ext.PositionalArgs.Count > 0 && ext.PositionalArgs[0].IsLiteral
                ? ext.PositionalArgs[0].Literal
                : ResolveNamedAsLiteral(ext, "ResourceKey");
            if (string.IsNullOrEmpty(key)) return;

            var lookupKind = ext.Name switch
            {
                "StaticResource" => "static",
                "DynamicResource" => "dynamic",
                _ => "theme",
            };
            var resolution = ResolveResource(key!);
            if (resolution.Status != ResourceResolutionStatus.Resolved
                || resolution.Definition is null)
            {
                EmitResourceFinding(
                    attr,
                    hostKey,
                    key!,
                    lookupKind,
                    resolution);
                return;
            }

            var resource = resolution.Definition;
            var targetKey = resource.ToCanonicalKey(_ctx.RepoRoot);
            var schemeRest = ClassifyResourceFromElementName(resource.ElementName);
            var edgeKind = string.Equals(
                schemeRest,
                "style",
                StringComparison.Ordinal)
                ? EdgeAppliesStyle
                : EdgeUsesResource;
            var meta = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [PayloadKeys.Key] = key!,
                ["resource-lookup"] = lookupKind,
            };
            Events.Add(new IndexEvent.EdgeEmitted(
                sourceCanonicalKey: hostKey,
                targetCanonicalKey: targetKey,
                edgeKindName: edgeKind,
                metadata: meta)
            {
                Evidence = CreateAttributeEvidence(
                    attr,
                    EvidenceConfidence.Exact,
                    meta,
                    producer: "xaml-resource"),
            });
        }

        private ResourceResolution ResolveResource(string key)
        {
            if (_localResources.TryGetValue(key, out var localCandidates)
                && localCandidates.Count > 0)
            {
                return localCandidates.Count == 1
                    ? new ResourceResolution(
                        ResourceResolutionStatus.Resolved,
                        localCandidates)
                    : new ResourceResolution(
                        ResourceResolutionStatus.Ambiguous,
                        localCandidates);
            }
            return _project?.ResolveResource(key) ?? ResourceResolution.Missing;
        }

        private void EmitResourceFinding(
            XamlAttribute attribute,
            string hostKey,
            string key,
            string lookupKind,
            ResourceResolution resolution)
        {
            var missing = resolution.Status == ResourceResolutionStatus.Missing;
            var code = missing ? "XAMLRESOURCE001" : "XAMLRESOURCE002";
            var name = missing ? "Resource不存在" : "Resource不明确";
            var finding = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = code,
                ["severity"] = "warning",
                ["message"] = name,
                ["key"] = key,
                ["resourceLookup"] = lookupKind,
                ["candidateCount"] = resolution.Candidates.Count,
                ["file"] = _ctx.FilePath,
                ["startLine"] = attribute.Line,
                ["startColumn"] = attribute.Column,
                ["endLine"] = attribute.Line,
                ["endColumn"] = attribute.Column
                    + Math.Max(1, QualifiedAttributeNameLength(attribute)),
                ["confidence"] = "exact",
                ["producer"] = "xaml-resource",
            };
            if (resolution.Candidates.Count > 0)
            {
                finding["candidates"] = resolution.Candidates
                    .Select(candidate => new Dictionary<string, object?>
                    {
                        ["canonicalKey"] = candidate.ToCanonicalKey(_ctx.RepoRoot),
                        ["file"] = candidate.FilePath,
                        ["line"] = candidate.Line,
                        ["column"] = candidate.Column,
                    })
                    .ToArray();
            }
            Events.Add(new IndexEvent.AnnotationAttached(
                symbolCanonicalKey: hostKey,
                annotationName: name,
                flavor: "xaml-resource-finding",
                fullName: code,
                argsJson: System.Text.Json.JsonSerializer.Serialize(finding)));
        }

        private void EmitPossibleEventHandlerEdge(XamlAttribute attr, string hostKey)
        {
            // Heuristic: any attribute whose value is a bare identifier and whose name follows the
            // PascalCase event-name convention is treated as a routed-event hookup. The proper
            // resolution would consult the WPF event metadata; we don't load that for v1, but the
            // false-positive rate is low (most non-event attributes are styled with markup
            // extensions / lowercase / quoted values).
            var value = attr.Value?.Trim();
            if (string.IsNullOrEmpty(value)) return;
            if (!IsBareIdentifier(value!)) return;
            if (!IsLikelyEventName(attr.LocalName)) return;
            if (string.IsNullOrEmpty(_xClass)) return;

            var handler = _semanticResolver?.ResolveEventHandler(_xClass, value!);
            if (handler is null) return;
            var handlerKey = XamlSemanticResolver.CanonicalKey(handler);
            if (handlerKey is null) return;
            var meta = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [PayloadKeys.Event] = attr.LocalName,
                [PayloadKeys.Handler] = value!,
            };
            Events.Add(new IndexEvent.EdgeEmitted(
                sourceCanonicalKey: hostKey,
                targetCanonicalKey: handlerKey,
                edgeKindName: EdgeHandlesEvent,
                metadata: meta)
            {
                Evidence = CreateAttributeEvidence(
                    attr,
                    EvidenceConfidence.Exact,
                    meta),
            });
        }

        private void EmitInstantiatesTypeEdge(XamlElement element)
        {
            // Only emit when we can resolve the element's CLR type. The simplest cross-language
            // resolution: a non-XAML-namespace element whose XML namespace declares a
            // `clr-namespace:` mapping → `csharp:T:<ns>.<localName>`. Defer the more general case
            // (xmlns to assembly) for v1 since we'd need to load assembly metadata.
            if (string.IsNullOrEmpty(element.Namespace)) return;
            if (string.Equals(element.Namespace, _dialect.XClassNamespace, StringComparison.Ordinal)) return;
            if (string.Equals(element.Namespace, WpfDialect.PresentationNamespace, StringComparison.Ordinal)) return;
            if (string.Equals(element.Namespace, AvaloniaDialect.AvaloniaNamespace, StringComparison.Ordinal)) return;

            var nsValue = element.Namespace;
            if (!nsValue.StartsWith(_dialect.ClrNamespacePrefix, StringComparison.Ordinal)) return;
            var withoutPrefix = nsValue.Substring(_dialect.ClrNamespacePrefix.Length);
            // `clr-namespace:Foo;assembly=Bar` strips the assembly hint for the canonical key.
            var semi = withoutPrefix.IndexOf(';');
            var clrNamespace = semi >= 0 ? withoutPrefix.Substring(0, semi) : withoutPrefix;
            if (string.IsNullOrEmpty(clrNamespace)) return;

            var typeKey = CanonicalKeys.ForType(clrNamespace + "." + element.LocalName);
            Events.Add(new IndexEvent.EdgeEmitted(
                sourceCanonicalKey: _viewKey,
                targetCanonicalKey: typeKey,
                edgeKindName: EdgeInstantiatesType));
        }

        private string? ResolveNameAttribute(XamlElement element)
        {
            var xName = element.FindAttribute(XamlReader.XamlNamespace, "Name");
            if (xName is not null && !string.IsNullOrEmpty(xName.Value)) return xName.Value;
            var name = element.FindAttributeByLocalName("Name");
            if (name is not null && string.IsNullOrEmpty(name.Prefix) && !string.IsNullOrEmpty(name.Value))
            {
                return name.Value;
            }
            return null;
        }

        private static bool AnyAncestorIsResources(IReadOnlyList<XamlElement> ancestors)
        {
            for (var i = 0; i < ancestors.Count; i++)
            {
                if (ancestors[i].LocalName.EndsWith(".Resources", StringComparison.Ordinal)) return true;
                if (string.Equals(ancestors[i].LocalName, "ResourceDictionary", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static bool IsStyleOrTemplate(XamlElement element) =>
            element.LocalName is "Style" or "DataTemplate" or "ControlTemplate" or "ItemsPanelTemplate" or "ControlTheme";

        private static string ClassifyKeyedKind(XamlElement element)
        {
            return element.LocalName switch
            {
                "Style" => KindXamlStyle,
                "DataTemplate" or "ControlTemplate" or "ItemsPanelTemplate" or "ControlTheme" => KindXamlTemplate,
                _ => KindXamlResource,
            };
        }

        private static string ClassifyResourceFromElementName(string elementName) =>
            elementName switch
            {
                "Style" => "style",
                "DataTemplate" or "ControlTemplate" or "ItemsPanelTemplate" or "ControlTheme" => "template",
                _ => "resource",
            };

        private static bool ElementNeedsSymbol(XamlElement element)
        {
            // Element warrants a symbol if it carries any binding/event/style attribute, OR an
            // attached property — attached properties annotate the element they're written on
            // (`<Button Grid.Row="2"/>` annotates the button), so the spec's
            // `xaml-attached-property` annotation needs a host symbol to attach to even when
            // the element has no other reason to exist as a node in the graph.
            foreach (var attr in element.Attributes)
            {
                var k = XamlAttributeClassifier.Classify(attr);
                if (k == XamlAttributeKind.MarkupExtension) return true;
                if (k == XamlAttributeKind.AttachedProperty) return true;
                if (k == XamlAttributeKind.Value && IsLikelyEventName(attr.LocalName) && IsBareIdentifier(attr.Value ?? "")) return true;
            }
            return false;
        }

        private static string? ResolveNamedAsLiteral(MarkupExtensionValue ext, string name)
        {
            if (!ext.NamedArgs.TryGetValue(name, out var v)) return null;
            return v.IsLiteral ? v.Literal : null;
        }

        private static string? ResolveBindingPath(MarkupExtensionValue ext)
        {
            if (ext.NamedArgs.TryGetValue("Path", out var named))
            {
                return named.IsLiteral ? named.Literal : null;
            }
            if (ext.PositionalArgs.Count > 0 && ext.PositionalArgs[0].IsLiteral) return ext.PositionalArgs[0].Literal;
            return null;
        }

        private static string NormaliseMode(string mode) => mode switch
        {
            "OneWay" => "one-way",
            "TwoWay" => "two-way",
            "OneTime" => "one-time",
            "OneWayToSource" => "one-way-to-source",
            "Default" => "default",
            _ => mode.ToLowerInvariant(),
        };

        private static string BuildSingleArgJson(string value) =>
            "{" +
            "\"ctor\":[" + System.Text.Json.JsonSerializer.Serialize(value) + "]," +
            "\"named\":{}}";

        private EdgeEvidence CreateAttributeEvidence(
            XamlAttribute attribute,
            EvidenceConfidence confidence,
            IReadOnlyDictionary<string, string>? metadata,
            string producer = "xaml-semantic")
        {
            var qualifiedNameLength = QualifiedAttributeNameLength(attribute);
            return new EdgeEvidence(
                new SourceLocation(
                    _ctx.FilePath,
                    attribute.Line,
                    attribute.Column,
                    attribute.Line,
                    attribute.Column + Math.Max(1, qualifiedNameLength)),
                confidence,
                producer,
                metadata);
        }

        private static int QualifiedAttributeNameLength(XamlAttribute attribute) =>
            attribute.LocalName.Length
            + (string.IsNullOrEmpty(attribute.Prefix)
                ? 0
                : attribute.Prefix.Length + 1);

        private static bool IsBareIdentifier(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            if (!char.IsLetter(value[0]) && value[0] != '_') return false;
            for (var i = 1; i < value.Length; i++)
            {
                var c = value[i];
                if (!char.IsLetterOrDigit(c) && c != '_') return false;
            }
            return true;
        }

        private static bool IsLikelyEventName(string attributeLocalName)
        {
            if (string.IsNullOrEmpty(attributeLocalName)) return false;
            // Common WPF/Avalonia routed-event names + the conventional pattern. The list is
            // deliberately small; the v1 indexer prioritises precision over recall on event
            // detection (false positives produce stranded edges nobody queries).
            return attributeLocalName is
                "Click" or "DoubleClick" or "Loaded" or "Unloaded" or "MouseDown" or "MouseUp" or
                "MouseEnter" or "MouseLeave" or "KeyDown" or "KeyUp" or "TextChanged" or
                "SelectionChanged" or "Checked" or "Unchecked" or "Tapped" or "Closing" or
                "Opening" or "PreviewMouseDown" or "PreviewMouseUp" or "PreviewKeyDown" or
                "PreviewKeyUp" or "GotFocus" or "LostFocus" or "Drop" or "DragEnter" or "DragLeave";
        }

        private static string ShortName(string fqn)
        {
            var dot = fqn.LastIndexOf('.');
            return dot >= 0 ? fqn.Substring(dot + 1) : fqn;
        }

        private static string ToRepoRelative(string repoRoot, string filePath)
        {
            var full = Path.GetFullPath(filePath);
            if (!string.IsNullOrEmpty(repoRoot))
            {
                var rootFull = Path.GetFullPath(repoRoot);
                if (full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                {
                    var rel = full.Substring(rootFull.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    return rel.Replace('\\', '/');
                }
            }
            return Path.GetFileName(full).Replace('\\', '/');
        }
    }
}
