using System.Text;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using ModelContextProtocol.Protocol;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

/// <summary>Builds the uniform index-generation/freshness footer for scope-aware queries.</summary>
public static class IndexFreshnessMetadata
{
    public static TextContentBlock Build(IReadOnlyList<ScopeHost> hosts) =>
        Build(hosts.Select(ToSnapshot).ToList());

    public static TextContentBlock Build(IReadOnlyList<IndexFreshnessSnapshot> snapshots) =>
        new()
        {
            Text = BuildText(snapshots),
            Annotations = new Annotations
            {
                Audience = new[] { Role.Assistant },
                Priority = AudienceMetadata.DefaultPriority,
            },
        };

    public static string BuildText(IReadOnlyList<ScopeHost> hosts)
    {
        return BuildText(hosts.Select(ToSnapshot).ToList());
    }

    public static string BuildText(IReadOnlyList<IndexFreshnessSnapshot> snapshots) =>
        "_index: " + BuildFields(snapshots) + "_";

    private static string BuildFields(IReadOnlyList<IndexFreshnessSnapshot> snapshots)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < snapshots.Count; i++)
        {
            if (i > 0) sb.Append("; ");
            var snapshot = snapshots[i];
            sb.Append("scope=`").Append(snapshot.Scope).Append("`, ")
                .Append("generation=").Append(snapshot.Generation).Append(", ")
                .Append("status=").Append(snapshot.Status).Append(", ")
                .Append("indexed_at=")
                .Append(snapshot.IndexedAt is null
                    ? "null"
                    : snapshot.IndexedAt.Value.ToString("O"))
                .Append(", watcher_lag_ms=").Append(snapshot.WatcherLagMs);
        }
        return sb.ToString();
    }

    public static CallToolResult Attach(CallToolResult result, IReadOnlyList<ScopeHost> hosts)
        => Attach(result, hosts.Select(ToSnapshot).ToList());

    public static CallToolResult Attach(
        CallToolResult result,
        IReadOnlyList<IndexFreshnessSnapshot> snapshots)
    {
        result.Content ??= new List<ContentBlock>();
        var existing = result.Content
            .OfType<TextContentBlock>()
            .LastOrDefault(block => block.Text?.StartsWith("_meta: ", StringComparison.Ordinal) == true
                && block.Annotations?.Audience?.Contains(Role.Assistant) == true);
        if (existing is not null)
        {
            existing.Text = existing.Text!.TrimEnd('_')
                + ", index=[" + BuildFields(snapshots) + "]_";
        }
        else
        {
            result.Content.Add(Build(snapshots));
        }
        return result;
    }

    private static IndexFreshnessSnapshot ToSnapshot(ScopeHost host) =>
        new(
            host.Scope.Id,
            host.IndexGeneration,
            host.Status,
            host.LastIndexedAt == default ? null : host.LastIndexedAt,
            host.WatcherLagMs);
}

public sealed record IndexFreshnessSnapshot(
    string Scope,
    long Generation,
    string Status,
    DateTimeOffset? IndexedAt,
    long WatcherLagMs);
