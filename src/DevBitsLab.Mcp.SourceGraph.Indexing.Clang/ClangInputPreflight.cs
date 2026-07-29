using System.Text;
using DevBitsLab.Mcp.SourceGraph.Core;

namespace DevBitsLab.Mcp.SourceGraph.Indexing.Clang;

/// <summary>
/// Performs the path checks that can be proven without starting libclang. This is intentionally
/// conservative: unsupported compiler-controlled file inputs and non-literal includes are
/// rejected until they can be validated in a bounded native worker.
/// </summary>
internal static class ClangInputPreflight
{
    private static readonly StringComparer _pathComparer =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static readonly string[] _unsupportedArgumentPrefixes =
    [
        "-include",
        "--include",
        "-imacros",
        "-isystem",
        "-internal-isystem",
        "-isysroot",
        "--sysroot",
        "-idirafter",
        "-iprefix",
        "-iwithprefix",
        "-iframework",
        "-F",
        "-resource-dir",
        "-gcc-toolchain",
        "--gcc-toolchain",
        "-ivfsoverlay",
        "-fmodules",
        "-fimplicit-modules",
        "-fcxx-modules",
        "-fmodule-file",
        "-fmodule-map-file",
        "-fmodules-cache-path",
        "-include-pch",
        "-include-pth",
        "-pch-through-hdr",
        "-working-directory",
        "-B",
        "--config",
        "-load",
        "-plugin",
        "-Xclang",
        "-Xpreprocessor",
        "-Wp,",
        "-cc1",
        "-o",
        "-MF",
        "-MJ",
        "-serialize-diagnostics",
        "/FI",
        "/external:I",
        "/imsvc",
        "/Fo",
        "/Fd",
    ];

    public static bool TryNormalizeCompilerArguments(
        IReadOnlyList<string> arguments,
        ScopePathPolicy pathPolicy,
        IReadOnlyList<string> systemIncludeDirectories,
        out string[] normalizedArguments,
        out IReadOnlyList<string> includeDirectories,
        out IReadOnlyList<string> normalizedSystemIncludeDirectories,
        out string rejectionReason)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(pathPolicy);
        ArgumentNullException.ThrowIfNull(systemIncludeDirectories);

        if (!TryNormalizeSystemIncludeDirectories(
                systemIncludeDirectories,
                out var systemIncludes))
        {
            normalizedArguments = [];
            includeDirectories = [];
            normalizedSystemIncludeDirectories = [];
            rejectionReason =
                "A runtime compiler/SDK include directory is missing or invalid.";
            return false;
        }

        var normalized = new List<string>(arguments.Count);
        var includes = new List<string>();
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (string.IsNullOrWhiteSpace(argument))
            {
                normalizedArguments = [];
                includeDirectories = [];
                normalizedSystemIncludeDirectories = [];
                rejectionReason = "A blank compiler argument cannot be validated.";
                return false;
            }
            if (argument[0] == '@'
                || IsUnsupportedArgument(argument))
            {
                normalizedArguments = [];
                includeDirectories = [];
                normalizedSystemIncludeDirectories = [];
                rejectionReason =
                    "A compiler-controlled file input is not supported by in-process parsing.";
                return false;
            }

            if (IsSeparateIncludeOption(argument))
            {
                if (++index >= arguments.Count
                    || !TryNormalizeIncludeDirectory(
                        arguments[index],
                        pathPolicy,
                        systemIncludes,
                        out var includeDirectory))
                {
                    normalizedArguments = [];
                    includeDirectories = [];
                    normalizedSystemIncludeDirectories = [];
                    rejectionReason =
                        "An explicit include directory is missing or outside the approved source/toolchain roots.";
                    return false;
                }

                normalized.Add(argument);
                normalized.Add(includeDirectory);
                includes.Add(includeDirectory);
                continue;
            }

            if (TryReadAttachedIncludeOption(
                argument,
                out var option,
                out var attachedPath))
            {
                if (!TryNormalizeIncludeDirectory(
                    attachedPath,
                    pathPolicy,
                    systemIncludes,
                    out var includeDirectory))
                {
                    normalizedArguments = [];
                    includeDirectories = [];
                    normalizedSystemIncludeDirectories = [];
                    rejectionReason =
                        "An explicit include directory is missing or outside the approved source/toolchain roots.";
                    return false;
                }

                normalized.Add(option + includeDirectory);
                includes.Add(includeDirectory);
                continue;
            }

            if (LooksLikeStandalonePath(argument))
            {
                normalizedArguments = [];
                includeDirectories = [];
                normalizedSystemIncludeDirectories = [];
                rejectionReason =
                    "A standalone compiler path cannot be validated as an approved input.";
                return false;
            }

            normalized.Add(argument);
        }

        normalizedArguments = normalized.ToArray();
        includeDirectories = includes
            .Distinct(_pathComparer)
            .ToArray();
        normalizedSystemIncludeDirectories = systemIncludes;
        rejectionReason = string.Empty;
        return true;
    }

    public static bool TryValidateExplicitIncludeGraph(
        string sourceFilePath,
        IReadOnlyList<string> includeDirectories,
        IReadOnlyList<string> systemIncludeDirectories,
        ScopePathPolicy pathPolicy,
        out IReadOnlyList<string> approvedInputFiles,
        out string rejectionReason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFilePath);
        ArgumentNullException.ThrowIfNull(includeDirectories);
        ArgumentNullException.ThrowIfNull(systemIncludeDirectories);
        ArgumentNullException.ThrowIfNull(pathPolicy);

        var visited = new HashSet<string>(_pathComparer);
        var pending = new Stack<string>();
        pending.Push(sourceFilePath);

        while (pending.Count > 0)
        {
            var filePath = pending.Pop();
            if (!visited.Add(filePath))
            {
                continue;
            }

            IEnumerable<string> lines;
            try
            {
                lines = File.ReadLines(filePath);
            }
            catch (Exception ex) when (
                ex is IOException
                    or UnauthorizedAccessException
                    or NotSupportedException)
            {
                approvedInputFiles = [];
                rejectionReason =
                    "An approved native source input could not be read during include preflight.";
                return false;
            }

            var inBlockComment = false;
            try
            {
                foreach (var line in lines)
                {
                    var directive = ReadDirective(line, ref inBlockComment);
                    if (directive is null)
                    {
                        continue;
                    }
                    if (directive.Value.Name == "include_next")
                    {
                        approvedInputFiles = [];
                        rejectionReason =
                            "include_next cannot be resolved safely before parsing.";
                        return false;
                    }
                    if (directive.Value.Name is not ("include" or "import"))
                    {
                        continue;
                    }
                    if (!TryParseHeaderName(
                        directive.Value.Operand,
                        out var headerName,
                        out var quoted))
                    {
                        approvedInputFiles = [];
                        rejectionReason =
                            "A non-literal or malformed include cannot be resolved safely.";
                        return false;
                    }
                    if (!TryResolveHeader(
                        filePath,
                        headerName,
                        quoted,
                        includeDirectories,
                        systemIncludeDirectories,
                        pathPolicy,
                        out var includedFilePath,
                        out var isSystemHeader,
                        out var rejectedExistingHeader))
                    {
                        if (rejectedExistingHeader)
                        {
                            approvedInputFiles = [];
                            rejectionReason =
                                "A literal include resolves outside the approved source/toolchain roots.";
                            return false;
                        }
                        // Conditional branches cannot be evaluated soundly without Clang. A
                        // literal header that is absent from every approved search root may be
                        // inactive; if it is active, Clang emits an error. Any file Clang does
                        // open is validated again by TryReadIncludedFiles before facts escape.
                        continue;
                    }
                    if (!isSystemHeader)
                    {
                        pending.Push(includedFilePath);
                    }
                }
            }
            catch (Exception ex) when (
                ex is IOException
                    or UnauthorizedAccessException
                    or NotSupportedException)
            {
                approvedInputFiles = [];
                rejectionReason =
                    "An approved native source input could not be read during include preflight.";
                return false;
            }
        }

        approvedInputFiles = visited
            .OrderBy(path => path, _pathComparer)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToArray();
        rejectionReason = string.Empty;
        return true;
    }

    public static bool TryResolveAllowedFile(
        string path,
        ScopePathPolicy pathPolicy,
        out string physicalPath)
    {
        physicalPath = string.Empty;
        try
        {
            if (pathPolicy.IsExcludedForDiscovery(path, out var resolvedPath)
                || resolvedPath is null
                || !File.Exists(resolvedPath))
            {
                return false;
            }

            physicalPath = Path.GetFullPath(resolvedPath);
            return true;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            return false;
        }
    }

    private static bool TryNormalizeIncludeDirectory(
        string path,
        ScopePathPolicy pathPolicy,
        IReadOnlyList<string> systemIncludeDirectories,
        out string physicalPath)
    {
        physicalPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            if (Path.IsPathFullyQualified(path))
            {
                var candidate = Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(path));
                if (Directory.Exists(candidate)
                    && IsSameOrDescendantOfAny(
                        candidate,
                        systemIncludeDirectories))
                {
                    physicalPath = candidate;
                    return true;
                }
            }
            if (pathPolicy.IsExcludedForDiscovery(
                    path,
                    out var resolvedPath)
                || resolvedPath is null
                || !Directory.Exists(resolvedPath))
            {
                physicalPath = string.Empty;
                return false;
            }
            physicalPath = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(resolvedPath));
            return true;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            return false;
        }
    }

    private static bool TryResolveHeader(
        string includingFilePath,
        string headerName,
        bool quoted,
        IReadOnlyList<string> includeDirectories,
        IReadOnlyList<string> systemIncludeDirectories,
        ScopePathPolicy pathPolicy,
        out string physicalPath,
        out bool isSystemHeader,
        out bool rejectedExistingHeader)
    {
        physicalPath = string.Empty;
        isSystemHeader = false;
        rejectedExistingHeader = false;
        var normalizedHeaderName = headerName
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathFullyQualified(normalizedHeaderName))
        {
            if (TryResolveAllowedFile(
                    normalizedHeaderName,
                    pathPolicy,
                    out physicalPath))
            {
                return true;
            }
            if (TryResolveSystemFile(
                    normalizedHeaderName,
                    systemIncludeDirectories,
                    out physicalPath))
            {
                isSystemHeader = true;
                return true;
            }
            rejectedExistingHeader = File.Exists(normalizedHeaderName);
            return false;
        }

        var searchDirectories = quoted
            ? new[] { Path.GetDirectoryName(includingFilePath)! }
                .Concat(includeDirectories)
            : includeDirectories;
        foreach (var directory in searchDirectories)
        {
            string candidate;
            try
            {
                candidate = Path.GetFullPath(
                    Path.Combine(directory, normalizedHeaderName));
                if (!File.Exists(candidate))
                {
                    continue;
                }
                if (TryResolveSystemFile(
                        candidate,
                        systemIncludeDirectories,
                        out physicalPath))
                {
                    isSystemHeader = true;
                    return true;
                }
                if (pathPolicy.IsExcludedForDiscovery(
                    candidate,
                    out var resolvedCandidate))
                {
                    rejectedExistingHeader = true;
                    return false;
                }
                if (resolvedCandidate is null
                    || !File.Exists(resolvedCandidate))
                {
                    continue;
                }

                physicalPath = Path.GetFullPath(resolvedCandidate);
                return true;
            }
            catch (Exception ex) when (
                ex is IOException
                    or UnauthorizedAccessException
                    or ArgumentException
                    or NotSupportedException
                    or PathTooLongException)
            {
                return false;
            }
        }

        return false;
    }

    public static bool TryResolveSystemFile(
        string path,
        IReadOnlyList<string> systemIncludeDirectories,
        out string physicalPath)
    {
        physicalPath = string.Empty;
        try
        {
            var candidate = Path.GetFullPath(path);
            if (!File.Exists(candidate)
                || !IsSameOrDescendantOfAny(
                    candidate,
                    systemIncludeDirectories))
            {
                return false;
            }
            physicalPath = candidate;
            return true;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            return false;
        }
    }

    private static bool TryNormalizeSystemIncludeDirectories(
        IReadOnlyList<string> directories,
        out string[] normalized)
    {
        var result = new HashSet<string>(_pathComparer);
        foreach (var directory in directories)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(directory)
                    || !Path.IsPathFullyQualified(directory)
                    || !Directory.Exists(directory))
                {
                    normalized = [];
                    return false;
                }
                result.Add(Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(directory)));
            }
            catch (Exception ex) when (
                ex is IOException
                    or UnauthorizedAccessException
                    or ArgumentException
                    or NotSupportedException
                    or PathTooLongException)
            {
                normalized = [];
                return false;
            }
        }
        normalized = result
            .OrderBy(path => path, _pathComparer)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToArray();
        return true;
    }

    private static bool IsSameOrDescendantOfAny(
        string path,
        IReadOnlyList<string> roots)
    {
        var fullPath = Path.GetFullPath(path);
        foreach (var root in roots)
        {
            var fullRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(root));
            if (_pathComparer.Equals(fullPath, fullRoot)
                || fullPath.StartsWith(
                    fullRoot + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsSeparateIncludeOption(string argument) =>
        string.Equals(argument, "-I", StringComparison.Ordinal)
        || string.Equals(argument, "-iquote", StringComparison.Ordinal)
        || string.Equals(argument, "/I", StringComparison.OrdinalIgnoreCase);

    private static bool TryReadAttachedIncludeOption(
        string argument,
        out string option,
        out string path)
    {
        if (argument.StartsWith("-iquote", StringComparison.Ordinal)
            && argument.Length > "-iquote".Length)
        {
            option = "-iquote";
            path = argument["-iquote".Length..];
            return true;
        }
        if (argument.StartsWith("-I", StringComparison.Ordinal)
            && argument.Length > 2)
        {
            option = "-I";
            path = argument[2..];
            return true;
        }
        if (argument.StartsWith("/I", StringComparison.OrdinalIgnoreCase)
            && argument.Length > 2)
        {
            option = argument[..2];
            path = argument[2..];
            return true;
        }

        option = string.Empty;
        path = string.Empty;
        return false;
    }

    private static bool IsUnsupportedArgument(string argument)
    {
        foreach (var prefix in _unsupportedArgumentPrefixes)
        {
            var comparison = prefix[0] == '/'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (string.Equals(argument, prefix, comparison)
                || argument.StartsWith(prefix + "=", comparison)
                || (AllowsAttachedValue(prefix)
                    && argument.StartsWith(prefix, comparison)))
            {
                return true;
            }
        }
        return false;
    }

    private static bool AllowsAttachedValue(string prefix) =>
        prefix is "-include"
            or "-imacros"
            or "-isystem"
            or "-internal-isystem"
            or "-isysroot"
            or "-idirafter"
            or "-iprefix"
            or "-iwithprefix"
            or "-iframework"
            or "-F"
            or "-resource-dir"
            or "-gcc-toolchain"
            or "-ivfsoverlay"
            or "-include-pch"
            or "-include-pth"
            or "-working-directory"
            or "-B"
            or "-o"
            or "-MF"
            or "-MJ"
            or "/FI"
            or "/external:I"
            or "/imsvc"
            or "/Fo"
            or "/Fd";

    private static bool LooksLikeStandalonePath(string argument)
    {
        if (Path.IsPathFullyQualified(argument))
        {
            return true;
        }
        if (argument[0] is '-' or '/')
        {
            return false;
        }
        return argument.StartsWith(".", StringComparison.Ordinal)
            || argument.Contains('\\')
            || argument.Contains('/');
    }

    private static (string Name, string Operand)? ReadDirective(
        string line,
        ref bool inBlockComment)
    {
        var withoutComments = RemoveComments(line, ref inBlockComment);
        var span = withoutComments.AsSpan().TrimStart();
        if (span.IsEmpty || span[0] != '#')
        {
            return null;
        }

        span = span[1..].TrimStart();
        var nameLength = 0;
        while (nameLength < span.Length
            && (char.IsLetter(span[nameLength]) || span[nameLength] == '_'))
        {
            nameLength++;
        }
        if (nameLength == 0)
        {
            return null;
        }

        return (
            span[..nameLength].ToString(),
            span[nameLength..].Trim().ToString());
    }

    private static string RemoveComments(string line, ref bool inBlockComment)
    {
        var result = new StringBuilder(line.Length);
        var quote = '\0';
        var escaped = false;
        for (var index = 0; index < line.Length; index++)
        {
            var current = line[index];
            var next = index + 1 < line.Length
                ? line[index + 1]
                : '\0';
            if (inBlockComment)
            {
                if (current == '*' && next == '/')
                {
                    inBlockComment = false;
                    index++;
                }
                continue;
            }
            if (quote != '\0')
            {
                result.Append(current);
                if (escaped)
                {
                    escaped = false;
                }
                else if (current == '\\')
                {
                    escaped = true;
                }
                else if (current == quote)
                {
                    quote = '\0';
                }
                continue;
            }
            if (current == '/' && next == '*')
            {
                inBlockComment = true;
                index++;
                continue;
            }
            if (current == '/' && next == '/')
            {
                break;
            }
            if (current is '"' or '\'')
            {
                quote = current;
            }
            result.Append(current);
        }
        return result.ToString();
    }

    private static bool TryParseHeaderName(
        string operand,
        out string headerName,
        out bool quoted)
    {
        headerName = string.Empty;
        quoted = false;
        if (operand.Length < 3)
        {
            return false;
        }

        var closing = operand[0] switch
        {
            '"' => '"',
            '<' => '>',
            _ => '\0',
        };
        if (closing == '\0')
        {
            return false;
        }

        var closingIndex = operand.IndexOf(closing, 1);
        if (closingIndex <= 1
            || !string.IsNullOrWhiteSpace(operand[(closingIndex + 1)..]))
        {
            return false;
        }

        headerName = operand[1..closingIndex];
        quoted = operand[0] == '"';
        return true;
    }
}
