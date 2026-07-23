using System;
using System.Collections.Generic;

namespace DevBitsLab.Mcp.SourceGraph.Sdk.Validation;

/// <summary>
/// Validates plugin-emitted canonical keys against the URI-prefixed convention.
///
/// <para>Format: <c>&lt;scheme&gt;:&lt;rest&gt;</c> where <c>&lt;scheme&gt;</c> is a kebab-case
/// identifier from the reserved-and-enforced set, and <c>&lt;rest&gt;</c> is plugin-defined but
/// MUST NOT contain backslash characters (paths embedded in keys are repo-relative with forward
/// slashes regardless of OS, so cross-platform identity is preserved).</para>
///
/// <para>Reserved-and-enforced schemes at this SDK version: <c>csharp</c>, <c>xaml</c>,
/// <c>js</c>, <c>ts</c>, <c>jsx</c>, <c>tsx</c>, <c>c</c>, <c>cpp</c>, and
/// <c>proto</c>.</para>
///
/// <para>Reserved-but-not-yet-enforced (documented for cross-language joins; emissions using these
/// schemes are rejected by the host until the corresponding language indexer ships): <c>vbnet</c>,
/// <c>fsharp</c>, <c>razor</c>, <c>vue</c>, <c>svelte</c>, <c>python</c>, <c>go</c>, <c>rust</c>.</para>
/// </summary>
public static class CanonicalKeyValidator
{
    private static readonly HashSet<string> _enforcedSchemes = new(StringComparer.Ordinal)
    {
        "csharp",
        "xaml",
        // Lifted by add-typescript-language-indexer (SDK 2.2.0): the TypeScript indexer emits
        // these. The four cover .ts, .tsx, .js, .jsx file extensions respectively.
        "js",
        "ts",
        "jsx",
        "tsx",
        // Added for the MedInteropLens native/protobuf analyzer contracts. The schemes are
        // accepted before the analyzers themselves ship so third-party adapters can emit the
        // same stable cross-language identities.
        "c",
        "cpp",
        "proto",
    };

    private static readonly HashSet<string> _reservedFutureSchemes = new(StringComparer.Ordinal)
    {
        "vbnet",
        "fsharp",
        "razor",
        "vue",
        "svelte",
        // Documented across the live extensibility/spec.md as reserved-for-future-use; listed
        // here so the validator's error hint mentions "reserved for a future language" rather
        // than a generic "unknown scheme" when a plugin emits one of these too early.
        "python",
        "go",
        "rust",
    };

    /// <summary>
    /// The schemes accepted by the host at this SDK version. Plugins emitting any other scheme
    /// (including reserved-but-not-yet-enforced names) cause an <see cref="ArgumentException"/>.
    /// </summary>
    public static IReadOnlyCollection<string> EnforcedSchemes => _enforcedSchemes;

    /// <summary>
    /// Schemes documented as "reserved for future use" but not yet accepted by the host. Listed
    /// here so cross-language emitters can spot the conflict before runtime; the validator still
    /// rejects emissions that use these schemes until the corresponding language ships.
    /// </summary>
    public static IReadOnlyCollection<string> ReservedFutureSchemes => _reservedFutureSchemes;

    /// <summary>Returns <c>true</c> when <paramref name="canonicalKey"/> matches the URI convention.</summary>
    public static bool IsValid(string? canonicalKey)
    {
        if (canonicalKey is null || canonicalKey.Length == 0) return false;
        var colon = canonicalKey.IndexOf(':');
        if (colon <= 0 || colon == canonicalKey.Length - 1) return false;
        var scheme = canonicalKey.Substring(0, colon);
        if (!KebabCaseValidator.IsValid(scheme)) return false;
        if (!_enforcedSchemes.Contains(scheme)) return false;
        var rest = canonicalKey.Substring(colon + 1);
        if (rest.IndexOf('\\') >= 0) return false;
        return true;
    }

    /// <summary>
    /// Throws <see cref="ArgumentException"/> when <paramref name="canonicalKey"/> is not a valid
    /// scheme-prefixed canonical key. The message identifies the specific failure (missing colon,
    /// unknown scheme, backslash path, etc.) so plugin authors can correct quickly.
    /// </summary>
    public static void Validate(string? canonicalKey, string paramName)
    {
        if (canonicalKey is null || canonicalKey.Length == 0)
        {
            throw new ArgumentException("Canonical key must be non-empty.", paramName);
        }
        var colon = canonicalKey.IndexOf(':');
        if (colon <= 0)
        {
            throw new ArgumentException(
                $"Canonical key '{canonicalKey}' is missing a scheme prefix (expected '<scheme>:<rest>').",
                paramName);
        }
        if (colon == canonicalKey.Length - 1)
        {
            throw new ArgumentException(
                $"Canonical key '{canonicalKey}' has an empty body after the scheme prefix.",
                paramName);
        }
        var scheme = canonicalKey.Substring(0, colon);
        if (!KebabCaseValidator.IsValid(scheme))
        {
            throw new ArgumentException(
                $"Canonical key '{canonicalKey}' uses scheme '{scheme}' which is not a valid kebab-case identifier.",
                paramName);
        }
        if (!_enforcedSchemes.Contains(scheme))
        {
            var hint = _reservedFutureSchemes.Contains(scheme)
                ? " (this scheme is reserved for a future language but not yet enforced; emit with one of the active schemes for now)"
                : "";
            throw new ArgumentException(
                $"Canonical key '{canonicalKey}' uses unknown scheme '{scheme}'. Enforced schemes at this SDK version: {string.Join(", ", EnforcedSchemes)}.{hint}",
                paramName);
        }
        var rest = canonicalKey.Substring(colon + 1);
        if (rest.IndexOf('\\') >= 0)
        {
            throw new ArgumentException(
                $"Canonical key '{canonicalKey}' contains a backslash; paths in keys MUST be repo-relative with forward slashes regardless of OS.",
                paramName);
        }
    }
}
