using System.ComponentModel;
using System.Text;
using System.Text.Json;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Embeddings;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Sdk.Validation;
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

    // Diagnostic short-circuits route through DevBitsLab.Mcp.SourceGraph.Server.Tools.Output.DiagnosticResult.Build
    // so the same wire shape is shared with HistoryTools, ScopeTools, and the multi-host fan-out
    // path in ScopedExecution. Use it for symbol-not-found, unknown-flag, and disabled-subsystem
    // short-circuits — anywhere a tool body returning Task<CallToolResult> needs to ship plain
    // prose without a structured payload while still feeding the leaf chokepoint.

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
        // noise to the human reading the chat. Routed through AudienceMetadata so every converted
        // tool emits the same wire shape and Audience/Priority annotations.
        content.Add(AudienceMetadata.Build(
            scopeId,
            elapsedMs,
            ("hits", hits.Count.ToString())));

        var result = new CallToolResult
        {
            Content = content,
            StructuredContent = JsonSerializer.SerializeToElement(
                new FindDefinitionResult(structuredHits),
                ToolOutputJsonContext.Default.FindDefinitionResult),
        };
        return result;
    }

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(FindByAnnotationResult))]
    [ToolTrigger("\"find every POST endpoint\", \"what's been deprecated?\", \"find all DI singletons\", \"every controller decorated with @Component\"")]
    [Description("Find every symbol that carries an annotation by short name (e.g. 'HttpGet', 'Obsolete', 'Authorize', 'Component'). Annotations span .NET attributes, TS decorators, Vue directives, etc. — `flavor` filters to one annotation pattern (e.g. 'csharp-attribute'), null matches across all flavors. Optional argValue does a trigram match against the annotation's serialised arguments (e.g. argValue='/api/v2' to find route attributes whose path contains that substring).")]
    public static Task<CallToolResult> FindByAnnotationAsync(
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
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var kindFilter = NormaliseKindFilter(kind);
                var hits = await host.Store.FindByAnnotationAsync(name, flavor, argValue, kindFilter, limit, ct).ConfigureAwait(false);
                if (hits.Count == 0)
                {
                    var argsClause = string.IsNullOrEmpty(argValue) ? "" : $" with argValue ~ '{argValue}'";
                    var flavorClause = string.IsNullOrEmpty(flavor) ? "" : $" (flavor='{flavor}')";
                    return BuildFindByAnnotationResult(
                        prose: $"No symbols carry [{name}]{flavorClause}{argsClause}.",
                        hits: Array.Empty<SymbolHit>(),
                        annotationsBySymbolId: new Dictionary<long, IReadOnlyList<AnnotationRecord>>(),
                        scopeId: host.Scope.Id,
                        elapsedMs: sw.ElapsedMilliseconds);
                }
                var multipleFlavors = await HasMultipleAnnotationFlavorsAsync(host.Store, ct).ConfigureAwait(false);
                var sb = new StringBuilder();
                sb.AppendLine($"{hits.Count} symbols carry [{name}]:");
                var annotationsBySymbolId = new Dictionary<long, IReadOnlyList<AnnotationRecord>>(hits.Count);
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
                        annotationsBySymbolId[h.Id] = anns;
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
                        annotationsBySymbolId[h.Id] = anns;
                        var line = AnnotationFormat.OneLine(anns, multipleFlavors);
                        if (line is not null) sb.AppendLine($"  - {line}");
                    }
                }
                return BuildFindByAnnotationResult(
                    prose: sb.ToString(),
                    hits: hits,
                    annotationsBySymbolId: annotationsBySymbolId,
                    scopeId: host.Scope.Id,
                    elapsedMs: sw.ElapsedMilliseconds);
            }, ct));

    private static CallToolResult BuildFindByAnnotationResult(
        string prose,
        IReadOnlyList<SymbolHit> hits,
        IReadOnlyDictionary<long, IReadOnlyList<AnnotationRecord>> annotationsBySymbolId,
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

        content.Add(AudienceMetadata.Build(
            scopeId,
            elapsedMs,
            ("hits", hits.Count.ToString())));

        var structuredHits = hits
            .Select(h =>
            {
                var anns = annotationsBySymbolId.TryGetValue(h.Id, out var a)
                    ? a
                    : (IReadOnlyList<AnnotationRecord>)Array.Empty<AnnotationRecord>();
                var entries = anns
                    .Select(a => new FindByAnnotationEntry(
                        Name: a.Name,
                        FullName: a.FullName,
                        Flavor: a.Flavor,
                        ArgsJson: string.IsNullOrEmpty(a.ArgsJson) ? null : a.ArgsJson))
                    .ToList();
                return new FindByAnnotationHit(
                    SymbolId: h.Id,
                    Fqn: h.Fqn,
                    Kind: h.Kind,
                    FilePath: h.FilePath,
                    Line: h.StartLine,
                    Column: h.StartCol,
                    Annotations: entries);
            })
            .ToList();

        return new CallToolResult
        {
            Content = content,
            StructuredContent = JsonSerializer.SerializeToElement(
                new FindByAnnotationResult(structuredHits),
                ToolOutputJsonContext.Default.FindByAnnotationResult),
        };
    }

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(FindReferencesResult))]
    [ToolTrigger("\"who uses X?\" or \"who calls X?\"")]
    [Description("Find every place that references a symbol. Resolves the symbol by name/FQN, then lists each call site or type-use as file:line. By default skips refs from source-generated files; pass includeGenerated=true to surface them.")]
    public static Task<CallToolResult> FindReferencesAsync(
        ScopeRouter router,
        [Description("Symbol name or FQN, same matching rules as find_definition")] string symbol,
        [Description("Maximum number of references to return (default 50)")] int limit = 50,
        [Description("Include references from source-generated files (default false)")] bool includeGenerated = false,
        [Description(ScopeDescription)] string? scope = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("find_references", new { symbol, limit, includeGenerated, scope }, () =>
            ScopedExecution.RunAsync(router, scope, async host =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var hits = await host.Store.FindSymbolsAsync(symbol, filePathHint: null, limit: 5, ct).ConfigureAwait(false);
                if (hits.Count == 0)
                {
                    // No symbol resolved — short-circuit through DiagnosticResult.Build instead of
                    // BuildFindReferencesResult. Without a resolved target we can't construct a
                    // meaningful FindReferencesResult (Target* fields would be sentinel placeholders,
                    // ambiguous to consumers). Matches every other target-shaped tool's no-resolve
                    // path. Telemetry counts as ok=true (success-with-empty), symmetric with the
                    // pre-conversion `Task<string>` "No matches for 'X'." behaviour.
                    return DiagnosticResult.Build($"No matches for '{symbol}'.");
                }

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
                    return BuildFindReferencesResult(
                        prose: sb.ToString(),
                        target: top,
                        refs: Array.Empty<ReferenceHit>(),
                        scopeId: host.Scope.Id,
                        elapsedMs: sw.ElapsedMilliseconds);
                }
                var (refsKept, refsOmitted) = OutputBudget.ChooseKeep(refs.Count, OutputBudget.CompactRowChars);
                if (refsOmitted > 0) refs = refs.Take(refsKept).ToList();
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
                return BuildFindReferencesResult(
                    prose: sb.ToString(),
                    target: top,
                    refs: refs,
                    scopeId: host.Scope.Id,
                    elapsedMs: sw.ElapsedMilliseconds,
                    omittedSize: refsOmitted);
            }, ct));

    /// <summary>
    /// Compose the multi-content <see cref="CallToolResult"/> for <c>find_references</c>: leading
    /// user-visible prose, one <see cref="ResourceLinkBlock"/> per reference row pointing at the
    /// resolved target's <c>graph://symbol/&lt;id&gt;</c> resource (per spec scenario "find_references
    /// emits a link per reference row" — each row's link targets the reference's symbol id, which
    /// in our schema is the resolved target shared across all rows), a trailing audience-restricted
    /// metadata block with scope + latency + reference count, and the typed
    /// <see cref="FindReferencesResult"/> in <see cref="CallToolResult.StructuredContent"/>.
    /// </summary>
    private static CallToolResult BuildFindReferencesResult(
        string prose,
        SymbolHit target,
        IReadOnlyList<ReferenceHit> refs,
        string scopeId,
        long elapsedMs,
        int omittedSize = 0)
    {
        var content = new List<ContentBlock>(capacity: 2 + refs.Count)
        {
            new TextContentBlock { Text = prose },
        };

        // Per spec scenario "find_references emits a link per reference row": each row's URI
        // is `graph://symbol/<id>` where the id is the *reference's symbol id*. In our schema
        // ReferenceHit.SymbolId is the *target* symbol's id (the thing being referenced),
        // shared across every row, so all emitted links carry the same Uri value. The Title
        // varies per row (kind + location of this specific reference) so card-rendering UIs
        // can distinguish individual occurrences. Wasteful in serialized bytes — N copies of
        // the same Uri — but matches the literal spec wording. Revisit (e.g. switch to
        // `graph://file/<path>` per row) if usage telemetry shows agents care.
        foreach (var r in refs)
        {
            content.Add(new ResourceLinkBlock
            {
                Uri = GraphResourceUris.Symbol(target.Id),
                Name = target.Fqn,
                Title = $"{RefKindLabel(r.Kind)} at {Format.Location(r.FilePath, r.Line, r.Col)}",
                Description = $"{KindLabel(target.Kind)} — {Format.Location(target.FilePath, target.StartLine, target.StartCol)}",
                MimeType = "text/markdown",
            });
        }

        var extras = omittedSize > 0
            ? new[] { ("references", refs.Count.ToString()), ("omitted_size", omittedSize.ToString()) }
            : new[] { ("references", refs.Count.ToString()) };
        content.Add(AudienceMetadata.Build(scopeId, elapsedMs, extras));

        var structuredReferences = refs
            .Select(r => new FindReferenceHit(
                Kind: RefKindLabel(r.Kind),
                FilePath: r.FilePath,
                Line: r.Line,
                Column: r.Col,
                IsGenerated: r.IsGenerated))
            .ToList();
        var dto = new FindReferencesResult(
            TargetFqn: target.Fqn,
            TargetKind: target.Kind,
            TargetSymbolId: target.Id,
            References: structuredReferences);

        return new CallToolResult
        {
            Content = content,
            StructuredContent = JsonSerializer.SerializeToElement(
                dto,
                ToolOutputJsonContext.Default.FindReferencesResult),
        };
    }

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(ListSymbolsInFileResult))]
    [ToolTrigger("\"what's in this file?\" — faster than reading the whole file")]
    [Description("List every symbol declared in a file (classes, methods, properties, etc.). Each row carries kind, accessibility, modifiers, and one-line XML summary.")]
    public static Task<CallToolResult> ListSymbolsInFileAsync(
        ScopeRouter router,
        [Description("Absolute path or path suffix that uniquely identifies the file")] string path,
        [Description(ScopeDescription)] string? scope = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("list_symbols_in_file", new { path, scope }, () =>
            ScopedExecution.RunAsync(router, scope, async host =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var hits = await host.Store.ListSymbolsInFileAsync(path, ct).ConfigureAwait(false);
                if (hits.Count == 0)
                {
                    return DiagnosticResult.Build(
                        $"No indexed symbols in '{path}'. The file may not be part of an indexed solution, or may not exist.");
                }
                var (hitsKept, hitsOmitted) = OutputBudget.ChooseKeep(hits.Count, OutputBudget.RichRowChars);
                if (hitsOmitted > 0) hits = hits.Take(hitsKept).ToList();

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
                return BuildListSymbolsInFileResult(
                    prose: sb.ToString(),
                    filePath: hits[0].FilePath,
                    symbols: hits,
                    scopeId: host.Scope.Id,
                    elapsedMs: sw.ElapsedMilliseconds,
                    omittedSize: hitsOmitted);
            }, ct));

    private static CallToolResult BuildListSymbolsInFileResult(
        string prose,
        string filePath,
        IReadOnlyList<SymbolHit> symbols,
        string scopeId,
        long elapsedMs,
        int omittedSize = 0)
    {
        var content = new List<ContentBlock>(capacity: 2 + symbols.Count)
        {
            new TextContentBlock { Text = prose },
        };

        foreach (var s in symbols)
        {
            content.Add(new ResourceLinkBlock
            {
                Uri = GraphResourceUris.Symbol(s.Id),
                Name = s.Fqn,
                Title = s.Name,
                Description = $"{Format.KindWithAttrs(s)} — line {s.StartLine}",
                MimeType = "text/markdown",
            });
        }

        var extras = omittedSize > 0
            ? new[]
            {
                ("symbols", symbols.Count.ToString()),
                ("file", $"`{filePath}`"),
                ("omitted_size", omittedSize.ToString()),
            }
            : new[]
            {
                ("symbols", symbols.Count.ToString()),
                ("file", $"`{filePath}`"),
            };
        content.Add(AudienceMetadata.Build(scopeId, elapsedMs, extras));

        var structuredSymbols = symbols
            .Select(s => new ListSymbolsInFileRow(
                SymbolId: s.Id,
                Name: s.Name,
                Fqn: s.Fqn,
                Kind: s.Kind,
                Line: s.StartLine,
                Column: s.StartCol,
                Signature: string.IsNullOrEmpty(s.Signature) ? null : s.Signature,
                XmlSummary: string.IsNullOrEmpty(s.XmlSummary) ? null : s.XmlSummary))
            .ToList();
        var dto = new ListSymbolsInFileResult(
            FilePath: filePath,
            Symbols: structuredSymbols);
        return new CallToolResult
        {
            Content = content,
            StructuredContent = JsonSerializer.SerializeToElement(
                dto,
                ToolOutputJsonContext.Default.ListSymbolsInFileResult),
        };
    }

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(ListCallersResult))]
    [ToolTrigger("\"who calls X?\" or \"who consumes type X?\"")]
    [Description("List inbound edges into a target symbol. Default kind=calls (i.e. callers). Use kind=uses-type to find consumers of a type, kind=overrides-member for derived implementations, kind=implements-member for members satisfying an interface, kind=instantiates for `new T()` sites, kind=throws for throw sites, kind=tests for inbound test edges, or any XAML kind (code-behind, binds-path, binds-element, handles-event, uses-resource, instantiates-type, merges, applies-style) on a scope that loaded the XAML indexer; kind=all walks every edge kind. Plugin-defined kinds (any kebab-case identifier) are accepted as-is.")]
    public static Task<CallToolResult> ListCallersAsync(
        ScopeRouter router,
        [Description("Target symbol name or FQN")] string symbol,
        [Description("Maximum number of results to return (default 50)")] int limit = 50,
        [Description("Edge kind to walk (kebab-case): calls (default) | uses-type | overrides-member | implements-member | instantiates | throws | tests | code-behind | binds-path | binds-element | handles-event | uses-resource | instantiates-type | merges | applies-style | all. Plugin-defined kinds are accepted.")] string? kind = null,
        [Description(ScopeDescription)] string? scope = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("list_callers", new { symbol, limit, kind, scope }, () =>
            ScopedExecution.RunAsync(router, scope, async host =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var (edgeKind, label, isAll) = NormaliseEdgeKindParam(kind);
                if (!isAll && edgeKind is not null)
                {
                    var unknownNote = await CheckUnknownEdgeKindAsync(host.Store, edgeKind, ct).ConfigureAwait(false);
                    if (unknownNote is not null) return DiagnosticResult.Error(unknownNote);
                }

                var hits = await host.Store.FindSymbolsAsync(symbol, filePathHint: null, limit: 5, ct).ConfigureAwait(false);
                if (hits.Count == 0) return DiagnosticResult.Build($"No matches for '{symbol}'.");
                var top = hits[0];
                var callers = await host.Store.ListCallersAsync(top.Id, limit, edgeKind, ct).ConfigureAwait(false);
                var sb = new StringBuilder();
                sb.AppendLine($"Inbound `{label}` to **{top.Fqn}** ({KindLabel(top.Kind)}):");
                if (callers.Count == 0)
                {
                    sb.AppendLine("- (none)");
                    return BuildListCallersResult(
                        prose: sb.ToString(),
                        target: top,
                        callers: Array.Empty<SymbolHit>(),
                        edgeKindLabel: label,
                        scopeId: host.Scope.Id,
                        elapsedMs: sw.ElapsedMilliseconds);
                }
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
                return BuildListCallersResult(
                    prose: sb.ToString(),
                    target: top,
                    callers: callers,
                    edgeKindLabel: label,
                    scopeId: host.Scope.Id,
                    elapsedMs: sw.ElapsedMilliseconds);
            }, ct));

    private static CallToolResult BuildListCallersResult(
        string prose,
        SymbolHit target,
        IReadOnlyList<SymbolHit> callers,
        string edgeKindLabel,
        string scopeId,
        long elapsedMs)
    {
        var content = new List<ContentBlock>(capacity: 2 + callers.Count)
        {
            new TextContentBlock { Text = prose },
        };

        foreach (var c in callers)
        {
            content.Add(new ResourceLinkBlock
            {
                Uri = GraphResourceUris.Symbol(c.Id),
                Name = c.Fqn,
                Title = c.Fqn,
                Description = $"{KindLabel(c.Kind)} — {Format.Location(c.FilePath, c.StartLine, c.StartCol)}",
                MimeType = "text/markdown",
            });
        }

        content.Add(AudienceMetadata.Build(
            scopeId,
            elapsedMs,
            ("edge_kind", edgeKindLabel),
            ("callers", callers.Count.ToString())));

        var structuredCallers = callers
            .Select(c => new ListCallersRow(
                SymbolId: c.Id,
                Fqn: c.Fqn,
                Kind: c.Kind,
                FilePath: c.FilePath,
                Line: c.StartLine,
                Column: c.StartCol,
                PayloadJson: string.IsNullOrEmpty(c.PayloadJson) ? null : c.PayloadJson))
            .ToList();
        var dto = new ListCallersResult(
            TargetFqn: target.Fqn,
            TargetKind: target.Kind,
            TargetSymbolId: target.Id,
            EdgeKind: edgeKindLabel,
            Callers: structuredCallers);
        return new CallToolResult
        {
            Content = content,
            StructuredContent = JsonSerializer.SerializeToElement(
                dto,
                ToolOutputJsonContext.Default.ListCallersResult),
        };
    }

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(ListCalleesResult))]
    [ToolTrigger("\"what does X call?\" or \"what types does X use?\"")]
    [Description("List outbound edges from the target symbol. Default kind=calls (callees). Use kind=uses-type for types touched in this member's signature/body, kind=overrides-member for the base it overrides, kind=implements-member for the interface member it satisfies, kind=instantiates for types it constructs, kind=throws for exception types it throws, kind=tests for outbound test edges, or any XAML kind (code-behind, binds-path, binds-element, handles-event, uses-resource, instantiates-type, merges, applies-style) on a scope that loaded the XAML indexer; kind=all walks every edge kind. Plugin-defined kinds (any kebab-case identifier) are accepted as-is.")]
    public static Task<CallToolResult> ListCalleesAsync(
        ScopeRouter router,
        [Description("Source symbol name or FQN")] string symbol,
        [Description("Maximum number of results to return (default 50)")] int limit = 50,
        [Description("Edge kind to walk (kebab-case): calls (default) | uses-type | overrides-member | implements-member | instantiates | throws | tests | code-behind | binds-path | binds-element | handles-event | uses-resource | instantiates-type | merges | applies-style | all. Plugin-defined kinds are accepted.")] string? kind = null,
        [Description(ScopeDescription)] string? scope = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("list_callees", new { symbol, limit, kind, scope }, () =>
            ScopedExecution.RunAsync(router, scope, async host =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var (edgeKind, label, isAll) = NormaliseEdgeKindParam(kind);
                if (!isAll && edgeKind is not null)
                {
                    var unknownNote = await CheckUnknownEdgeKindAsync(host.Store, edgeKind, ct).ConfigureAwait(false);
                    if (unknownNote is not null) return DiagnosticResult.Error(unknownNote);
                }

                var hits = await host.Store.FindSymbolsAsync(symbol, filePathHint: null, limit: 5, ct).ConfigureAwait(false);
                if (hits.Count == 0) return DiagnosticResult.Build($"No matches for '{symbol}'.");
                var top = hits[0];
                var callees = await host.Store.ListCalleesAsync(top.Id, limit, edgeKind, ct).ConfigureAwait(false);
                var sb = new StringBuilder();
                sb.AppendLine($"Outbound `{label}` from **{top.Fqn}** ({KindLabel(top.Kind)}):");
                if (callees.Count == 0)
                {
                    sb.AppendLine("- (none)");
                    return BuildListCalleesResult(
                        prose: sb.ToString(),
                        target: top,
                        callees: Array.Empty<SymbolHit>(),
                        edgeKindLabel: label,
                        scopeId: host.Scope.Id,
                        elapsedMs: sw.ElapsedMilliseconds);
                }
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
                return BuildListCalleesResult(
                    prose: sb.ToString(),
                    target: top,
                    callees: callees,
                    edgeKindLabel: label,
                    scopeId: host.Scope.Id,
                    elapsedMs: sw.ElapsedMilliseconds);
            }, ct));

    private static CallToolResult BuildListCalleesResult(
        string prose,
        SymbolHit target,
        IReadOnlyList<SymbolHit> callees,
        string edgeKindLabel,
        string scopeId,
        long elapsedMs)
    {
        var content = new List<ContentBlock>(capacity: 2 + callees.Count)
        {
            new TextContentBlock { Text = prose },
        };

        foreach (var c in callees)
        {
            content.Add(new ResourceLinkBlock
            {
                Uri = GraphResourceUris.Symbol(c.Id),
                Name = c.Fqn,
                Title = c.Fqn,
                Description = $"{KindLabel(c.Kind)} — {Format.Location(c.FilePath, c.StartLine, c.StartCol)}",
                MimeType = "text/markdown",
            });
        }

        content.Add(AudienceMetadata.Build(
            scopeId,
            elapsedMs,
            ("edge_kind", edgeKindLabel),
            ("callees", callees.Count.ToString())));

        var structuredCallees = callees
            .Select(c => new ListCalleesRow(
                SymbolId: c.Id,
                Fqn: c.Fqn,
                Kind: c.Kind,
                FilePath: c.FilePath,
                Line: c.StartLine,
                Column: c.StartCol,
                PayloadJson: string.IsNullOrEmpty(c.PayloadJson) ? null : c.PayloadJson))
            .ToList();
        var dto = new ListCalleesResult(
            TargetFqn: target.Fqn,
            TargetKind: target.Kind,
            TargetSymbolId: target.Id,
            EdgeKind: edgeKindLabel,
            Callees: structuredCallees);
        return new CallToolResult
        {
            Content = content,
            StructuredContent = JsonSerializer.SerializeToElement(
                dto,
                ToolOutputJsonContext.Default.ListCalleesResult),
        };
    }

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(FindImplementationsResult))]
    [ToolTrigger("\"who implements IGreeter.Greet?\"")]
    [Description("Find every concrete member that implements an interface member (uses implements-member edges).")]
    public static Task<CallToolResult> FindImplementationsAsync(
        ScopeRouter router,
        [Description("Interface member name or FQN, e.g. 'IGreeter.Greet'")] string symbol,
        [Description("Include abstract base members in the result (default false)")] bool includeAbstract = false,
        [Description("Maximum results (default 50)")] int limit = 50,
        [Description(ScopeDescription)] string? scope = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("find_implementations", new { symbol, includeAbstract, limit, scope }, () =>
            ScopedExecution.RunAsync(router, scope, async host =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var hits = await host.Store.FindSymbolsAsync(symbol, filePathHint: null, limit: 5, ct).ConfigureAwait(false);
                if (hits.Count == 0) return DiagnosticResult.Build($"No matches for '{symbol}'.");
                var top = hits[0];
                var impls = await host.Store.ListImplementationsAsync(top.Id, limit, ct).ConfigureAwait(false);
                var filtered = includeAbstract
                    ? impls
                    : impls.Where(h => !(h.Signature?.Contains("abstract", StringComparison.Ordinal) ?? false)).ToList();
                var sb = new StringBuilder();
                sb.AppendLine($"Implementations of **{top.Fqn}** ({KindLabel(top.Kind)}):");
                if (filtered.Count == 0)
                {
                    sb.AppendLine("- (none)");
                    return BuildFindImplementationsResult(
                        prose: sb.ToString(),
                        target: top,
                        impls: Array.Empty<SymbolHit>(),
                        scopeId: host.Scope.Id,
                        elapsedMs: sw.ElapsedMilliseconds);
                }
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
                return BuildFindImplementationsResult(
                    prose: sb.ToString(),
                    target: top,
                    impls: filtered,
                    scopeId: host.Scope.Id,
                    elapsedMs: sw.ElapsedMilliseconds);
            }, ct));

    private static CallToolResult BuildFindImplementationsResult(
        string prose,
        SymbolHit target,
        IReadOnlyList<SymbolHit> impls,
        string scopeId,
        long elapsedMs)
    {
        var content = new List<ContentBlock>(capacity: 2 + impls.Count)
        {
            new TextContentBlock { Text = prose },
        };

        foreach (var c in impls)
        {
            content.Add(new ResourceLinkBlock
            {
                Uri = GraphResourceUris.Symbol(c.Id),
                Name = c.Fqn,
                Title = c.Fqn,
                Description = $"{KindLabel(c.Kind)} — {Format.Location(c.FilePath, c.StartLine, c.StartCol)}",
                MimeType = "text/markdown",
            });
        }

        content.Add(AudienceMetadata.Build(
            scopeId,
            elapsedMs,
            ("implementations", impls.Count.ToString())));

        var structuredImpls = impls
            .Select(c => new FindImplementationsRow(
                SymbolId: c.Id,
                Fqn: c.Fqn,
                Kind: c.Kind,
                FilePath: c.FilePath,
                Line: c.StartLine,
                Column: c.StartCol,
                Signature: string.IsNullOrEmpty(c.Signature) ? null : c.Signature))
            .ToList();
        var dto = new FindImplementationsResult(
            TargetFqn: target.Fqn,
            TargetKind: target.Kind,
            TargetSymbolId: target.Id,
            Implementations: structuredImpls);
        return new CallToolResult
        {
            Content = content,
            StructuredContent = JsonSerializer.SerializeToElement(
                dto,
                ToolOutputJsonContext.Default.FindImplementationsResult),
        };
    }

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(FindDataBindingsResult))]
    [ToolTrigger("\"where does this property bind?\", \"find every TwoWay binding\", \"which views use this converter?\"")]
    [Description(
        "Find or audit data bindings between XAML/UI elements and viewmodel properties. Walks `binds-path` " +
        "edges; payload-aware filters map to the SDK PayloadKeys constants `path`, `mode`, and `converter` " +
        "(plus optional `target`/`source` endpoint narrowing). The `converter-parameter` payload key is " +
        "rendered in the response sub-line when present but is not itself a filter knob. At least one " +
        "filter should be supplied; without one, the tool returns the first `limit` rows and prepends a " +
        "note hinting the agent to narrow. On a scope whose stored `binds-path` edge set is empty, the " +
        "tool returns an empty list plus a one-line `note:` rather than an error.")]
    public static Task<CallToolResult> FindDataBindingsAsync(
        ScopeRouter router,
        [Description("Target symbol — the bound viewmodel/property (name, FQN, or canonical key). Matched against the edge's `dst`. Resolved via the same lookup `find_definition` uses.")] string? target = null,
        [Description("Source symbol — the XAML element that hosts the binding (name, FQN, or canonical key). Matched against the edge's `src`.")] string? source = null,
        [Description("Substring filter on the binding's `payload.path` (case-sensitive INSTR match). Example: 'User.' matches 'User.Name', 'User.Email'.")] string? path = null,
        [Description("Exact match on `payload.mode`. Typical values: 'one-way', 'two-way', 'one-time', 'one-way-to-source'.")] string? mode = null,
        [Description("Exact match on `payload.converter`. The XAML indexer writes the converter's source identifier (e.g. 'BoolToVisibility'), not a canonical key — pass the same string the markup uses.")] string? converter = null,
        [Description(ScopeDescription)] string? scope = null,
        [Description("Maximum rows (default 50)")] int limit = 50,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("find_data_bindings", new { target, source, path, mode, converter, scope, limit }, () =>
            ScopedExecution.RunAsync(router, scope, async host =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();

                // Soft-empty: when the scope has no binds-path edges in storage AND binds-path
                // isn't an SDK-built-in (the indexer might just not have run yet), return the
                // documented note plus an empty list. The condition is "no binds-path edges
                // observed" — the cause could be either no XAML/web-stack indexer loaded for this
                // scope, or one is loaded but indexing hasn't produced any bindings yet. The
                // wording reflects that ambiguity rather than asserting "no indexer".
                var storedKinds = await host.Store.GetDistinctEdgeKindsAsync(ct).ConfigureAwait(false);
                var hasBindsPath = storedKinds.Contains(EdgeKindBindsPath, StringComparer.Ordinal)
                                   || ServerVocabulary.SdkEdgeKinds.Contains(EdgeKindBindsPath);
                if (!hasBindsPath)
                {
                    var msg = $"scope `{host.Scope.Id}` has no `binds-path` edges in storage. Either no indexer that emits `binds-path` (XAML, web stack) is loaded for this scope, or indexing hasn't produced any bindings yet.";
                    return BuildFindDataBindingsResult(
                        prose: $"note: {msg}",
                        note: msg,
                        bindings: Array.Empty<EdgeWithPayload>(),
                        scopeId: host.Scope.Id,
                        elapsedMs: sw.ElapsedMilliseconds);
                }

                // Resolve target / source. Bare canonical keys (start with `csharp:` / `xaml:` / a
                // language prefix) are passed through verbatim; any other input goes through the
                // same FindSymbolsAsync lookup that `find_definition` uses, then we feed the top
                // hit's canonical_key down to storage.
                var targetKey = await ResolveCanonicalKeyAsync(host.Store, target, ct).ConfigureAwait(false);
                var sourceKey = await ResolveCanonicalKeyAsync(host.Store, source, ct).ConfigureAwait(false);

                // All-filters-null detection: emit the documented note but still run the storage
                // call so the agent sees what's available. Path/mode/converter are pure strings;
                // a non-null target/source that failed to resolve is treated as "no filter." Per
                // the spec scenario "All filters null returns hint" — the trigger is "every
                // filter null AND no scope restriction"; an explicit scope (single id or list,
                // anything other than the wildcard "*") narrows the query enough that the
                // hint becomes noise, so it counts as a filter here.
                var scopeIsRestrictive = !string.IsNullOrEmpty(scope) && scope.Trim() != "*";
                var anyFilter = targetKey is not null || sourceKey is not null
                    || !string.IsNullOrEmpty(path) || !string.IsNullOrEmpty(mode) || !string.IsNullOrEmpty(converter)
                    || scopeIsRestrictive;

                var bindings = await host.Store.FindDataBindingsAsync(
                    targetCanonicalKey: targetKey,
                    sourceCanonicalKey: sourceKey,
                    pathContains: string.IsNullOrEmpty(path) ? null : path,
                    modeExact: string.IsNullOrEmpty(mode) ? null : mode,
                    converterExact: string.IsNullOrEmpty(converter) ? null : converter,
                    limit: limit,
                    ct: ct).ConfigureAwait(false);

                string? note = null;
                if (!anyFilter)
                {
                    note = "provide at least one filter (target, source, path, mode, converter) to narrow the result";
                }

                var sb = new StringBuilder();
                if (note is not null)
                {
                    sb.AppendLine($"note: {note}.");
                    sb.AppendLine();
                }
                if (bindings.Count == 0)
                {
                    sb.AppendLine("No data bindings matched.");
                }
                else
                {
                    sb.AppendLine($"{bindings.Count} `binds-path` edge(s):");
                    foreach (var edge in bindings)
                    {
                        sb.AppendLine($"- **{edge.Source.Fqn}**  →  {BindingTargetLabel(edge.Target)}");
                        var payloadLine = Format.PayloadSubLine(edge.PayloadJson);
                        if (payloadLine is not null) sb.AppendLine(payloadLine);
                    }
                }

                return BuildFindDataBindingsResult(
                    prose: sb.ToString(),
                    note: note,
                    bindings: bindings,
                    scopeId: host.Scope.Id,
                    elapsedMs: sw.ElapsedMilliseconds);
            }, ct));

    /// <summary>
    /// Render the right-hand side of a binds-path row: bold FQN for a resolved target, an
    /// italic <c>(unresolved)</c> tag plus the synthesised key for the placeholder rows the XAML
    /// indexer creates when a binding's data context can't be statically pinned.
    /// </summary>
    private static string BindingTargetLabel(SymbolHit target)
    {
        // Synthetic placeholder canonical keys for unresolved binds-path targets carry the
        // `xaml:binding-target:` prefix (see XamlLanguageIndexer). The agent reads "(unresolved)"
        // and knows the data-context type wasn't statically known — the binding still lands as an
        // edge so the path is searchable, but no concrete viewmodel symbol matched.
        const string UnresolvedPrefix = "xaml:binding-target:";
        if (target.CanonicalKey is { } key && key.StartsWith(UnresolvedPrefix, StringComparison.Ordinal))
        {
            return $"_(unresolved)_  `{target.Fqn}`";
        }
        return $"**{target.Fqn}**";
    }

    /// <summary>
    /// Resolve a free-form symbol identifier (canonical key, name, or FQN suffix) to a canonical
    /// key. Inputs that pass <see cref="CanonicalKeyValidator.IsValid"/> are passed through
    /// verbatim so the storage WHERE clause matches by key — single source of truth for the
    /// scheme list (delegating to the SDK keeps this in lockstep when new languages get
    /// promoted into <see cref="CanonicalKeyValidator.EnforcedSchemes"/>). Other inputs go
    /// through the same name-lookup path <c>find_definition</c> uses; the top hit's canonical
    /// key wins. Returns <c>null</c> for null/empty input or when the lookup produces no hits.
    /// </summary>
    private static async Task<string?> ResolveCanonicalKeyAsync(IGraphStore store, string? identifier, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(identifier)) return null;
        // CanonicalKeyValidator rejects inputs whose scheme isn't an enforced one — XAML
        // placeholder FQNs like `binding-target:Views/Main.xaml#Text@12:24` fail (the leading
        // token isn't a valid scheme), so they correctly fall through to FindSymbolsAsync.
        if (CanonicalKeyValidator.IsValid(identifier)) return identifier;
        var hits = await store.FindSymbolsAsync(identifier, filePathHint: null, limit: 1, ct).ConfigureAwait(false);
        return hits.Count == 0 ? null : hits[0].CanonicalKey;
    }

    private const string EdgeKindBindsPath = "binds-path";
    private const string EdgeKindHandlesEvent = "handles-event";

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(FindEventHandlersResult))]
    [ToolTrigger("\"find all Click handlers\", \"where is OnSave wired up?\", \"which buttons fire this command?\"")]
    [Description(
        "Find or audit event-to-handler wiring in XAML or component-based UI. Walks `handles-event` " +
        "edges; payload-aware filters are `event` (mapped to the SDK `PayloadKeys.Event` constant) " +
        "and the forward-looking `command` key for command-bound flavours — `command` is not yet a " +
        "PayloadKeys constant; the kebab string is treated as the canonical key until the SDK " +
        "promotes it. The `handler` and `element` parameters narrow by edge endpoints (resolved " +
        "canonical keys), not payload keys. On a scope whose stored `handles-event` edge set is " +
        "empty, the tool returns an empty list plus a one-line `note:` rather than an error; " +
        "`command` filtering against a scope whose `handles-event` edges never carry a `command` " +
        "payload key returns the same soft-empty shape with a tailored note.")]
    public static Task<CallToolResult> FindEventHandlersAsync(
        ScopeRouter router,
        [Description("Handler symbol — the resolved handler method (name, FQN, or canonical key). Matched against the edge's `dst`. Resolved via the same lookup `find_definition` uses.")] string? handler = null,
        [Description("Exact match on `payload.event` (e.g. 'Click', 'MouseEnter', 'Loaded'). The kebab-case PayloadKeys constant `event` is what the indexer fills.")] string? @event = null,
        [Description("Element symbol — the XAML / UI element that owns the event attribute (name, FQN, or canonical key). Matched against the edge's `src`.")] string? element = null,
        [Description("Exact match on `payload.command` for command-bound flavours where the indexer recorded a command name. Empty on scopes without `Command=\"{Binding ...}\"` patterns.")] string? command = null,
        [Description(ScopeDescription)] string? scope = null,
        [Description("Maximum rows (default 50)")] int limit = 50,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("find_event_handlers", new { handler, @event, element, command, scope, limit }, () =>
            ScopedExecution.RunAsync(router, scope, async host =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();

                // Soft-empty: same shape as find_data_bindings — emit a note plus an empty list
                // when the scope has no handles-event edges in storage. Cause is ambiguous (no
                // indexer that emits the kind, OR one is loaded but indexing hasn't run yet);
                // the wording reflects that.
                var storedKinds = await host.Store.GetDistinctEdgeKindsAsync(ct).ConfigureAwait(false);
                var hasHandlesEvent = storedKinds.Contains(EdgeKindHandlesEvent, StringComparer.Ordinal)
                                      || ServerVocabulary.SdkEdgeKinds.Contains(EdgeKindHandlesEvent);
                if (!hasHandlesEvent)
                {
                    var msg = $"scope `{host.Scope.Id}` has no `handles-event` edges in storage. Either no indexer that emits `handles-event` (XAML, web stack) is loaded for this scope, or indexing hasn't produced any handlers yet.";
                    return BuildFindEventHandlersResult(
                        prose: $"note: {msg}",
                        note: msg,
                        handlers: Array.Empty<EdgeWithPayload>(),
                        scopeId: host.Scope.Id,
                        elapsedMs: sw.ElapsedMilliseconds);
                }

                // Resolve handler / element via the find_definition-shaped lookup. Bare canonical
                // keys pass through verbatim.
                var handlerKey = await ResolveCanonicalKeyAsync(host.Store, handler, ct).ConfigureAwait(false);
                var elementKey = await ResolveCanonicalKeyAsync(host.Store, element, ct).ConfigureAwait(false);

                var handlers = await host.Store.FindEventHandlersAsync(
                    handlerCanonicalKey: handlerKey,
                    eventExact: string.IsNullOrEmpty(@event) ? null : @event,
                    elementCanonicalKey: elementKey,
                    commandExact: string.IsNullOrEmpty(command) ? null : command,
                    limit: limit,
                    ct: ct).ConfigureAwait(false);

                // Secondary soft-empty: when `command` was the filter and we got 0 rows, probe
                // whether ANY handles-event edge in storage carries a `command` payload key. None
                // → the indexer for this scope doesn't record commands at all (XAML scopes
                // without `Command="{Binding ...}"` patterns); emit the documented `note:` so
                // the agent doesn't keep guessing other command names.
                string? commandSoftEmptyNote = null;
                if (handlers.Count == 0 && !string.IsNullOrEmpty(command))
                {
                    var anyCommand = await host.Store.AnyEdgeHasPayloadKeyAsync(
                        EdgeKindHandlesEvent, "command", ct).ConfigureAwait(false);
                    if (!anyCommand)
                    {
                        commandSoftEmptyNote = $"no `handles-event` edges in scope `{host.Scope.Id}` carry a `command` payload key — the loaded indexer for this scope doesn't record command-bound wirings.";
                    }
                }

                var sb = new StringBuilder();
                if (commandSoftEmptyNote is not null)
                {
                    sb.AppendLine($"note: {commandSoftEmptyNote}");
                    sb.AppendLine();
                }
                if (handlers.Count == 0)
                {
                    sb.AppendLine("No event handlers matched.");
                }
                else
                {
                    sb.AppendLine($"{handlers.Count} `handles-event` edge(s):");
                    foreach (var edge in handlers)
                    {
                        var (eventName, commandName) = ExtractEventHandlerPayloadFields(edge.PayloadJson);
                        var leftLabel = string.IsNullOrEmpty(eventName) ? $"**{edge.Source.Fqn}**" : $"**{edge.Source.Fqn}**.`{eventName}`";
                        var rightLabel = string.IsNullOrEmpty(commandName) ? $"**{edge.Target.Fqn}**" : $"**{edge.Target.Fqn}** (cmd: `{commandName}`)";
                        sb.AppendLine($"- {leftLabel}  →  {rightLabel}");
                        var payloadLine = Format.PayloadSubLine(edge.PayloadJson);
                        if (payloadLine is not null) sb.AppendLine(payloadLine);
                    }
                }

                return BuildFindEventHandlersResult(
                    prose: sb.ToString(),
                    note: commandSoftEmptyNote,
                    handlers: handlers,
                    scopeId: host.Scope.Id,
                    elapsedMs: sw.ElapsedMilliseconds);
            }, ct));

    /// <summary>
    /// Pre-extract the well-known <c>event</c> and <c>command</c> keys from a handles-event edge
    /// payload. Returns <c>(null, null)</c> for missing / malformed payloads — defensive: storage
    /// stores opaque JSON, so a bad row never breaks the renderer or the structured output.
    /// </summary>
    private static (string? Event, string? Command) ExtractEventHandlerPayloadFields(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return (null, null);
            string? evt = doc.RootElement.TryGetProperty("event", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            string? cmd = doc.RootElement.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            return (evt, cmd);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static CallToolResult BuildFindEventHandlersResult(
        string prose,
        string? note,
        IReadOnlyList<EdgeWithPayload> handlers,
        string scopeId,
        long elapsedMs)
    {
        var content = new List<ContentBlock>(capacity: 2 + handlers.Count)
        {
            new TextContentBlock { Text = prose },
        };

        // ResourceLink per handler so the agent can pivot into the handler method's symbol view.
        foreach (var edge in handlers)
        {
            content.Add(new ResourceLinkBlock
            {
                Uri = GraphResourceUris.Symbol(edge.Target.Id),
                Name = edge.Target.Fqn,
                Title = edge.Target.Fqn,
                Description = $"handles-event handler — {Format.Location(edge.Target.FilePath, edge.Target.StartLine, edge.Target.StartCol)}",
                MimeType = "text/markdown",
            });
        }

        content.Add(AudienceMetadata.Build(
            scopeId,
            elapsedMs,
            ("edge_kind", EdgeKindHandlesEvent),
            ("handlers", handlers.Count.ToString())));

        var structuredRows = handlers
            .Select(e =>
            {
                var (evt, cmd) = ExtractEventHandlerPayloadFields(e.PayloadJson);
                return new FindEventHandlersRow(
                    ElementSymbolId: e.Source.Id,
                    ElementFqn: e.Source.Fqn,
                    ElementCanonicalKey: e.Source.CanonicalKey,
                    HandlerSymbolId: e.Target.Id,
                    HandlerFqn: e.Target.Fqn,
                    HandlerCanonicalKey: e.Target.CanonicalKey,
                    EventName: evt,
                    CommandName: cmd,
                    PayloadJson: string.IsNullOrEmpty(e.PayloadJson) ? null : e.PayloadJson);
            })
            .ToList();
        var dto = new FindEventHandlersResult(
            EdgeKind: EdgeKindHandlesEvent,
            Note: note,
            Handlers: structuredRows);
        return new CallToolResult
        {
            Content = content,
            StructuredContent = JsonSerializer.SerializeToElement(
                dto,
                ToolOutputJsonContext.Default.FindEventHandlersResult),
        };
    }

    private static CallToolResult BuildFindDataBindingsResult(
        string prose,
        string? note,
        IReadOnlyList<EdgeWithPayload> bindings,
        string scopeId,
        long elapsedMs)
    {
        var content = new List<ContentBlock>(capacity: 2 + bindings.Count)
        {
            new TextContentBlock { Text = prose },
        };

        // One ResourceLink per resolved target so the agent can chain into graph://symbol/{id}
        // for each viewmodel property it found bindings into.
        foreach (var edge in bindings)
        {
            content.Add(new ResourceLinkBlock
            {
                Uri = GraphResourceUris.Symbol(edge.Target.Id),
                Name = edge.Target.Fqn,
                Title = edge.Target.Fqn,
                Description = $"binds-path target — {Format.Location(edge.Target.FilePath, edge.Target.StartLine, edge.Target.StartCol)}",
                MimeType = "text/markdown",
            });
        }

        content.Add(AudienceMetadata.Build(
            scopeId,
            elapsedMs,
            ("edge_kind", EdgeKindBindsPath),
            ("bindings", bindings.Count.ToString())));

        var structuredRows = bindings
            .Select(e => new FindDataBindingsRow(
                SourceSymbolId: e.Source.Id,
                SourceFqn: e.Source.Fqn,
                SourceCanonicalKey: e.Source.CanonicalKey,
                TargetSymbolId: e.Target.Id,
                TargetFqn: e.Target.Fqn,
                TargetCanonicalKey: e.Target.CanonicalKey,
                TargetIsUnresolved: e.Target.CanonicalKey?.StartsWith("xaml:binding-target:", StringComparison.Ordinal) == true,
                PayloadJson: string.IsNullOrEmpty(e.PayloadJson) ? null : e.PayloadJson))
            .ToList();
        var dto = new FindDataBindingsResult(
            EdgeKind: EdgeKindBindsPath,
            Note: note,
            Bindings: structuredRows);
        return new CallToolResult
        {
            Content = content,
            StructuredContent = JsonSerializer.SerializeToElement(
                dto,
                ToolOutputJsonContext.Default.FindDataBindingsResult),
        };
    }

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(SearchSymbolsResult))]
    [ToolTrigger("\"I only have a fragment of the name (e.g. 'Calc', 'Greet', 'Async')\"")]
    [Description("Free-text search for symbols by partial name, FQN, or signature using FTS5.")]
    public static Task<CallToolResult> SearchSymbolsAsync(
        ScopeRouter router,
        [Description("Search query (a few characters, words, or substring of name/FQN/signature)")] string query,
        [Description("Optional kebab-case symbol kind filter: class|method|property|field|interface|namespace|enum|enum-member|operator|record|delegate|struct|event|xaml-view|xaml-element|xaml-resource|xaml-style|xaml-template|... (any plugin-defined kebab-case kind also accepted).")] string? kind = null,
        [Description("Maximum results (default 25)")] int topK = 25,
        [Description(ScopeDescription)] string? scope = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("search_symbols", new { query, kind, topK, scope }, () =>
            ScopedExecution.RunAsync(router, scope, async host =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var kindFilter = NormaliseKindFilter(kind);

                var hits = await host.Store.SearchSymbolsAsync(query, kindFilter, topK, ct).ConfigureAwait(false);
                if (hits.Count == 0)
                {
                    return BuildSearchSymbolsResult(
                        prose: $"No symbols match '{query}'.",
                        hits: Array.Empty<SymbolHit>(),
                        scopeId: host.Scope.Id,
                        elapsedMs: sw.ElapsedMilliseconds);
                }
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
                return BuildSearchSymbolsResult(
                    prose: sb.ToString(),
                    hits: hits,
                    scopeId: host.Scope.Id,
                    elapsedMs: sw.ElapsedMilliseconds);
            }, ct));

    private static CallToolResult BuildSearchSymbolsResult(
        string prose,
        IReadOnlyList<SymbolHit> hits,
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
                Description = $"{KindLabel(h.Kind)} — {Format.Location(h.FilePath, h.StartLine, h.StartCol)}",
                MimeType = "text/markdown",
            });
        }

        content.Add(AudienceMetadata.Build(
            scopeId,
            elapsedMs,
            ("hits", hits.Count.ToString())));

        var structuredHits = hits
            .Select(h => new SearchSymbolHit(
                Fqn: h.Fqn,
                Kind: h.Kind,
                FilePath: h.FilePath,
                Line: h.StartLine,
                Column: h.StartCol,
                Signature: string.IsNullOrEmpty(h.Signature) ? null : h.Signature,
                XmlSummary: string.IsNullOrEmpty(h.XmlSummary) ? null : h.XmlSummary))
            .ToList();
        return new CallToolResult
        {
            Content = content,
            StructuredContent = JsonSerializer.SerializeToElement(
                new SearchSymbolsResult(structuredHits),
                ToolOutputJsonContext.Default.SearchSymbolsResult),
        };
    }

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(NeighborhoodResult))]
    [ToolTrigger("\"give me a quick overview around X\" — orient before diving in")]
    [Description("Get the immediate graph neighborhood of a symbol: callers, callees, and inheritance/implements edges. Default kind=calls; pass kind=uses-type, overrides-member, implements-member, instantiates, throws, tests, all, any XAML edge kind (code-behind, binds-path, binds-element, handles-event, uses-resource, instantiates-type, merges, applies-style) on a scope that loaded the XAML indexer, or any plugin-defined kebab-case kind to inspect other edge layers.")]
    public static Task<CallToolResult> NeighborhoodAsync(
        ScopeRouter router,
        [Description("Symbol name or FQN")] string symbol,
        [Description("Max items per category (default 20)")] int perCategory = 20,
        [Description("Edge kind to walk (kebab-case): calls (default) | uses-type | overrides-member | implements-member | instantiates | throws | tests | code-behind | binds-path | binds-element | handles-event | uses-resource | instantiates-type | merges | applies-style | all. Plugin-defined kinds are accepted.")] string? kind = null,
        [Description(ScopeDescription)] string? scope = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("neighborhood", new { symbol, perCategory, kind, scope }, () =>
            ScopedExecution.RunAsync(router, scope, async host =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var (edgeKind, label, isAll) = NormaliseEdgeKindParam(kind);
                if (!isAll && edgeKind is not null)
                {
                    var unknownNote = await CheckUnknownEdgeKindAsync(host.Store, edgeKind, ct).ConfigureAwait(false);
                    if (unknownNote is not null) return DiagnosticResult.Error(unknownNote);
                }

                var hits = await host.Store.FindSymbolsAsync(symbol, filePathHint: null, limit: 5, ct).ConfigureAwait(false);
                if (hits.Count == 0) return DiagnosticResult.Build($"No matches for '{symbol}'.");
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
                return BuildNeighborhoodResult(
                    prose: sb.ToString(),
                    target: top,
                    inbound: callers,
                    outbound: callees,
                    edgeKindLabel: label,
                    scopeId: host.Scope.Id,
                    elapsedMs: sw.ElapsedMilliseconds);
            }, ct));

    private static CallToolResult BuildNeighborhoodResult(
        string prose,
        SymbolHit target,
        IReadOnlyList<SymbolHit> inbound,
        IReadOnlyList<SymbolHit> outbound,
        string edgeKindLabel,
        string scopeId,
        long elapsedMs)
    {
        var content = new List<ContentBlock>(capacity: 2 + inbound.Count + outbound.Count)
        {
            new TextContentBlock { Text = prose },
        };

        foreach (var c in inbound)
        {
            content.Add(new ResourceLinkBlock
            {
                Uri = GraphResourceUris.Symbol(c.Id),
                Name = c.Fqn,
                Title = $"inbound: {c.Fqn}",
                Description = $"{Format.KindWithAttrs(c)} — {Format.Location(c.FilePath, c.StartLine, c.StartCol)}",
                MimeType = "text/markdown",
            });
        }
        foreach (var c in outbound)
        {
            content.Add(new ResourceLinkBlock
            {
                Uri = GraphResourceUris.Symbol(c.Id),
                Name = c.Fqn,
                Title = $"outbound: {c.Fqn}",
                Description = $"{Format.KindWithAttrs(c)} — {Format.Location(c.FilePath, c.StartLine, c.StartCol)}",
                MimeType = "text/markdown",
            });
        }

        content.Add(AudienceMetadata.Build(
            scopeId,
            elapsedMs,
            ("edge_kind", edgeKindLabel),
            ("inbound", inbound.Count.ToString()),
            ("outbound", outbound.Count.ToString())));

        static NeighborhoodRow Project(SymbolHit s) => new(
            SymbolId: s.Id,
            Fqn: s.Fqn,
            Kind: s.Kind,
            FilePath: s.FilePath,
            Line: s.StartLine,
            Column: s.StartCol,
            PayloadJson: string.IsNullOrEmpty(s.PayloadJson) ? null : s.PayloadJson);
        var dto = new NeighborhoodResult(
            SymbolId: target.Id,
            Fqn: target.Fqn,
            Kind: target.Kind,
            FilePath: target.FilePath,
            Line: target.StartLine,
            Column: target.StartCol,
            EdgeKind: edgeKindLabel,
            Inbound: inbound.Select(Project).ToList(),
            Outbound: outbound.Select(Project).ToList());
        return new CallToolResult
        {
            Content = content,
            StructuredContent = JsonSerializer.SerializeToElement(
                dto,
                ToolOutputJsonContext.Default.NeighborhoodResult),
        };
    }

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

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(ModuleSummaryResult))]
    [ToolTrigger("\"what's important in this namespace?\" or \"what's the entrypoint to module Y?\"")]
    [Description("Summarize a namespace or directory: lists the most-referenced symbols (highest in-degree) so you know what to read first. Pass 'Sample.Domain' or a path fragment.")]
    public static Task<CallToolResult> ModuleSummaryAsync(
        ScopeRouter router,
        [Description("Namespace (e.g. 'Sample.Domain') or path-substring that identifies the module")] string namespaceOrPath,
        [Description("Top-K most-referenced symbols to return (default 25)")] int topK = 25,
        [Description(ScopeDescription)] string? scope = null,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("module_summary", new { namespaceOrPath, topK, scope }, () =>
            ScopedExecution.RunAsync(router, scope, async host =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                progress?.Report(Format.Progress(0.0, "querying"));
                var rows = await host.Store.ModuleSummaryAsync(namespaceOrPath, topK, ct).ConfigureAwait(false);
                if (rows.Count == 0)
                {
                    return BuildModuleSummaryResult(
                        prose: $"No symbols matched module '{namespaceOrPath}'.",
                        module: namespaceOrPath,
                        rows: Array.Empty<ModuleSymbol>(),
                        scopeId: host.Scope.Id,
                        elapsedMs: sw.ElapsedMilliseconds);
                }
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
                return BuildModuleSummaryResult(
                    prose: sb.ToString(),
                    module: namespaceOrPath,
                    rows: rows,
                    scopeId: host.Scope.Id,
                    elapsedMs: sw.ElapsedMilliseconds);
            }, ct));

    private static CallToolResult BuildModuleSummaryResult(
        string prose,
        string module,
        IReadOnlyList<ModuleSymbol> rows,
        string scopeId,
        long elapsedMs)
    {
        var content = new List<ContentBlock>(capacity: 2 + rows.Count)
        {
            new TextContentBlock { Text = prose },
        };

        foreach (var row in rows)
        {
            content.Add(new ResourceLinkBlock
            {
                Uri = GraphResourceUris.Symbol(row.Symbol.Id),
                Name = row.Symbol.Fqn,
                Title = $"in-deg {row.InDegree}: {row.Symbol.Fqn}",
                Description = $"{Format.KindWithAttrs(row.Symbol)} — {Format.Location(row.Symbol.FilePath, row.Symbol.StartLine, row.Symbol.StartCol)}",
                MimeType = "text/markdown",
            });
        }

        content.Add(AudienceMetadata.Build(
            scopeId,
            elapsedMs,
            ("module", $"`{module}`"),
            ("rows", rows.Count.ToString())));

        var structuredRows = rows
            .Select(row => new ModuleSummaryRow(
                InDegree: row.InDegree,
                SymbolId: row.Symbol.Id,
                Fqn: row.Symbol.Fqn,
                Kind: row.Symbol.Kind,
                FilePath: row.Symbol.FilePath,
                Line: row.Symbol.StartLine,
                Column: row.Symbol.StartCol))
            .ToList();
        var dto = new ModuleSummaryResult(Module: module, Top: structuredRows);
        return new CallToolResult
        {
            Content = content,
            StructuredContent = JsonSerializer.SerializeToElement(
                dto,
                ToolOutputJsonContext.Default.ModuleSummaryResult),
        };
    }

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(ImpactOfChangeResult))]
    [ToolTrigger("\"what would change if I edit X?\" — transitive callers")]
    [Description("Compute the transitive set of upstream callers for a symbol (impact of changing it). Walks the call graph backward up to maxDepth. Default kind=calls; pass kind=uses-type, overrides-member, implements-member, instantiates, throws, tests, all, or any plugin-defined kebab-case kind to traverse other edge layers.")]
    public static Task<CallToolResult> ImpactOfChangeAsync(
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
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var (edgeKind, label, isAll) = NormaliseEdgeKindParam(kind);
                if (!isAll && edgeKind is not null)
                {
                    var unknownNote = await CheckUnknownEdgeKindAsync(host.Store, edgeKind, ct).ConfigureAwait(false);
                    if (unknownNote is not null) return DiagnosticResult.Error(unknownNote);
                }

                var hits = await host.Store.FindSymbolsAsync(symbol, filePathHint: null, limit: 5, ct).ConfigureAwait(false);
                if (hits.Count == 0) return DiagnosticResult.Build($"No matches for '{symbol}'.");
                var top = hits[0];
                progress?.Report(Format.Progress(0.0, "querying"));
                var rows = await host.Store.ImpactOfChangeAsync(top.Id, maxDepth, limit, edgeKind, ct).ConfigureAwait(false);
                var sb = new StringBuilder();
                sb.AppendLine($"Upstream impact of **{top.Fqn}** ({KindLabel(top.Kind)}) [kind={label}] up to depth {maxDepth}:");
                if (rows.Count == 0)
                {
                    sb.AppendLine("- (no upstream callers found in graph)");
                    return BuildImpactOfChangeResult(
                        prose: sb.ToString(),
                        target: top,
                        rows: Array.Empty<ImpactedSymbol>(),
                        edgeKindLabel: label,
                        maxDepth: maxDepth,
                        scopeId: host.Scope.Id,
                        elapsedMs: sw.ElapsedMilliseconds);
                }
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
                return BuildImpactOfChangeResult(
                    prose: sb.ToString(),
                    target: top,
                    rows: rows,
                    edgeKindLabel: label,
                    maxDepth: maxDepth,
                    scopeId: host.Scope.Id,
                    elapsedMs: sw.ElapsedMilliseconds);
            }, ct));

    private static CallToolResult BuildImpactOfChangeResult(
        string prose,
        SymbolHit target,
        IReadOnlyList<ImpactedSymbol> rows,
        string edgeKindLabel,
        int maxDepth,
        string scopeId,
        long elapsedMs)
    {
        var content = new List<ContentBlock>(capacity: 2 + rows.Count)
        {
            new TextContentBlock { Text = prose },
        };

        foreach (var r in rows)
        {
            content.Add(new ResourceLinkBlock
            {
                Uri = GraphResourceUris.Symbol(r.Symbol.Id),
                Name = r.Symbol.Fqn,
                Title = $"d{r.Depth}: {r.Symbol.Fqn}",
                Description = $"{KindLabel(r.Symbol.Kind)} — {Format.Location(r.Symbol.FilePath, r.Symbol.StartLine, r.Symbol.StartCol)}",
                MimeType = "text/markdown",
            });
        }

        content.Add(AudienceMetadata.Build(
            scopeId,
            elapsedMs,
            ("edge_kind", edgeKindLabel),
            ("max_depth", maxDepth.ToString()),
            ("upstream", rows.Count.ToString())));

        var structuredRows = rows
            .Select(r => new ImpactOfChangeRow(
                Depth: r.Depth,
                SymbolId: r.Symbol.Id,
                Fqn: r.Symbol.Fqn,
                Kind: r.Symbol.Kind,
                FilePath: r.Symbol.FilePath,
                Line: r.Symbol.StartLine,
                Column: r.Symbol.StartCol))
            .ToList();
        var dto = new ImpactOfChangeResult(
            TargetFqn: target.Fqn,
            TargetKind: target.Kind,
            TargetSymbolId: target.Id,
            EdgeKind: edgeKindLabel,
            MaxDepth: maxDepth,
            Upstream: structuredRows);
        return new CallToolResult
        {
            Content = content,
            StructuredContent = JsonSerializer.SerializeToElement(
                dto,
                ToolOutputJsonContext.Default.ImpactOfChangeResult),
        };
    }

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(ListMembersResult))]
    [ToolTrigger("\"what members does X have?\" or \"list members of namespace Y\"")]
    [Description("List the direct members of a container (class, struct, interface, namespace) by FQN, optionally filtered by accessibility. Walks the populated container_id chain — replaces 'list_symbols_in_file then filter'.")]
    public static Task<CallToolResult> ListMembersAsync(
        ScopeRouter router,
        [Description("Container FQN (e.g. 'Sample.Domain.Calculator', 'Sample.Domain'). Resolved with the same matching rules as find_definition; the top match is used.")] string container,
        [Description("Reserved for a future change that follows inherits/implements edges; currently ignored.")] bool includeInherited = false,
        [Description("Optional accessibility filter: public|internal|private|protected|protected internal|private protected.")] string? accessibility = null,
        [Description("Maximum members to return (default 100)")] int limit = 100,
        [Description(ScopeDescription)] string? scope = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("list_members", new { container, includeInherited, accessibility, limit, scope }, () =>
            ScopedExecution.RunAsync(router, scope, async host =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var hits = await host.Store.FindSymbolsAsync(container, filePathHint: null, limit: 5, ct).ConfigureAwait(false);
                if (hits.Count == 0) return DiagnosticResult.Build($"No matches for container '{container}'.");
                var top = hits[0];

                int? accFilter = ParseAccessibility(accessibility);
                if (!string.IsNullOrEmpty(accessibility) && accFilter is null)
                {
                    return DiagnosticResult.Error(
                        $"Unknown accessibility '{accessibility}'. Valid: public, internal, private, protected, protected internal, private protected.");
                }

                var members = await host.Store.ListMembersAsync(top.Id, accFilter, limit, ct).ConfigureAwait(false);
                var (membersKept, membersOmitted) = OutputBudget.ChooseKeep(members.Count, OutputBudget.RichRowChars);
                if (membersOmitted > 0) members = members.Take(membersKept).ToList();
                var sb = new StringBuilder();
                var filterNote = accFilter is null ? "" : $" (accessibility = {accessibility})";
                sb.AppendLine($"{members.Count} members of **{top.Fqn}** ({Format.KindWithAttrs(top)}){filterNote}:");
                if (includeInherited)
                {
                    sb.AppendLine("_(includeInherited is reserved for a future change; only direct members are returned.)_");
                }
                if (members.Count == 0)
                {
                    sb.AppendLine("- (none)");
                    return BuildListMembersResult(
                        prose: sb.ToString(),
                        container: top,
                        members: Array.Empty<SymbolHit>(),
                        scopeId: host.Scope.Id,
                        elapsedMs: sw.ElapsedMilliseconds);
                }
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
                return BuildListMembersResult(
                    prose: sb.ToString(),
                    container: top,
                    members: members,
                    scopeId: host.Scope.Id,
                    elapsedMs: sw.ElapsedMilliseconds,
                    omittedSize: membersOmitted);
            }, ct));

    private static CallToolResult BuildListMembersResult(
        string prose,
        SymbolHit container,
        IReadOnlyList<SymbolHit> members,
        string scopeId,
        long elapsedMs,
        int omittedSize = 0)
    {
        var content = new List<ContentBlock>(capacity: 2 + members.Count)
        {
            new TextContentBlock { Text = prose },
        };

        foreach (var m in members)
        {
            content.Add(new ResourceLinkBlock
            {
                Uri = GraphResourceUris.Symbol(m.Id),
                Name = m.Fqn,
                Title = m.Name,
                Description = $"{Format.KindWithAttrs(m)} — {Format.Location(m.FilePath, m.StartLine, m.StartCol)}",
                MimeType = "text/markdown",
            });
        }

        var extras = omittedSize > 0
            ? new[] { ("members", members.Count.ToString()), ("omitted_size", omittedSize.ToString()) }
            : new[] { ("members", members.Count.ToString()) };
        content.Add(AudienceMetadata.Build(scopeId, elapsedMs, extras));

        var structuredMembers = members
            .Select(m => new ListMembersRow(
                SymbolId: m.Id,
                Name: m.Name,
                Fqn: m.Fqn,
                Kind: m.Kind,
                FilePath: m.FilePath,
                Line: m.StartLine,
                Column: m.StartCol,
                Signature: string.IsNullOrEmpty(m.Signature) ? null : m.Signature,
                XmlSummary: string.IsNullOrEmpty(m.XmlSummary) ? null : m.XmlSummary))
            .ToList();
        var dto = new ListMembersResult(
            ContainerSymbolId: container.Id,
            ContainerFqn: container.Fqn,
            ContainerKind: container.Kind,
            Members: structuredMembers);
        return new CallToolResult
        {
            Content = content,
            StructuredContent = JsonSerializer.SerializeToElement(
                dto,
                ToolOutputJsonContext.Default.ListMembersResult),
        };
    }

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(SemanticSearchResult))]
    [ToolTrigger("\"find code that does retry logic\", \"how does this codebase handle authentication\", \"show me the rate-limiting code\"")]
    [Description("Semantic / intent search: encode a natural-language query, find symbols whose code embeddings are nearest by cosine similarity. Complements (not replaces) search_symbols, which does name-fragment FTS5 trigram matching. Returns a top-k list with location, kind, and a similarity score in [-1, 1].")]
    public static Task<CallToolResult> SemanticSearchAsync(
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
                var sw = System.Diagnostics.Stopwatch.StartNew();
                // The next three short-circuits ship as plain prose (no structuredContent) — by
                // design. They're config-/input-/encoder-failure diagnostics, NOT zero-row query
                // results. The "Empty result populates structured content" spec scenario applies
                // only when the search ran cleanly and produced zero matches (handled below by
                // BuildSemanticSearchResult with hits=[]). Distinguishing the two lets agents
                // detect "search was attempted, found nothing" vs. "search couldn't run" without
                // string-matching the prose.
                if (!host.EmbeddingsStore.IsAvailable || !generator.IsAvailable)
                {
                    return DiagnosticResult.Error(
                        "semantic_search disabled: install the embedding model (run with the network on for first start) or remove `--no-embeddings`. The graph itself is fully indexed; other tools (`find_definition`, `search_symbols`, `list_callers`, …) work as normal.");
                }
                if (string.IsNullOrWhiteSpace(query))
                {
                    return DiagnosticResult.Error("semantic_search: provide a non-empty query.");
                }

                var kindFilter = NormaliseKindFilter(kind);

                // Cold-start: the JinaCodeEmbeddingGenerator singleton is lazy-instantiated by DI
                // on first use, so the first `EmbedAsync` call after server start carries the ONNX
                // model load (3-5s). Subsequent calls reuse the loaded model and are sub-second.
                progress?.Report(Format.Progress(0.0, "encoding query"));
                var queryEmbeddings = await generator.EmbedAsync(new[] { query }, ct).ConfigureAwait(false);
                if (queryEmbeddings.Count == 0)
                {
                    return DiagnosticResult.Error("semantic_search: encoder produced no vector for the query.");
                }

                progress?.Report(Format.Progress(0.5, "searching"));
                var hits = await host.EmbeddingsStore.SearchAsync(queryEmbeddings[0], k, kindFilter, ct).ConfigureAwait(false);
                if (hits.Count == 0)
                {
                    return BuildSemanticSearchResult(
                        prose: $"No semantic matches for '{query}'. The graph may not have any embeddings yet — let the indexer's embedding pass complete after a fresh `index` and try again.",
                        query: query,
                        rows: Array.Empty<(EmbeddingHit Hit, SymbolHit Symbol)>(),
                        scopeId: host.Scope.Id,
                        elapsedMs: sw.ElapsedMilliseconds);
                }

                progress?.Report(Format.Progress(0.9, "formatting results"));
                // Apply the size budget at the hit level *before* resolving symbols so omitted rows
                // don't incur per-row GetSymbolByIdAsync calls — important when callers pass a large
                // k and the budget trims to ~30. Resolving only the kept hits keeps prose-row count
                // and structured-array length in lockstep with the budget decision.
                var (hitsKept, hitsOmitted) = OutputBudget.ChooseKeep(hits.Count, OutputBudget.SnippetRowChars);
                IEnumerable<EmbeddingHit> hitsToResolve = hitsOmitted > 0 ? hits.Take(hitsKept) : hits;
                var resolved = new List<(EmbeddingHit Hit, SymbolHit Symbol)>(hitsKept);
                foreach (var h in hitsToResolve)
                {
                    var sym = await host.Store.GetSymbolByIdAsync(h.SymbolId, ct).ConfigureAwait(false);
                    if (sym is not null) resolved.Add((h, sym));
                }
                var sb = new StringBuilder();
                sb.AppendLine($"{resolved.Count} semantic hits for '{query}':");
                if (resolved.Count >= 2)
                {
                    var rows = new List<IReadOnlyList<string>>(resolved.Count);
                    foreach (var (h, sym) in resolved)
                    {
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
                    foreach (var (h, sym) in resolved)
                    {
                        sb.Append($"- score {h.Score:F3} — **{sym.Fqn}** ({Format.KindWithAttrs(sym)}) at {Format.Location(sym.FilePath, sym.StartLine, sym.StartCol)}");
                        var s = Format.OneLineSummary(sym.XmlSummary);
                        if (!string.IsNullOrEmpty(s)) sb.Append(" — _" + s + "_");
                        sb.AppendLine();
                    }
                }
                return BuildSemanticSearchResult(
                    prose: sb.ToString(),
                    query: query,
                    rows: resolved,
                    scopeId: host.Scope.Id,
                    elapsedMs: sw.ElapsedMilliseconds,
                    omittedSize: hitsOmitted);
            }, ct));

    private static CallToolResult BuildSemanticSearchResult(
        string prose,
        string query,
        IReadOnlyList<(EmbeddingHit Hit, SymbolHit Symbol)> rows,
        string scopeId,
        long elapsedMs,
        int omittedSize = 0)
    {
        var content = new List<ContentBlock>(capacity: 2 + rows.Count)
        {
            new TextContentBlock { Text = prose },
        };

        foreach (var (h, sym) in rows)
        {
            content.Add(new ResourceLinkBlock
            {
                Uri = GraphResourceUris.Symbol(sym.Id),
                Name = sym.Fqn,
                Title = $"score {h.Score:F3}: {sym.Fqn}",
                Description = $"{Format.KindWithAttrs(sym)} — {Format.Location(sym.FilePath, sym.StartLine, sym.StartCol)}",
                MimeType = "text/markdown",
            });
        }

        var extras = omittedSize > 0
            ? new[] { ("hits", rows.Count.ToString()), ("omitted_size", omittedSize.ToString()) }
            : new[] { ("hits", rows.Count.ToString()) };
        content.Add(AudienceMetadata.Build(scopeId, elapsedMs, extras));

        var structuredHits = rows
            .Select(r => new SemanticSearchHit(
                Score: r.Hit.Score,
                SymbolId: r.Symbol.Id,
                Fqn: r.Symbol.Fqn,
                Kind: r.Symbol.Kind,
                FilePath: r.Symbol.FilePath,
                Line: r.Symbol.StartLine,
                Column: r.Symbol.StartCol,
                XmlSummary: string.IsNullOrEmpty(r.Symbol.XmlSummary) ? null : r.Symbol.XmlSummary))
            .ToList();
        var dto = new SemanticSearchResult(Query: query, Hits: structuredHits);
        return new CallToolResult
        {
            Content = content,
            StructuredContent = JsonSerializer.SerializeToElement(
                dto,
                ToolOutputJsonContext.Default.SemanticSearchResult),
        };
    }

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(GraphStatsResult))]
    [Description("Print summary counts (files, symbols, references, edges) for the current graph database. Use to confirm the index is populated.")]
    public static Task<CallToolResult> GraphStatsAsync(
        ScopeRouter router,
        [Description(ScopeDescription)] string? scope = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("graph_stats", new { scope }, () =>
            ScopedExecution.RunAsync(router, scope, async host =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var s = await host.Store.GetStatsAsync(ct).ConfigureAwait(false);
                var prose = $"files={s.FileCount} symbols={s.SymbolCount} references={s.ReferenceCount} edges={s.EdgeCount}";
                return BuildGraphStatsResult(
                    prose: prose,
                    stats: s,
                    scopeId: host.Scope.Id,
                    elapsedMs: sw.ElapsedMilliseconds);
            }, ct));

    private static CallToolResult BuildGraphStatsResult(
        string prose,
        GraphStats stats,
        string scopeId,
        long elapsedMs)
    {
        // Per spec: no resource link — graph_stats is a singleton summary, not a list.
        var content = new List<ContentBlock>(capacity: 2)
        {
            new TextContentBlock { Text = prose },
            AudienceMetadata.Build(scopeId, elapsedMs),
        };

        var dto = new GraphStatsResult(
            Files: stats.FileCount,
            Symbols: stats.SymbolCount,
            References: stats.ReferenceCount,
            Edges: stats.EdgeCount);
        return new CallToolResult
        {
            Content = content,
            StructuredContent = JsonSerializer.SerializeToElement(
                dto,
                ToolOutputJsonContext.Default.GraphStatsResult),
        };
    }

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(FindDiagnosticsResult))]
    [ToolTrigger("\"what does this codebase warn about?\" or \"is X being warned on?\"")]
    [Description("Find Roslyn diagnostics (analyzer warnings, compiler errors, etc.) captured during indexing. Filter by severity (default 'warning' = severity >= 2), diagnostic code (e.g. 'CS0618'), and/or symbol.")]
    public static Task<CallToolResult> FindDiagnosticsAsync(
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
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var sev = ParseSeverity(severity);
                if (sev == -1)
                {
                    return DiagnosticResult.Error(
                        $"Unknown severity '{severity}'. Expected one of: hidden | info | warning | error | all.");
                }

                long? symbolId = null;
                string? symbolFqn = null;
                if (!string.IsNullOrWhiteSpace(symbol))
                {
                    var hits = await host.Store.FindSymbolsAsync(symbol, filePathHint: null, limit: 5, ct).ConfigureAwait(false);
                    if (hits.Count == 0) return DiagnosticResult.Build($"No matches for '{symbol}'.");
                    symbolId = hits[0].Id;
                    symbolFqn = hits[0].Fqn;
                }

                var rows = await host.Store.FindDiagnosticsAsync(sev, code, symbolId, limit, ct).ConfigureAwait(false);
                var sb = new StringBuilder();
                var sevLabel = sev is null ? "all" : $">= {SeverityLabel(sev.Value)}";
                var codeClause = string.IsNullOrEmpty(code) ? "" : $", code={code}";
                var symClause = symbolFqn is null ? "" : $", symbol={symbolFqn}";
                sb.AppendLine($"Diagnostics (severity {sevLabel}{codeClause}{symClause}): {rows.Count}");
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
                else if (rows.Count == 1)
                {
                    foreach (var d in rows)
                    {
                        sb.AppendLine($"- **{SeverityLabel(d.Severity)} {d.Code}** at {Format.Location(d.FilePath, d.Line, d.Col)} — {d.Message}");
                    }
                }
                return BuildFindDiagnosticsResult(
                    prose: sb.ToString(),
                    severityFloor: sevLabel,
                    code: code,
                    symbolFqn: symbolFqn,
                    symbolId: symbolId,
                    rows: rows,
                    scopeId: host.Scope.Id,
                    elapsedMs: sw.ElapsedMilliseconds);
            }, ct));

    private static CallToolResult BuildFindDiagnosticsResult(
        string prose,
        string severityFloor,
        string? code,
        string? symbolFqn,
        long? symbolId,
        IReadOnlyList<DiagnosticHit> rows,
        string scopeId,
        long elapsedMs)
    {
        // Per spec: no resource link per row — diagnostics aren't first-class graph entities yet.
        // Just the leading prose + trailing audience-restricted metadata + structured payload.
        var content = new List<ContentBlock>(capacity: 2)
        {
            new TextContentBlock { Text = prose },
        };

        content.Add(AudienceMetadata.Build(
            scopeId,
            elapsedMs,
            ("diagnostics", rows.Count.ToString())));

        var structuredRows = rows
            .Select(d => new FindDiagnosticsRow(
                Severity: SeverityLabel(d.Severity),
                Code: d.Code,
                Message: d.Message,
                FilePath: d.FilePath,
                Line: d.Line,
                Column: d.Col))
            .ToList();
        var dto = new FindDiagnosticsResult(
            SeverityFloor: severityFloor,
            Code: string.IsNullOrEmpty(code) ? null : code,
            Symbol: symbolFqn,
            SymbolId: symbolId,
            Diagnostics: structuredRows);
        return new CallToolResult
        {
            Content = content,
            StructuredContent = JsonSerializer.SerializeToElement(
                dto,
                ToolOutputJsonContext.Default.FindDiagnosticsResult),
        };
    }

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(ListGeneratedFilesResult))]
    [ToolTrigger("\"what's source-generated in this codebase?\"")]
    [Description("List every source-generated file (Roslyn IIncrementalGenerator output: regex source-gen, MVVM Toolkit, ASP.NET routing, JSON source-gen, etc.) tracked by the index. Each row shows the path and the count of symbols emitted from that file.")]
    public static Task<CallToolResult> ListGeneratedFilesAsync(
        ScopeRouter router,
        [Description("Maximum rows (default 100)")] int limit = 100,
        [Description(ScopeDescription)] string? scope = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("list_generated_files", new { limit, scope }, () =>
            ScopedExecution.RunAsync(router, scope, async host =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var rows = await host.Store.ListGeneratedFilesAsync(limit, ct).ConfigureAwait(false);
                var sb = new StringBuilder();
                sb.AppendLine($"Generated files: {rows.Count}");
                if (rows.Count == 0)
                {
                    sb.AppendLine("_(no source-generated documents in this solution)_");
                    return BuildListGeneratedFilesResult(
                        prose: sb.ToString(),
                        rows: Array.Empty<GeneratedFileRow>(),
                        scopeId: host.Scope.Id,
                        elapsedMs: sw.ElapsedMilliseconds);
                }
                sb.AppendLine();
                sb.AppendLine("| Symbols | Path |");
                sb.AppendLine("|--------:|------|");
                foreach (var r in rows)
                {
                    sb.AppendLine($"| {r.SymbolCount} | `{r.FilePath}` |");
                }
                return BuildListGeneratedFilesResult(
                    prose: sb.ToString(),
                    rows: rows,
                    scopeId: host.Scope.Id,
                    elapsedMs: sw.ElapsedMilliseconds);
            }, ct));

    private static CallToolResult BuildListGeneratedFilesResult(
        string prose,
        IReadOnlyList<GeneratedFileRow> rows,
        string scopeId,
        long elapsedMs)
    {
        var content = new List<ContentBlock>(capacity: 2 + rows.Count)
        {
            new TextContentBlock { Text = prose },
        };

        foreach (var r in rows)
        {
            content.Add(new ResourceLinkBlock
            {
                Uri = GraphResourceUris.File(r.FilePath),
                Name = r.FilePath,
                Title = r.FilePath,
                Description = $"{r.SymbolCount} symbol(s) emitted",
                MimeType = "text/markdown",
            });
        }

        content.Add(AudienceMetadata.Build(
            scopeId,
            elapsedMs,
            ("files", rows.Count.ToString())));

        var structuredRows = rows
            .Select(r => new ListGeneratedFilesRow(
                FilePath: r.FilePath,
                SymbolCount: r.SymbolCount))
            .ToList();
        var dto = new ListGeneratedFilesResult(Files: structuredRows);
        return new CallToolResult
        {
            Content = content,
            StructuredContent = JsonSerializer.SerializeToElement(
                dto,
                ToolOutputJsonContext.Default.ListGeneratedFilesResult),
        };
    }

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
