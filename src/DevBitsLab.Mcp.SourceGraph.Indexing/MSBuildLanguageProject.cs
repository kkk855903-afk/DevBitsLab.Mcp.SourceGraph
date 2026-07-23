using DevBitsLab.Mcp.SourceGraph.Sdk;

namespace DevBitsLab.Mcp.SourceGraph.Indexing;

/// <summary>
/// <see cref="ILanguageProject"/> wrapper around a Roslyn <see cref="Microsoft.CodeAnalysis.Project"/>.
/// The host stashes one of these on <see cref="IndexContext.Project"/> so per-document calls into the
/// C# indexer can reach the workspace's project handle (compilation, source-generated docs,
/// references) without re-discovering it.
///
/// <para><see cref="FilePaths"/> is materialised once at construction time. The Roslyn project is
/// long-lived for the lifetime of the workspace; if downstream code mutates the document list
/// after this wrapper is built, callers should construct a new <see cref="MSBuildLanguageProject"/>
/// rather than mutating the snapshot.</para>
/// </summary>
public sealed class MSBuildLanguageProject : ILanguageProject
{
    private readonly Microsoft.CodeAnalysis.Project _project;

    public MSBuildLanguageProject(Microsoft.CodeAnalysis.Project project)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        FilePaths = _project.Documents
            .Select(d => d.FilePath)
            .Where(p => !string.IsNullOrEmpty(p))
            .Cast<string>()
            .ToList();
    }

    /// <summary>
    /// The underlying Roslyn project. Exposed for the C# indexer's bulk path so it can reach
    /// the same compilation it would have built itself.
    /// </summary>
    public Microsoft.CodeAnalysis.Project RoslynProject => _project;

    /// <inheritdoc />
    public string Id => _project.FilePath ?? _project.Name;

    /// <inheritdoc />
    public IReadOnlyCollection<string> FilePaths { get; }
}
