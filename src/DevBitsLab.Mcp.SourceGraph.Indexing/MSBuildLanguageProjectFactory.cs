using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace DevBitsLab.Mcp.SourceGraph.Indexing;

/// <summary>
/// <see cref="ILanguageProjectFactory"/> backed by a Roslyn <see cref="Solution"/> snapshot.
/// The host binds it to <see cref="RoslynIndexer.SanitizedSolution"/> so project discovery shares
/// the same privacy-filtered project state as the bulk indexer path.
///
/// <para>The workspace constructor remains for API compatibility. Its snapshot is sanitized inside
/// <see cref="DiscoverAsync"/> before any project is exposed.</para>
/// </summary>
public sealed class MSBuildLanguageProjectFactory : IExclusionAwareLanguageProjectFactory
{
    private readonly Func<Solution?> _solutionProvider;

    public MSBuildLanguageProjectFactory(MSBuildWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        _solutionProvider = () => workspace.CurrentSolution;
    }

    public MSBuildLanguageProjectFactory(RoslynIndexer indexer)
    {
        ArgumentNullException.ThrowIfNull(indexer);
        _solutionProvider = () => indexer.SanitizedSolution;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> ProjectMarkers { get; } = new[]
    {
        "*.csproj",
        "*.fsproj",
        "*.vbproj",
        "*.sln",
        "*.slnx",
    };

    /// <inheritdoc />
    public Task<IReadOnlyList<ILanguageProject>> DiscoverAsync(
        string repoRoot,
        CancellationToken ct) =>
        DiscoverAsync(repoRoot, Array.Empty<string>(), ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<ILanguageProject>> DiscoverAsync(
        string repoRoot,
        IReadOnlyList<string> excludePatterns,
        CancellationToken ct)
    {
        var solution = _solutionProvider();
        if (solution is null)
        {
            return Task.FromResult<IReadOnlyList<ILanguageProject>>(Array.Empty<ILanguageProject>());
        }

        // Apply the boundary here as well for callers using the compatibility workspace
        // constructor. An MSBuild workspace is not a sandbox; this only prevents excluded Roslyn
        // inputs from being handed to downstream indexers after project evaluation.
        var pathPolicy = new ScopePathPolicy(Path.GetFullPath(repoRoot), excludePatterns);
        var sanitized = SolutionPrivacySanitizer.SanitizeForScope(
            solution,
            pathPolicy,
            RoslynIndexer.IsBuildGeneratedDocument);
        var projects = sanitized.Projects
            .Select(p => (ILanguageProject)new MSBuildLanguageProject(p))
            .ToList();
        return Task.FromResult<IReadOnlyList<ILanguageProject>>(projects);
    }
}
