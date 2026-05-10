using System.Collections.Concurrent;
using TreeSitter;

namespace DevBitsLab.Mcp.SourceGraph.Indexing.TreeSitter;

/// <summary>
/// Thin wrapper over <c>TreeSitter.DotNet</c>'s API surface. The host's abstract base
/// (<see cref="TreeSitterLanguageIndexer{TGrammarConfig}"/>) goes through this single
/// adapter so any upstream API churn lands in one place rather than scattered across
/// the codebase.
///
/// <para>The adapter caches <see cref="Language"/> instances by name. Constructing a
/// <see cref="Language"/> P/Invokes into <c>libtree-sitter</c> and resolves the grammar's
/// native binary; doing it once per indexer warmup beats once per file.</para>
/// </summary>
public static class TreeSitterAdapter
{
    private static readonly ConcurrentDictionary<string, Language> LanguageCache = new();

    /// <summary>
    /// Resolve a grammar by its tree-sitter identifier (<c>"JavaScript"</c>, <c>"TypeScript"</c>,
    /// <c>"TSX"</c>, <c>"Python"</c>, …). Cached process-wide; same name returns the same
    /// instance. Throws <see cref="System.ArgumentException"/> if the underlying native
    /// binary is missing or the name is unrecognised.
    /// </summary>
    public static Language GetLanguage(string grammarName) =>
        LanguageCache.GetOrAdd(grammarName, static name => new Language(name));

    /// <summary>
    /// Convert a tree-sitter <see cref="Point"/> (0-based row/column) into the SDK's
    /// 1-based line/column convention. Tree-sitter's <see cref="Point.Column"/> is a byte
    /// offset within the line, not a UTF-16 character offset; for ASCII the two coincide
    /// and for the source-graph tools (which surface line:col only as positional hints)
    /// the byte-vs-char distinction is below the noise floor.
    /// </summary>
    public static (int Line, int Column) ToOneBased(Point point) =>
        (point.Row + 1, point.Column + 1);
}
