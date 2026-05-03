using System.ComponentModel;
using System.Text;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Server.Observability;
using DevBitsLab.Mcp.SourceGraph.Storage;
using ModelContextProtocol.Server;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools;

[McpServerToolType]
public static class GraphTools
{
    [McpServerTool]
    [Description("Find the definition of a symbol by name or fully-qualified name. Returns location, kind, signature, accessibility, modifiers, and one-line XML summary for each match. Use for 'where is X defined?'.")]
    public static Task<string> FindDefinitionAsync(
        IGraphStore store,
        [Description("Symbol name (e.g. 'Calculator', 'Divide') or FQN suffix (e.g. 'Calculator.Add', 'Sample.Domain.Calculator')")] string symbol,
        [Description("Optional substring to narrow the search to specific file paths")] string? fileHint = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("find_definition", new { symbol, fileHint }, async () =>
        {
            var hits = await store.FindSymbolsAsync(symbol, fileHint, limit: 25, ct).ConfigureAwait(false);
            if (hits.Count == 0) return $"No definition found for '{symbol}'.";

            var sb = new StringBuilder();
            sb.AppendLine($"Found {hits.Count} match(es) for '{symbol}':");
            sb.AppendLine();
            foreach (var h in hits)
            {
                sb.AppendLine($"- **{h.Fqn}** ({Format.KindWithAttrs(h)})");
                sb.AppendLine($"  - {Format.Location(h.FilePath, h.StartLine, h.StartCol)}");
                if (!string.IsNullOrEmpty(h.Signature)) sb.AppendLine($"  - `{h.Signature}`");
                var oneLine = Format.OneLineSummary(h.XmlSummary);
                if (!string.IsNullOrEmpty(oneLine)) sb.AppendLine($"  - _{oneLine}_");
                var attrs = await store.GetAttributesForSymbolAsync(h.Id, ct).ConfigureAwait(false);
                var attrLine = AttributeFormat.OneLine(attrs);
                if (attrLine is not null) sb.AppendLine($"  - {attrLine}");
            }
            return sb.ToString();
        });

    [McpServerTool]
    [Description("Find every symbol that carries an attribute by short name (e.g. 'HttpGet', 'Obsolete', 'Authorize'). Optional argValue does a trigram match against the attribute's serialised arguments (e.g. argValue='/api/v2' to find route attributes whose path contains that substring). Use for 'find every POST endpoint', 'what's been deprecated?', 'find all DI singletons'.")]
    public static Task<string> FindByAttributeAsync(
        IGraphStore store,
        [Description("Attribute short name (e.g. 'HttpGet', 'Obsolete', 'Authorize'). Trailing 'Attribute' is implied; use the form you'd type in source.")] string name,
        [Description("Optional substring to match against the attribute's arguments via FTS5 trigram (>= 3 chars). E.g. '/api/users' or 'Use Foo'.")] string? argValue = null,
        [Description("Optional kind filter: class|method|property|field|...")] string? kind = null,
        [Description("Maximum results (default 50)")] int limit = 50,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("find_by_attribute", new { name, argValue, kind, limit }, async () =>
        {
            SymbolKind? kindFilter = string.IsNullOrEmpty(kind)
                ? null
                : Enum.TryParse<SymbolKind>(kind, ignoreCase: true, out var k) ? k : null;
            var hits = await store.FindByAttributeAsync(name, argValue, kindFilter, limit, ct).ConfigureAwait(false);
            if (hits.Count == 0)
            {
                var argsClause = string.IsNullOrEmpty(argValue) ? "" : $" with argValue ~ '{argValue}'";
                return $"No symbols carry [{name}]{argsClause}.";
            }
            var sb = new StringBuilder();
            sb.AppendLine($"{hits.Count} symbol(s) carry [{name}]:");
            foreach (var h in hits)
            {
                sb.AppendLine($"- **{h.Fqn}** ({Format.KindWithAttrs(h)}) at {Format.Location(h.FilePath, h.StartLine, h.StartCol)}");
                var attrs = await store.GetAttributesForSymbolAsync(h.Id, ct).ConfigureAwait(false);
                var line = AttributeFormat.OneLine(attrs);
                if (line is not null) sb.AppendLine($"  - {line}");
            }
            return sb.ToString();
        });

    [McpServerTool]
    [Description("Find every place that references a symbol. Resolves the symbol by name/FQN, then lists each call site or type-use as file:line. Use for 'who uses X?' or 'who calls X?'. By default skips refs from source-generated files; pass includeGenerated=true to surface them.")]
    public static Task<string> FindReferencesAsync(
        IGraphStore store,
        [Description("Symbol name or FQN, same matching rules as find_definition")] string symbol,
        [Description("Maximum number of references to return (default 200)")] int limit = 200,
        [Description("Include references from source-generated files (default false)")] bool includeGenerated = false,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("find_references", new { symbol, limit, includeGenerated }, async () =>
        {
            var hits = await store.FindSymbolsAsync(symbol, filePathHint: null, limit: 5, ct).ConfigureAwait(false);
            if (hits.Count == 0) return $"No symbol found for '{symbol}'.";

            var sb = new StringBuilder();
            if (hits.Count > 1)
            {
                sb.AppendLine($"Multiple symbols match '{symbol}'; reporting references for the top match. Other matches: {string.Join(", ", hits.Skip(1).Select(h => h.Fqn))}");
                sb.AppendLine();
            }
            var top = hits[0];
            var refs = await store.FindReferencesAsync(top.Id, includeGenerated, limit, ct).ConfigureAwait(false);
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
            sb.AppendLine($"{refs.Count} reference(s):");
            foreach (var r in refs)
            {
                sb.AppendLine($"- {RefKindLabel(r.Kind)} at {Format.Location(r.FilePath, r.Line, r.Col)}{GeneratedSuffix(r.IsGenerated)}");
            }
            return sb.ToString();
        });

    [McpServerTool]
    [Description("List every symbol declared in a file (classes, methods, properties, etc.). Each row carries kind, accessibility, modifiers, and one-line XML summary. Use for 'what's in this file?' to skip a Read.")]
    public static Task<string> ListSymbolsInFileAsync(
        IGraphStore store,
        [Description("Absolute path or path suffix that uniquely identifies the file")] string path,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("list_symbols_in_file", new { path }, async () =>
        {
            var hits = await store.ListSymbolsInFileAsync(path, ct).ConfigureAwait(false);
            if (hits.Count == 0) return $"No indexed symbols found in '{path}'. The file may not be part of an indexed solution, or may not exist.";

            var sb = new StringBuilder();
            sb.AppendLine($"{hits.Count} symbol(s) in {hits[0].FilePath}:");
            foreach (var h in hits)
            {
                sb.AppendLine($"- L{h.StartLine}: **{h.Name}** ({Format.KindWithAttrs(h)}) — {h.Fqn}");
                if (!string.IsNullOrEmpty(h.Signature)) sb.AppendLine($"    `{h.Signature}`");
                var oneLine = Format.OneLineSummary(h.XmlSummary);
                if (!string.IsNullOrEmpty(oneLine)) sb.AppendLine($"    _{oneLine}_");
                var attrs = await store.GetAttributesForSymbolAsync(h.Id, ct).ConfigureAwait(false);
                var line = AttributeFormat.OneLine(attrs);
                if (line is not null) sb.AppendLine($"    {line}");
            }
            return sb.ToString();
        });

    [McpServerTool]
    [Description("List inbound edges into a target symbol. Default kind=calls (i.e. callers). Use kind=uses_type to find consumers of a type, kind=overrides for derived implementations, kind=implements_member for members satisfying an interface, kind=instantiates for `new T()` sites, kind=throws for throw sites, or kind=all for every edge kind.")]
    public static Task<string> ListCallersAsync(
        IGraphStore store,
        [Description("Target symbol name or FQN")] string symbol,
        [Description("Maximum number of results to return (default 50)")] int limit = 50,
        [Description("Edge kind to walk: calls (default) | uses_type | overrides | implements_member | instantiates | throws | all")] string? kind = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("list_callers", new { symbol, limit, kind }, async () =>
        {
            var (edgeKind, label, errorMsg) = ParseEdgeKind(kind);
            if (errorMsg is not null) return errorMsg;

            var hits = await store.FindSymbolsAsync(symbol, filePathHint: null, limit: 5, ct).ConfigureAwait(false);
            if (hits.Count == 0) return $"No symbol found for '{symbol}'.";
            var top = hits[0];
            var callers = await store.ListCallersAsync(top.Id, limit, edgeKind, ct).ConfigureAwait(false);
            var sb = new StringBuilder();
            sb.AppendLine($"Inbound `{label}` to **{top.Fqn}** ({KindLabel(top.Kind)}):");
            if (callers.Count == 0) { sb.AppendLine("- (none)"); return sb.ToString(); }
            foreach (var c in callers)
            {
                sb.AppendLine($"- **{c.Fqn}** ({KindLabel(c.Kind)}) at {Format.Location(c.FilePath, c.StartLine, c.StartCol)}");
            }
            return sb.ToString();
        });

    [McpServerTool]
    [Description("List outbound edges from the target symbol. Default kind=calls (callees). Use kind=uses_type for types touched in this member's signature/body, kind=overrides for the base it overrides, kind=implements_member for the interface member it satisfies, kind=instantiates for types it constructs, kind=throws for exception types it throws, or kind=all.")]
    public static Task<string> ListCalleesAsync(
        IGraphStore store,
        [Description("Source symbol name or FQN")] string symbol,
        [Description("Maximum number of results to return (default 50)")] int limit = 50,
        [Description("Edge kind to walk: calls (default) | uses_type | overrides | implements_member | instantiates | throws | all")] string? kind = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("list_callees", new { symbol, limit, kind }, async () =>
        {
            var (edgeKind, label, errorMsg) = ParseEdgeKind(kind);
            if (errorMsg is not null) return errorMsg;

            var hits = await store.FindSymbolsAsync(symbol, filePathHint: null, limit: 5, ct).ConfigureAwait(false);
            if (hits.Count == 0) return $"No symbol found for '{symbol}'.";
            var top = hits[0];
            var callees = await store.ListCalleesAsync(top.Id, limit, edgeKind, ct).ConfigureAwait(false);
            var sb = new StringBuilder();
            sb.AppendLine($"Outbound `{label}` from **{top.Fqn}** ({KindLabel(top.Kind)}):");
            if (callees.Count == 0) { sb.AppendLine("- (none)"); return sb.ToString(); }
            foreach (var c in callees)
            {
                sb.AppendLine($"- **{c.Fqn}** ({KindLabel(c.Kind)}) at {Format.Location(c.FilePath, c.StartLine, c.StartCol)}");
            }
            return sb.ToString();
        });

    [McpServerTool]
    [Description("Find every concrete member that implements an interface member (uses ImplementsMember edges). Use for 'who implements IGreeter.Greet?'.")]
    public static Task<string> FindImplementationsAsync(
        IGraphStore store,
        [Description("Interface member name or FQN, e.g. 'IGreeter.Greet'")] string symbol,
        [Description("Include abstract base members in the result (default false)")] bool includeAbstract = false,
        [Description("Maximum results (default 50)")] int limit = 50,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("find_implementations", new { symbol, includeAbstract, limit }, async () =>
        {
            var hits = await store.FindSymbolsAsync(symbol, filePathHint: null, limit: 5, ct).ConfigureAwait(false);
            if (hits.Count == 0) return $"No symbol found for '{symbol}'.";
            var top = hits[0];
            var impls = await store.ListImplementationsAsync(top.Id, limit, ct).ConfigureAwait(false);
            var filtered = includeAbstract
                ? impls
                : impls.Where(h => !(h.Signature?.Contains("abstract", StringComparison.Ordinal) ?? false)).ToList();
            var sb = new StringBuilder();
            sb.AppendLine($"Implementations of **{top.Fqn}** ({KindLabel(top.Kind)}):");
            if (filtered.Count == 0) { sb.AppendLine("- (none)"); return sb.ToString(); }
            foreach (var c in filtered)
            {
                sb.AppendLine($"- **{c.Fqn}** ({KindLabel(c.Kind)}) at {Format.Location(c.FilePath, c.StartLine, c.StartCol)}");
            }
            return sb.ToString();
        });

    [McpServerTool]
    [Description("Free-text search for symbols by partial name, FQN, or signature using FTS5. Use this when you only have a fragment ('Calc', 'Greet', 'Async').")]
    public static Task<string> SearchSymbolsAsync(
        IGraphStore store,
        [Description("Search query (a few characters, words, or substring of name/FQN/signature)")] string query,
        [Description("Optional kind filter: class|method|property|field|interface|namespace|...")] string? kind = null,
        [Description("Maximum results (default 25)")] int topK = 25,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("search_symbols", new { query, kind, topK }, async () =>
        {
            SymbolKind? kindFilter = string.IsNullOrEmpty(kind)
                ? null
                : Enum.TryParse<SymbolKind>(kind, ignoreCase: true, out var k) ? k : null;

            var hits = await store.SearchSymbolsAsync(query, kindFilter, topK, ct).ConfigureAwait(false);
            if (hits.Count == 0) return $"No symbols match '{query}'.";
            var sb = new StringBuilder();
            sb.AppendLine($"{hits.Count} match(es) for '{query}':");
            foreach (var h in hits)
            {
                sb.AppendLine($"- **{h.Fqn}** ({KindLabel(h.Kind)}) at {Format.Location(h.FilePath, h.StartLine, h.StartCol)}");
            }
            return sb.ToString();
        });

    [McpServerTool]
    [Description("Get the immediate graph neighborhood of a symbol: callers, callees, and inheritance/implements edges. Use to orient yourself around a symbol before diving in. Default kind=calls; pass kind=uses_type, overrides, implements_member, instantiates, throws, or all to inspect other edge layers.")]
    public static Task<string> NeighborhoodAsync(
        IGraphStore store,
        [Description("Symbol name or FQN")] string symbol,
        [Description("Max items per category (default 20)")] int perCategory = 20,
        [Description("Edge kind to walk: calls (default) | uses_type | overrides | implements_member | instantiates | throws | all")] string? kind = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("neighborhood", new { symbol, perCategory, kind }, async () =>
        {
            var (edgeKind, label, errorMsg) = ParseEdgeKind(kind);
            if (errorMsg is not null) return errorMsg;

            var hits = await store.FindSymbolsAsync(symbol, filePathHint: null, limit: 5, ct).ConfigureAwait(false);
            if (hits.Count == 0) return $"No symbol found for '{symbol}'.";
            var top = hits[0];
            var callers = await store.ListCallersAsync(top.Id, perCategory, edgeKind, ct).ConfigureAwait(false);
            var callees = await store.ListCalleesAsync(top.Id, perCategory, edgeKind, ct).ConfigureAwait(false);

            var sb = new StringBuilder();
            sb.AppendLine($"Neighborhood of **{top.Fqn}** ({Format.KindWithAttrs(top)}) [kind={label}]");
            sb.AppendLine($"definition: {Format.Location(top.FilePath, top.StartLine, top.StartCol)}");
            var topSummary = Format.OneLineSummary(top.XmlSummary);
            if (!string.IsNullOrEmpty(topSummary)) sb.AppendLine($"_{topSummary}_");
            var topAttrs = await store.GetAttributesForSymbolAsync(top.Id, ct).ConfigureAwait(false);
            var topAttrLine = AttributeFormat.OneLine(topAttrs);
            if (topAttrLine is not null) sb.AppendLine(topAttrLine);
            sb.AppendLine();
            sb.AppendLine($"### Inbound ({callers.Count})");
            foreach (var c in callers)
            {
                sb.Append($"- {c.Fqn} ({Format.KindWithAttrs(c)}) — {Format.Location(c.FilePath, c.StartLine, c.StartCol)}");
                var s = Format.OneLineSummary(c.XmlSummary);
                if (!string.IsNullOrEmpty(s)) sb.Append(" — _" + s + "_");
                sb.AppendLine();
                var ca = await store.GetAttributesForSymbolAsync(c.Id, ct).ConfigureAwait(false);
                var caLine = AttributeFormat.OneLine(ca);
                if (caLine is not null) sb.AppendLine($"  {caLine}");
            }
            if (callers.Count == 0) sb.AppendLine("- (none)");
            sb.AppendLine();
            sb.AppendLine($"### Outbound ({callees.Count})");
            foreach (var c in callees)
            {
                sb.Append($"- {c.Fqn} ({Format.KindWithAttrs(c)}) — {Format.Location(c.FilePath, c.StartLine, c.StartCol)}");
                var s = Format.OneLineSummary(c.XmlSummary);
                if (!string.IsNullOrEmpty(s)) sb.Append(" — _" + s + "_");
                sb.AppendLine();
                var ca = await store.GetAttributesForSymbolAsync(c.Id, ct).ConfigureAwait(false);
                var caLine = AttributeFormat.OneLine(ca);
                if (caLine is not null) sb.AppendLine($"  {caLine}");
            }
            if (callees.Count == 0) sb.AppendLine("- (none)");
            return sb.ToString();
        });

    [McpServerTool]
    [Description("Summarize a namespace or directory: lists the most-referenced symbols (highest in-degree) so you know what to read first. Use 'Sample.Domain' or a path fragment.")]
    public static Task<string> ModuleSummaryAsync(
        IGraphStore store,
        [Description("Namespace (e.g. 'Sample.Domain') or path-substring that identifies the module")] string namespaceOrPath,
        [Description("Top-K most-referenced symbols to return (default 25)")] int topK = 25,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("module_summary", new { namespaceOrPath, topK }, async () =>
        {
            var rows = await store.ModuleSummaryAsync(namespaceOrPath, topK, ct).ConfigureAwait(false);
            if (rows.Count == 0) return $"No symbols matched module '{namespaceOrPath}'.";
            var sb = new StringBuilder();
            sb.AppendLine($"Top {rows.Count} symbol(s) in '{namespaceOrPath}' (by inbound calls):");
            foreach (var row in rows)
            {
                sb.Append($"- in-deg {row.InDegree,3} — **{row.Symbol.Fqn}** ({Format.KindWithAttrs(row.Symbol)}) at {Format.Location(row.Symbol.FilePath, row.Symbol.StartLine, row.Symbol.StartCol)}");
                var s = Format.OneLineSummary(row.Symbol.XmlSummary);
                if (!string.IsNullOrEmpty(s)) sb.Append(" — _" + s + "_");
                sb.AppendLine();
                var attrs = await store.GetAttributesForSymbolAsync(row.Symbol.Id, ct).ConfigureAwait(false);
                var line = AttributeFormat.OneLine(attrs);
                if (line is not null) sb.AppendLine($"  - {line}");
            }
            return sb.ToString();
        });

    [McpServerTool]
    [Description("Compute the transitive set of upstream callers for a symbol (impact of changing it). Walks the call graph backward up to maxDepth. Default kind=calls; pass kind=uses_type, overrides, implements_member, instantiates, throws, or all to traverse other edge layers.")]
    public static Task<string> ImpactOfChangeAsync(
        IGraphStore store,
        [Description("Symbol name or FQN")] string symbol,
        [Description("Maximum traversal depth (default 4)")] int maxDepth = 4,
        [Description("Maximum results (default 100)")] int limit = 100,
        [Description("Edge kind to walk: calls (default) | uses_type | overrides | implements_member | instantiates | throws | all")] string? kind = null,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("impact_of_change", new { symbol, maxDepth, limit, kind }, async () =>
        {
            var (edgeKind, label, errorMsg) = ParseEdgeKind(kind);
            if (errorMsg is not null) return errorMsg;

            var hits = await store.FindSymbolsAsync(symbol, filePathHint: null, limit: 5, ct).ConfigureAwait(false);
            if (hits.Count == 0) return $"No symbol found for '{symbol}'.";
            var top = hits[0];
            var rows = await store.ImpactOfChangeAsync(top.Id, maxDepth, limit, edgeKind, ct).ConfigureAwait(false);
            var sb = new StringBuilder();
            sb.AppendLine($"Upstream impact of **{top.Fqn}** ({KindLabel(top.Kind)}) [kind={label}] up to depth {maxDepth}:");
            if (rows.Count == 0) { sb.AppendLine("- (no upstream callers found in graph)"); return sb.ToString(); }
            foreach (var r in rows)
            {
                sb.AppendLine($"- d{r.Depth}: **{r.Symbol.Fqn}** ({KindLabel(r.Symbol.Kind)}) at {Format.Location(r.Symbol.FilePath, r.Symbol.StartLine, r.Symbol.StartCol)}");
            }
            return sb.ToString();
        });

    [McpServerTool]
    [Description("List the direct members of a container (class, struct, interface, namespace) by FQN, optionally filtered by accessibility. Walks the populated container_id chain — replaces 'list_symbols_in_file then filter'.")]
    public static Task<string> ListMembersAsync(
        IGraphStore store,
        [Description("Container FQN (e.g. 'Sample.Domain.Calculator', 'Sample.Domain'). Resolved with the same matching rules as find_definition; the top match is used.")] string container,
        [Description("Reserved for a future change that follows inherits/implements edges; currently ignored.")] bool includeInherited = false,
        [Description("Optional accessibility filter: public|internal|private|protected|protected internal|private protected.")] string? accessibility = null,
        [Description("Maximum members to return (default 200)")] int limit = 200,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("list_members", new { container, includeInherited, accessibility, limit }, async () =>
        {
            var hits = await store.FindSymbolsAsync(container, filePathHint: null, limit: 5, ct).ConfigureAwait(false);
            if (hits.Count == 0) return $"No symbol found for container '{container}'.";
            var top = hits[0];

            int? accFilter = ParseAccessibility(accessibility);
            if (!string.IsNullOrEmpty(accessibility) && accFilter is null)
            {
                return $"Unknown accessibility '{accessibility}'. Valid: public, internal, private, protected, protected internal, private protected.";
            }

            var members = await store.ListMembersAsync(top.Id, accFilter, limit, ct).ConfigureAwait(false);
            var sb = new StringBuilder();
            var filterNote = accFilter is null ? "" : $" (accessibility = {accessibility})";
            sb.AppendLine($"{members.Count} member(s) of **{top.Fqn}** ({Format.KindWithAttrs(top)}){filterNote}:");
            if (includeInherited)
            {
                sb.AppendLine("_(includeInherited is reserved for a future change; only direct members are returned.)_");
            }
            if (members.Count == 0) { sb.AppendLine("- (none)"); return sb.ToString(); }
            foreach (var m in members)
            {
                sb.Append($"- L{m.StartLine}: **{m.Name}** ({Format.KindWithAttrs(m)}) — `{m.Signature ?? m.Fqn}`");
                var s = Format.OneLineSummary(m.XmlSummary);
                if (!string.IsNullOrEmpty(s)) sb.Append(" — _" + s + "_");
                sb.AppendLine();
            }
            return sb.ToString();
        });

    [McpServerTool]
    [Description("Print summary counts (files, symbols, references, edges) for the current graph database. Use to confirm the index is populated.")]
    public static Task<string> GraphStatsAsync(IGraphStore store, CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("graph_stats", null, async () =>
        {
            var s = await store.GetStatsAsync(ct).ConfigureAwait(false);
            return $"files={s.FileCount} symbols={s.SymbolCount} references={s.ReferenceCount} edges={s.EdgeCount}";
        });

    [McpServerTool]
    [Description("Find Roslyn diagnostics (analyzer warnings, compiler errors, etc.) captured during indexing. Filter by severity (default 'warning' = severity >= 2), diagnostic code (e.g. 'CS0618'), and/or symbol. Use for 'what does this codebase warn about?' or 'is X being warned on?'.")]
    public static Task<string> FindDiagnosticsAsync(
        IGraphStore store,
        [Description("Severity floor: hidden | info | warning (default) | error | all. Numeric values 0-3 also accepted.")] string? severity = "warning",
        [Description("Optional diagnostic code filter, e.g. 'CS0618' for [Obsolete] usage")] string? code = null,
        [Description("Optional symbol name/FQN to scope the lookup to a single symbol's diagnostics")] string? symbol = null,
        [Description("Maximum rows to return (default 100)")] int limit = 100,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("find_diagnostics", new { severity, code, symbol, limit }, async () =>
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
                var hits = await store.FindSymbolsAsync(symbol, filePathHint: null, limit: 5, ct).ConfigureAwait(false);
                if (hits.Count == 0) return $"No symbol found for '{symbol}'.";
                symbolId = hits[0].Id;
                symbolFqn = hits[0].Fqn;
            }

            var rows = await store.FindDiagnosticsAsync(sev, code, symbolId, limit, ct).ConfigureAwait(false);
            var sb = new StringBuilder();
            var sevLabel = sev is null ? "all" : $">= {SeverityLabel(sev.Value)}";
            var codeClause = string.IsNullOrEmpty(code) ? "" : $", code={code}";
            var symClause = symbolFqn is null ? "" : $", symbol={symbolFqn}";
            sb.AppendLine($"Diagnostics (severity {sevLabel}{codeClause}{symClause}): {rows.Count}");
            if (rows.Count == 0) return sb.ToString();
            foreach (var d in rows)
            {
                sb.AppendLine($"- **{SeverityLabel(d.Severity)} {d.Code}** at {Format.Location(d.FilePath, d.Line, d.Col)} — {d.Message}");
            }
            return sb.ToString();
        });

    [McpServerTool]
    [Description("List every source-generated file (Roslyn IIncrementalGenerator output: regex source-gen, MVVM Toolkit, ASP.NET routing, JSON source-gen, etc.) tracked by the index. Each row shows the path and the count of symbols emitted from that file.")]
    public static Task<string> ListGeneratedFilesAsync(
        IGraphStore store,
        [Description("Maximum rows (default 100)")] int limit = 100,
        CancellationToken ct = default) =>
        ToolMetrics.TrackAsync("list_generated_files", new { limit }, async () =>
        {
            var rows = await store.ListGeneratedFilesAsync(limit, ct).ConfigureAwait(false);
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
        });

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
            if (!string.IsNullOrEmpty(ToolMetrics.LogPath))
            {
                sb.AppendLine();
                sb.AppendLine($"_Persistent JSONL log: `{ToolMetrics.LogPath}`_");
            }
            return sb.ToString();
        });

    private static string KindLabel(SymbolKind kind) => kind switch
    {
        SymbolKind.Namespace => "namespace",
        SymbolKind.Class => "class",
        SymbolKind.Struct => "struct",
        SymbolKind.Interface => "interface",
        SymbolKind.Enum => "enum",
        SymbolKind.EnumMember => "enum member",
        SymbolKind.Delegate => "delegate",
        SymbolKind.Method => "method",
        SymbolKind.Constructor => "ctor",
        SymbolKind.Property => "property",
        SymbolKind.Field => "field",
        SymbolKind.Event => "event",
        SymbolKind.Local => "local",
        SymbolKind.Parameter => "parameter",
        SymbolKind.TypeParameter => "type parameter",
        _ => "?",
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

    internal static string KindLabelOf(SymbolKind kind) => KindLabel(kind);

    /// <summary>
    /// Parses the optional `kind` parameter accepted by list_callers/list_callees/neighborhood/impact_of_change.
    /// Returns (edgeKind, label, errorMsg). errorMsg non-null means caller should short-circuit with it.
    /// edgeKind null means "walk every kind" (kind = "all"). label is the canonical lowercase token.
    /// </summary>
    private static (EdgeKind? edgeKind, string label, string? errorMsg) ParseEdgeKind(string? kind)
    {
        if (string.IsNullOrEmpty(kind)) return (EdgeKind.Calls, "calls", null);
        var normalised = kind.Trim().ToLowerInvariant();
        return normalised switch
        {
            "calls" or "call" => (EdgeKind.Calls, "calls", null),
            "uses_type" or "usestype" or "uses-type" => (EdgeKind.UsesType, "uses_type", null),
            "overrides" or "overrides_member" or "override" => (EdgeKind.OverridesMember, "overrides_member", null),
            "implements" or "implements_member" or "impl" => (EdgeKind.ImplementsMember, "implements_member", null),
            "instantiates" or "new" => (EdgeKind.Instantiates, "instantiates", null),
            "throws" or "throw" => (EdgeKind.Throws, "throws", null),
            "inherits" or "inherit" => (EdgeKind.Inherits, "inherits", null),
            "all" or "any" or "*" => (null, "all", null),
            _ => (EdgeKind.Calls, "calls", $"Unknown kind '{kind}'. Expected one of: calls | uses_type | overrides | implements_member | instantiates | throws | all."),
        };
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
}

internal static class AttributeFormat
{
    /// <summary>
    /// Render the attribute set as a single annotation line, e.g.
    /// <c>attributes: [HttpGet("/api/users"), Authorize, Obsolete]</c>. Returns
    /// <c>null</c> when there are no attributes so callers can skip the line entirely.
    /// </summary>
    public static string? OneLine(IReadOnlyList<AttributeRecord> attrs)
    {
        if (attrs.Count == 0) return null;
        var sb = new StringBuilder();
        sb.Append("attributes: [");
        for (var i = 0; i < attrs.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(attrs[i].Name);
            var preview = ArgPreview(attrs[i].ArgsJson);
            if (preview is not null) sb.Append(preview);
        }
        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>
    /// Render an attribute card row for the <c>graph://symbol/{id}</c> resource.
    /// Includes the full name and the raw args JSON so an agent can pick out the
    /// values it needs.
    /// </summary>
    public static string Card(AttributeRecord attr)
    {
        var args = string.IsNullOrEmpty(attr.ArgsJson) ? "" : $" — `{attr.ArgsJson}`";
        var link = attr.AttributeSymbolId is null ? "" : $" → symbol#{attr.AttributeSymbolId}";
        return $"- `[{attr.Name}]` ({attr.FullName}){link}{args}";
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
