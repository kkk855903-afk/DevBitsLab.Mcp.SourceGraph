using System.Collections.Generic;

namespace DevBitsLab.Mcp.SourceGraph.Indexing.Xaml;

/// <summary>Outcome of resolving one XAML resource key against a visibility cascade.</summary>
public enum ResourceResolutionStatus
{
    Missing,
    Resolved,
    Ambiguous,
}

/// <summary>
/// Evidence-first resource resolution. A key resolves only when exactly one visible declaration
/// exists; duplicate declarations remain explicit instead of being collapsed by dictionary
/// insertion order.
/// </summary>
public sealed record ResourceResolution(
    ResourceResolutionStatus Status,
    IReadOnlyList<ResourceDefinition> Candidates)
{
    /// <summary>The sole declaration when <see cref="Status"/> is <see cref="ResourceResolutionStatus.Resolved"/>.</summary>
    public ResourceDefinition? Definition =>
        Status == ResourceResolutionStatus.Resolved && Candidates.Count == 1
            ? Candidates[0]
            : null;

    public static ResourceResolution Missing { get; } =
        new(ResourceResolutionStatus.Missing, Array.Empty<ResourceDefinition>());
}
