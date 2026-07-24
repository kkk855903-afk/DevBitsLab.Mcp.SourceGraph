using System;
using System.Collections.Generic;

namespace DevBitsLab.Mcp.SourceGraph.Indexing.Xaml;

/// <summary>
/// Compatibility view of <see cref="XamlResolutionStatus"/> for existing resource callers.
/// </summary>
public enum ResourceResolutionStatus
{
    Missing,
    Resolved,
    Ambiguous,
    Unsupported,
    Incomplete,
    Unknown,
}

/// <summary>
/// Evidence-first resource resolution. A key resolves only when exactly one visible declaration
/// exists; duplicate declarations remain explicit instead of being collapsed by dictionary
/// insertion order.
/// </summary>
public sealed record ResourceResolution
{
    public ResourceResolution(
        XamlResolutionOutcome outcome,
        IReadOnlyList<ResourceDefinition> candidates)
    {
        Outcome = outcome ?? throw new ArgumentNullException(nameof(outcome));
        Candidates = candidates ?? throw new ArgumentNullException(nameof(candidates));
    }

    public ResourceResolution(
        ResourceResolutionStatus status,
        IReadOnlyList<ResourceDefinition> candidates)
        : this(
            new XamlResolutionOutcome(
                ToSharedStatus(status),
                "legacy-resource-" + status.ToString().ToLowerInvariant()),
            candidates)
    {
    }

    /// <summary>The shared six-state outcome and stable reason.</summary>
    public XamlResolutionOutcome Outcome { get; }

    public XamlResolutionStatus ResolutionStatus => Outcome.Status;

    public string Reason => Outcome.Reason;

    /// <summary>Compatibility status for existing resource-resolution callers.</summary>
    public ResourceResolutionStatus Status => ToResourceStatus(Outcome.Status);

    public IReadOnlyList<ResourceDefinition> Candidates { get; }

    /// <summary>The sole declaration when <see cref="Status"/> is <see cref="ResourceResolutionStatus.Resolved"/>.</summary>
    public ResourceDefinition? Definition =>
        Status == ResourceResolutionStatus.Resolved && Candidates.Count == 1
            ? Candidates[0]
            : null;

    public static ResourceResolution Missing { get; } =
        new(
            new XamlResolutionOutcome(
                XamlResolutionStatus.Missing,
                "resource-key-not-visible"),
            Array.Empty<ResourceDefinition>());

    private static XamlResolutionStatus ToSharedStatus(ResourceResolutionStatus status) =>
        status switch
        {
            ResourceResolutionStatus.Resolved => XamlResolutionStatus.Resolved,
            ResourceResolutionStatus.Missing => XamlResolutionStatus.Missing,
            ResourceResolutionStatus.Ambiguous => XamlResolutionStatus.Ambiguous,
            ResourceResolutionStatus.Unsupported => XamlResolutionStatus.Unsupported,
            ResourceResolutionStatus.Incomplete => XamlResolutionStatus.Incomplete,
            _ => XamlResolutionStatus.Unknown,
        };

    private static ResourceResolutionStatus ToResourceStatus(XamlResolutionStatus status) =>
        status switch
        {
            XamlResolutionStatus.Resolved => ResourceResolutionStatus.Resolved,
            XamlResolutionStatus.Missing => ResourceResolutionStatus.Missing,
            XamlResolutionStatus.Ambiguous => ResourceResolutionStatus.Ambiguous,
            XamlResolutionStatus.Unsupported => ResourceResolutionStatus.Unsupported,
            XamlResolutionStatus.Incomplete => ResourceResolutionStatus.Incomplete,
            _ => ResourceResolutionStatus.Unknown,
        };
}
