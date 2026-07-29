using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using DevBitsLab.Mcp.SourceGraph.Core;
using Microsoft.Win32;

namespace DevBitsLab.Mcp.SourceGraph.Server.Interop;

internal sealed record VcxProjectImportFailure(
    string Code,
    string Message,
    string ConfiguredPath);

internal sealed record VcxProjectImportResult(
    IReadOnlyList<InteropTranslationUnitConfig> TranslationUnits,
    IReadOnlyList<VcxProjectImportFailure> Failures)
{
    public bool IsComplete => Failures.Count == 0;
    public int AttemptedProjects { get; init; }
    public IReadOnlyList<string> ImportedProjects { get; init; } = [];
}

/// <summary>
/// Imports the declarative compile surface of an explicitly selected Visual C++ project.
/// This reader intentionally does not invoke MSBuild: targets, tasks, imported props, response
/// files, build events, and custom tooling are never executed.
/// </summary>
internal static partial class VcxProjectImporter
{
    private const long MaximumProjectCharacters = 8L * 1024L * 1024L;
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    public static VcxProjectImportResult Import(
        string scopeRoot,
        ScopeInteropConfig configuration,
        ScopePathPolicy pathPolicy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeRoot);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(pathPolicy);

        var units = new List<InteropTranslationUnitConfig>(
            configuration.TranslationUnits);
        var failures = new List<VcxProjectImportFailure>();
        var importedProjects = new List<string>();
        var configuredSources = new HashSet<string>(
            configuration.TranslationUnits.Select(unit => unit.Path),
            PathComparer);

        foreach (var project in configuration.VcxProjects)
        {
            var candidateUnits = new List<InteropTranslationUnitConfig>();
            var candidateFailures = new List<VcxProjectImportFailure>();
            var candidateSources = new HashSet<string>(
                configuredSources,
                PathComparer);
            ImportProject(
                scopeRoot,
                configuration.Target,
                project,
                pathPolicy,
                candidateSources,
                candidateUnits,
                candidateFailures);
            if (candidateFailures.Count > 0)
            {
                failures.AddRange(candidateFailures);
                continue;
            }

            units.AddRange(candidateUnits);
            configuredSources.UnionWith(candidateSources);
            importedProjects.Add(project.Path);
        }

        return new VcxProjectImportResult(units, failures)
        {
            AttemptedProjects = configuration.VcxProjects.Count,
            ImportedProjects = importedProjects,
        };
    }

    private static void ImportProject(
        string scopeRoot,
        InteropTarget target,
        InteropVcxProjectConfig project,
        ScopePathPolicy pathPolicy,
        ISet<string> configuredSources,
        ICollection<InteropTranslationUnitConfig> units,
        ICollection<VcxProjectImportFailure> failures)
    {
        if (!TryResolveScopeFile(
                scopeRoot,
                project.Path,
                pathPolicy,
                out var projectPath))
        {
            failures.Add(Failure(
                "vcxproj-path-rejected",
                "The configured Visual C++ project is missing or outside the approved scope.",
                project.Path));
            return;
        }

        XDocument document;
        try
        {
            using var stream = new FileStream(
                projectPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = XmlReader.Create(
                stream,
                new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = MaximumProjectCharacters,
                    IgnoreComments = true,
                    IgnoreProcessingInstructions = true,
                });
            document = XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or XmlException
                or InvalidOperationException)
        {
            failures.Add(Failure(
                "vcxproj-read-failed",
                $"The Visual C++ project could not be read safely ({ex.GetType().Name}).",
                project.Path));
            return;
        }

        var root = document.Root;
        if (root is null
            || !string.Equals(root.Name.LocalName, "Project", StringComparison.Ordinal))
        {
            failures.Add(Failure(
                "vcxproj-invalid",
                "The configured file is not a Visual C++ MSBuild project.",
                project.Path));
            return;
        }

        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        var properties = CreateProperties(
            scopeRoot,
            projectPath,
            project);
        if (!HasSelectedConfiguration(root, project, properties))
        {
            failures.Add(Failure(
                "vcxproj-configuration-not-found",
                $"Configuration `{project.Configuration}|{project.Platform}` is not declared by the project.",
                project.Path));
            return;
        }
        if (!PlatformMatchesTarget(project.Platform, target.Architecture))
        {
            failures.Add(Failure(
                "vcxproj-target-mismatch",
                $"Platform `{project.Platform}` does not match ABI target `{target.Architecture}`.",
                project.Path));
            return;
        }

        ReadApplicableProperties(root, properties);
        var defaults = ReadCompileSettings(root, properties);
        var toolchain = MsvcToolchainIncludes.Discover(
            properties.GetValueOrDefault("PlatformToolset"),
            properties.GetValueOrDefault("WindowsTargetPlatformVersion"));

        var selected = new HashSet<string>(
            project.SourceFiles.Select(NormalizeRelativePath),
            PathComparer);
        var observedSelections = new HashSet<string>(PathComparer);
        var sourceCountBefore = units.Count;

        foreach (var item in root
                     .Descendants()
                     .Where(element =>
                         string.Equals(
                             element.Name.LocalName,
                             "ClCompile",
                             StringComparison.Ordinal)
                         && element.Attribute("Include") is not null))
        {
            if (!ConditionMatches(
                    item.Attribute("Condition")?.Value,
                    properties))
            {
                continue;
            }
            var rawInclude = Expand(
                item.Attribute("Include")!.Value,
                properties);
            if (ContainsUnresolvedProperty(rawInclude))
            {
                failures.Add(Failure(
                    "vcxproj-property-unsupported",
                    "A ClCompile path contains an unresolved MSBuild property.",
                    project.Path));
                return;
            }
            var projectRelative = NormalizeRelativePath(rawInclude);
            if (selected.Count > 0 && !selected.Contains(projectRelative))
            {
                continue;
            }
            observedSelections.Add(projectRelative);
            if (IsExcludedFromBuild(item, properties))
            {
                continue;
            }

            string sourcePath;
            try
            {
                sourcePath = Path.GetFullPath(
                    Path.Combine(projectDirectory, rawInclude));
            }
            catch (Exception ex) when (
                ex is ArgumentException
                    or NotSupportedException
                    or PathTooLongException)
            {
                failures.Add(Failure(
                    "vcxproj-source-path-invalid",
                    "A selected ClCompile path is invalid.",
                    project.Path));
                return;
            }
            if (!TryMakeScopeRelative(
                    scopeRoot,
                    sourcePath,
                    pathPolicy,
                    out var scopeRelativeSource))
            {
                failures.Add(Failure(
                    "vcxproj-source-path-rejected",
                    $"Selected source `{projectRelative}` is missing or outside the approved scope.",
                    project.Path));
                return;
            }
            if (!configuredSources.Add(scopeRelativeSource))
            {
                failures.Add(Failure(
                    "vcxproj-source-duplicate",
                    $"Selected source `{scopeRelativeSource}` is configured more than once.",
                    project.Path));
                return;
            }

            var settings = defaults.ApplyItemOverrides(item, properties);
            if (!TryBuildArguments(
                    scopeRoot,
                    projectDirectory,
                    target,
                    sourcePath,
                    properties,
                    settings,
                    toolchain,
                    project.AdditionalArguments,
                    pathPolicy,
                    out var arguments,
                    out var argumentFailure))
            {
                failures.Add(Failure(
                    "vcxproj-arguments-unsupported",
                    argumentFailure,
                    project.Path));
                return;
            }

            units.Add(new InteropTranslationUnitConfig(
                scopeRelativeSource,
                project.Library,
                arguments,
                project.BinaryPath)
            {
                SystemIncludeDirectories = toolchain,
            });
        }

        var missingSelections = selected
            .Where(path => !observedSelections.Contains(path))
            .OrderBy(path => path, PathComparer)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (missingSelections.Length > 0)
        {
            failures.Add(Failure(
                "vcxproj-source-not-found",
                "Configured source_files are not ClCompile items: "
                + string.Join(", ", missingSelections),
                project.Path));
            return;
        }
        if (units.Count == sourceCountBefore)
        {
            failures.Add(Failure(
                "vcxproj-no-sources",
                "The selected configuration contains no enabled ClCompile items.",
                project.Path));
        }
    }

    private static Dictionary<string, string> CreateProperties(
        string scopeRoot,
        string projectPath,
        InteropVcxProjectConfig project)
    {
        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Configuration"] = project.Configuration,
            ["Platform"] = project.Platform,
            ["ProjectDir"] = EnsureTrailingSeparator(projectDirectory),
            ["ProjectPath"] = projectPath,
            ["ProjectName"] = Path.GetFileNameWithoutExtension(projectPath),
            ["SolutionDir"] = EnsureTrailingSeparator(scopeRoot),
        };
    }

    private static bool HasSelectedConfiguration(
        XElement root,
        InteropVcxProjectConfig project,
        IReadOnlyDictionary<string, string> properties)
    {
        var expected = $"{project.Configuration}|{project.Platform}";
        return root.Descendants()
            .Where(element =>
                string.Equals(
                    element.Name.LocalName,
                    "ProjectConfiguration",
                    StringComparison.Ordinal))
            .Any(element =>
                ConditionMatches(
                    element.Attribute("Condition")?.Value,
                    properties)
                && string.Equals(
                    element.Attribute("Include")?.Value,
                    expected,
                    StringComparison.OrdinalIgnoreCase));
    }

    private static void ReadApplicableProperties(
        XElement root,
        Dictionary<string, string> properties)
    {
        foreach (var group in root.Elements().Where(element =>
                     string.Equals(
                         element.Name.LocalName,
                         "PropertyGroup",
                         StringComparison.Ordinal)
                     && ConditionMatches(
                         element.Attribute("Condition")?.Value,
                         properties)))
        {
            foreach (var property in group.Elements())
            {
                if (!ConditionMatches(
                        property.Attribute("Condition")?.Value,
                        properties))
                {
                    continue;
                }
                var value = Expand(property.Value.Trim(), properties);
                if (!ContainsUnresolvedProperty(value))
                {
                    properties[property.Name.LocalName] = value;
                }
            }
        }
    }

    private static CompileSettings ReadCompileSettings(
        XElement root,
        IReadOnlyDictionary<string, string> properties)
    {
        var settings = CompileSettings.Empty;
        foreach (var group in root.Elements().Where(element =>
                     string.Equals(
                         element.Name.LocalName,
                         "ItemDefinitionGroup",
                         StringComparison.Ordinal)
                     && ConditionMatches(
                         element.Attribute("Condition")?.Value,
                         properties)))
        {
            var compile = group.Elements().FirstOrDefault(element =>
                string.Equals(
                    element.Name.LocalName,
                    "ClCompile",
                    StringComparison.Ordinal));
            if (compile is not null)
            {
                settings = settings.Apply(compile, properties);
            }
        }
        return settings;
    }

    private static bool TryBuildArguments(
        string scopeRoot,
        string projectDirectory,
        InteropTarget target,
        string sourcePath,
        IReadOnlyDictionary<string, string> properties,
        CompileSettings settings,
        IReadOnlyList<string> systemIncludes,
        IReadOnlyList<string> additionalArguments,
        ScopePathPolicy pathPolicy,
        out IReadOnlyList<string> arguments,
        out string failure)
    {
        var result = new List<string>
        {
            "-x",
            IsCSource(sourcePath, settings.CompileAs) ? "c" : "c++",
            "--target=" + TargetTriple(target),
        };
        if (target.CompilerAbi == InteropCompilerAbi.Msvc)
        {
            result.Add("-fms-extensions");
            result.Add("-fms-compatibility");
        }

        var standard = MapLanguageStandard(settings.LanguageStandard);
        if (standard is not null)
        {
            result.Add(standard);
        }

        var definitions = SplitMsBuildList(settings.PreprocessorDefinitions);
        if (string.Equals(
                properties.GetValueOrDefault("CharacterSet"),
                "Unicode",
                StringComparison.OrdinalIgnoreCase))
        {
            definitions.Add("UNICODE");
            definitions.Add("_UNICODE");
        }
        foreach (var definition in definitions.Distinct(StringComparer.Ordinal))
        {
            var expanded = Expand(definition, properties);
            if (ContainsUnresolvedProperty(expanded))
            {
                arguments = [];
                failure = "A preprocessor definition contains an unresolved MSBuild property.";
                return false;
            }
            result.Add("-D" + expanded);
        }

        foreach (var rawDirectory in
                 SplitMsBuildList(settings.AdditionalIncludeDirectories))
        {
            var expanded = Expand(rawDirectory, properties);
            if (ContainsUnresolvedProperty(expanded))
            {
                arguments = [];
                failure = "An include directory contains an unresolved MSBuild property.";
                return false;
            }
            string fullDirectory;
            try
            {
                fullDirectory = Path.GetFullPath(
                    Path.IsPathFullyQualified(expanded)
                        ? expanded
                        : Path.Combine(projectDirectory, expanded));
            }
            catch (Exception ex) when (
                ex is ArgumentException
                    or NotSupportedException
                    or PathTooLongException)
            {
                arguments = [];
                failure = "An include directory has an invalid path.";
                return false;
            }
            if (!Directory.Exists(fullDirectory))
            {
                // MSVC accepts stale/nonexistent search roots and continues with the remaining
                // include path. Preserve that behavior; an actually required header will still
                // produce a fail-closed Clang diagnostic.
                continue;
            }
            if (pathPolicy.IsExcluded(fullDirectory))
            {
                arguments = [];
                failure =
                    "A project include directory is outside the approved scope.";
                return false;
            }
            result.Add("-I");
            result.Add(fullDirectory);
        }
        foreach (var systemInclude in systemIncludes)
        {
            result.Add("-I");
            result.Add(systemInclude);
        }

        foreach (var argument in additionalArguments)
        {
            result.Add(Expand(argument, properties));
        }
        if (result.Any(ContainsUnresolvedProperty))
        {
            arguments = [];
            failure = "An additional argument contains an unresolved MSBuild property.";
            return false;
        }

        arguments = result;
        failure = string.Empty;
        return true;
    }

    private static bool IsExcludedFromBuild(
        XElement item,
        IReadOnlyDictionary<string, string> properties) =>
        item.Elements()
            .Where(element =>
                string.Equals(
                    element.Name.LocalName,
                    "ExcludedFromBuild",
                    StringComparison.Ordinal)
                && ConditionMatches(
                    element.Attribute("Condition")?.Value,
                    properties))
            .Select(element => Expand(element.Value.Trim(), properties))
            .LastOrDefault()
            ?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;

    private static bool ConditionMatches(
        string? condition,
        IReadOnlyDictionary<string, string> properties)
    {
        if (string.IsNullOrWhiteSpace(condition))
        {
            return true;
        }
        var expanded = Expand(condition.Trim(), properties);
        var match = EqualityCondition().Match(expanded);
        if (!match.Success)
        {
            return false;
        }
        var equals = string.Equals(
            match.Groups["left"].Value,
            match.Groups["right"].Value,
            StringComparison.OrdinalIgnoreCase);
        return match.Groups["operator"].Value == "==" ? equals : !equals;
    }

    private static string Expand(
        string value,
        IReadOnlyDictionary<string, string> properties) =>
        PropertyReference().Replace(
            value,
            match => properties.TryGetValue(
                match.Groups["name"].Value,
                out var replacement)
                ? replacement
                : match.Value);

    private static bool ContainsUnresolvedProperty(string value) =>
        PropertyReference().IsMatch(value);

    private static List<string> SplitMsBuildList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }
        return value.Split(
                ';',
                StringSplitOptions.TrimEntries
                | StringSplitOptions.RemoveEmptyEntries)
            .Where(item => !item.StartsWith("%(", StringComparison.Ordinal))
            .ToList();
    }

    private static bool TryResolveScopeFile(
        string scopeRoot,
        string configuredPath,
        ScopePathPolicy pathPolicy,
        out string fullPath)
    {
        fullPath = string.Empty;
        try
        {
            var candidate = Path.GetFullPath(
                Path.Combine(scopeRoot, configuredPath));
            if (!File.Exists(candidate) || pathPolicy.IsExcluded(candidate))
            {
                return false;
            }
            fullPath = candidate;
            return true;
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or NotSupportedException
                or PathTooLongException
                or IOException
                or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryMakeScopeRelative(
        string scopeRoot,
        string fullPath,
        ScopePathPolicy pathPolicy,
        out string relativePath)
    {
        relativePath = string.Empty;
        if (!File.Exists(fullPath) || pathPolicy.IsExcluded(fullPath))
        {
            return false;
        }
        var relative = Path.GetRelativePath(scopeRoot, fullPath);
        if (relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith(
                ".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            return false;
        }
        relativePath = relative.Replace('\\', '/');
        return true;
    }

    private static bool PlatformMatchesTarget(
        string platform,
        InteropArchitecture architecture) =>
        architecture switch
        {
            InteropArchitecture.X86 =>
                platform.Equals("Win32", StringComparison.OrdinalIgnoreCase)
                || platform.Equals("x86", StringComparison.OrdinalIgnoreCase),
            InteropArchitecture.X64 =>
                platform.Equals("x64", StringComparison.OrdinalIgnoreCase),
            InteropArchitecture.Arm64 =>
                platform.Equals("ARM64", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

    private static string TargetTriple(InteropTarget target) =>
        (target.Architecture, target.CompilerAbi) switch
        {
            (InteropArchitecture.X86, InteropCompilerAbi.Msvc) =>
                "i686-pc-windows-msvc",
            (InteropArchitecture.X64, InteropCompilerAbi.Msvc) =>
                "x86_64-pc-windows-msvc",
            (InteropArchitecture.Arm64, InteropCompilerAbi.Msvc) =>
                "aarch64-pc-windows-msvc",
            (InteropArchitecture.X86, _) => "i686-unknown-linux-gnu",
            (InteropArchitecture.X64, _) => "x86_64-unknown-linux-gnu",
            (InteropArchitecture.Arm64, _) => "aarch64-unknown-linux-gnu",
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };

    private static string? MapLanguageStandard(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "default" => null,
            "stdcpp14" => "-std=c++14",
            "stdcpp17" => "-std=c++17",
            "stdcpp20" => "-std=c++20",
            "stdcpplatest" => "-std=c++23",
            "stdc11" => "-std=c11",
            "stdc17" => "-std=c17",
            _ => null,
        };

    private static bool IsCSource(string path, string? compileAs) =>
        compileAs?.Equals("CompileAsC", StringComparison.OrdinalIgnoreCase)
            ?? string.Equals(
                Path.GetExtension(path),
                ".c",
                StringComparison.OrdinalIgnoreCase);

    private static string NormalizeRelativePath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private static string EnsureTrailingSeparator(string path) =>
        Path.EndsInDirectorySeparator(path)
            ? path
            : path + Path.DirectorySeparatorChar;

    private static VcxProjectImportFailure Failure(
        string code,
        string message,
        string configuredPath) =>
        new(code, message, configuredPath);

    [GeneratedRegex(
        "^\\s*['\"](?<left>.*?)['\"]\\s*(?<operator>==|!=)\\s*['\"](?<right>.*?)['\"]\\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex EqualityCondition();

    [GeneratedRegex(
        "\\$\\((?<name>[A-Za-z_][A-Za-z0-9_.-]*)\\)",
        RegexOptions.CultureInvariant)]
    private static partial Regex PropertyReference();

    private sealed record CompileSettings(
        string? PreprocessorDefinitions,
        string? AdditionalIncludeDirectories,
        string? LanguageStandard,
        string? CompileAs)
    {
        public static CompileSettings Empty { get; } =
            new(null, null, null, null);

        public CompileSettings Apply(
            XElement compile,
            IReadOnlyDictionary<string, string> properties) =>
            new(
                Read(
                    compile,
                    "PreprocessorDefinitions",
                    properties,
                    PreprocessorDefinitions),
                Read(
                    compile,
                    "AdditionalIncludeDirectories",
                    properties,
                    AdditionalIncludeDirectories),
                Read(
                    compile,
                    "LanguageStandard",
                    properties,
                    LanguageStandard),
                Read(compile, "CompileAs", properties, CompileAs));

        public CompileSettings ApplyItemOverrides(
            XElement item,
            IReadOnlyDictionary<string, string> properties) =>
            Apply(item, properties);

        private static string? Read(
            XElement parent,
            string name,
            IReadOnlyDictionary<string, string> properties,
            string? inherited)
        {
            var value = parent.Elements()
                .Where(element =>
                    string.Equals(
                        element.Name.LocalName,
                        name,
                        StringComparison.Ordinal)
                    && ConditionMatches(
                        element.Attribute("Condition")?.Value,
                        properties))
                .Select(element => Expand(element.Value.Trim(), properties))
                .LastOrDefault();
            if (value is null)
            {
                return inherited;
            }
            var inheritToken = $"%({name})";
            return value.Contains(inheritToken, StringComparison.Ordinal)
                ? value.Replace(
                    inheritToken,
                    inherited ?? string.Empty,
                    StringComparison.Ordinal)
                : value;
        }
    }

    private static class MsvcToolchainIncludes
    {
        public static IReadOnlyList<string> Discover(
            string? platformToolset,
            string? windowsTargetPlatformVersion)
        {
            if (!OperatingSystem.IsWindows())
            {
                return [];
            }
            var includes = new HashSet<string>(PathComparer);
            AddMsvcIncludes(includes, platformToolset);
            AddWindowsSdkIncludes(
                includes,
                windowsTargetPlatformVersion);
            return includes
                .OrderBy(path => path, PathComparer)
                .ThenBy(path => path, StringComparer.Ordinal)
                .ToArray();
        }

        private static void AddMsvcIncludes(
            ISet<string> includes,
            string? platformToolset)
        {
            var direct = Environment.GetEnvironmentVariable(
                "VCToolsInstallDir");
            AddIfDirectory(includes, Path.Join(direct ?? "", "include"));

            foreach (var visualStudioRoot in VisualStudioRoots())
            {
                var toolsRoot = Path.Join(
                    visualStudioRoot,
                    "VC",
                    "Tools",
                    "MSVC");
                if (!Directory.Exists(toolsRoot))
                {
                    continue;
                }
                var candidates = Directory.EnumerateDirectories(toolsRoot)
                    .Where(path =>
                        Directory.Exists(Path.Join(path, "include")))
                    .Select(path => new
                    {
                        Path = path,
                        Version = Path.GetFileName(path),
                    })
                    .Where(candidate =>
                        ToolsetMatches(
                            candidate.Version,
                            platformToolset))
                    .OrderByDescending(
                        candidate => candidate.Version,
                        StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var selected = candidates.FirstOrDefault()
                    ?? Directory.EnumerateDirectories(toolsRoot)
                        .Where(path =>
                            Directory.Exists(Path.Join(path, "include")))
                        .OrderByDescending(
                            path => Path.GetFileName(path),
                            StringComparer.OrdinalIgnoreCase)
                        .Select(path => new
                        {
                            Path = path,
                            Version = Path.GetFileName(path),
                        })
                        .FirstOrDefault();
                if (selected is not null)
                {
                    AddIfDirectory(
                        includes,
                        Path.Join(selected.Path, "include"));
                    return;
                }
            }
        }

        private static void AddWindowsSdkIncludes(
            ISet<string> includes,
            string? requestedVersion)
        {
            foreach (var sdkRoot in WindowsSdkRoots())
            {
                var includeRoot = Path.Join(sdkRoot, "Include");
                if (!Directory.Exists(includeRoot))
                {
                    continue;
                }
                var versionDirectory = !string.IsNullOrWhiteSpace(
                        requestedVersion)
                    ? Path.Join(
                        includeRoot,
                        requestedVersion.TrimEnd('\\', '/'))
                    : string.Empty;
                if (!Directory.Exists(versionDirectory))
                {
                    versionDirectory = Directory
                        .EnumerateDirectories(includeRoot)
                        .OrderByDescending(
                            path => Path.GetFileName(path),
                            StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault() ?? string.Empty;
                }
                if (!Directory.Exists(versionDirectory))
                {
                    continue;
                }
                foreach (var child in new[]
                         {
                             "ucrt",
                             "shared",
                             "um",
                             "winrt",
                             "cppwinrt",
                         })
                {
                    AddIfDirectory(
                        includes,
                        Path.Join(versionDirectory, child));
                }
                return;
            }
        }

        private static IEnumerable<string> VisualStudioRoots()
        {
            var roots = new HashSet<string>(PathComparer);
            AddEnvironmentRoot(roots, "VSINSTALLDIR");
            AddRegistryValues(
                roots,
                @"SOFTWARE\Microsoft\VisualStudio\SxS\VS7");
            AddRegistryValues(
                roots,
                @"SOFTWARE\WOW6432Node\Microsoft\VisualStudio\SxS\VS7");
            AddVsWhereRoots(roots);
            return roots.Where(Directory.Exists);
        }

        private static void AddVsWhereRoots(ISet<string> roots)
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }
            var programFilesX86 = Environment.GetFolderPath(
                Environment.SpecialFolder.ProgramFilesX86);
            var executable = Path.Join(
                programFilesX86,
                "Microsoft Visual Studio",
                "Installer",
                "vswhere.exe");
            if (!File.Exists(executable))
            {
                return;
            }

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments =
                        "-all -products * -property installationPath -utf8",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            try
            {
                if (!process.Start())
                {
                    return;
                }
                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(milliseconds: 5_000)
                    || process.ExitCode != 0)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException)
                    {
                        // The bounded discovery helper already exited.
                    }
                    return;
                }
                var output = outputTask.GetAwaiter().GetResult();
                _ = errorTask.GetAwaiter().GetResult();
                foreach (var line in output.Split(
                             ['\r', '\n'],
                             StringSplitOptions.TrimEntries
                             | StringSplitOptions.RemoveEmptyEntries))
                {
                    roots.Add(line);
                }
            }
            catch (Exception ex) when (
                ex is InvalidOperationException
                    or System.ComponentModel.Win32Exception
                    or IOException
                    or UnauthorizedAccessException)
            {
                // The registry/environment candidates remain available.
            }
        }

        private static IEnumerable<string> WindowsSdkRoots()
        {
            var roots = new HashSet<string>(PathComparer);
            AddEnvironmentRoot(roots, "WindowsSdkDir");
            AddRegistryValues(
                roots,
                @"SOFTWARE\Microsoft\Windows Kits\Installed Roots",
                "KitsRoot10");
            AddRegistryValues(
                roots,
                @"SOFTWARE\WOW6432Node\Microsoft\Windows Kits\Installed Roots",
                "KitsRoot10");
            return roots.Where(Directory.Exists);
        }

        private static void AddEnvironmentRoot(
            ISet<string> roots,
            string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                roots.Add(value);
            }
        }

        private static void AddRegistryValues(
            ISet<string> roots,
            string subKey,
            string? exactName = null)
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(subKey);
                if (key is null)
                {
                    return;
                }
                var names = exactName is null
                    ? key.GetValueNames()
                    : [exactName];
                foreach (var name in names)
                {
                    if (key.GetValue(name) is string value
                        && !string.IsNullOrWhiteSpace(value))
                    {
                        roots.Add(value);
                    }
                }
            }
            catch (Exception ex) when (
                ex is UnauthorizedAccessException
                    or IOException
                    or System.Security.SecurityException)
            {
                // Toolchain discovery is best effort. Clang diagnostics remain fail closed.
            }
        }

        private static bool ToolsetMatches(
            string version,
            string? platformToolset)
        {
            if (string.IsNullOrWhiteSpace(platformToolset)
                || !platformToolset.StartsWith(
                    "v",
                    StringComparison.OrdinalIgnoreCase)
                || platformToolset.Length < 4)
            {
                return true;
            }
            var digits = platformToolset[1..];
            var prefix = digits.Length switch
            {
                3 => $"{digits[..2]}.{digits[2]}",
                2 => $"{digits[0]}.{digits[1]}",
                _ => string.Empty,
            };
            return prefix.Length == 0
                || version.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static void AddIfDirectory(
            ISet<string> paths,
            string path)
        {
            if (!string.IsNullOrWhiteSpace(path)
                && Directory.Exists(path))
            {
                paths.Add(Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(path)));
            }
        }
    }
}
