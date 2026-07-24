using System.Text.RegularExpressions;

namespace DevBitsLab.Mcp.SourceGraph.Core;

/// <summary>
/// A named indexable unit. Each scope owns its own SQLite database under
/// <c>&lt;repo&gt;/.sourcegraph/scopes/&lt;id&gt;.db</c> and one <c>MSBuildWorkspace</c>.
/// Cross-scope queries fan out and merge by <c>canonical_key</c>.
/// </summary>
/// <param name="Id">
///     Kebab-case slug, validated against <c>^[a-z0-9][a-z0-9-]{0,63}$</c>. Becomes the per-scope
///     DB filename (<c>scopes/&lt;id&gt;.db</c>) so it must be filesystem-safe.
/// </param>
/// <param name="Name">Human-readable label; defaults to <see cref="Id"/> when not supplied.</param>
/// <param name="Root">Absolute repo-root path; sources outside this directory are ignored.</param>
/// <param name="ProjectSet">One of solutions / projects / paths. Determines what the indexer walks.</param>
/// <param name="Isolated">
///     When <c>true</c>, the scope is excluded from <c>scope = "*"</c> fan-out and is reachable only
///     when listed explicitly. Used for vendor / generated / fixtures we don't want polluting
///     <c>find_references</c> on production code.
/// </param>
/// <param name="LastIndexedAt">
///     Wall-clock of the most recent successful (full or live) reindex. Surfaced by
///     <c>list_scopes</c> so the agent can tell which scopes are stale.
/// </param>
public sealed record Scope(
    string Id,
    string Name,
    string Root,
    ScopeProjectSet ProjectSet,
    bool Isolated,
    DateTimeOffset LastIndexedAt)
{
    /// <summary>
    /// Optional kebab-case language identifier (<c>"typescript"</c>, <c>"python"</c>, …).
    /// Forward-declared at this version: the loader validates the shape (kebab-case if
    /// present) and surfaces the value via <c>scopes info</c>, but indexer dispatch still
    /// runs purely off file extensions today. A future change will read this hint to
    /// disambiguate when a single extension could plausibly be claimed by multiple plugins.
    /// </summary>
    public string? Language { get; init; }

    /// <summary>
    /// Optional enrichment configuration block. Forward-declared at this version: the loader
    /// parses and validates the shape, the host surfaces it via <c>scopes info</c>, but no
    /// first-party indexer consumes <see cref="ScopeEnrichmentConfig.Lsp"/> yet. The first
    /// runtime consumer will be a follow-up <c>add-typescript-lsp-enrichment</c> change that
    /// wires <c>typescript-language-server</c> against TS scopes; this field locks the
    /// on-disk shape so the follow-up doesn't need to retouch the loader.
    /// </summary>
    public ScopeEnrichmentConfig? Enrichment { get; init; }

    /// <summary>
    /// Optional native-interop indexing configuration. A scope has one explicit ABI target and
    /// one or more translation units so managed/native matching never depends on host-machine
    /// defaults.
    /// </summary>
    public ScopeInteropConfig? Interop { get; init; }
}

/// <summary>
/// Explicit ABI target plus the ordered native translation units indexed for one scope.
/// Translation-unit order is configuration-significant and is preserved during round trips.
/// </summary>
public sealed record ScopeInteropConfig(
    InteropTarget Target,
    IReadOnlyList<InteropTranslationUnitConfig> TranslationUnits);

/// <summary>
/// One native translation unit and the exact compiler arguments used to parse it. Paths are
/// repository-relative and validated by the scope configuration loader.
/// </summary>
public sealed record InteropTranslationUnitConfig(
    string Path,
    string Library,
    IReadOnlyList<string> Arguments,
    string? BinaryPath);

/// <summary>
/// Optional per-scope enrichment configuration. Currently carries one nested <see cref="Lsp"/>
/// block; future enrichment kinds (<c>embeddings</c>, <c>static-analysis</c>) hang off the
/// same root when their indexers ship.
/// </summary>
public sealed record ScopeEnrichmentConfig(LspEnrichmentConfig? Lsp);

/// <summary>
/// LSP-server configuration for enrichment-aware language indexers. <see cref="Command"/> is the
/// executable to launch (resolved against PATH); <see cref="Args"/> is the argument list.
/// Loader validates that <see cref="Command"/> is non-empty when the block is present;
/// <see cref="Args"/> defaults to an empty array.
/// </summary>
public sealed record LspEnrichmentConfig(string Command, IReadOnlyList<string> Args);

/// <summary>
/// Discriminated union over the three ways a scope can describe its source set: a list of
/// <c>.slnx</c> / <c>.sln</c> files, a list of <c>.csproj</c> files, or path-glob patterns.
/// All three resolve to a project set internally; the variant just controls how that resolution
/// happens.
/// </summary>
public abstract record ScopeProjectSet(IReadOnlyList<string> Exclude)
{
    /// <summary>Solution-based scope (<c>.sln</c> / <c>.slnx</c>).</summary>
    public sealed record Solutions(IReadOnlyList<string> Items, IReadOnlyList<string> Exclude) : ScopeProjectSet(Exclude);

    /// <summary>Project-based scope (explicit list of <c>.csproj</c> files).</summary>
    public sealed record Projects(IReadOnlyList<string> Items, IReadOnlyList<string> Exclude) : ScopeProjectSet(Exclude);

    /// <summary>Path-glob scope (e.g. <c>"third_party/**/*.csproj"</c>).</summary>
    public sealed record Paths(IReadOnlyList<string> Globs, IReadOnlyList<string> Exclude) : ScopeProjectSet(Exclude);
}

/// <summary>
/// Validation helpers for scope ids. ScopeId is a plain <see cref="string"/> in the public surface,
/// but every ingest point goes through <see cref="ScopeIdValidator.Validate"/> so a malformed id
/// can never reach the storage layer (where it would become a filename).
/// </summary>
public static class ScopeIdValidator
{
    /// <summary>
    /// Allowed: lowercase letters, digits, and hyphens. Must start with a letter or digit. Length
    /// 1-64 characters. Mirrors the kebab-case slug convention of the rest of the .NET MCP
    /// ecosystem and keeps the id safe to use as a SQLite filename across every supported OS.
    /// </summary>
    private static readonly Regex _pattern = new("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.Compiled);

    public static bool IsValid(string id) => !string.IsNullOrEmpty(id) && _pattern.IsMatch(id);

    /// <summary>
    /// Throws <see cref="ArgumentException"/> with a precise message naming the invalid id when
    /// the input doesn't match the kebab-case slug pattern.
    /// </summary>
    public static void Validate(string id, string parameterName = "scope")
    {
        if (!IsValid(id))
        {
            throw new ArgumentException(
                $"Invalid scope id '{id}'. Must match ^[a-z0-9][a-z0-9-]{{0,63}}$ (kebab-case slug, 1-64 chars).",
                parameterName);
        }
    }
}
