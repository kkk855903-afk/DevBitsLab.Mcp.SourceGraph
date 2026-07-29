using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace DevBitsLab.Mcp.SourceGraph.Core;

public sealed record SolutionProjectMembershipFailure(
    string Code,
    string Message,
    string? Path = null);

public sealed record SolutionProjectMember(
    string Name,
    string ProjectPath,
    string FullPath,
    string? ProjectGuid,
    IReadOnlyDictionary<string, string> ActiveConfigurations);

public sealed record SolutionProjectMembership(
    IReadOnlyList<SolutionProjectMember> Projects,
    IReadOnlyList<string> SolutionConfigurations,
    IReadOnlyList<SolutionProjectMembershipFailure> Failures)
{
    public IReadOnlyList<SolutionProjectMember> VisualCppProjects =>
        Projects
            .Where(project => string.Equals(
                System.IO.Path.GetExtension(project.FullPath),
                ".vcxproj",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
}

/// <summary>
/// Reads only the declarative project membership and configuration map of .sln/.slnx files.
/// It never invokes MSBuild or evaluates project imports, targets, tasks, or build events.
/// </summary>
public static partial class SolutionProjectMembershipResolver
{
    private const long MaximumSolutionCharacters = 8L * 1024L * 1024L;
    private const int MaximumProjects = 4096;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    public static SolutionProjectMembership Resolve(
        string repositoryRoot,
        ScopeProjectSet.Solutions solutions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(solutions);

        var root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(repositoryRoot));
        var policy = new ScopePathPolicy(root, solutions.Exclude);
        var projects = new Dictionary<string, SolutionProjectMember>(
            PathComparer);
        var configurations = new List<string>();
        var failures = new List<SolutionProjectMembershipFailure>();

        foreach (var configuredSolution in solutions.Items)
        {
            if (!TryResolveFile(
                    root,
                    configuredSolution,
                    policy,
                    out var solutionPath))
            {
                failures.Add(new SolutionProjectMembershipFailure(
                    "solution-path-rejected",
                    "The configured solution is missing or outside the approved scope.",
                    configuredSolution));
                continue;
            }

            SolutionParseResult parsed;
            try
            {
                parsed = string.Equals(
                    Path.GetExtension(solutionPath),
                    ".slnx",
                    StringComparison.OrdinalIgnoreCase)
                    ? ParseSlnx(solutionPath)
                    : ParseSln(solutionPath);
            }
            catch (Exception ex) when (
                ex is IOException
                    or UnauthorizedAccessException
                    or XmlException
                    or InvalidOperationException)
            {
                failures.Add(new SolutionProjectMembershipFailure(
                    "solution-read-failed",
                    $"The solution could not be read safely ({ex.GetType().Name}).",
                    configuredSolution));
                continue;
            }

            foreach (var configuration in parsed.Configurations)
            {
                if (!configurations.Contains(
                        configuration,
                        StringComparer.OrdinalIgnoreCase))
                {
                    configurations.Add(configuration);
                }
            }

            var solutionDirectory = Path.GetDirectoryName(solutionPath)!;
            foreach (var candidate in parsed.Projects)
            {
                if (projects.Count >= MaximumProjects)
                {
                    failures.Add(new SolutionProjectMembershipFailure(
                        "solution-project-limit-exceeded",
                        $"Solution membership exceeds the {MaximumProjects}-project limit.",
                        configuredSolution));
                    break;
                }
                if (!TryResolveProject(
                        root,
                        solutionDirectory,
                        candidate.RelativePath,
                        policy,
                        out var fullPath))
                {
                    failures.Add(new SolutionProjectMembershipFailure(
                        "solution-project-path-rejected",
                        "A solution project is missing or outside the approved scope.",
                        candidate.RelativePath));
                    continue;
                }

                var relative = Path.GetRelativePath(root, fullPath)
                    .Replace('\\', '/');
                projects[fullPath] = new SolutionProjectMember(
                    candidate.Name,
                    relative,
                    fullPath,
                    candidate.ProjectGuid,
                    candidate.ActiveConfigurations);
            }
        }

        return new SolutionProjectMembership(
            projects.Values
                .OrderBy(project => project.ProjectPath, PathComparer)
                .ThenBy(project => project.ProjectPath, StringComparer.Ordinal)
                .ToArray(),
            configurations,
            failures);
    }

    private static SolutionParseResult ParseSln(string solutionPath)
    {
        var text = ReadBoundedText(solutionPath);
        var candidates = new List<MutableProject>();
        var configurations = new List<string>();
        var inSolutionConfigurations = false;
        var inProjectConfigurations = false;

        foreach (var line in text.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            var projectMatch = ProjectLine().Match(line);
            if (projectMatch.Success)
            {
                var typeGuid = projectMatch.Groups["type"].Value;
                var relativePath = projectMatch.Groups["path"].Value;
                if (!typeGuid.Equals(
                        "{2150E333-8FDC-42A3-9474-1A3956D46DE8}",
                        StringComparison.OrdinalIgnoreCase)
                    && Path.HasExtension(relativePath))
                {
                    candidates.Add(new MutableProject(
                        projectMatch.Groups["name"].Value,
                        relativePath,
                        NormalizeGuid(projectMatch.Groups["guid"].Value)));
                }
                continue;
            }

            if (line.Contains(
                    "GlobalSection(SolutionConfigurationPlatforms)",
                    StringComparison.Ordinal))
            {
                inSolutionConfigurations = true;
                inProjectConfigurations = false;
                continue;
            }
            if (line.Contains(
                    "GlobalSection(ProjectConfigurationPlatforms)",
                    StringComparison.Ordinal))
            {
                inSolutionConfigurations = false;
                inProjectConfigurations = true;
                continue;
            }
            if (line.TrimStart().StartsWith(
                    "EndGlobalSection",
                    StringComparison.Ordinal))
            {
                inSolutionConfigurations = false;
                inProjectConfigurations = false;
                continue;
            }

            if (inSolutionConfigurations)
            {
                var separator = line.IndexOf('=');
                if (separator > 0)
                {
                    var configuration = line[..separator].Trim();
                    if (configuration.Length > 0)
                    {
                        configurations.Add(configuration);
                    }
                }
                continue;
            }
            if (!inProjectConfigurations)
            {
                continue;
            }

            var mapping = ProjectConfigurationLine().Match(line);
            if (!mapping.Success)
            {
                continue;
            }
            var guid = NormalizeGuid(mapping.Groups["guid"].Value);
            var project = candidates.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.ProjectGuid,
                    guid,
                    StringComparison.OrdinalIgnoreCase));
            if (project is not null)
            {
                project.ActiveConfigurations[
                    mapping.Groups["solution"].Value.Trim()] =
                    mapping.Groups["project"].Value.Trim();
            }
        }

        return new SolutionParseResult(
            candidates.Select(candidate => candidate.ToCandidate()).ToArray(),
            configurations);
    }

    private static SolutionParseResult ParseSlnx(string solutionPath)
    {
        using var stream = new FileStream(
            solutionPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = XmlReader.Create(
            stream,
            new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumSolutionCharacters,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            });
        var document = XDocument.Load(reader);
        var projects = document
            .Descendants()
            .Where(element => string.Equals(
                element.Name.LocalName,
                "Project",
                StringComparison.Ordinal))
            .Select(element => element.Attribute("Path")?.Value)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Take(MaximumProjects + 1)
            .Select(path => new ProjectCandidate(
                Path.GetFileNameWithoutExtension(path!),
                path!,
                ProjectGuid: null,
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)))
            .ToArray();
        if (projects.Length > MaximumProjects)
        {
            throw new InvalidOperationException(
                $"Solution membership exceeds the {MaximumProjects}-project limit.");
        }
        return new SolutionParseResult(projects, []);
    }

    private static string ReadBoundedText(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaximumSolutionCharacters * 4)
        {
            throw new InvalidOperationException("The solution file is too large.");
        }
        var text = File.ReadAllText(path);
        if (text.Length > MaximumSolutionCharacters)
        {
            throw new InvalidOperationException("The solution file is too large.");
        }
        return text;
    }

    private static bool TryResolveFile(
        string root,
        string configuredPath,
        ScopePathPolicy policy,
        out string fullPath)
    {
        fullPath = string.Empty;
        try
        {
            var candidate = Path.GetFullPath(
                Path.IsPathFullyQualified(configuredPath)
                    ? configuredPath
                    : Path.Join(root, configuredPath));
            if (!IsSameOrDescendant(root, candidate)
                || policy.IsExcludedForDiscovery(candidate, out var physical)
                || physical is null
                || !File.Exists(physical))
            {
                return false;
            }
            fullPath = Path.GetFullPath(physical);
            return IsSameOrDescendant(root, fullPath);
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or PathTooLongException)
        {
            return false;
        }
    }

    private static bool TryResolveProject(
        string root,
        string solutionDirectory,
        string relativePath,
        ScopePathPolicy policy,
        out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathFullyQualified(relativePath))
        {
            return false;
        }
        try
        {
            var candidate = Path.GetFullPath(
                Path.Join(
                    solutionDirectory,
                    relativePath
                        .Replace('\\', Path.DirectorySeparatorChar)
                        .Replace('/', Path.DirectorySeparatorChar)));
            if (!IsSameOrDescendant(root, candidate)
                || policy.IsExcludedForDiscovery(candidate, out var physical)
                || physical is null
                || !File.Exists(physical))
            {
                return false;
            }
            fullPath = Path.GetFullPath(physical);
            return IsSameOrDescendant(root, fullPath);
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsSameOrDescendant(string root, string candidate)
    {
        if (PathComparer.Equals(root, candidate))
        {
            return true;
        }
        return candidate.StartsWith(
            root + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }

    private static string NormalizeGuid(string value) =>
        value.Trim().ToUpperInvariant();

    [GeneratedRegex(
        "^\\s*Project\\(\"(?<type>\\{[^}]+\\})\"\\)\\s*=\\s*\"(?<name>[^\"]+)\",\\s*\"(?<path>[^\"]+)\",\\s*\"(?<guid>\\{[^}]+\\})\"",
        RegexOptions.CultureInvariant)]
    private static partial Regex ProjectLine();

    [GeneratedRegex(
        "^\\s*(?<guid>\\{[^}]+\\})\\.(?<solution>.+?)\\.ActiveCfg\\s*=\\s*(?<project>.+?)\\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ProjectConfigurationLine();

    private sealed record ProjectCandidate(
        string Name,
        string RelativePath,
        string? ProjectGuid,
        IReadOnlyDictionary<string, string> ActiveConfigurations);

    private sealed record SolutionParseResult(
        IReadOnlyList<ProjectCandidate> Projects,
        IReadOnlyList<string> Configurations);

    private sealed class MutableProject
    {
        public MutableProject(string name, string relativePath, string projectGuid)
        {
            Name = name;
            RelativePath = relativePath;
            ProjectGuid = projectGuid;
        }

        public string Name { get; }
        public string RelativePath { get; }
        public string ProjectGuid { get; }
        public Dictionary<string, string> ActiveConfigurations { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public ProjectCandidate ToCandidate() =>
            new(Name, RelativePath, ProjectGuid, ActiveConfigurations);
    }
}
