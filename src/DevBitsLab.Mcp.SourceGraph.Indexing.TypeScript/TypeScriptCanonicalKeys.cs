using System.IO;

namespace DevBitsLab.Mcp.SourceGraph.Indexing.TypeScript;

/// <summary>
/// Builds TypeScript / JavaScript / TSX / JSX canonical keys in the documented form
/// <c>&lt;scheme&gt;:&lt;kind-prefix&gt;:&lt;repo-relative-path&gt;::&lt;lexical-path&gt;</c>.
///
/// <list type="table">
/// <listheader><term>Prefix</term><description>Kind family</description></listheader>
/// <item><term>T</term><description>type, class, interface, enum, type-alias</description></item>
/// <item><term>M</term><description>function, method</description></item>
/// <item><term>V</term><description>const, let, var</description></item>
/// <item><term>P</term><description>property, field, enum-member</description></item>
/// <item><term>N</term><description>namespace, module</description></item>
/// </list>
///
/// <para>The lexical path uses <c>::</c> for container nesting and <c>#</c> for the type-property
/// separator. Position disambiguators take the form <c>@L&lt;line&gt;</c> appended to the lexical
/// path when declaration merging produces colliding keys (a class and an interface sharing a
/// name in the same file).</para>
///
/// <para>Examples:
/// <code>
/// ts:M:src/web/foo.ts::greet               // exported function
/// ts:M:src/web/counter.ts::Counter::tick   // method on class Counter
/// ts:T:src/web/types.ts::User              // interface
/// ts:P:src/web/types.ts::User#name         // property of interface
/// ts:T:src/web/foo.ts::User@L42            // disambiguated declaration-merging entry
/// </code></para>
/// </summary>
public static class TypeScriptCanonicalKeys
{
    /// <summary>Type, class, interface, enum, type-alias.</summary>
    public const string PrefixType = "T";

    /// <summary>Function, method.</summary>
    public const string PrefixMethod = "M";

    /// <summary>const, let, var (top-level value declarations).</summary>
    public const string PrefixVariable = "V";

    /// <summary>Property, field, enum-member.</summary>
    public const string PrefixProperty = "P";

    /// <summary>Namespace, module (the file itself, when the source treats it as a namespace).</summary>
    public const string PrefixNamespace = "N";

    /// <summary>
    /// Build a canonical key. <paramref name="scheme"/> is one of <c>ts</c> / <c>tsx</c> /
    /// <c>js</c> / <c>jsx</c>; <paramref name="kindPrefix"/> is one of the constants on this
    /// class. <paramref name="repoRelativePath"/> SHALL use forward slashes (the helper
    /// normalises Windows-style separators); <paramref name="lexicalPath"/> is <c>::</c>-joined
    /// container path. <paramref name="lineDisambiguator"/> is appended as <c>@L&lt;line&gt;</c>
    /// when non-null (used for declaration-merging colliders).
    /// </summary>
    public static string Build(string scheme, string kindPrefix, string repoRelativePath, string lexicalPath, int? lineDisambiguator = null)
    {
        var path = NormalisePath(repoRelativePath);
        var key = $"{scheme}:{kindPrefix}:{path}::{lexicalPath}";
        return lineDisambiguator is null ? key : $"{key}@L{lineDisambiguator.Value}";
    }

    /// <summary>
    /// Build a property/field key with the type-property separator. Equivalent to
    /// <see cref="Build"/> with <paramref name="lexicalPath"/> set to
    /// <c>"&lt;containerLexical&gt;#&lt;memberName&gt;"</c>.
    /// </summary>
    public static string BuildProperty(string scheme, string repoRelativePath, string containerLexical, string memberName, int? lineDisambiguator = null) =>
        Build(scheme, PrefixProperty, repoRelativePath, $"{containerLexical}#{memberName}", lineDisambiguator);

    /// <summary>
    /// Pick the scheme by file extension (case-insensitive). Returns <c>null</c> for unsupported
    /// extensions; callers should use the documented set
    /// <c>{ .ts, .tsx, .js, .jsx, .mjs, .cjs }</c> only.
    /// </summary>
    public static string? SchemeFromExtension(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".ts" => "ts",
            ".tsx" => "tsx",
            ".js" or ".mjs" or ".cjs" => "js",
            ".jsx" => "jsx",
            _ => null,
        };
    }

    private static string NormalisePath(string path) => path.Replace('\\', '/');
}
