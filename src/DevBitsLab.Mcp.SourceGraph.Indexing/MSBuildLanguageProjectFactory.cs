using DevBitsLab.Mcp.SourceGraph.Sdk;
using Microsoft.CodeAnalysis.MSBuild;

namespace DevBitsLab.Mcp.SourceGraph.Indexing;

/// <summary>
/// <see cref="ILanguageProjectFactory"/> backed by an <see cref="MSBuildWorkspace"/>. The host
/// hands the same workspace it uses for <see cref="RoslynIndexer"/> so the discovered projects
/// share state (compilations, source-generated docs) with the bulk indexer path.
///
/// <para><see cref="DiscoverAsync"/> reflects the workspace's solution at call time — if the host
/// reloads the workspace between calls, the next discovery picks up the change without the factory
/// needing its own invalidation hook.</para>
/// </summary>
public sealed class MSBuildLanguageProjectFactory : ILanguageProjectFactory
{
    private readonly MSBuildWorkspace _workspace;

    public MSBuildLanguageProjectFactory(MSBuildWorkspace workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
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
    public Task<IReadOnlyList<ILanguageProject>> DiscoverAsync(string repoRoot, CancellationToken ct)
    {
        // The workspace owns solution open/close; the factory just exposes whatever is currently
        // loaded. Re-discovering after a solution reload will surface the new project list.
        var projects = _workspace.CurrentSolution.Projects
            .Select(p => (ILanguageProject)new MSBuildLanguageProject(p))
            .ToList();
        return Task.FromResult<IReadOnlyList<ILanguageProject>>(projects);
    }
}
