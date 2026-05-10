using System.Collections.Generic;

namespace DevBitsLab.Mcp.SourceGraph.Sdk;

/// <summary>
/// Generic-constraint contract for <c>TreeSitterLanguageIndexer&lt;TGrammarConfig&gt;</c>'s
/// per-language config object. A plugin implements this once and supplies the result as a
/// type parameter; the abstract base reads <see cref="GrammarName"/> to ask the runtime for
/// the right grammar handle, and <see cref="Options"/> to apply default excludes / size caps.
///
/// <para>Kept as an interface (not a concrete record) so plugins can hang language-specific
/// extra state on their implementation: tsconfig path on a TypeScript config, module-search
/// roots on a Python config, etc. The host only reads the two members declared here; the rest
/// is plugin-private.</para>
/// </summary>
public interface ITreeSitterGrammarConfig
{
    /// <summary>
    /// The tree-sitter grammar name this indexer drives, as accepted by the runtime's
    /// <c>Language</c> constructor (e.g. <c>"TypeScript"</c>, <c>"TSX"</c>,
    /// <c>"JavaScript"</c>, <c>"Python"</c>). The runtime resolves the name to a bundled
    /// grammar binary; an unknown name surfaces as a load error at indexer warmup.
    /// </summary>
    string GrammarName { get; }

    /// <summary>The per-language defaults the host applies before dispatching files.</summary>
    LanguageIndexerOptions Options { get; }

    /// <summary>
    /// File extensions this grammar config handles. The abstract base re-exports them as
    /// <see cref="ILanguageIndexer.FileExtensions"/> so subclasses that implement
    /// <see cref="ITreeSitterGrammarConfig"/> on the same type don't repeat themselves.
    /// </summary>
    IReadOnlyCollection<string> FileExtensions { get; }
}
