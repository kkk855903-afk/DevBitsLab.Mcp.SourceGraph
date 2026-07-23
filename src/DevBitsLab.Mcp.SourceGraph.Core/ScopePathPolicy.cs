namespace DevBitsLab.Mcp.SourceGraph.Core;

/// <summary>
/// Applies a scope's repository-relative exclude globs on top of the mandatory
/// <see cref="PrivacyPathPolicy"/> boundary.
/// </summary>
/// <remarks>
/// Configured excludes can only narrow the indexable set. The privacy policy is evaluated first
/// and cannot be negated by a scope include, a registered file extension, or an exclude pattern.
/// Glob matching is case-insensitive and supports <c>*</c>, <c>?</c>, and a complete
/// <c>**</c> path segment.
/// </remarks>
public sealed class ScopePathPolicy
{
    private readonly string _repoRoot;
    private readonly PrivacyPathPolicy _privacyPathPolicy;
    private readonly IReadOnlyList<GlobPattern> _excludePatterns;

    public ScopePathPolicy(string repoRoot, IReadOnlyList<string>? excludePatterns = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        if (!Path.IsPathFullyQualified(repoRoot))
        {
            throw new ArgumentException("Repository root must be an absolute path.", nameof(repoRoot));
        }

        _repoRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(NormalizeSeparators(repoRoot)));
        _privacyPathPolicy = new PrivacyPathPolicy(_repoRoot);
        var configuredPatterns = excludePatterns ?? Array.Empty<string>();
        var parsedPatterns = new List<GlobPattern>(configuredPatterns.Count);
        for (var i = 0; i < configuredPatterns.Count; i++)
        {
            try
            {
                parsedPatterns.Add(GlobPattern.Create(configuredPatterns[i]));
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException(
                    $"Invalid scope exclude pattern at index {i}: {ex.Message}",
                    nameof(excludePatterns),
                    ex);
            }
        }
        _excludePatterns = parsedPatterns;
    }

    /// <summary>
    /// Returns <see langword="true"/> when the path violates the mandatory privacy boundary or
    /// matches any configured scope exclude. Relative paths resolve against the repository root;
    /// invalid and out-of-root paths fail closed.
    /// </summary>
    public bool IsExcluded(string? path)
    {
        if (_privacyPathPolicy.IsExcluded(path)) return true;
        return MatchesConfiguredExclude(path);
    }

    /// <summary>
    /// Applies the generated-document boundary: synthetic paths below <c>obj/</c> or
    /// <c>bin/</c> may pass, but repository containment, medical/privacy directories, sensitive
    /// extensions, and configured scope excludes remain enforced.
    /// </summary>
    public bool IsGeneratedDocumentExcluded(string? path)
    {
        if (_privacyPathPolicy.IsGeneratedDocumentExcluded(path)) return true;
        return MatchesConfiguredExclude(path);
    }

    /// <summary>
    /// Matches only the scope-supplied glob set. The full disk boundary remains
    /// <see cref="IsExcluded"/>; generated Roslyn documents use
    /// <see cref="IsGeneratedDocumentExcluded"/>.
    /// </summary>
    public bool MatchesConfiguredExclude(string? path)
    {
        if (_excludePatterns.Count == 0) return false;
        if (string.IsNullOrWhiteSpace(path)) return true;

        string relativePath;
        try
        {
            var normalizedPath = NormalizeSeparators(path);
            var fullPath = Path.IsPathFullyQualified(normalizedPath)
                ? Path.GetFullPath(normalizedPath)
                : Path.GetFullPath(normalizedPath, _repoRoot);
            relativePath = Path.GetRelativePath(_repoRoot, fullPath)
                .Replace('\\', '/');
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (NotSupportedException)
        {
            return true;
        }
        catch (PathTooLongException)
        {
            return true;
        }

        if (relativePath == ".") relativePath = string.Empty;
        return _excludePatterns.Any(pattern => pattern.IsMatch(relativePath));
    }

    private static string NormalizeSeparators(string path) =>
        path.Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);

    private sealed class GlobPattern
    {
        private readonly string[] _segments;

        private GlobPattern(string[] segments) => _segments = segments;

        public static GlobPattern Create(string? pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                throw new ArgumentException("Pattern must not be blank.");
            }

            var trimmed = pattern.Trim();
            if (trimmed.StartsWith("/", StringComparison.Ordinal)
                || trimmed.StartsWith("\\", StringComparison.Ordinal)
                || (trimmed.Length >= 2 && char.IsLetter(trimmed[0]) && trimmed[1] == ':'))
            {
                throw new ArgumentException(
                    "Pattern must be repository-relative and must not start at a filesystem root.");
            }

            var normalized = trimmed.Replace('\\', '/');
            while (normalized.StartsWith("./", StringComparison.Ordinal))
            {
                normalized = normalized[2..];
            }
            if (normalized.EndsWith("/", StringComparison.Ordinal))
            {
                normalized += "**";
            }

            var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                throw new ArgumentException("Pattern must contain at least one path segment.");
            }
            if (segments.Any(segment => segment is "." or ".."))
            {
                throw new ArgumentException(
                    "Pattern must not contain current-directory or parent-directory segments.");
            }

            return new GlobPattern(segments);
        }

        public bool IsMatch(string relativePath)
        {
            var pathSegments = relativePath.Split(
                '/',
                StringSplitOptions.RemoveEmptyEntries);
            for (var pathLength = pathSegments.Length; pathLength >= 0; pathLength--)
            {
                if (MatchesPrefix(pathLength)) return true;
            }
            return false;

            bool MatchesPrefix(int pathLength)
            {
                var memo = new Dictionary<(int Pattern, int Path), bool>();
                return Match(patternIndex: 0, pathIndex: 0);

                bool Match(int patternIndex, int pathIndex)
                {
                    var key = (patternIndex, pathIndex);
                    if (memo.TryGetValue(key, out var cached)) return cached;

                    bool result;
                    if (patternIndex == _segments.Length)
                    {
                        result = pathIndex == pathLength;
                    }
                    else if (_segments[patternIndex] == "**")
                    {
                        result = Match(patternIndex + 1, pathIndex)
                            || (pathIndex < pathLength && Match(patternIndex, pathIndex + 1));
                    }
                    else
                    {
                        result = pathIndex < pathLength
                            && SegmentMatches(_segments[patternIndex], pathSegments[pathIndex])
                            && Match(patternIndex + 1, pathIndex + 1);
                    }

                    memo[key] = result;
                    return result;
                }
            }
        }

        private static bool SegmentMatches(string pattern, string value)
        {
            var patternIndex = 0;
            var valueIndex = 0;
            var starIndex = -1;
            var retryValueIndex = -1;

            while (valueIndex < value.Length)
            {
                if (patternIndex < pattern.Length
                    && (pattern[patternIndex] == '?'
                        || CharsEqual(pattern[patternIndex], value[valueIndex])))
                {
                    patternIndex++;
                    valueIndex++;
                    continue;
                }

                if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
                {
                    starIndex = patternIndex++;
                    retryValueIndex = valueIndex;
                    continue;
                }

                if (starIndex >= 0)
                {
                    patternIndex = starIndex + 1;
                    valueIndex = ++retryValueIndex;
                    continue;
                }

                return false;
            }

            while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                patternIndex++;
            }
            return patternIndex == pattern.Length;
        }

        private static bool CharsEqual(char left, char right) =>
            char.ToUpperInvariant(left) == char.ToUpperInvariant(right);
    }
}
