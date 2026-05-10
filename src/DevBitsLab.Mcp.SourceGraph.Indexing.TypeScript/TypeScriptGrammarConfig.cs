using DevBitsLab.Mcp.SourceGraph.Sdk;

namespace DevBitsLab.Mcp.SourceGraph.Indexing.TypeScript;

/// <summary>
/// <see cref="ITreeSitterGrammarConfig"/> implementation for the TypeScript / JavaScript /
/// TSX / JSX language family. <see cref="GrammarName"/> reports <c>"TypeScript"</c> as the
/// default, but the actual grammar selection is per-file: the indexer overrides
/// <c>GetGrammarName</c> to dispatch <c>.ts</c> → TypeScript, <c>.tsx</c> → TSX,
/// <c>.js</c> / <c>.jsx</c> → JavaScript at parse time.
///
/// <para>Default excludes catch the build-output and dependency directories every TS / JS
/// repo accumulates. Defaults are floors — operator-supplied <c>exclude</c> patterns add to
/// the list, never override it. Indexing <c>node_modules</c> requires opting in via an
/// <c>isolated: true</c> scope rooted at the package(s) of interest.</para>
/// </summary>
public sealed class TypeScriptGrammarConfig : ITreeSitterGrammarConfig
{
    /// <summary>The four extensions this language family handles.</summary>
    public static readonly IReadOnlyCollection<string> Extensions = new[] { ".ts", ".tsx", ".js", ".jsx" };

    /// <summary>The default excludes documented in the design — applied as floors.</summary>
    public static readonly IReadOnlyList<string> StandardExcludes = new[]
    {
        "**/node_modules/**",
        "**/dist/**",
        "**/.next/**",
        "**/build/**",
        "**/coverage/**",
        "**/.cache/**",
        "**/.parcel-cache/**",
        "**/out/**",
    };

    public TypeScriptGrammarConfig()
    {
        Options = new LanguageIndexerOptions(defaultExcludes: StandardExcludes);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The indexer overrides <c>GetGrammarName</c> for per-file grammar dispatch; this default
    /// is the safe fallback when the override does not fire (e.g. when a future caller invokes
    /// the abstract base on a path with no matching extension). <c>"TypeScript"</c> is the most
    /// permissive of the three TS-family grammars and parses plain JavaScript fine.
    /// </remarks>
    public string GrammarName => "TypeScript";

    /// <inheritdoc />
    public LanguageIndexerOptions Options { get; }

    /// <inheritdoc />
    public IReadOnlyCollection<string> FileExtensions => Extensions;
}
