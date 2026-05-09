using System;

namespace DevBitsLab.Mcp.SourceGraph.Sdk.Validation;

/// <summary>
/// Validates kebab-case identifiers used for edge kinds, symbol kinds, and annotation flavors.
/// A valid identifier consists of one or more lowercase ASCII letters or digits, with optional
/// single hyphens between segments. Rejects leading/trailing hyphens, double hyphens, uppercase
/// letters, underscores, whitespace, and empty strings.
/// </summary>
public static class KebabCaseValidator
{
    /// <summary>Returns <c>true</c> when <paramref name="value"/> is a non-empty kebab-case identifier.</summary>
    public static bool IsValid(string? value)
    {
        if (value is null || value.Length == 0) return false;
        if (value[0] == '-' || value[value.Length - 1] == '-') return false;
        var prevHyphen = false;
        foreach (var c in value)
        {
            if (c == '-')
            {
                if (prevHyphen) return false;
                prevHyphen = true;
                continue;
            }
            prevHyphen = false;
            var isLower = c >= 'a' && c <= 'z';
            var isDigit = c >= '0' && c <= '9';
            if (!isLower && !isDigit) return false;
        }
        return true;
    }

    /// <summary>
    /// Throws <see cref="ArgumentException"/> when <paramref name="value"/> is not a valid
    /// kebab-case identifier; the exception's <c>ParamName</c> equals <paramref name="paramName"/>.
    /// </summary>
    public static void Validate(string? value, string paramName)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException(
                $"Value '{value}' is not a valid kebab-case identifier (lowercase letters/digits with single hyphens, no leading/trailing hyphens).",
                paramName);
        }
    }
}
