using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace DevBitsLab.Mcp.SourceGraph.Indexing.Xaml;

/// <summary>
/// Immutable project resource-cascade snapshot. Completeness is stored alongside candidates so
/// resource resolution can distinguish "not found" from "not proven absent" when a merged
/// dictionary could not be read or resolved during incremental discovery.
/// </summary>
public sealed class XamlResourceSnapshot
{
    /// <summary>
    /// Copies the supplied collections to prevent a later discovery mutation from changing a
    /// snapshot already observed by concurrent document indexers.
    /// </summary>
    public XamlResourceSnapshot(
        IReadOnlyDictionary<string, IReadOnlyList<ResourceDefinition>> definitions,
        IReadOnlyCollection<string> contributorPaths,
        bool isComplete,
        IReadOnlyList<string> unknownReasons)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(contributorPaths);
        ArgumentNullException.ThrowIfNull(unknownReasons);

        var definitionCopy = new Dictionary<string, IReadOnlyList<ResourceDefinition>>(
            definitions.Count,
            StringComparer.Ordinal);
        foreach (var pair in definitions)
        {
            definitionCopy.Add(
                pair.Key,
                new ReadOnlyCollection<ResourceDefinition>(pair.Value.ToArray()));
        }

        Definitions = new ReadOnlyDictionary<string, IReadOnlyList<ResourceDefinition>>(definitionCopy);
        ContributorPaths = new ReadOnlyCollection<string>(contributorPaths.ToArray());
        UnknownReasons = new ReadOnlyCollection<string>(unknownReasons.ToArray());
        IsComplete = isComplete;
    }

    /// <summary>Visible definitions keyed by <c>x:Key</c>.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ResourceDefinition>> Definitions { get; }

    /// <summary>
    /// Every real App/Generic/merged-dictionary file that contributed to the attempted cascade,
    /// including contributor files that declare no keyed resources.
    /// </summary>
    public IReadOnlyCollection<string> ContributorPaths { get; }

    /// <summary>
    /// Whether every statically supported project resource-cascade branch was resolved.
    /// </summary>
    public bool IsComplete { get; }

    /// <summary>Stable explanations for unsupported or unavailable cascade branches.</summary>
    public IReadOnlyList<string> UnknownReasons { get; }
}
