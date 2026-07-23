namespace DevBitsLab.Mcp.SourceGraph.Sdk;

/// <summary>
/// Plugin-supplied module-import resolver. Given an <c>import './foo'</c> (TypeScript), an
/// <c>import bar.baz</c> (Python), or any language's equivalent, returns the absolute path of
/// the file that declares the imported symbols — or <c>null</c> when the import is outside the
/// indexer's reach (third-party module, missing file, unrecognised specifier shape).
///
/// <para>Consumed by <c>TreeSitterLanguageIndexer&lt;T&gt;</c> to resolve cross-file references
/// emitted by the AST walk. When the resolver returns <c>null</c>, the indexer drops the
/// cross-file ref silently — agents see "this symbol's references stop at the module boundary"
/// rather than a broken pointer.</para>
///
/// <para>Per-language implementations encode language-specific module-resolution semantics:
/// TypeScript uses tsconfig <c>paths</c> aliases plus extension probing
/// (<c>./foo</c> → <c>foo.ts</c> → <c>foo.tsx</c> → <c>foo/index.ts</c>); Python looks for
/// <c>__init__.py</c> in candidate directories; Go consults the module path in <c>go.mod</c>.
/// The SDK contract is intentionally narrow — one function, three string-shaped arguments —
/// so per-language plugins compose freely.</para>
/// </summary>
public interface IModuleResolver
{
    /// <summary>
    /// Resolve <paramref name="importSpecifier"/> as it appears in <paramref name="fromAbsolutePath"/>
    /// to the absolute path of the declaring file. Returns <c>null</c> when the import cannot be
    /// resolved within the indexer's reach.
    /// </summary>
    /// <param name="fromAbsolutePath">Absolute path of the file containing the import statement.</param>
    /// <param name="importSpecifier">The raw import string as it appears in source
    /// (<c>"./foo"</c>, <c>"@/utils"</c>, <c>"react"</c>, …); resolution rules vary by language.</param>
    /// <param name="project">The owning <see cref="ILanguageProject"/> when one was discovered;
    /// per-language resolvers use it to read tsconfig <c>paths</c>, <c>baseUrl</c>, etc. without
    /// re-parsing the project file on every import.</param>
    string? Resolve(string fromAbsolutePath, string importSpecifier, ILanguageProject? project);
}
