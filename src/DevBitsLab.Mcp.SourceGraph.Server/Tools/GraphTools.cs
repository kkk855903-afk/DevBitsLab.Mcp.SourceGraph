using System.ComponentModel;
using System.Text;
using System.Text.Json;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Embeddings;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Server.Observability;
using DevBitsLab.Mcp.SourceGraph.Server.Resources;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;
using DevBitsLab.Mcp.SourceGraph.Storage;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools;

[McpServerToolType]
public static class GraphTools
{
    private const string ScopeDescription =
        "Optional scope id, the literal '*' for all non-isolated scopes, or a comma-separated list of ids " +
        "(e.g. 'frontend,backend'). Omit to use `default_scope` from .sourcegraph.json. Call `list_scopes` to discover.";

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(FindDefinitionResult))]
    [ToolTrigger("\"where is X defined?\"")]
    [Description("Find the definition of a symbol by name or fully-qualified name. Returns location, kind, signature, accessibility, modifiers, and one-line XML summary for each match.")]
    public static Task<CallToolResult> FindDefinitionAsync(
        ScopeRouter router,
        [Description("Symbol name (e.g. 'Calculator', 'Divide') or FQN suffix (e.g. 'Calculator.Add', 'Sample.Domain.Calculator')")] string symbol,
        [Description("Optional substring to narrow the search to specific file paths")] string? fileHint = null,
        [Description(ScopeDescription)] string? scope = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("find_definition", new { symbol, fileHint, scope }, () =>
            ScopedExecution.RunAsync(router, scope, async host =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var hits = await host.Store.FindSymbolsAsync(symbol, fileHint, limit: 25, ct).ConfigureAwait(false);
                if (hits.Count == 0)
                {
                    return BuildFindDefinitionResult(
                        prose: $"No matches for '{symbol}'.",
                        hits: Array.Empty<SymbolHit>(),
                        structuredHits: Array.Empty<FindDefinitionHit>(),
                        scopeId: host.Scope.Id,
                        elapsedMs: sw.ElapsedMilliseconds);
                }

                // Pre-fetch history rows in one batch so we don't fire a query per hit.
                var historyById = await host.Store.GetSymbolHistoryBatchAsync(hits.Select(h => h.Id).ToList(), ct).ConfigureAwait(false);
                var multipleFlavors = await HasMultipleAnnotationFlavorsAsync(host.Store, ct).ConfigureAwait(false);

                // Build prose and the structured DTO from the same enumeration so the two stay in
                // lockstep — the spec scenario "structured array length equals prose row count"
                // depends on this single source of truth.
                var sb = new StringBuilder();
                sb.AppendLine($"{hits.Count} hits for '{symbol}':");
                sb.AppendLine();
                var structuredHits = new List<FindDefinitionHit>(hits.Count);
                foreach (var h in hits)
                {
                    sb.AppendLine($"- **{h.Fqn}** ({Format.KindWithAttrs(h)})");
                    sb.AppendLine($"  - {Format.Location(h.FilePath, h.StartLine, h.StartCol)}");
                    if (!string.IsNullOrEmpty(h.Signature)) sb.AppendLine($"  - `{h.Signature}`");
                    var oneLine = Format.OneLineSummary(h.XmlSummary);
                    if (!string.IsNullOrEmpty(oneLine)) sb.AppendLine($"  - _{oneLine}_");
                    var anns = await host.Store.GetAnnotationsForSymbolAsync(h.Id, ct).ConfigureAwait(false);
                    var annLine = AnnotationFormat.OneLine(anns, multipleFlavors);
                    if (annLine is not null) sb.AppendLine($"  - {annLine}");
                    if (historyById.TryGetValue(h.Id, out var hist))
                    {
                        var line = Format.HistoryLine(hist);
                        if (line is not null) sb.AppendLine($"  - {line}");
                    }
                    structuredHits.Add(new FindDefinitionHit(
                        Fqn: h.Fqn,
                        Kind: h.Kind,
                        FilePath: h.FilePath,
                        Line: h.StartLine,
                        Column: h.StartCol,
                        Signature: string.IsNullOrEmpty(h.Signature) ? null : h.Signature,
                        XmlSummary: string.IsNullOrEmpty(h.XmlSummary) ? null : h.XmlSummary));
                }

                return BuildFindDefinitionResult(
                    prose: sb.ToString(),
                    hits: hits,
                    structuredHits: structuredHits,
                    scopeId: host.Scope.Id,
                    elapsedMs: sw.ElapsedMilliseconds);
            }, ct));

    /// <summary>
    /// Compose the multi-content <see cref="CallToolResult"/> for <c>find_definition</c>: the
    /// leading user-visible prose <see cref="TextContentBlock"/>, one
    /// <see cref="ResourceLinkBlock"/> per matched symbol pointing at the corresponding
    /// <c>graph://symbol/&lt;id&gt;</c> resource, a trailing audience-restricted
    /// <see cref="TextContentBlock"/> carrying scope id + latency + hit count for the model only,
    /// and the typed <see cref="FindDefinitionResult"/> serialized into
    /// <see cref="CallToolResult.StructuredContent"/>.
    ///
    /// The structured payload is serialised through the source-generated
    /// <see cref="ToolOutputJsonContext"/> so we stay off reflection and the wire shape uses
    /// snake_case property names (matching the SDK's tools/list outputSchema generator).
    /// </summary>
    private static CallToolResult BuildFindDefinitionResult(
        string prose,
        IReadOnlyList<SymbolHit> hits,
        IReadOnlyList<FindDefinitionHit> structuredHits,
        string scopeId,
        long elapsedMs)
    {
        var content = new List<ContentBlock>(capacity: 2 + hits.Count)
        {
            new TextContentBlock { Text = prose },
        };

        foreach (var h in hits)
        {
            content.Add(new ResourceLinkBlock
            {
                Uri = GraphResourceUris.Symbol(h.Id),
                Name = h.Fqn,
                Title = h.Fqn,
                Description = $"{Format.KindWithAttrs(h)} — {Format.Location(h.FilePath, h.StartLine, h.StartCol)}",
                MimeType = "text/markdown",
            });
        }

        // Audience-restricted metadata: scope id + latency + hit count are useful to the agent for
        // chaining (e.g. "if I see latency_ms > 500 maybe drop this scope from a fan-out") but pure
        // noise to the human reading the chat. Priority < 0.5 is the "informational, deprioritise"
        // signal documented in the design.
        content.Add(new TextContentBlock
        {
            Text = $"_meta: scope=`{scopeId}`, latency_ms={elapsedMs}, hits={hits.Count}_",
            Annotations = new Annotations
            {
                Audience = new[] { Role.Assistant },
                Priority = 0.2f,
            },
        });

        var result = new CallToolResult
        {
            Content = content,
            StructuredContent = JsonSerializer.SerializeToElement(
                new FindDefinitionResult(structuredHits),
                ToolOutputJsonContext.Default.FindDefinitionResult),
        };
        return result;
    }

    [McpServerTool]
    [ToolTrigger("\"find every POST endpoint\", \"what's been deprecated?\", \"find all DI singletons\", \"every controller decorated with @Component\"")]
    [Description("Find every symbol that carries an annotation by short name (e.g. 'HttpGet', 'Obsolete', 'Authorize', 'Component'). Annotations span .NET attributes, TS decorators, Vue directives, etc. — `flavor` filters to one annotation pattern (e.g. 'csharp-attribute'), null matches across all flavors. Optional argValue does a trigram match against the annotation's serialised arguments (e.g. argValue='/api/v2' to find route attributes whose path contains that substring).")]
    public static Task<string> FindByAnnotationAsync(
        ScopeRouter router,
        [Description("Annotation short name (e.g. 'HttpGet', 'Obsolete', 'Authorize'). Trailing 'Attribute' is implied; use the form you'd type in source.")] string name,
        [Description("Optional flavor filter (kebab-case, e.g. 'csharp-attribute', 'xaml-attached-property', 'ts-decorator'). Null matches across all flavors. Call `list_scopes` and inspect `annotation_flavors` from the initialize response to discover what's available on the active scope.")] string? flavor = null,
        [Description("Optional substring to match against the annotation's arguments via FTS5 trigram (>= 3 chars). E.g. '/api/users' or 'Use Foo'.")] string? argValue = null,
        [Description("Optional kebab-case symbol kind filter: class|method|property|field|interface|namespace|enum|enum-member|operator|record|delegate|struct|event|xaml-view|xaml-element|xaml-resource|xaml-style|xaml-template|... (any plugin-defined kebab-case kind also accepted).")] string? kind = null,
        [Description("Maximum results (default 50)")] int limit = 50,
        [Description(ScopeDescription)] string? scope = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("find_by_annotation", new { name, flavor, argValue, kind, limit, scope }, () =>
            ScopedExecution.RunAsync(router, scope, async host =>
            {
                var kindFilter = NormaliseKindFilter(kind);
                var hits = await host.Store.FindByAnnotationAsync(name, flavor, argValue, kindFilter, limit, ct).ConfigureAwait(false);
                if (hits.Count == 0)
                {
                    var argsClause = string.IsNullOrEmpty(argValue) ? "" : $" with argValue ~ '{argValue}'";
                    var flavorClause = string.IsNullOrEmpty(flavor) ? "" : $" (flavor='{flavor}')";
                    return $"No symbols carry [{name}]{flavorClause}{argsClause}.";
                }
                var multipleFlavors = await HasMultipleAnnotationFlavorsAsync(host.Store, ct).ConfigureAwait(false);
                var sb = new StringBuilder();
                sb.AppendLine($"{hits.Count} symbols carry [{name}]:");
                if (hits.Count >= 2)
                {
                    var rows = new List<IReadOnlyList<string>>(hits.Count);
                    foreach (var h in hits)
                    {
                        rows.Add(new[]
                        {
                            $"**{h.Fqn}**",
                            Format.KindWithAttrs(h),
                            Format.Location(h.FilePath, h.StartLine, h.StartCol),
                        });
                    }
                    Format.AppendTable(sb, new[] { "Symbol", "Kind", "Location" }, rows);
                    // Annotation detail still rendered as a follow-on bullet so the agent can read
                    // each match's args without a per-cell wall-of-text in the table.
                    foreach (var h in hits)
                    {
                        var anns = await host.Store.GetAnnotationsForSymbolAsync(h.Id, ct).ConfigureAwait(false);
                        var line = AnnotationFormat.OneLine(anns, multipleFlavors);
                        if (line is not null) sb.AppendLine($"- **{h.Fqn}**: {line}");
                    }
                }
                else
                {
                    foreach (var h in hits)
                    {
                        sb.AppendLine($"- **{h.Fqn}** ({Format.KindWithAttrs(h)}) at {Format.Location(h.FilePath, h.StartLine, h.StartCol)}");
                        var anns = await host.Store.GetAnnotationsForSymbolAsync(h.Id, ct).ConfigureAwait(false);
                        var line = AnnotationFormat.OneLine(anns, multipleFlavors);
                        if (line is not null) sb.AppendLine($"  - {line}");
                    }
                }
                return sb.ToString();
            }, ct));

    [McpServerTool]
    [ToolTrigger("\"who uses X?\" or \"who calls X?\"")]
    [Description("Find every place that references a symbol. Resolves the symbol by name/FQN, then lists each call site or type-use as file:line. By default skips refs from source-generated files; pass includeGenerated=true to surface them.")]
    public static Task<string> FindReferencesAsync(
        ScopeRouter router,
        [Description("Symbol name or FQN, same matching rules as find_definition")] string symbol,
        [Description("Maximum number of references to return (default 200)")] int limit = 200,
        [Description("Include references from source-generated files (default false)")] bool includeGenerated = false,
        [Description(ScopeDescription)] string? scope = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("find_references", new { symbol, limit, includeGenerated, scope }, () =>
            ScopedExecution.RunAsync(router, scope, async host =>
            {
                var hits = await host.Store.FindSymbolsAsync(symbol, filePathHint: null, limit: 5, ct).ConfigureAwait(false);
                if (hits.Count == 0) return $"No matches for '{symbol}'.";

                var sb = new StringBuilder();
                if (hits.Count > 1)
                {
                    sb.AppendLine($"Multiple symbols match '{symbol}'; reporting references for the top match. Other matches: {string.Join(", ", hits.Skip(1).Select(h => h.Fqn))}");
                    sb.AppendLine();
                }
                var top = hits[0];
                var refs = await host.Store.FindReferencesAsync(top.Id, includeGenerated, limit, ct).ConfigureAwait(false);
                sb.AppendLine($"References to **{top.Fqn}** ({KindLabel(top.Kind)}){GeneratedSuffix(top.IsGenerated)}:");
                sb.AppendLine($"- definition: {Format.Location(top.FilePath, top.StartLine, top.StartCol)}");
                if (refs.Count == 0)
                {
                    sb.AppendLine(includeGenerated
                        ? "- no other references in the graph"
                        : "- no other references in the graph (pass includeGenerated=true to include source-generated files)");
                    return sb.ToString();
                }
                sb.AppendLine();
                sb.AppendLine($"{refs.Count} references:");
                if (refs.Count >= 2)
                {
                    var rows = new List<IReadOnlyList<string>>(refs.Count);
                    foreach (var r in refs)
                    {
                        rows.Add(new[]
                        {
                            RefKindLabel(r.Kind),
                            Format.Location(r.FilePath, r.Line, r.Col) + GeneratedSuffix(r.IsGenerated),
                        });
                    }
                    Format.AppendTable(sb, new[] { "Kind", "Location" }, rows);
                }
                else
                {
                    foreach (var r in refs)
                    {
                        sb.AppendLine($"- {RefKindLabel(r.Kind)} at {Format.Location(r.FilePath, r.Line, r.Col)}{GeneratedSuffix(r.IsGenerated)}");
                    }
                }
                return sb.ToString();
            }, ct));

    [McpServerTool]
    [ToolTrigger("\"what's in this file?\" — faster than reading the whole file")]
    [Description("List every symbol declared in a file (classes, methods, properties, etc.). Each row carries kind, accessibility, modifiers, and one-line XML summary.")]
    public static Task<string> ListSymbolsInFileAsync(
        ScopeRouter router,
        [Description("Absolute path or path suffix that uniquely identifies the file")] string path,
        [Description(ScopeDescription)] string? scope = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("list_symbols_in_file", new { path, scope }, () =>
            ScopedExecution.RunAsync(router, scope, async host =>
            {
                var hits = await host.Store.ListSymbolsInFileAsync(path, ct).ConfigureAwait(false);
                if (hits.Count == 0) return $"No indexed symbols in '{path}'. The file may not be part of an indexed solution, or may not exist.";

                var historyById = await host.Store.GetSymbolHistoryBatchAsync(hits.Select(h => h.Id).ToList(), ct).ConfigureAwait(false);
                var multipleFlavors = await HasMultipleAnnotationFlavorsAsync(host.Store, ct).ConfigureAwait(false);

                var sb = new StringBuilder();
                sb.AppendLine($"{hits.Count} symbols in {hits[0].FilePath}:");
                foreach (var h in hits)
                {
                    sb.AppendLine($"- L{h.StartLine}: **{h.Name}** ({Format.KindWithAttrs(h)}) — {h.Fqn}");
                    if (!string.IsNullOrEmpty(h.Signature)) sb.AppendLine($"    `{h.Signature}`");
                    var oneLine = Format.OneLineSummary(h.XmlSummary);
                    if (!string.IsNullOrEmpty(oneLine)) sb.AppendLine($"    _{oneLine}_");
                    var anns = await host.Store.GetAnnotationsForSymbolAsync(h.Id, ct).ConfigureAwait(false);
                    var line = AnnotationFormat.OneLine(anns, multipleFlavors);
                    if (line is not null) sb.AppendLine($"    {line}");
                    if (historyById.TryGetValue(h.Id, out var hist))
                    {
                        var hline = Format.HistoryLine(hist);
                        if (hline is not null) sb.AppendLine($"    {hline}");
                    }
                }
                return sb.ToString();
            }, ct));

    [McpServerTool]
    [ToolTrigger("\"who calls X?\" or \"who consumes type X?\"")]
    [Description("List inbound edges into a target symbol. Default kind=calls (i.e. callers). Use kind=uses-type to find consumers of a type, kind=overrides-member for derived implementations, kind=implements-member for members satisfying an interface, kind=instantiates for `new T()` sites, kind=throws for throw sites, kind=tests for inbound test edges, or any XAML kind (code-behind, binds-path, binds-element, handles-event, uses-resource, instantiates-type, merges, applies-style) on a scope that loaded the XAML indexer; kind=all walks every edge kind. Plugin-defined kinds (any kebab-case identifier) are accepted as-is.")]
    public static Task<string> ListCallersAsync(
        ScopeRouter router,
        [Description("Target symbol name or FQN")] string symbol,
        [Description("Maximum number of results to return (default 50)")] int limit = 50,
        [Description("Edge kind to walk (kebab-case): calls (default) | uses-type | overrides-member | implements-member | instantiates | throws | tests | code-behind | binds-path | binds-element | handles-event | uses-resource | instantiates-type | merges | applies-style | all. Plugin-defined kinds are accepted.")] string? kind = null,
        [Description(ScopeDescription)] string? scope = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("list_callers", new { symbol, limit, kind, scope }, () =>
            ScopedExecution.RunAsync(router, scope, async host =>
            {
                var (edgeKind, label, isAll) = NormaliseEdgeKindParam(kind);
                if (!isAll && edgeKind is not null)
                {
                    var unknownNote = await CheckUnknownEdgeKindAsync(host.Store, edgeKind, ct).ConfigureAwait(false);
                    if (unknownNote is not null) return unknownNote;
                }

                var hits = await host.Store.FindSymbolsAsync(symbol, filePathHint: null, limit: 5, ct).ConfigureAwait(false);
                if (hits.Count == 0) return $"No matches for '{symbol}'.";
                var top = hits[0];
                var callers = await host.Store.ListCallersAsync(top.Id, limit, edgeKind, ct).ConfigureAwait(false);
                var sb = new StringBuilder();
                sb.AppendLine($"Inbound `{label}` to **{top.Fqn}** ({KindLabel(top.Kind)}):");
                if (callers.Count == 0) { sb.AppendLine("- (none)"); return sb.ToString(); }
                if (callers.Count >= 2)
                {
                    var rows = new List<IReadOnlyList<string>>(callers.Count);
                    foreach (var c in callers)
                    {
                        rows.Add(new[]
                        {
                            $"**{c.Fqn}**",
                            KindLabel(c.Kind),
                            Format.Location(c.FilePath, c.StartLine, c.StartCol),
                        });
                    }
                    Format.AppendTable(sb, new[] { "Symbol", "Kind", "Location" }, rows);
                    // Per-edge payload (XAML binds-path, etc.) trails the table as bulleted sub-lines
                    // for any rows that carry one. Most C# call edges have no payload and produce
                    // nothing here; future edge kinds with metadata surface their detail without
                    // bloating the table cells.
                    foreach (var c in callers)
                    {
                        var payloadLine = Format.PayloadSubLine(c.PayloadJson);
                        if (payloadLine is null) continue;
                        sb.AppendLine($"- **{c.Fqn}**");
                        sb.AppendLine(payloadLine);
                    }
                }
                else
                {
                    foreach (var c in callers)
                    {
                        sb.AppendLine($"- **{c.Fqn}** ({KindLabel(c.Kind)}) at {Format.Location(c.FilePath, c.StartLine, c.StartCol)}");
                        var payloadLine = Format.PayloadSubLine(c.PayloadJson);
                        if (payloadLine is not null) sb.AppendLine(payloadLine);
                    }
                }
                return sb.ToString();
            }, ct));

    [McpServerTool]
    [ToolTrigger("\"what does X call?\" or \"what types does X use?\"")]
    [Description("List outbound edges from the target symbol. Default kind=calls (callees). Use kind=uses-type for types touched in this member's signature/body, kind=overrides-member for the base it overrides, kind=implements-member for the interface member it satisfies, kind=instantiates for types it constructs, kind=throws for exception types it throws, kind=tests for outbound test edges, or any XAML kind (code-behind, binds-path, binds-element, handles-event, uses-resource, instantiates-type, merges, applies-style) on a scope that loaded the XAML indexer; kind=all walks every edge kind. Plugin-defined kinds (any kebab-case identifier) are accepted as-is.")]
    public static Task<string> ListCalleesAsync(
        ScopeRouter router,
        [Description("Source symbol name or FQN")] string symbol,
        [Description("Maximum number of results to return (default 50)")] int limit = 50,
        [Description("Edge kind to walk (kebab-case): calls (default) | uses-type | overrides-member | implements-member | instantiates | throws | tests | code-behind | binds-path | binds-element | handles-event | uses-resource | instantiates-type | merges | applies-style | all. Plugin-defined kinds are accepted.")] string? kind = null,
        [Description(ScopeDescription)] string? scope = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("list_callees", new { symbol, limit, kind, scope }, () =>
            ScopedExecution.RunAsync(router, scope, async host =>
            {
                var (edgeKind, label, isAll) = NormaliseEdgeKindParam(kind);
                if (!isAll && edgeKind is not null)
                {
                    var unknownNote = await CheckUnknownEdgeKindAsync(host.Store, edgeKind, ct).ConfigureAwait(false);
                    if (unknownNote is not null) return unknownNote;
                }

                var hits = await host.Store.FindSymbolsAsync(symbol, filePathHint: null, limit: 5, ct).ConfigureAwait(false);
                if (hits.Count == 0) return $"No matches for '{symbol}'.";
                var top = hits[0];
                var callees = await host.Store.ListCalleesAsync(top.Id, limit, edgeKind, ct).ConfigureAwait(false);
                var sb = new StringBuilder();
                sb.AppendLine($"Outbound `{label}` from **{top.Fqn}** ({KindLabel(top.Kind)}):");
                if (callees.Count == 0) { sb.AppendLine("- (none)"); return sb.ToString(); }
                if (callees.Count >= 2)
                {
                    var rows = new List<IReadOnlyList<string>>(callees.Count);
                    foreach (var c in callees)
                    {
                        rows.Add(new[]
                        {
                            $"**{c.Fqn}**",
                            KindLabel(c.Kind),
                            Format.Location(c.FilePath, c.StartLine, c.StartCol),
                        });
                    }
                    Format.AppendTable(sb, new[] { "Symbol", "Kind", "Location" }, rows);
                    foreach (var c in callees)
                    {
                        var payloadLine = Format.PayloadSubLine(c.PayloadJson);
                        if (payloadLine is null) continue;
                        sb.AppendLine($"- **{c.Fqn}**");
                        sb.AppendLine(payloadLine);
                    }
                }
                else
                {
                    foreach (var c in callees)
                    {
                        sb.AppendLine($"- **{c.Fqn}** ({KindLabel(c.Kind)}) at {Format.Location(c.FilePath, c.StartLine, c.StartCol)}");
                        var payloadLine = Format.PayloadSubLine(c.PayloadJson);
                        if (payloadLine is not null) sb.AppendLine(payloadLine);
                    }
                }
                return sb.ToString();
            }, ct));

    [McpServerTool]
    [ToolTrigger("\"who implements IGreeter.Greet?\"")]
    [Description("Find every concrete member that implements an interface member (uses implements-member edges).")]
    public static Task<string> FindImplementationsAsync(
        ScopeRouter router,
        [Description("Interface member name or FQN, e.g. 'IGreeter.Greet'")] string symbol,
        [Description("Include abstract base members in the result (default false)")] bool includeAbstract = false,
        [Description("Maximum results (default 50)")] int limit = 50,
        [Description(ScopeDescription)] string? scope = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("find_implementations", new { symbol, includeAbstract, limit, scope }, () =>
            ScopedExecution.RunAsync(router, scope, async host =>
            {
                var hits = await host.Store.FindSymbolsAsync(symbol, filePathHint: null, limit: 5, ct).ConfigureAwait(false);
                if (hits.Count == 0) return $"No matches for '{symbol}'.";
                var top = hits[0];
                var impls = await host.Store.ListImplementationsAsync(top.Id, limit, ct).ConfigureAwait(false);
                var filtered = includeAbstract
                    ? impls
                    : impls.Where(h => !(h.Signature?.Contains("abstract", StringComparison.Ordinal) ?? false)).ToList();
                var sb = new StringBuilder();
                sb.AppendLine($"Implementations of **{top.Fqn}** ({KindLabel(top.Kind)}):");
                if (filtered.Count == 0) { sb.AppendLine("- (none)"); return sb.ToString(); }
                if (filtered.Count >= 2)
                {
                    var rows = new List<IReadOnlyList<string>>(filtered.Count);
                    foreach (var c in filtered)
                    {
                        rows.Add(new[]
                        {
                            $"**{c.Fqn}**",
                            KindLabel(c.Kind),
                            Format.Location(c.FilePath, c.StartLine, c.StartCol),
                        });
                    }
                    Format.AppendTable(sb, new[] { "Symbol", "Kind", "Location" }, rows);
                }
                else
                {
                    foreach (var c in filtered)
                    {
                        sb.AppendLine($"- **{c.Fqn}** ({KindLabel(c.Kind)}) at {Format.Location(c.FilePath, c.StartLine, c.StartCol)}");
                    }
                }
                return sb.ToString();
            }, ct));

    [McpServerTool]
    [ToolTrigger("\"I only have a fragment of the name (e.g. 'Calc', 'Greet', 'Async')\"")]
    [Description("Free-text search for symbols by partial name, FQN, or signature using FTS5.")]
    public static Task<string> SearchSymbolsAsync(
        ScopeRouter router,
        [Description("Search query (a few characters, words, or substring of name/FQN/signature)")] string query,
        [Description("Optional kebab-case symbol kind filter: class|method|property|field|interface|namespace|enum|enum-member|operator|record|delegate|struct|event|xaml-view|xaml-element|xaml-resource|xaml-style|xaml-template|... (any plugin-defined kebab-case kind also accepted).")] string? kind = null,
        [Description("Maximum results (default 25)")] int topK = 25,
        [Description(ScopeDescription)] string? scope = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("search_symbols", new { query, kind, topK, scope }, () =>
            ScopedExecution.RunAsync(router, scope, async host =>
            {
                var kindFilter = NormaliseKindFilter(kind);

                var hits = await host.Store.SearchSymbolsAsync(query, kindFilter, topK, ct).ConfigureAwait(false);
                if (hits.Count == 0) return $"No symbols match '{query}'.";
                var sb = new StringBuilder();
                sb.AppendLine($"{hits.Count} hits for '{query}':");
                if (hits.Count >= 2)
                {
                    var rows = new List<IReadOnlyList<string>>(hits.Count);
                    foreach (var h in hits)
                    {
                        rows.Add(new[]
                        {
                            $"**{h.Fqn}**",
                            KindLabel(h.Kind),
                            Format.Location(h.FilePath, h.StartLine, h.StartCol),
                        });
                    }
                    Format.AppendTable(sb, new[] { "Symbol", "Kind", "Location" }, rows);
                }
                else
                {
                    foreach (var h in hits)
                    {
                        sb.AppendLine($"- **{h.Fqn}** ({KindLabel(h.Kind)}) at {Format.Location(h.FilePath, h.StartLine, h.StartCol)}");
                    }
                }
                return sb.ToString();
            }, ct));

    [McpServerTool]
    [ToolTrigger("\"give me a quick overview around X\" — orient before diving in")]
    [Description("Get the immediate graph neighborhood of a symbol: callers, callees, and inheritance/implements edges. Default kind=calls; pass kind=uses-type, overrides-member, implements-member, instantiates, throws, tests, all, any XAML edge kind (code-behind, binds-path, binds-element, handles-event, uses-resource, instantiates-type, merges, applies-style) on a scope that loaded the XAML indexer, or any plugin-defined kebab-case kind to inspect other edge layers.")]
    public static Task<string> NeighborhoodAsync(
        ScopeRouter router,
        [Description("Symbol name or FQN")] string symbol,
        [Description("Max items per category (default 20)")] int perCategory = 20,
        [Description("Edge kind to walk (kebab-case): calls (default) | uses-type | overrides-member | implements-member | instantiates | throws | tests | code-behind | binds-path | binds-element | handles-event | uses-resource | instantiates-type | merges | applies-style | all. Plugin-defined kinds are accepted.")] string? kind = null,
        [Description(ScopeDescription)] string? scope = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("neighborhood", new { symbol, perCategory, kind, scope }, () =>
            ScopedExecution.RunAsync(router, scope, async host =>
            {
                var (edgeKind, label, isAll) = NormaliseEdgeKindParam(kind);
                if (!isAll && edgeKind is not null)
                {
                    var unknownNote = await CheckUnknownEdgeKindAsync(host.Store, edgeKind, ct).ConfigureAwait(false);
                    if (unknownNote is not null) return unknownNote;
                }

                var hits = await host.Store.FindSymbolsAsync(symbol, filePathHint: null, limit: 5, ct).ConfigureAwait(false);
                if (hits.Count == 0) return $"No matches for '{symbol}'.";
                var top = hits[0];
                var callers = await host.Store.ListCallersAsync(top.Id, perCategory, edgeKind, ct).ConfigureAwait(false);
                var callees = await host.Store.ListCalleesAsync(top.Id, perCategory, edgeKind, ct).ConfigureAwait(false);

                var multipleFlavors = await HasMultipleAnnotationFlavorsAsync(host.Store, ct).ConfigureAwait(false);
                var sb = new StringBuilder();
                sb.AppendLine($"Neighborhood of **{top.Fqn}** ({Format.KindWithAttrs(top)}) [kind={label}]");
                sb.AppendLine($"definition: {Format.Location(top.FilePath, top.StartLine, top.StartCol)}");
                var topSummary = Format.OneLineSummary(top.XmlSummary);
                if (!string.IsNullOrEmpty(topSummary)) sb.AppendLine($"_{topSummary}_");
                var topAnns = await host.Store.GetAnnotationsForSymbolAsync(top.Id, ct).ConfigureAwait(false);
                var topAnnLine = AnnotationFormat.OneLine(topAnns, multipleFlavors);
                if (topAnnLine is not null) sb.AppendLine(topAnnLine);
                sb.AppendLine();
                sb.AppendLine($"### Inbound ({callers.Count})");
                await AppendNeighborhoodSectionAsync(sb, callers, host.Store, multipleFlavors, ct).ConfigureAwait(false);
                sb.AppendLine();
                sb.AppendLine($"### Outbound ({callees.Count})");
                await AppendNeighborhoodSectionAsync(sb, callees, host.Store, multipleFlavors, ct).ConfigureAwait(false);
                return sb.ToString();
            }, ct));

    /// <summary>
    /// Render one of <c>neighborhood</c>'s Inbound / Outbound sections. When the row count is
    /// at least two, the rows go into a `| Symbol | Kind | Location |` table — matching
    /// `list_callers` / `list_callees` so the same edge data renders the same way across tools.
    /// Per-row summary + annotation detail follows as bullets so it stays discoverable without
    /// inflating the table cells. Empty / single-row sections retain the bulleted shape.
    /// </summary>
    private static async Task AppendNeighborhoodSectionAsync(
        StringBuilder sb,
        IReadOnlyList<SymbolHit> rows,
        IGraphStore store,
        bool multipleFlavors,
        CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            sb.AppendLine("- (none)");
            return;
        }
        if (rows.Count >= 2)
        {
            var tableRows = new List<IReadOnlyList<string>>(rows.Count);
            foreach (var c in rows)
            {
                tableRows.Add(new[]
                {
                    $"**{c.Fqn}**",
                    Format.KindWithAttrs(c),
                    Format.Location(c.FilePath, c.StartLine, c.StartCol),
                });
            }
            Format.AppendTable(sb, new[] { "Symbol", "Kind", "Location" }, tableRows);
            // Per-row detail (one-line summary + annotations + edge payload) trails the table as
            // a bulleted section so each row's prose-shaped context stays discoverable without
            // bloating the table cells.
            foreach (var c in rows)
            {
                var summary = Format.OneLineSummary(c.XmlSummary);
                var anns = await store.GetAnnotationsForSymbolAsync(c.Id, ct).ConfigureAwait(false);
                var annLine = AnnotationFormat.OneLine(anns, multipleFlavors);
                var payloadLine = Format.PayloadSubLine(c.PayloadJson);
                if (string.IsNullOrEmpty(summary) && annLine is null && payloadLine is null) continue;
                sb.Append($"- **{c.Fqn}**");
                if (!string.IsNullOrEmpty(summary)) sb.Append(" — _" + summary + "_");
                sb.AppendLine();
                if (annLine is not null) sb.AppendLine($"  - {annLine}");
                if (payloadLine is not null) sb.AppendLine(payloadLine);
            }
        }
        else
        {
            foreach (var c in rows)
            {
                sb.Append($"- {c.Fqn} ({Format.KindWithAttrs(c)}) — {Format.Location(c.FilePath, c.StartLine, c.StartCol)}");
                var s = Format.OneLineSummary(c.XmlSummary);
                if (!string.IsNullOrEmpty(s)) sb.Append(" — _" + s + "_");
                sb.AppendLine();
                var ca = await store.GetAnnotationsForSymbolAsync(c.Id, ct).ConfigureAwait(false);
                var caLine = AnnotationFormat.OneLine(ca, multipleFlavors);
                if (caLine is not null) sb.AppendLine($"  {caLine}");
                var payloadLine = Format.PayloadSubLine(c.PayloadJson);
                if (payloadLine is not null) sb.AppendLine(payloadLine);
            }
        }
    }

    [McpServerTool]
    [ToolTrigger("\"what's important in this namespace?\" or \"what's the entrypoint to module Y?\"")]
    [Description("Summarize a namespace or directory: lists the most-referenced symbols (highest in-degree) so you know what to read first. Pass 'Sample.Domain' or a path fragment.")]
    public static Task<string> ModuleSummaryAsync(
        ScopeRouter router,
        [Description("Namespace (e.g. 'Sample.Domain') or path-substring that identifies the module")] string namespaceOrPath,
        [Description("Top-K most-referenced symbols to return (default 25)")] int topK = 25,
        [Description(ScopeDescription)] string? scope = null,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("module_summary", new { namespaceOrPath, topK, scope }, () =>
            ScopedExecution.RunAsync(router, scope, async host =>
            {
                progress?.Report(Format.Progress(0.0, "querying"));
                var rows = await host.Store.ModuleSummaryAsync(namespaceOrPath, topK, ct).ConfigureAwait(false);
                if (rows.Count == 0) return $"No symbols matched module '{namespaceOrPath}'.";
                var multipleFlavors = await HasMultipleAnnotationFlavorsAsync(host.Store, ct).ConfigureAwait(false);
                var sb = new StringBuilder();
                sb.AppendLine($"Top {rows.Count} symbols in '{namespaceOrPath}' (by inbound calls):");
                // module_summary deliberately omits payload sub-lines per harden-sdk-pre-xaml
                // design.md decision (renderer dense by design — top-K rows already carry FQN +
                // kind + location + summary + annotations, plus an in-degree prefix; per-edge
                // payload would push the row past readable). The dedicated edge-walking tools
                // (list_callers, list_callees, neighborhood) surface payload instead.
                if (rows.Count >= 2)
                {
                    var tableRows = new List<IReadOnlyList<string>>(rows.Count);
                    foreach (var row in rows)
                    {
                        tableRows.Add(new[]
                        {
                            row.InDegree.ToString(),
                            $"**{row.Symbol.Fqn}**",
                            Format.KindWithAttrs(row.Symbol),
                            Format.Location(row.Symbol.FilePath, row.Symbol.StartLine, row.Symbol.StartCol),
                        });
                    }
                    Format.AppendTable(
                        sb,
                        new[] { "In-deg", "Symbol", "Kind", "Location" },
                        tableRows,
                        new[] { TableAlignment.Right, TableAlignment.Left, TableAlignment.Left, TableAlignment.Left });
                    // Summary + annotation rendering kept as follow-on bullets so each per-row detail
                    // remains discoverable without flooding the table cells.
                    foreach (var row in rows)
                    {
                        var s = Format.OneLineSummary(row.Symbol.XmlSummary);
                        var anns = await host.Store.GetAnnotationsForSymbolAsync(row.Symbol.Id, ct).ConfigureAwait(false);
                        var annLine = AnnotationFormat.OneLine(anns, multipleFlavors);
                        if (string.IsNullOrEmpty(s) && annLine is null) continue;
                        sb.Append($"- **{row.Symbol.Fqn}**");
                        if (!string.IsNullOrEmpty(s)) sb.Append(" — _" + s + "_");
                        sb.AppendLine();
                        if (annLine is not null) sb.AppendLine($"  - {annLine}");
                    }
                }
                else
                {
                    foreach (var row in rows)
                    {
                        sb.Append($"- in-deg {row.InDegree,3} — **{row.Symbol.Fqn}** ({Format.KindWithAttrs(row.Symbol)}) at {Format.Location(row.Symbol.FilePath, row.Symbol.StartLine, row.Symbol.StartCol)}");
                        var s = Format.OneLineSummary(row.Symbol.XmlSummary);
                        if (!string.IsNullOrEmpty(s)) sb.Append(" — _" + s + "_");
                        sb.AppendLine();
                        var anns = await host.Store.GetAnnotationsForSymbolAsync(row.Symbol.Id, ct).ConfigureAwait(false);
                        var line = AnnotationFormat.OneLine(anns, multipleFlavors);
                        if (line is not null) sb.AppendLine($"  - {line}");
                    }
                }
                return sb.ToString();
            }, ct));

    [McpServerTool]
    [ToolTrigger("\"what would change if I edit X?\" — transitive callers")]
    [Description("Compute the transitive set of upstream callers for a symbol (impact of changing it). Walks the call graph backward up to maxDepth. Default kind=calls; pass kind=uses-type, overrides-member, implements-member, instantiates, throws, tests, all, or any plugin-defined kebab-case kind to traverse other edge layers.")]
    public static Task<string> ImpactOfChangeAsync(
        ScopeRouter router,
        [Description("Symbol name or FQN")] string symbol,
        [Description("Maximum traversal depth (default 4)")] int maxDepth = 4,
        [Description("Maximum results (default 100)")] int limit = 100,
        [Description("Edge kind to walk (kebab-case): calls (default) | uses-type | overrides-member | implements-member | instantiates | throws | tests | code-behind | binds-path | binds-element | handles-event | uses-resource | instantiates-type | merges | applies-style | all. Plugin-defined kinds are accepted.")] string? kind = null,
        [Description(ScopeDescription)] string? scope = null,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("impact_of_change", new { symbol, maxDepth, limit, kind, scope }, () =>
            ScopedExecution.RunAsync(router, scope, async host =>
            {
                var (edgeKind, label, isAll) = NormaliseEdgeKindParam(kind);
                if (!isAll && edgeKind is not null)
                {
                    var unknownNote = await CheckUnknownEdgeKindAsync(host.Store, edgeKind, ct).ConfigureAwait(false);
                    if (unknownNote is not null) return unknownNote;
                }

                var hits = await host.Store.FindSymbolsAsync(symbol, filePathHint: null, limit: 5, ct).ConfigureAwait(false);
                if (hits.Count == 0) return $"No matches for '{symbol}'.";
                var top = hits[0];
                progress?.Report(Format.Progress(0.0, "querying"));
                var rows = await host.Store.ImpactOfChangeAsync(top.Id, maxDepth, limit, edgeKind, ct).ConfigureAwait(false);
                var sb = new StringBuilder();
                sb.AppendLine($"Upstream impact of **{top.Fqn}** ({KindLabel(top.Kind)}) [kind={label}] up to depth {maxDepth}:");
                if (rows.Count == 0) { sb.AppendLine("- (no upstream callers found in graph)"); return sb.ToString(); }
                if (rows.Count >= 2)
                {
                    var tableRows = new List<IReadOnlyList<string>>(rows.Count);
                    foreach (var r in rows)
                    {
                        tableRows.Add(new[]
                        {
                            r.Depth.ToString(),
                            $"**{r.Symbol.Fqn}**",
                            KindLabel(r.Symbol.Kind),
                            Format.Location(r.Symbol.FilePath, r.Symbol.StartLine, r.Symbol.StartCol),
                        });
                    }
                    Format.AppendTable(
                        sb,
                        new[] { "Depth", "Symbol", "Kind", "Location" },
                        tableRows,
                        new[] { TableAlignment.Right, TableAlignment.Left, TableAlignment.Left, TableAlignment.Left });
                }
                else
                {
                    foreach (var r in rows)
                    {
                        sb.AppendLine($"- d{r.Depth}: **{r.Symbol.Fqn}** ({KindLabel(r.Symbol.Kind)}) at {Format.Location(r.Symbol.FilePath, r.Symbol.StartLine, r.Symbol.StartCol)}");
                    }
                }
                return sb.ToString();
            }, ct));

    [McpServerTool]
    [ToolTrigger("\"what members does X have?\" or \"list members of namespace Y\"")]
    [Description("List the direct members of a container (class, struct, interface, namespace) by FQN, optionally filtered by accessibility. Walks the populated container_id chain — replaces 'list_symbols_in_file then filter'.")]
    public static Task<string> ListMembersAsync(
        ScopeRouter router,
        [Description("Container FQN (e.g. 'Sample.Domain.Calculator', 'Sample.Domain'). Resolved with the same matching rules as find_definition; the top match is used.")] string container,
        [Description("Reserved for a future change that follows inherits/implements edges; currently ignored.")] bool includeInherited = false,
        [Description("Optional accessibility filter: public|internal|private|protected|protected internal|private protected.")] string? accessibility = null,
        [Description("Maximum members to return (default 200)")] int limit = 200,
        [Description(ScopeDescription)] string? scope = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("list_members", new { container, includeInherited, accessibility, limit, scope }, () =>
            ScopedExecution.RunAsync(router, scope, async host =>
            {
                var hits = await host.Store.FindSymbolsAsync(container, filePathHint: null, limit: 5, ct).ConfigureAwait(false);
                if (hits.Count == 0) return $"No matches for container '{container}'.";
                var top = hits[0];

                int? accFilter = ParseAccessibility(accessibility);
                if (!string.IsNullOrEmpty(accessibility) && accFilter is null)
                {
                    return $"Unknown accessibility '{accessibility}'. Valid: public, internal, private, protected, protected internal, private protected.";
                }

                var members = await host.Store.ListMembersAsync(top.Id, accFilter, limit, ct).ConfigureAwait(false);
                var sb = new StringBuilder();
                var filterNote = accFilter is null ? "" : $" (accessibility = {accessibility})";
                sb.AppendLine($"{members.Count} members of **{top.Fqn}** ({Format.KindWithAttrs(top)}){filterNote}:");
                if (includeInherited)
                {
                    sb.AppendLine("_(includeInherited is reserved for a future change; only direct members are returned.)_");
                }
                if (members.Count == 0) { sb.AppendLine("- (none)"); return sb.ToString(); }
                if (members.Count >= 2)
                {
                    var rows = new List<IReadOnlyList<string>>(members.Count);
                    foreach (var m in members)
                    {
                        rows.Add(new[]
                        {
                            $"L{m.StartLine}: **{m.Name}**",
                            Format.KindWithAttrs(m),
                            $"`{m.Signature ?? m.Fqn}`",
                        });
                    }
                    Format.AppendTable(sb, new[] { "Member", "Kind", "Signature" }, rows);
                }
                else
                {
                    foreach (var m in members)
                    {
                        sb.Append($"- L{m.StartLine}: **{m.Name}** ({Format.KindWithAttrs(m)}) — `{m.Signature ?? m.Fqn}`");
                        var s = Format.OneLineSummary(m.XmlSummary);
                        if (!string.IsNullOrEmpty(s)) sb.Append(" — _" + s + "_");
                        sb.AppendLine();
                    }
                }
                return sb.ToString();
            }, ct));

    [McpServerTool]
    [ToolTrigger("\"find code that does retry logic\", \"how does this codebase handle authentication\", \"show me the rate-limiting code\"")]
    [Description("Semantic / intent search: encode a natural-language query, find symbols whose code embeddings are nearest by cosine similarity. Complements (not replaces) search_symbols, which does name-fragment FTS5 trigram matching. Returns a top-k list with location, kind, and a similarity score in [-1, 1].")]
    public static Task<string> SemanticSearchAsync(
        ScopeRouter router,
        ICodeEmbeddingGenerator generator,
        [Description("Free-text intent query, e.g. 'retry on transient errors', 'masks PII in logs'")] string query,
        [Description("Top-K results to return (default 20)")] int k = 20,
        [Description("Optional kebab-case symbol kind filter: class|method|property|field|interface|namespace|enum|enum-member|operator|record|delegate|struct|event|xaml-view|xaml-element|xaml-resource|xaml-style|xaml-template|... (any plugin-defined kebab-case kind also accepted).")] string? kind = null,
        [Description(ScopeDescription)] string? scope = null,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("semantic_search", new { query, k, kind, scope }, () =>
            ScopedExecution.RunAsync(router, scope, async host =>
            {
                if (!host.EmbeddingsStore.IsAvailable || !generator.IsAvailable)
                {
                    return "semantic_search disabled: install the embedding model (run with the network on for first start) or remove `--no-embeddings`. The graph itself is fully indexed; other tools (`find_definition`, `search_symbols`, `list_callers`, …) work as normal.";
                }
                if (string.IsNullOrWhiteSpace(query))
                {
                    return "semantic_search: provide a non-empty query.";
                }

                var kindFilter = NormaliseKindFilter(kind);

                // Cold-start: the JinaCodeEmbeddingGenerator singleton is lazy-instantiated by DI
                // on first use, so the first `EmbedAsync` call after server start carries the ONNX
                // model load (3-5s). Subsequent calls reuse the loaded model and are sub-second.
                progress?.Report(Format.Progress(0.0, "encoding query"));
                var queryEmbeddings = await generator.EmbedAsync(new[] { query }, ct).ConfigureAwait(false);
                if (queryEmbeddings.Count == 0)
                {
                    return "semantic_search: encoder produced no vector for the query.";
                }

                progress?.Report(Format.Progress(0.5, "searching"));
                var hits = await host.EmbeddingsStore.SearchAsync(queryEmbeddings[0], k, kindFilter, ct).ConfigureAwait(false);
                if (hits.Count == 0)
                {
                    return $"No semantic matches for '{query}'. The graph may not have any embeddings yet — let the indexer's embedding pass complete after a fresh `index` and try again.";
                }

                progress?.Report(Format.Progress(0.9, "formatting results"));
                var sb = new StringBuilder();
                sb.AppendLine($"{hits.Count} semantic hits for '{query}':");
                if (hits.Count >= 2)
                {
                    var rows = new List<IReadOnlyList<string>>(hits.Count);
                    foreach (var h in hits)
                    {
                        var sym = await host.Store.GetSymbolByIdAsync(h.SymbolId, ct).ConfigureAwait(false);
                        if (sym is null) continue;
                        rows.Add(new[]
                        {
                            h.Score.ToString("F3"),
                            $"**{sym.Fqn}**",
                            Format.KindWithAttrs(sym),
                            Format.Location(sym.FilePath, sym.StartLine, sym.StartCol),
                        });
                    }
                    Format.AppendTable(
                        sb,
                        new[] { "Score", "Symbol", "Kind", "Location" },
                        rows,
                        new[] { TableAlignment.Right, TableAlignment.Left, TableAlignment.Left, TableAlignment.Left });
                }
                else
                {
                    foreach (var h in hits)
                    {
                        var sym = await host.Store.GetSymbolByIdAsync(h.SymbolId, ct).ConfigureAwait(false);
                        if (sym is null) continue;
                        sb.Append($"- score {h.Score:F3} — **{sym.Fqn}** ({Format.KindWithAttrs(sym)}) at {Format.Location(sym.FilePath, sym.StartLine, sym.StartCol)}");
                        var s = Format.OneLineSummary(sym.XmlSummary);
                        if (!string.IsNullOrEmpty(s)) sb.Append(" — _" + s + "_");
                        sb.AppendLine();
                    }
                }
                return sb.ToString();
            }, ct));

    [McpServerTool]
    [Description("Print summary counts (files, symbols, references, edges) for the current graph database. Use to confirm the index is populated.")]
    public static Task<string> GraphStatsAsync(
        ScopeRouter router,
        [Description(ScopeDescription)] string? scope = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("graph_stats", new { scope }, () =>
            ScopedExecution.RunAsync(router, scope, async host =>
            {
                var s = await host.Store.GetStatsAsync(ct).ConfigureAwait(false);
                return $"files={s.FileCount} symbols={s.SymbolCount} references={s.ReferenceCount} edges={s.EdgeCount}";
            }, ct));

    [McpServerTool]
    [ToolTrigger("\"what does this codebase warn about?\" or \"is X being warned on?\"")]
    [Description("Find Roslyn diagnostics (analyzer warnings, compiler errors, etc.) captured during indexing. Filter by severity (default 'warning' = severity >= 2), diagnostic code (e.g. 'CS0618'), and/or symbol.")]
    public static Task<string> FindDiagnosticsAsync(
        ScopeRouter router,
        [Description("Severity floor: hidden | info | warning (default) | error | all. Numeric values 0-3 also accepted.")] string? severity = "warning",
        [Description("Optional diagnostic code filter, e.g. 'CS0618' for [Obsolete] usage")] string? code = null,
        [Description("Optional symbol name/FQN to scope the lookup to a single symbol's diagnostics")] string? symbol = null,
        [Description("Maximum rows to return (default 100)")] int limit = 100,
        [Description(ScopeDescription)] string? scope = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("find_diagnostics", new { severity, code, symbol, limit, scope }, () =>
            ScopedExecution.RunAsync(router, scope, async host =>
            {
                var sev = ParseSeverity(severity);
                if (sev == -1)
                {
                    return $"Unknown severity '{severity}'. Expected one of: hidden | info | warning | error | all.";
                }

                long? symbolId = null;
                string? symbolFqn = null;
                if (!string.IsNullOrWhiteSpace(symbol))
                {
                    var hits = await host.Store.FindSymbolsAsync(symbol, filePathHint: null, limit: 5, ct).ConfigureAwait(false);
                    if (hits.Count == 0) return $"No matches for '{symbol}'.";
                    symbolId = hits[0].Id;
                    symbolFqn = hits[0].Fqn;
                }

                var rows = await host.Store.FindDiagnosticsAsync(sev, code, symbolId, limit, ct).ConfigureAwait(false);
                var sb = new StringBuilder();
                var sevLabel = sev is null ? "all" : $">= {SeverityLabel(sev.Value)}";
                var codeClause = string.IsNullOrEmpty(code) ? "" : $", code={code}";
                var symClause = symbolFqn is null ? "" : $", symbol={symbolFqn}";
                sb.AppendLine($"Diagnostics (severity {sevLabel}{codeClause}{symClause}): {rows.Count}");
                if (rows.Count == 0) return sb.ToString();
                if (rows.Count >= 2)
                {
                    var tableRows = new List<IReadOnlyList<string>>(rows.Count);
                    foreach (var d in rows)
                    {
                        tableRows.Add(new[]
                        {
                            SeverityLabel(d.Severity),
                            d.Code,
                            Format.Location(d.FilePath, d.Line, d.Col),
                            d.Message,
                        });
                    }
                    Format.AppendTable(sb, new[] { "Severity", "Code", "Location", "Message" }, tableRows);
                }
                else
                {
                    foreach (var d in rows)
                    {
                        sb.AppendLine($"- **{SeverityLabel(d.Severity)} {d.Code}** at {Format.Location(d.FilePath, d.Line, d.Col)} — {d.Message}");
                    }
                }
                return sb.ToString();
            }, ct));

    [McpServerTool]
    [ToolTrigger("\"what's source-generated in this codebase?\"")]
    [Description("List every source-generated file (Roslyn IIncrementalGenerator output: regex source-gen, MVVM Toolkit, ASP.NET routing, JSON source-gen, etc.) tracked by the index. Each row shows the path and the count of symbols emitted from that file.")]
    public static Task<string> ListGeneratedFilesAsync(
        ScopeRouter router,
        [Description("Maximum rows (default 100)")] int limit = 100,
        [Description(ScopeDescription)] string? scope = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("list_generated_files", new { limit, scope }, () =>
            ScopedExecution.RunAsync(router, scope, async host =>
            {
                var rows = await host.Store.ListGeneratedFilesAsync(limit, ct).ConfigureAwait(false);
                var sb = new StringBuilder();
                sb.AppendLine($"Generated files: {rows.Count}");
                if (rows.Count == 0)
                {
                    sb.AppendLine("_(no source-generated documents in this solution)_");
                    return sb.ToString();
                }
                sb.AppendLine();
                sb.AppendLine("| Symbols | Path |");
                sb.AppendLine("|--------:|------|");
                foreach (var r in rows)
                {
                    sb.AppendLine($"| {r.SymbolCount} | `{r.FilePath}` |");
                }
                return sb.ToString();
            }, ct));

    [McpServerTool]
    [Description("Show how often each MCP tool was called this server-process session: count, errors, avg/max latency, avg response size, last-called time. Use this to verify the agent is actually using the source-graph tools (vs grep+read fallback). Persistent log of every call is at usage.jsonl next to graph.db.")]
    public static string UsageStats() =>
        ToolMetrics.TrackSync("usage_stats", null, () =>
        {
            var snap = ToolMetrics.Snapshot();
            var sb = new StringBuilder();
            var uptime = DateTimeOffset.UtcNow - ToolMetrics.ProcessStart;
            sb.AppendLine($"sourcegraph-mcp uptime: {uptime:hh\\:mm\\:ss} (since {ToolMetrics.ProcessStart:HH:mm:ss UTC})");
            if (snap.Count == 0)
            {
                sb.AppendLine();
                sb.AppendLine("No tool calls recorded yet.");
                return sb.ToString();
            }
            sb.AppendLine();
            sb.AppendLine("| Tool | Calls | Errors | Avg ms | Max ms | Avg resp | Last |");
            sb.AppendLine("|------|------:|-------:|-------:|-------:|---------:|------|");
            foreach (var (name, s) in snap.OrderByDescending(kv => kv.Value.Count))
            {
                var lastAgo = (DateTimeOffset.UtcNow - s.LastCalledAt).TotalSeconds;
                sb.AppendLine($"| `{name}` | {s.Count} | {s.Errors} | {s.AvgMs:F1} | {s.MaxMs} | {(int)s.AvgResponseLen}B | {lastAgo:F0}s ago |");
            }

            // Per-scope breakdown: surface which scopes account for the lion's share of tool calls
            // so the agent can see where the index is actually being exercised.
            var scopeSnap = ToolMetrics.ScopeSnapshot();
            if (scopeSnap.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("| Scope | Calls |");
                sb.AppendLine("|-------|------:|");
                foreach (var (scopeName, count) in scopeSnap.OrderByDescending(kv => kv.Value))
                {
                    sb.AppendLine($"| `{scopeName}` | {count} |");
                }
            }

            if (!string.IsNullOrEmpty(ToolMetrics.LogPath))
            {
                sb.AppendLine();
                sb.AppendLine($"_Persistent JSONL log: `{ToolMetrics.LogPath}`_");
            }
            return sb.ToString();
        });

    /// <summary>
    /// Render a kebab-case symbol kind (or any plugin-defined kind) as a human-friendly label.
    /// We map well-known C# kinds to the same legacy spellings the v0.4 enum produced; unknown
    /// kebab-case kinds pass through as-is so plugin-defined kinds (e.g. <c>"vue-component"</c>)
    /// surface unmodified in tool output.
    /// </summary>
    private static string KindLabel(string kind) => kind switch
    {
        SymbolKinds.Namespace => "namespace",
        SymbolKinds.Class => "class",
        SymbolKinds.Struct => "struct",
        SymbolKinds.Interface => "interface",
        SymbolKinds.Enum => "enum",
        SymbolKinds.EnumMember => "enum member",
        SymbolKinds.Delegate => "delegate",
        SymbolKinds.Method => "method",
        SymbolKinds.Constructor => "ctor",
        SymbolKinds.Property => "property",
        SymbolKinds.Field => "field",
        SymbolKinds.Event => "event",
        SymbolKinds.Local => "local",
        SymbolKinds.Parameter => "parameter",
        SymbolKinds.TypeParameter => "type parameter",
        SymbolKinds.Operator => "operator",
        SymbolKinds.Record => "record",
        _ => string.IsNullOrEmpty(kind) ? "?" : kind,
    };

    private static string RefKindLabel(ReferenceKind kind) => kind switch
    {
        ReferenceKind.Definition => "def",
        ReferenceKind.Reference => "ref",
        ReferenceKind.Call => "call",
        ReferenceKind.Implements => "impl",
        ReferenceKind.Inherits => "inherit",
        ReferenceKind.Read => "read",
        ReferenceKind.Write => "write",
        _ => "?",
    };

    private static int? ParseAccessibility(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return raw.Trim().ToLowerInvariant() switch
        {
            "public" => 6,
            "protected internal" or "protected_or_internal" => 5,
            "internal" => 4,
            "protected" => 3,
            "private protected" or "protected_and_internal" => 2,
            "private" => 1,
            _ => null,
        };
    }

    /// <summary>Render " (generated)" when the symbol or reference came from a source-generated
    /// file, "" otherwise. Use it to suffix file:line locations rendered with
    /// <see cref="Format.Location"/>.</summary>
    private static string GeneratedSuffix(bool isGenerated) => isGenerated ? " (generated)" : "";

    /// <summary>
    /// Map a textual severity token to the corresponding <c>Microsoft.CodeAnalysis.DiagnosticSeverity</c>
    /// integer, with the convention of "warning" being the default. Returns the int value (Hidden=0,
    /// Info=1, Warning=2, Error=3) or <c>null</c> when the input token is unknown.
    /// </summary>
    private static int? ParseSeverity(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 2; // default warning
        return raw.Trim().ToLowerInvariant() switch
        {
            "hidden" or "0" => 0,
            "info" or "information" or "1" => 1,
            "warning" or "warn" or "2" => 2,
            "error" or "err" or "3" => 3,
            "all" or "any" => null,
            _ => -1, // sentinel for "unknown"
        };
    }

    private static string SeverityLabel(int severity) => severity switch
    {
        0 => "hidden",
        1 => "info",
        2 => "warning",
        3 => "error",
        _ => "?",
    };

    internal static string KindLabelOf(string kind) => KindLabel(kind);

    /// <summary>
    /// Normalise the user-supplied kebab-case kind filter for storage queries. Empty or whitespace
    /// becomes <c>null</c> ("all kinds"); other values are lowercased and trimmed but otherwise
    /// passed through verbatim — so plugin-defined kinds work without a host-side allow-list. The
    /// underscore-flavored aliases ("uses_type", "implements_member", …) are tolerated as a thin
    /// migration helper for clients pinned to v0.6 syntax.
    /// </summary>
    private static string? NormaliseKindFilter(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim().ToLowerInvariant();
        // Translate underscores → hyphens for legacy callers (matches the kebab-case convention).
        return trimmed.Replace('_', '-');
    }

    /// <summary>
    /// Parse the optional <c>kind</c> parameter accepted by list_callers/list_callees/neighborhood/impact_of_change.
    /// Returns <c>(edgeKind, label, isAll)</c>. <c>isAll = true</c> means the user asked for every
    /// kind (translated to a null edgeKind for the storage call). Any other input is passed through
    /// as kebab-case so plugin-defined edge kinds work without a host-side allow-list. Underscore
    /// aliases are tolerated.
    /// </summary>
    private static (string? edgeKind, string label, bool isAll) NormaliseEdgeKindParam(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return (EdgeKinds.Calls, EdgeKinds.Calls, false);
        var normalised = raw.Trim().ToLowerInvariant().Replace('_', '-');
        if (normalised is "all" or "any" or "*") return (null, "all", true);
        // Compatibility shims for the v0.6 spelling: "overrides" → "overrides-member",
        // "implements" → "implements-member" (the unqualified form was the old tool-arg shorthand).
        if (normalised == "overrides") normalised = EdgeKinds.OverridesMember;
        if (normalised == "implements") normalised = EdgeKinds.ImplementsMember;
        return (normalised, normalised, false);
    }

    /// <summary>
    /// Check whether an edge kind is in the active scope's published <c>edge_kinds</c> vocabulary
    /// — the union of the SDK's well-known constants (which the indexer is configured to emit
    /// regardless of whether storage has any rows yet) with the distinct kinds already present in
    /// storage. Returning <c>null</c> means "kind is valid, proceed with the query"; a non-null
    /// string is a one-line note pointing at the active vocabulary so the agent can pick a real
    /// one. SDK constants in the union mean a fresh / never-indexed scope still accepts a
    /// built-in kind name like <c>"calls"</c> instead of false-flagging it as "unknown".
    /// </summary>
    private static async Task<string?> CheckUnknownEdgeKindAsync(IGraphStore store, string edgeKind, CancellationToken ct)
    {
        if (ServerVocabulary.SdkEdgeKinds.Contains(edgeKind)) return null;
        var stored = await store.GetDistinctEdgeKindsAsync(ct).ConfigureAwait(false);
        if (stored.Contains(edgeKind, StringComparer.Ordinal)) return null;
        // Be lenient when no edges are stored AND the kind isn't a built-in (graph is empty /
        // not indexed yet, plugin-defined kind we can't disprove) — let the storage call run;
        // it'll return zero rows.
        if (stored.Count == 0) return null;
        var union = ServerVocabulary.SdkEdgeKinds
            .Concat(stored)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal);
        var avail = string.Join(", ", union);
        return $"Edge kind `{edgeKind}` isn't in this scope's published `edge_kinds` vocabulary. Available: [{avail}]. Use `kind=all` to walk every kind.";
    }

    /// <summary>
    /// Fast probe used by the renderers: when a scope has more than one annotation flavor present
    /// (e.g. csharp-attribute + ts-decorator in a polyglot repo), every annotation line gets a
    /// <c>(flavor)</c> suffix so the agent can tell them apart. With one flavor we omit the
    /// suffix to keep output dense. Calling this per-render is cheap — the underlying query is a
    /// distinct over a small column.
    /// </summary>
    private static async Task<bool> HasMultipleAnnotationFlavorsAsync(IGraphStore store, CancellationToken ct)
    {
        var flavors = await store.GetDistinctAnnotationFlavorsAsync(ct).ConfigureAwait(false);
        return flavors.Count > 1;
    }
}

internal static class Format
{
    public static string Location(string path, int line, int col) => $"{path}:{line}:{col}";

    /// <summary>Joins kind, accessibility, and modifiers into a compact parenthetical such as
    /// "public class", "private readonly field", "method", "public async method". Appends
    /// "(generated)" when the symbol's file is marked is_generated = 1, so agents can tell
    /// hand-written code apart from source-generator output at a glance.</summary>
    public static string KindWithAttrs(SymbolHit h)
    {
        var sb = new StringBuilder();
        var acc = AccessibilityLabel(h.Accessibility);
        if (!string.IsNullOrEmpty(acc)) sb.Append(acc);
        if (!string.IsNullOrEmpty(h.Modifiers))
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(h.Modifiers.Replace(',', ' '));
        }
        if (sb.Length > 0) sb.Append(' ');
        sb.Append(GraphTools.KindLabelOf(h.Kind));
        if (h.IsGenerated) sb.Append(" (generated)");
        return sb.ToString();
    }

    /// <summary>First sentence of an XML doc summary, capped to ~120 chars. Single-line; trailing
    /// whitespace and the period are preserved up to the first '. ' or newline boundary.</summary>
    public static string? OneLineSummary(string? xmlSummary)
    {
        if (string.IsNullOrWhiteSpace(xmlSummary)) return null;
        var s = xmlSummary.Replace('\r', ' ').Replace('\n', ' ').Trim();
        var endIdx = s.IndexOf(". ", StringComparison.Ordinal);
        if (endIdx >= 0) s = s[..(endIdx + 1)];
        const int maxLen = 120;
        if (s.Length > maxLen) s = s[..maxLen].TrimEnd() + "…";
        return s;
    }

    public static string AccessibilityLabel(int accessibility) => accessibility switch
    {
        6 => "public",
        5 => "protected internal",
        4 => "internal",
        3 => "protected",
        2 => "private protected",
        1 => "private",
        _ => "",
    };

    /// <summary>
    /// Render a single-line history annotation: <c>last touched 2026-04-12 by jacques (a1b2c3d)</c>.
    /// Returns <c>null</c> when <paramref name="history"/> is null or has no commit data so callers
    /// can skip the line entirely.
    /// </summary>
    public static string? HistoryLine(SymbolHistory? history)
    {
        if (history is null) return null;
        if (string.IsNullOrEmpty(history.LastCommitSha) && history.LastAuthor is null) return null;
        var sha = history.LastCommitSha is { Length: > 0 } s ? s[..Math.Min(7, s.Length)] : "(none)";
        var author = history.LastAuthor ?? "(unknown)";
        var time = history.LastAuthoredAt is { } t ? t.ToString("yyyy-MM-dd") : "?";
        return $"last touched {time} by {author} ({sha})";
    }

    /// <summary>
    /// Append a GitHub-Flavored-Markdown (GFM) table to <paramref name="sb"/>: a header row, an
    /// alignment-cued separator row, and one data row per entry in <paramref name="rows"/>.
    /// Cells are pipe-escaped (<c>|</c> → <c>\|</c>) so literal pipes in symbols / paths don't
    /// split table cells in the consuming client. Throws when any row's column count differs from
    /// the header's. Used by tools that emit list-shaped results once their row count reaches the
    /// table threshold (>= 2).
    /// </summary>
    public static void AppendTable(
        StringBuilder sb,
        IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<string>> rows,
        IReadOnlyList<TableAlignment>? alignments = null)
    {
        if (headers.Count == 0) throw new ArgumentException("Table requires at least one column.", nameof(headers));
        if (alignments is not null && alignments.Count != headers.Count)
        {
            throw new ArgumentException(
                $"Alignments length ({alignments.Count}) must match headers length ({headers.Count}).",
                nameof(alignments));
        }

        // Header row.
        sb.Append('|');
        foreach (var h in headers)
        {
            sb.Append(' ');
            sb.Append(EscapeCell(h));
            sb.Append(" |");
        }
        sb.AppendLine();

        // Separator row with optional alignment cues.
        sb.Append('|');
        for (var i = 0; i < headers.Count; i++)
        {
            var align = alignments is null ? TableAlignment.Left : alignments[i];
            sb.Append(align switch
            {
                TableAlignment.Right => "---:",
                TableAlignment.Center => ":---:",
                _ => "---",
            });
            sb.Append('|');
        }
        sb.AppendLine();

        // Data rows.
        foreach (var row in rows)
        {
            if (row.Count != headers.Count)
            {
                throw new ArgumentException(
                    $"Row column count ({row.Count}) must match headers length ({headers.Count}).",
                    nameof(rows));
            }
            sb.Append('|');
            foreach (var cell in row)
            {
                sb.Append(' ');
                sb.Append(EscapeCell(cell));
                sb.Append(" |");
            }
            sb.AppendLine();
        }
    }

    /// <summary>Escape a literal <c>|</c> in cell content so it doesn't break GFM table parsing.</summary>
    private static string EscapeCell(string s) =>
        string.IsNullOrEmpty(s) ? string.Empty : s.Replace("|", "\\|", StringComparison.Ordinal);

    /// <summary>
    /// Build a <see cref="ProgressNotificationValue"/> for emission via an injected
    /// <see cref="IProgress{T}"/>. Centralises the notification shape so every tool's checkpoints
    /// share the same contract: <c>Total = 1.0</c>, <paramref name="fraction"/> in <c>[0.0, 1.0]</c>,
    /// and a short imperative <paramref name="message"/> with no caller-supplied substrings (avoids
    /// PII echo back to the chat UI).
    /// </summary>
    public static ProgressNotificationValue Progress(double fraction, string message) =>
        new() { Progress = (float)fraction, Total = 1f, Message = message };

    /// <summary>
    /// Render an indented <c>    payload: { key: "value", ... }</c> sub-line for an edge whose
    /// originating <c>edges.payload</c> column was non-null. Returns <c>null</c> when
    /// <paramref name="payloadJson"/> is null, empty, blank, or fails to deserialise as a JSON
    /// object (defensive: storage stores opaque JSON, so a malformed string never crashes the
    /// renderer — it just gets dropped). Caps the output to <see cref="PayloadKeyLimit"/> keys;
    /// when more keys are present, appends <c> (N more)</c> at the end so the agent knows the
    /// rendered slice is partial. String values are rendered with surrounding double quotes;
    /// non-string JSON values (numbers, booleans, nulls, objects, arrays) round-trip through
    /// <see cref="JsonElement.GetRawText"/>.
    /// </summary>
    public static string? PayloadSubLine(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        Dictionary<string, JsonElement>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payloadJson);
        }
        catch (JsonException)
        {
            // Defensive: payload column is opaque JSON; any non-object shape (array literal, bare
            // string, etc.) gets dropped so a malformed row never breaks tool output.
            return null;
        }
        if (parsed is null || parsed.Count == 0) return null;

        var sb = new StringBuilder();
        sb.Append("    payload: { ");
        var rendered = 0;
        foreach (var kv in parsed)
        {
            if (rendered >= PayloadKeyLimit) break;
            if (rendered > 0) sb.Append(", ");
            sb.Append(kv.Key);
            sb.Append(": ");
            // Use GetRawText() for every JSON value kind: for strings it returns the JSON-encoded
            // form (already wrapped in double quotes, with embedded quotes / backslashes / control
            // characters escaped per RFC 8259), so a binding path containing a quote or newline
            // can't break the markdown line. Numbers, bools, nulls, and nested object/array shapes
            // also round-trip verbatim through GetRawText.
            sb.Append(kv.Value.GetRawText());
            rendered++;
        }
        if (parsed.Count > PayloadKeyLimit)
        {
            sb.Append(" (");
            sb.Append(parsed.Count - PayloadKeyLimit);
            sb.Append(" more)");
        }
        sb.Append(" }");
        return sb.ToString();
    }

    private const int PayloadKeyLimit = 5;
}

/// <summary>
/// Column alignment cue for <see cref="Format.AppendTable"/>. <see cref="Left"/> emits
/// <c>---</c> (the GFM default), <see cref="Right"/> emits <c>---:</c> (used by numeric columns
/// like <c>In-deg</c>, <c>Depth</c>, <c>Score</c>), and <see cref="Center"/> emits <c>:---:</c>.
/// </summary>
public enum TableAlignment
{
    Left,
    Right,
    Center,
}

internal static class AnnotationFormat
{
    /// <summary>
    /// Render the annotation set as a single line, e.g.
    /// <c>annotations: [HttpGet("/api/users"), Authorize, Obsolete]</c>. When the active scope
    /// has more than one annotation flavor (e.g. csharp-attribute + ts-decorator), each entry is
    /// suffixed with its flavor in parentheses so the agent can tell them apart. Returns
    /// <c>null</c> when there are no annotations so callers can skip the line entirely.
    /// </summary>
    public static string? OneLine(IReadOnlyList<AnnotationRecord> annotations, bool includeFlavor)
    {
        if (annotations.Count == 0) return null;
        var sb = new StringBuilder();
        sb.Append("annotations: [");
        for (var i = 0; i < annotations.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(annotations[i].Name);
            var preview = ArgPreview(annotations[i].ArgsJson);
            if (preview is not null) sb.Append(preview);
            if (includeFlavor && !string.IsNullOrEmpty(annotations[i].Flavor))
            {
                sb.Append(" (");
                sb.Append(annotations[i].Flavor);
                sb.Append(')');
            }
        }
        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>
    /// Render an annotation card row for the <c>graph://symbol/{id}</c> resource.
    /// Includes the full name and the raw args JSON so an agent can pick out the
    /// values it needs.
    /// </summary>
    public static string Card(AnnotationRecord ann)
    {
        var args = string.IsNullOrEmpty(ann.ArgsJson) ? "" : $" — `{ann.ArgsJson}`";
        var link = ann.AttributeSymbolId is null ? "" : $" → symbol#{ann.AttributeSymbolId}";
        var flavor = string.IsNullOrEmpty(ann.Flavor) ? "" : $" [{ann.Flavor}]";
        return $"- `[{ann.Name}]` ({ann.FullName}){flavor}{link}{args}";
    }

    private static string? ArgPreview(string? argsJson)
    {
        if (string.IsNullOrEmpty(argsJson)) return null;
        // Truncate noisy payloads to keep the annotation single-line. The full payload is
        // always available on the symbol resource.
        const int maxLen = 64;
        return argsJson.Length <= maxLen ? $"({argsJson})" : $"({argsJson[..maxLen]}…)";
    }
}
