using System;

namespace DevBitsLab.Mcp.SourceGraph.Indexing.Xaml;

/// <summary>
/// Shared, evidence-first outcome vocabulary for XAML semantic resolution.
/// </summary>
public enum XamlResolutionStatus
{
    Resolved,
    Missing,
    Ambiguous,
    Unsupported,
    Incomplete,
    Unknown,
}

/// <summary>
/// One explicit XAML resolution result. <see cref="Reason"/> is a stable,
/// machine-readable explanation and is always present, including for successful results.
/// </summary>
public sealed record XamlResolutionOutcome
{
    public XamlResolutionOutcome(XamlResolutionStatus status, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A resolution outcome reason is required.", nameof(reason));
        }

        Status = status;
        Reason = reason;
    }

    public XamlResolutionStatus Status { get; }

    public string Reason { get; }

    public string StatusName => Status.ToString().ToLowerInvariant();
}
