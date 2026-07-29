using System.Runtime.InteropServices;
using System.Xml;
using System.Xml.Linq;
using DevBitsLab.Mcp.SourceGraph.Core;

namespace DevBitsLab.Mcp.SourceGraph.Server.Interop;

internal sealed record SolutionNativeInteropResolutionFailure(
    string Code,
    string Message,
    string? Path = null);

internal sealed record ResolvedSolutionNativeInterop(
    ScopeInteropConfig? Configuration,
    int DiscoveredProjects,
    IReadOnlyList<SolutionNativeInteropResolutionFailure> Failures);

/// <summary>
/// Converts solution membership into a deterministic native configuration without executing
/// MSBuild. In solution scopes, the solution is the authoritative project/source boundary;
/// authored interop entries can enrich matching members but cannot add or narrow membership.
/// </summary>
internal static class SolutionNativeInteropResolver
{
    private const long MaximumProjectCharacters = 8L * 1024L * 1024L;

    public static ResolvedSolutionNativeInterop Resolve(Scope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.ProjectSet is not ScopeProjectSet.Solutions solutions)
        {
            return new ResolvedSolutionNativeInterop(
                scope.Interop,
                scope.Interop?.VcxProjects.Count ?? 0,
                []);
        }

        var membership = SolutionProjectMembershipResolver.Resolve(
            scope.Root,
            solutions);
        var failures = membership.Failures
            .Select(failure => new SolutionNativeInteropResolutionFailure(
                failure.Code,
                failure.Message,
                failure.Path))
            .ToList();
        var nativeProjects = membership.VisualCppProjects;
        if (nativeProjects.Count == 0)
        {
            return new ResolvedSolutionNativeInterop(
                Configuration: null,
                DiscoveredProjects: 0,
                failures);
        }

        var target = scope.Interop?.Target ?? PreferredTarget();
        var solutionConfiguration = SelectSolutionConfiguration(
            membership.SolutionConfigurations,
            target.Architecture);
        var explicitByPath = (scope.Interop?.VcxProjects ?? [])
            .GroupBy(
                project => NormalizeRelativePath(project.Path),
                PathComparer)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                PathComparer);

        foreach (var explicitProject in explicitByPath)
        {
            if (!nativeProjects.Any(project => PathComparer.Equals(
                    NormalizeRelativePath(project.ProjectPath),
                    explicitProject.Key)))
            {
                failures.Add(new SolutionNativeInteropResolutionFailure(
                    "vcxproj-not-in-solution",
                    "An explicitly configured Visual C++ project is not a member of the solution and was ignored.",
                    explicitProject.Value.Path));
            }
        }

        var resolved = new List<InteropVcxProjectConfig>();
        foreach (var project in nativeProjects)
        {
            var relativePath = NormalizeRelativePath(project.ProjectPath);
            explicitByPath.TryGetValue(relativePath, out var authored);
            var projectConfiguration = ResolveProjectConfiguration(
                project,
                solutionConfiguration,
                target.Architecture,
                failures);
            if (projectConfiguration is null)
            {
                continue;
            }

            var separator = projectConfiguration.IndexOf('|');
            if (separator <= 0 || separator == projectConfiguration.Length - 1)
            {
                failures.Add(new SolutionNativeInteropResolutionFailure(
                    "vcxproj-configuration-invalid",
                    "The selected project configuration is malformed.",
                    relativePath));
                continue;
            }
            var configuration = projectConfiguration[..separator];
            var platform = projectConfiguration[(separator + 1)..];
            if (!PlatformMatchesTarget(platform, target.Architecture))
            {
                failures.Add(new SolutionNativeInteropResolutionFailure(
                    "vcxproj-target-mismatch",
                    $"Project mapping `{projectConfiguration}` does not match ABI target `{target.Architecture}`.",
                    relativePath));
                continue;
            }

            var library = !string.IsNullOrWhiteSpace(authored?.Library)
                ? authored.Library
                : ResolveLibraryName(
                    project.FullPath,
                    configuration,
                    platform);
            resolved.Add(new InteropVcxProjectConfig(
                relativePath,
                configuration,
                platform,
                library,
                SourceFiles: [],
                authored?.AdditionalArguments ?? [],
                authored?.BinaryPath));
        }

        if (resolved.Count == 0)
        {
            return new ResolvedSolutionNativeInterop(
                Configuration: null,
                nativeProjects.Count,
                failures);
        }
        return new ResolvedSolutionNativeInterop(
            new ScopeInteropConfig(target, TranslationUnits: [])
            {
                VcxProjects = resolved,
            },
            nativeProjects.Count,
            failures);
    }

    private static InteropTarget PreferredTarget() =>
        RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => InteropTarget.WindowsX86Msvc,
            Architecture.Arm64 => new InteropTarget(
                "win-arm64",
                InteropArchitecture.Arm64,
                InteropCompilerAbi.Msvc,
                pointerSizeBytes: 8,
                defaultPack: 8),
            _ => InteropTarget.WindowsX64Msvc,
        };

    private static string? SelectSolutionConfiguration(
        IReadOnlyList<string> configurations,
        InteropArchitecture architecture)
    {
        var platform = PlatformName(architecture);
        var preferences = new[]
        {
            $"Release|{platform}",
            $"Debug|{platform}",
        };
        foreach (var preference in preferences)
        {
            var match = configurations.FirstOrDefault(configuration =>
                configuration.Equals(
                    preference,
                    StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }
        return configurations
            .Where(configuration => configuration.EndsWith(
                "|" + platform,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(configuration =>
                configuration.StartsWith(
                    "Release|",
                    StringComparison.OrdinalIgnoreCase)
                    ? 0
                    : 1)
            .ThenBy(configuration => configuration, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static string? ResolveProjectConfiguration(
        SolutionProjectMember project,
        string? solutionConfiguration,
        InteropArchitecture architecture,
        ICollection<SolutionNativeInteropResolutionFailure> failures)
    {
        if (solutionConfiguration is not null
            && project.ActiveConfigurations.TryGetValue(
                solutionConfiguration,
                out var mapped))
        {
            return mapped;
        }

        IReadOnlyList<string> declared;
        try
        {
            declared = ReadDeclaredConfigurations(project.FullPath);
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or XmlException
                or InvalidOperationException)
        {
            failures.Add(new SolutionNativeInteropResolutionFailure(
                "vcxproj-read-failed",
                $"The Visual C++ project could not be read safely ({ex.GetType().Name}).",
                project.ProjectPath));
            return null;
        }

        var platform = PlatformName(architecture);
        foreach (var preference in new[]
                 {
                     $"Release|{platform}",
                     $"Debug|{platform}",
                 })
        {
            var match = declared.FirstOrDefault(configuration =>
                configuration.Equals(
                    preference,
                    StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        failures.Add(new SolutionNativeInteropResolutionFailure(
            "vcxproj-configuration-not-found",
            $"The project has no compatible Release/Debug `{platform}` configuration.",
            project.ProjectPath));
        return null;
    }

    private static IReadOnlyList<string> ReadDeclaredConfigurations(string path)
    {
        var document = ReadProject(path);
        return document
            .Descendants()
            .Where(element => string.Equals(
                element.Name.LocalName,
                "ProjectConfiguration",
                StringComparison.Ordinal))
            .Select(element => element.Attribute("Include")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ResolveLibraryName(
        string projectPath,
        string configuration,
        string platform)
    {
        try
        {
            var document = ReadProject(projectPath);
            var properties = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["Configuration"] = configuration,
                ["Platform"] = platform,
            };
            var applicable = document.Root?
                .Elements()
                .Where(element => string.Equals(
                    element.Name.LocalName,
                    "PropertyGroup",
                    StringComparison.Ordinal)
                    && ConditionMatches(
                        element.Attribute("Condition")?.Value,
                        configuration,
                        platform))
                .ToArray() ?? [];
            var targetName = LastProperty(applicable, "TargetName")
                ?? Path.GetFileNameWithoutExtension(projectPath);
            var configurationType = LastProperty(
                applicable,
                "ConfigurationType");
            var targetExtension = LastProperty(applicable, "TargetExt")
                ?? configurationType?.ToLowerInvariant() switch
                {
                    "staticlibrary" => ".lib",
                    "application" => ".exe",
                    _ => ".dll",
                };
            targetName = ExpandKnown(targetName, properties);
            targetExtension = ExpandKnown(targetExtension, properties);
            return targetName.EndsWith(
                targetExtension,
                StringComparison.OrdinalIgnoreCase)
                ? targetName
                : targetName + targetExtension;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or XmlException
                or InvalidOperationException)
        {
            return Path.GetFileNameWithoutExtension(projectPath) + ".dll";
        }
    }

    private static XDocument ReadProject(string path)
    {
        using var stream = new FileStream(
            path,
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
        return XDocument.Load(reader);
    }

    private static string? LastProperty(
        IEnumerable<XElement> groups,
        string name) =>
        groups
            .SelectMany(group => group.Elements())
            .Where(element => string.Equals(
                element.Name.LocalName,
                name,
                StringComparison.Ordinal))
            .Select(element => element.Value.Trim())
            .LastOrDefault(value => value.Length > 0);

    private static bool ConditionMatches(
        string? condition,
        string configuration,
        string platform)
    {
        if (string.IsNullOrWhiteSpace(condition))
        {
            return true;
        }
        var normalized = condition
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("\"", "'", StringComparison.Ordinal);
        return normalized.Contains(
            $"'$(Configuration)|$(Platform)'=='{configuration}|{platform}'",
            StringComparison.OrdinalIgnoreCase);
    }

    private static string ExpandKnown(
        string value,
        IReadOnlyDictionary<string, string> properties)
    {
        foreach (var property in properties)
        {
            value = value.Replace(
                "$(" + property.Key + ")",
                property.Value,
                StringComparison.OrdinalIgnoreCase);
        }
        return value;
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

    private static string PlatformName(InteropArchitecture architecture) =>
        architecture switch
        {
            InteropArchitecture.X86 => "Win32",
            InteropArchitecture.X64 => "x64",
            InteropArchitecture.Arm64 => "ARM64",
            _ => throw new ArgumentOutOfRangeException(nameof(architecture)),
        };

    private static string NormalizeRelativePath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}
