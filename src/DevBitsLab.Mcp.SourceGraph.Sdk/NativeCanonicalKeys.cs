using System;
using System.Collections.Generic;

namespace DevBitsLab.Mcp.SourceGraph.Sdk;

/// <summary>
/// Builds deterministic C/C++ canonical keys in the form
/// <c>&lt;scheme&gt;:&lt;kind-prefix&gt;:&lt;repo-relative-path&gt;::&lt;qualified-name&gt;</c>.
/// The source path is part of the identity because translation-unit-local declarations may share
/// the same spelling. Function identities SHOULD include their parameter types in
/// the qualified-name value when overloads are possible.
/// </summary>
public static class NativeCanonicalKeys
{
    /// <summary>Function declaration.</summary>
    public const string PrefixFunction = "F";

    /// <summary>Struct, union, enum, class, or other named type declaration.</summary>
    public const string PrefixType = "T";

    /// <summary>typedef or using-alias declaration.</summary>
    public const string PrefixTypeAlias = "A";

    /// <summary>Exported native ABI entry point.</summary>
    public const string PrefixExport = "E";

    private static readonly HashSet<string> _schemes = new(StringComparer.Ordinal)
    {
        "c",
        "cpp",
    };

    private static readonly HashSet<string> _kindPrefixes = new(StringComparer.Ordinal)
    {
        PrefixFunction,
        PrefixType,
        PrefixTypeAlias,
        PrefixExport,
    };

    /// <summary>
    /// Builds a native canonical key. Windows separators in
    /// <paramref name="repoRelativePath"/> are normalised to forward slashes.
    /// </summary>
    private static string Build(
        string scheme,
        string kindPrefix,
        string repoRelativePath,
        string qualifiedName)
    {
        if (!_schemes.Contains(scheme))
        {
            throw new ArgumentException(
                "Native canonical-key scheme must be either 'c' or 'cpp'.",
                nameof(scheme));
        }
        if (!_kindPrefixes.Contains(kindPrefix))
        {
            throw new ArgumentException(
                "Native canonical-key kind prefix must be one of F, T, A, or E.",
                nameof(kindPrefix));
        }

        var path = NormalizeRepoRelativePath(
            repoRelativePath,
            nameof(repoRelativePath));
        var name = NormalizeIdentity(qualifiedName, nameof(qualifiedName));
        return $"{scheme}:{kindPrefix}:{path}::{name}";
    }

    /// <summary>Builds a key for a C/C++ function declaration.</summary>
    public static string ForFunction(
        string scheme,
        string repoRelativePath,
        string qualifiedName) =>
        Build(scheme, PrefixFunction, repoRelativePath, qualifiedName);

    /// <summary>
    /// Builds a key for a C++ member function. Member functions share the <c>F</c> prefix with
    /// free functions; <paramref name="typeQualifiedMethodSignature"/> MUST carry the declaring
    /// type (for example <c>medical::Scanner::Run(int)</c>).
    /// </summary>
    public static string ForMethod(
        string repoRelativePath,
        string typeQualifiedMethodSignature) =>
        Build("cpp", PrefixFunction, repoRelativePath, typeQualifiedMethodSignature);

    /// <summary>Builds a key for a C/C++ named type declaration.</summary>
    public static string ForType(
        string scheme,
        string repoRelativePath,
        string qualifiedName) =>
        Build(scheme, PrefixType, repoRelativePath, qualifiedName);

    /// <summary>Builds a key for a C/C++ typedef or using-alias declaration.</summary>
    public static string ForTypeAlias(
        string scheme,
        string repoRelativePath,
        string qualifiedName) =>
        Build(scheme, PrefixTypeAlias, repoRelativePath, qualifiedName);

    /// <summary>
    /// Builds a key for a native export declaration. The analyzer chooses <c>c</c> for an
    /// <c>extern "C"</c> entry point even when its source file has a C++ extension.
    /// </summary>
    public static string ForExport(
        string scheme,
        string repoRelativePath,
        string entryPoint) =>
        Build(scheme, PrefixExport, repoRelativePath, entryPoint);

    private static string NormalizeRepoRelativePath(
        string value,
        string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                "Repository-relative path must be non-empty.",
                paramName);
        }

        var normalized = value.Trim().Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized.Substring(2);
        }
        if (normalized.Length == 0
            || normalized[0] == '/'
            || (normalized.Length >= 2
                && char.IsLetter(normalized[0])
                && normalized[1] == ':'))
        {
            throw new ArgumentException(
                "Native canonical-key paths must be repository-relative.",
                paramName);
        }

        var segments = normalized.Split('/');
        var retained = new List<string>(segments.Length);
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }
            if (segment == "..")
            {
                throw new ArgumentException(
                    "Native canonical-key paths must not contain parent traversal.",
                    paramName);
            }
            if (segment.IndexOf(':') >= 0
                || ContainsControlCharacter(segment))
            {
                throw new ArgumentException(
                    "Native canonical-key paths contain an invalid segment.",
                    paramName);
            }
            retained.Add(segment);
        }
        if (retained.Count == 0)
        {
            throw new ArgumentException(
                "Repository-relative path must identify a file.",
                paramName);
        }
        return string.Join("/", retained);
    }

    private static string NormalizeIdentity(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                "Native symbol identity must be non-empty.",
                paramName);
        }
        var normalized = value.Trim();
        if (normalized.IndexOf('\\') >= 0
            || ContainsControlCharacter(normalized))
        {
            throw new ArgumentException(
                "Native symbol identity must not contain backslashes.",
                paramName);
        }
        return normalized;
    }

    private static bool ContainsControlCharacter(string value)
    {
        foreach (var character in value)
        {
            if (char.IsControl(character))
            {
                return true;
            }
        }
        return false;
    }
}
