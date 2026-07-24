using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DevBitsLab.Mcp.SourceGraph.Sdk;

/// <summary>
/// Plugin contract for a per-file-extension indexer. The built-in C# <c>RoslynLanguageIndexer</c>
/// implements this for <c>.cs</c>; third-party plugins can register additional extensions
/// (<c>.py</c>, <c>.ts</c>, ...).
///
/// <para>
/// The host invokes <see cref="IndexAsync"/> once per document whose extension appears in
/// <see cref="FileExtensions"/>. The returned <see cref="IndexEvent"/> stream is fanned out to
/// the storage layer; each variant maps to a single SQL upsert/insert. Plugins do NOT touch
/// <c>IGraphStore</c> directly — the indirection lets the host enforce schema invariants and
/// keeps the plugin assembly small.
/// </para>
/// </summary>
public interface ILanguageIndexer
{
    /// <summary>
    /// File extensions handled by this indexer. Lowercase, leading dot included
    /// (e.g. <c>{ ".cs" }</c>). The host treats this as a set (duplicates ignored). Each
    /// extension may be claimed by AT MOST one indexer per scope; a duplicate registration causes
    /// the latter plugin to be marked <c>failed</c>.
    /// </summary>
    IReadOnlyCollection<string> FileExtensions { get; }

    /// <summary>
    /// Walk the document described by <paramref name="ctx"/> and produce a sequence of
    /// <see cref="IndexEvent"/>s. Returning an empty list means "the document had no indexable
    /// content"; throwing means "this plugin failed for this document" — the host catches and
    /// marks the plugin failed for the file.
    /// </summary>
    Task<IReadOnlyList<IndexEvent>> IndexAsync(IndexContext ctx, CancellationToken ct);
}

/// <summary>
/// Optional capability for an <see cref="ILanguageIndexer"/> that requires the host to enforce a
/// hard byte limit while reading its source file. Hosts that understand this capability reject a
/// growing or oversized file before allocating or forwarding bytes beyond the declared limit.
/// </summary>
public interface IBoundedSourceLanguageIndexer
{
    /// <summary>Maximum source bytes accepted for one document. Must be positive.</summary>
    int MaximumSourceSizeBytes { get; }
}

/// <summary>
/// Helper base class with sensible no-op defaults. A plugin that handles <c>.txt</c> can derive
/// from <see cref="LanguageIndexerBase"/>, override <see cref="LanguageIndexerBase.FileExtensions"/>
/// and <see cref="LanguageIndexerBase.IndexAsync"/>, and skip the boilerplate.
/// </summary>
public abstract class LanguageIndexerBase : ILanguageIndexer
{
    /// <inheritdoc />
    public abstract IReadOnlyCollection<string> FileExtensions { get; }

    /// <inheritdoc />
    public virtual Task<IReadOnlyList<IndexEvent>> IndexAsync(IndexContext ctx, CancellationToken ct)
    {
        IReadOnlyList<IndexEvent> empty = System.Array.Empty<IndexEvent>();
        return Task.FromResult(empty);
    }
}

/// <summary>
/// Per-document context handed to <see cref="ILanguageIndexer.IndexAsync"/>. Plugins read the
/// file's bytes (or text) and the absolute path; the host owns the higher-level abstractions
/// (workspace, symbol map, diagnostics).
/// </summary>
public sealed class IndexContext
{
    public IndexContext(string filePath, byte[] contents, string scopeId, string repoRoot, ILanguageProject? project = null)
        : this(
            filePath,
            contents,
            scopeId,
            repoRoot,
            project,
            System.Array.Empty<string>())
    {
    }

    /// <summary>
    /// Creates a context with the scope's configured exclude snapshot. This overload preserves
    /// the original constructor for binary compatibility while allowing built-in and plugin
    /// adapters to apply the host boundary before reading secondary inputs.
    /// </summary>
    public IndexContext(
        string filePath,
        byte[] contents,
        string scopeId,
        string repoRoot,
        ILanguageProject? project,
        IReadOnlyList<string> excludePatterns)
    {
        if (excludePatterns is null)
        {
            throw new System.ArgumentNullException(nameof(excludePatterns));
        }
        FilePath = filePath;
        Contents = contents;
        ScopeId = scopeId;
        RepoRoot = repoRoot;
        Project = project;
        ExcludePatterns = new List<string>(excludePatterns).AsReadOnly();
    }

    /// <summary>Absolute path to the file being indexed.</summary>
    public string FilePath { get; }

    /// <summary>Raw file contents (UTF-8 bytes).</summary>
    public byte[] Contents { get; }

    /// <summary>Id of the scope this document belongs to.</summary>
    public string ScopeId { get; }

    /// <summary>Repo root directory (i.e. <see cref="ScopeId"/>'s <c>Scope.Root</c>).</summary>
    public string RepoRoot { get; }

    /// <summary>
    /// The language project that owns this file, when one was discovered by an
    /// <see cref="ILanguageProjectFactory"/>. Plugin-private state (resource caches, language
    /// service handles, …) lives on the plugin's own <see cref="ILanguageProject"/> subclass and
    /// is reused across every per-document call belonging to the same project. <c>null</c> when
    /// the file isn't owned by any discovered project.
    /// </summary>
    public ILanguageProject? Project { get; }

    /// <summary>
    /// Scope-configured excludes captured by the host. These complement mandatory privacy and
    /// repository-containment checks; adapters that follow includes or other secondary inputs
    /// must apply both before reading bytes.
    /// </summary>
    public IReadOnlyList<string> ExcludePatterns { get; }

    /// <summary>Convenience: <see cref="System.Text.Encoding.UTF8"/>-decoded text view of <see cref="Contents"/>.</summary>
    public string GetText() => System.Text.Encoding.UTF8.GetString(Contents);
}
