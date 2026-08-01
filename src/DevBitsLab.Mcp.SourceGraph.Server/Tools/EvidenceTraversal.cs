using System.Text;
using DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;
using DevBitsLab.Mcp.SourceGraph.Storage;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools;

/// <summary>
/// Shared evidence-first edge loading and bounded upstream traversal used by the Phase 1
/// caller/callee/impact tools. Logical edges without occurrence evidence never leave this layer.
/// </summary>
internal static class EvidenceTraversal
{
    internal static async Task<BoundedRelations> LoadInboundAsync(
        IGraphStore store,
        SymbolHit target,
        int limit,
        string? edgeKind,
        CancellationToken ct)
    {
        var stored = await store.ListAuditableInboundEdgesAsync(
            target.Id,
            checked(limit + 1),
            edgeKind,
            ct).ConfigureAwait(false);
        var relations = new List<AuditableRelation>(Math.Min(stored.Count, limit));
        foreach (var edge in stored.Take(limit))
        {
            var hop = await TraceCallPathTools.BuildAuditableHopAsync(
                store,
                edge.Symbol,
                target,
                edge.Relation,
                ct).ConfigureAwait(false);
            if (hop is null) continue;
            relations.Add(new AuditableRelation(
                edge.Symbol,
                target,
                hop,
                edge.PayloadJson));
        }
        return new BoundedRelations(relations, stored.Count > limit);
    }

    internal static async Task<BoundedRelations> LoadOutboundAsync(
        IGraphStore store,
        SymbolHit source,
        int limit,
        string? edgeKind,
        CancellationToken ct)
    {
        var stored = await store.ListAuditableOutboundEdgesAsync(
            source.Id,
            checked(limit + 1),
            edgeKind,
            ct).ConfigureAwait(false);
        var relations = new List<AuditableRelation>(Math.Min(stored.Count, limit));
        foreach (var edge in stored.Take(limit))
        {
            var hop = await TraceCallPathTools.BuildAuditableHopAsync(
                store,
                source,
                edge.Symbol,
                edge.Relation,
                ct).ConfigureAwait(false);
            if (hop is null) continue;
            relations.Add(new AuditableRelation(
                source,
                edge.Symbol,
                hop,
                edge.PayloadJson));
        }
        return new BoundedRelations(relations, stored.Count > limit);
    }

    internal static async Task<ImpactTraversal> TraceImpactAsync(
        IGraphStore store,
        SymbolHit target,
        int maxDepth,
        int limit,
        string? edgeKind,
        CancellationToken ct)
    {
        var queue = new Queue<ImpactState>();
        queue.Enqueue(new ImpactState(
            target,
            Depth: 0,
            Path: Array.Empty<TraceCallPathHop>()));
        var visited = new HashSet<long> { target.Id };
        var rows = new List<AuditableImpact>(limit);
        var expandedNodes = 0;
        var truncated = false;
        var branchLimit = checked(limit + 1);

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var state = queue.Dequeue();
            expandedNodes++;

            var inbound = await store.ListAuditableInboundEdgesAsync(
                state.Current.Id,
                branchLimit,
                edgeKind,
                ct).ConfigureAwait(false);
            if (inbound.Count == branchLimit)
            {
                // The query may have another relation beyond the sentinel row. Keep this
                // conservative even when the visible prefix only points at visited nodes.
                truncated = true;
            }

            if (state.Depth >= maxDepth)
            {
                if (inbound.Any(edge => !visited.Contains(edge.Symbol.Id)))
                {
                    truncated = true;
                }
                continue;
            }

            foreach (var edge in inbound)
            {
                if (visited.Contains(edge.Symbol.Id)) continue;
                if (rows.Count >= limit)
                {
                    return new ImpactTraversal(rows, Truncated: true, expandedNodes);
                }

                var hop = await TraceCallPathTools.BuildAuditableHopAsync(
                    store,
                    edge.Symbol,
                    state.Current,
                    edge.Relation,
                    ct).ConfigureAwait(false);
                if (hop is null) continue;

                visited.Add(edge.Symbol.Id);
                var path = new List<TraceCallPathHop>(state.Path.Count + 1) { hop };
                path.AddRange(state.Path);
                var confidence = TraceCallPathTools.ConfidenceName(
                    path.Min(pathHop =>
                        TraceCallPathTools.ConfidenceValue(pathHop.Confidence)));
                rows.Add(new AuditableImpact(
                    edge.Symbol,
                    state.Current,
                    state.Depth + 1,
                    confidence,
                    path));
                queue.Enqueue(new ImpactState(
                    edge.Symbol,
                    state.Depth + 1,
                    path));
            }
        }

        return new ImpactTraversal(rows, truncated, expandedNodes);
    }

    internal static void AppendRelations(
        StringBuilder sb,
        IReadOnlyList<AuditableRelation> relations)
    {
        if (relations.Count == 0) return;
        sb.AppendLine();
        sb.AppendLine("Evidence:");
        foreach (var relation in relations)
        {
            AppendHop(sb, relation.Hop, prefix: "- ");
        }
    }

    internal static void AppendImpactPaths(
        StringBuilder sb,
        IReadOnlyList<AuditableImpact> rows)
    {
        if (rows.Count == 0) return;
        sb.AppendLine();
        sb.AppendLine("Auditable predecessor paths:");
        foreach (var row in rows)
        {
            sb.Append("- d")
              .Append(row.Depth)
              .Append(" `")
              .Append(CanonicalIdentity(TraceCallPathTools.MapSymbol(row.Symbol)))
              .Append("` via predecessor `")
              .Append(CanonicalIdentity(TraceCallPathTools.MapSymbol(row.Predecessor)))
              .Append("` [")
              .Append(row.Confidence)
              .AppendLine("]");
            foreach (var hop in row.Path)
            {
                AppendHop(sb, hop, prefix: "  - ");
            }
        }
    }

    internal static void AppendImpactPredecessors(
        StringBuilder sb,
        IReadOnlyList<AuditableImpact> rows)
    {
        if (rows.Count == 0) return;
        sb.AppendLine();
        sb.AppendLine("Predecessor chains:");
        foreach (var row in rows)
        {
            sb.Append("- d")
              .Append(row.Depth)
              .Append(" `")
              .Append(CanonicalIdentity(TraceCallPathTools.MapSymbol(row.Symbol)))
              .Append("` via `")
              .Append(CanonicalIdentity(TraceCallPathTools.MapSymbol(row.Predecessor)))
              .Append("` [")
              .Append(row.Confidence)
              .AppendLine("]");
        }
    }

    private static void AppendHop(
        StringBuilder sb,
        TraceCallPathHop hop,
        string prefix)
    {
        sb.Append(prefix)
          .Append('`')
          .Append(CanonicalIdentity(hop.From))
          .Append("` -[")
          .Append(hop.Relation)
          .Append("]-> `")
          .Append(CanonicalIdentity(hop.To))
          .Append("` [")
          .Append(hop.Confidence)
          .AppendLine("]");
        foreach (var evidence in hop.Evidence)
        {
            sb.Append("    - `")
              .Append(evidence.FilePath)
              .Append(':')
              .Append(evidence.StartLine)
              .Append(':')
              .Append(evidence.StartColumn)
              .Append("..")
              .Append(evidence.EndLine)
              .Append(':')
              .Append(evidence.EndColumn)
              .Append("` (1-based half-open) [")
              .Append(evidence.Confidence)
              .Append(", ")
              .Append(evidence.Producer)
              .AppendLine("]");
        }
        if (hop.EvidenceTruncated)
        {
            sb.AppendLine("    - (additional edge evidence omitted by the per-hop cap)");
        }
    }

    private static string CanonicalIdentity(TraceCallPathSymbol symbol) =>
        symbol.CanonicalKey ?? symbol.Fqn;

    private sealed record ImpactState(
        SymbolHit Current,
        int Depth,
        IReadOnlyList<TraceCallPathHop> Path);
}

internal sealed record AuditableRelation(
    SymbolHit Source,
    SymbolHit Target,
    TraceCallPathHop Hop,
    string? PayloadJson);

internal sealed record BoundedRelations(
    IReadOnlyList<AuditableRelation> Relations,
    bool Truncated);

internal sealed record AuditableImpact(
    SymbolHit Symbol,
    SymbolHit Predecessor,
    int Depth,
    string Confidence,
    IReadOnlyList<TraceCallPathHop> Path);

internal sealed record ImpactTraversal(
    IReadOnlyList<AuditableImpact> Rows,
    bool Truncated,
    int ExpandedNodes);
