using System;
using System.Collections.Generic;

namespace DevBitsLab.Mcp.SourceGraph.Sdk;

/// <summary>
/// Strongly-typed options bag carried on an <see cref="ITreeSitterGrammarConfig"/>. Holds
/// per-language defaults (excludes, max file size, optional resolver factory) so the host's
/// abstract base can apply them without each subclass re-implementing the merge.
///
/// <para><see cref="DefaultExcludes"/> are floors, not ceilings: scope-supplied <c>exclude</c>
/// patterns add to this list, they don't replace it. A language plugin that says
/// "always skip <c>**/node_modules/**</c>" guarantees that operators cannot accidentally
/// index a half-million-file dependency tree even when their <c>paths</c> glob is wide.</para>
/// </summary>
public sealed class LanguageIndexerOptions
{
    public LanguageIndexerOptions(
        IReadOnlyList<string>? defaultExcludes = null,
        int maxFileSizeBytes = 10 * 1024 * 1024,
        Func<IModuleResolver?>? resolverFactory = null)
    {
        DefaultExcludes = defaultExcludes ?? Array.Empty<string>();
        MaxFileSizeBytes = maxFileSizeBytes;
        ResolverFactory = resolverFactory;
    }

    /// <summary>
    /// Glob patterns the host SHALL union into the scope's <c>exclude</c> list before file
    /// discovery. Per-language defaults like <c>"**/node_modules/**"</c> for TS/JS or
    /// <c>"**/__pycache__/**"</c> for Python live here.
    /// </summary>
    public IReadOnlyList<string> DefaultExcludes { get; }

    /// <summary>
    /// Hard cap on file size sent to the parser. Files larger than this are skipped with a
    /// debug-log entry. Default 10 MB — large enough for hand-written code, small enough to
    /// stop a vendored minified bundle from wedging the indexer.
    /// </summary>
    public int MaxFileSizeBytes { get; }

    /// <summary>
    /// Optional factory producing the per-scope <see cref="IModuleResolver"/>. <c>null</c>
    /// means "no cross-file resolution"; the indexer emits intra-file refs only and leaves
    /// cross-file resolution to a later pass (LSP enrichment or similar).
    /// </summary>
    public Func<IModuleResolver?>? ResolverFactory { get; }
}
