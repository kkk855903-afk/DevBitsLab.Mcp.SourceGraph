using DevBitsLab.Mcp.SourceGraph.Core;

namespace DevBitsLab.Mcp.SourceGraph.Server.Interop;

internal sealed record NativeInteropDiscoveryResult(
    IReadOnlyList<string> VcxProjects,
    IReadOnlyList<string> CMakeProjects,
    IReadOnlyList<string> CompilationDatabases,
    IReadOnlyList<string> Binaries,
    IReadOnlyList<string> Architectures,
    IReadOnlyList<string> Configurations,
    bool Truncated)
{
    public bool IsSolutionScoped { get; init; }

    public string ToDiagnostic()
    {
        var inputs = VcxProjects
            .Concat(CMakeProjects)
            .Concat(CompilationDatabases)
            .ToArray();
        if (inputs.Length == 0 && Binaries.Count == 0)
        {
            return " No .vcxproj, CMakeLists.txt, compile_commands.json, or DLL candidate "
                + "was found in the bounded repository scan.";
        }

        var architecture = Architectures.Count switch
        {
            0 => "architecture=unknown",
            1 => $"architecture={Architectures[0]}",
            _ => $"architecture=ambiguous({string.Join(",", Architectures)})",
        };
        var configuration = Configurations.Count switch
        {
            0 => "configuration=unknown",
            1 => $"configuration={Configurations[0]}",
            _ => $"configuration=ambiguous({string.Join(",", Configurations)})",
        };
        var suffix = Truncated ? "; scan=truncated" : "";
        var boundary = IsSolutionScoped
            ? $" Solution membership contains {VcxProjects.Count} native project(s)."
            : "";
        return boundary
            + $" Discovered native inputs=[{string.Join(", ", inputs)}]; "
            + $"binaries=[{string.Join(", ", Binaries)}]; {architecture}; "
            + $"{configuration}{suffix}."
            + (IsSolutionScoped
                ? " Native inputs outside the solution are excluded."
                : " Configure `interop.target` and `interop.translation_units` explicitly; "
                    + "ambiguous targets are not selected.");
    }
}

internal static class NativeInteropDiscovery
{
    private const int MaximumVisitedFiles = 20_000;
    private const int MaximumCandidatesPerKind = 8;

    public static NativeInteropDiscoveryResult Discover(Scope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.ProjectSet is not ScopeProjectSet.Solutions solutions)
        {
            return Discover(scope.Root);
        }

        var membership = SolutionProjectMembershipResolver.Resolve(
            scope.Root,
            solutions);
        return new NativeInteropDiscoveryResult(
            membership.VisualCppProjects
                .Select(project => project.ProjectPath.Replace('\\', '/'))
                .ToArray(),
            CMakeProjects: [],
            CompilationDatabases: [],
            Binaries: [],
            Architectures: [],
            Configurations: [],
            Truncated: membership.Failures.Count > 0)
        {
            IsSolutionScoped = true,
        };
    }

    public static NativeInteropDiscoveryResult Discover(string repositoryRoot)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var vcxProjects = new List<string>();
        var cmakeProjects = new List<string>();
        var compilationDatabases = new List<string>();
        var binaries = new List<string>();
        var architectures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var configurations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(root);
        var visitedFiles = 0;
        var truncated = false;

        while (pending.Count > 0 && !truncated)
        {
            var directory = pending.Pop();
            try
            {
                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    var name = Path.GetFileName(child);
                    if (name is ".git" or ".sourcegraph"
                        || File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint))
                    {
                        continue;
                    }
                    pending.Push(child);
                }

                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    if (++visitedFiles > MaximumVisitedFiles)
                    {
                        truncated = true;
                        break;
                    }

                    var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                    var name = Path.GetFileName(file);
                    if (file.EndsWith(".vcxproj", StringComparison.OrdinalIgnoreCase))
                    {
                        AddBounded(vcxProjects, relative);
                    }
                    else if (name.Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase))
                    {
                        AddBounded(cmakeProjects, relative);
                    }
                    else if (name.Equals("compile_commands.json", StringComparison.OrdinalIgnoreCase))
                    {
                        AddBounded(compilationDatabases, relative);
                    }
                    else if (file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    {
                        AddBounded(binaries, relative);
                        ClassifyTarget(relative, architectures, configurations);
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                truncated = true;
            }
            catch (IOException)
            {
                truncated = true;
            }
        }

        return new NativeInteropDiscoveryResult(
            vcxProjects,
            cmakeProjects,
            compilationDatabases,
            binaries,
            architectures.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            configurations.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            truncated);
    }

    private static void AddBounded(List<string> target, string value)
    {
        if (target.Count < MaximumCandidatesPerKind)
        {
            target.Add(value);
        }
    }

    private static void ClassifyTarget(
        string path,
        ISet<string> architectures,
        ISet<string> configurations)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (segment.Equals("x86", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("win32", StringComparison.OrdinalIgnoreCase))
            {
                architectures.Add("x86");
            }
            else if (segment.Equals("x64", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("amd64", StringComparison.OrdinalIgnoreCase))
            {
                architectures.Add("x64");
            }
            else if (segment.Equals("arm64", StringComparison.OrdinalIgnoreCase))
            {
                architectures.Add("arm64");
            }

            if (segment.Equals("debug", StringComparison.OrdinalIgnoreCase))
            {
                configurations.Add("Debug");
            }
            else if (segment.Equals("release", StringComparison.OrdinalIgnoreCase))
            {
                configurations.Add("Release");
            }
        }
    }
}
