namespace DevBitsLab.Mcp.SourceGraph.Core;

/// <summary>
/// Applies the mandatory local privacy exclusions to paths within one repository.
/// The policy is purely lexical: it normalizes paths but never probes the file system.
/// </summary>
public sealed class PrivacyPathPolicy
{
    private static readonly HashSet<string> _excludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin",
        "obj",
        ".vs",
        "Debug",
        "Release",
        "Images",
        "PatientData",
        "Database",
        "Logs",
        ".git",
        ".sourcegraph",
        "node_modules",
    };

    private static readonly HashSet<string> _excludedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dcm",
        ".jpg",
        ".jpeg",
        ".png",
    };

    private static readonly HashSet<string> _generatedDocumentBuildRootNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin",
        "obj",
    };

    private static readonly HashSet<string> _buildOutputDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin",
        "obj",
        "Debug",
        "Release",
    };

    private readonly string _repoRoot;
    private readonly string _repoRootPrefix;

    public PrivacyPathPolicy(string repoRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        if (!Path.IsPathFullyQualified(repoRoot))
        {
            throw new ArgumentException("Repository root must be an absolute path.", nameof(repoRoot));
        }

        _repoRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(NormalizeSeparators(repoRoot)));
        _repoRootPrefix = Path.EndsInDirectorySeparator(_repoRoot)
            ? _repoRoot
            : _repoRoot + Path.DirectorySeparatorChar;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="path"/> is outside the repository,
    /// contains an excluded directory segment, or has an excluded medical/image extension.
    /// Relative paths are resolved against the repository root. Invalid paths fail closed.
    /// </summary>
    public bool IsExcluded(string? path) =>
        IsExcludedCore(path, allowGeneratedDocumentBuildOutput: false);

    /// <summary>
    /// Generated Roslyn documents are in memory but conventionally carry synthetic paths below
    /// <c>obj/</c> or <c>bin/</c>. Permit only that build-output portion of the directory policy;
    /// repository containment, medical/image extensions, and every non-build privacy directory
    /// remain mandatory.
    /// </summary>
    internal bool IsGeneratedDocumentExcluded(string? path) =>
        IsExcludedCore(path, allowGeneratedDocumentBuildOutput: true);

    private bool IsExcludedCore(string? path, bool allowGeneratedDocumentBuildOutput)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;

        string fullPath;
        try
        {
            var normalizedPath = NormalizeSeparators(path);
            fullPath = Path.IsPathFullyQualified(normalizedPath)
                ? Path.GetFullPath(normalizedPath)
                : Path.GetFullPath(normalizedPath, _repoRoot);
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

        if (string.Equals(fullPath, _repoRoot, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!fullPath.StartsWith(_repoRootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var relativePath = fullPath[_repoRootPrefix.Length..];
        var segments = relativePath.Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);

        var generatedBuildRootIndex = allowGeneratedDocumentBuildOutput
            ? Array.FindIndex(segments, _generatedDocumentBuildRootNames.Contains)
            : -1;
        for (var i = 0; i < segments.Length; i++)
        {
            if (!_excludedDirectoryNames.Contains(segments[i])) continue;
            if (generatedBuildRootIndex >= 0
                && i >= generatedBuildRootIndex
                && _buildOutputDirectoryNames.Contains(segments[i]))
            {
                continue;
            }
            return true;
        }

        return segments.Length > 0
            && _excludedExtensions.Contains(Path.GetExtension(segments[^1]));
    }

    private static string NormalizeSeparators(string path) =>
        path.Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
}
