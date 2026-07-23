using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DevBitsLab.Mcp.SourceGraph.Sdk;

/// <summary>
/// A unit of project-level state owned by a language plugin. Hung off
/// <see cref="IndexContext.Project"/> so per-document indexer calls that belong to the same
/// project can share state (resource caches, type maps, language-service handles) without the
/// host needing to know what's inside.
///
/// <para>The interface is intentionally minimal: <see cref="Id"/> for identity and
/// <see cref="FilePaths"/> for routing. Plugins extend it with their own subclass that holds the
/// heavy state. The C# pathway provides <c>MSBuildLanguageProject</c> wrapping a Roslyn
/// <c>Project</c>; future TypeScript / XAML plugins will hold their own analogues.</para>
/// </summary>
public interface ILanguageProject
{
    /// <summary>
    /// Stable identifier for the project. Convention: absolute path of the project anchor file
    /// (csproj, tsconfig.json, …) using the host's path separator. Used for logging and for
    /// dictionary keys when the host routes documents to the right project.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Absolute paths of files this project considers part of itself. Read by the host to build
    /// a file → project map; the dispatcher uses this map to populate
    /// <see cref="IndexContext.Project"/> on each <see cref="ILanguageIndexer.IndexAsync"/> call.
    /// </summary>
    IReadOnlyCollection<string> FilePaths { get; }
}

/// <summary>
/// Optional project capability for languages whose cross-file edges require declaration symbols
/// to exist before consuming files are flushed. Hosts that recognise this interface SHALL
/// schedule the absolute paths in <see cref="DeclarationFilePaths"/> before the remaining
/// <see cref="ILanguageProject.FilePaths"/> for the same indexing pass.
/// </summary>
/// <remarks>
/// The collection is an ordering hint, not an expanded read boundary: every entry MUST also
/// appear in <see cref="ILanguageProject.FilePaths"/>, and hosts still apply their scope/privacy
/// policy before opening it. Projects that do not implement this interface retain ordinary host
/// ordering.
/// </remarks>
public interface IDeclarationFirstLanguageProject : ILanguageProject
{
    /// <summary>
    /// Absolute, project-owned source paths that declare symbols targeted by other project files.
    /// Entries SHOULD be deterministic and duplicate-free.
    /// </summary>
    IReadOnlyCollection<string> DeclarationFilePaths { get; }
}

/// <summary>
/// Plugin-supplied factory that discovers <see cref="ILanguageProject"/> instances under a repo
/// root. The host invokes <see cref="DiscoverAsync"/> once per scope at startup and again when
/// <c>.sourcegraph.json</c> changes.
/// </summary>
public interface ILanguageProjectFactory
{
    /// <summary>
    /// Glob patterns identifying the files that anchor a project of this type
    /// (e.g. <c>["*.csproj", "*.fsproj"]</c> for C#, <c>["tsconfig.json"]</c> for TypeScript).
    /// The host uses these to short-circuit factory invocation when a scope contains no anchors.
    /// </summary>
    IReadOnlyCollection<string> ProjectMarkers { get; }

    /// <summary>
    /// Walk <paramref name="repoRoot"/>, locate every project of this factory's kind, and return
    /// an <see cref="ILanguageProject"/> per project. Implementations MAY hold heavy state
    /// (workspaces, language service instances) on the returned project objects so subsequent
    /// per-document indexer calls reuse it.
    /// </summary>
    Task<IReadOnlyList<ILanguageProject>> DiscoverAsync(string repoRoot, CancellationToken ct);
}

/// <summary>
/// Optional project-factory capability for hosts that carry repository-relative scope excludes.
/// Implementations apply the patterns before reading project anchors or project-owned files.
/// Older factories remain compatible through <see cref="ILanguageProjectFactory"/>; the host
/// still filters their returned file maps.
/// </summary>
public interface IExclusionAwareLanguageProjectFactory : ILanguageProjectFactory
{
    /// <summary>
    /// Discover projects while excluding every path matched by <paramref name="excludePatterns"/>.
    /// Patterns narrow the host's mandatory privacy boundary and cannot expand it.
    /// </summary>
    Task<IReadOnlyList<ILanguageProject>> DiscoverAsync(
        string repoRoot,
        IReadOnlyList<string> excludePatterns,
        CancellationToken ct);
}
