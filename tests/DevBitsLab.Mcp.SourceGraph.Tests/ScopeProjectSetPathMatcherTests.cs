using DevBitsLab.Mcp.SourceGraph.Core;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class ScopeProjectSetPathMatcherTests : IDisposable
{
    private readonly string _root =
        Path.Join(Path.GetTempPath(), "sourcegraph-project-set-" + Guid.NewGuid().ToString("N"));

    public ScopeProjectSetPathMatcherTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Paths_scope_usesScopeGlobSemantics_toSelectProjectRoots()
    {
        await PlantAsync(Path.Join(_root, "src", "web", "Web.csproj"));
        await PlantAsync(Path.Join(_root, "vendor", "Vendor.csproj"));
        var matcher = new ScopeProjectSetPathMatcher(
            _root,
            new ScopeProjectSet.Paths(["src/**/*.csproj"], Array.Empty<string>()));

        matcher.Includes(Path.Join(_root, "src", "web", "app.ts")).Should().BeTrue();
        matcher.Includes(Path.Join(_root, "src", "web", "nested", "app.ts")).Should().BeTrue();
        matcher.Includes(Path.Join(_root, "vendor", "app.ts")).Should().BeFalse();
    }

    [Fact]
    public async Task Projects_scope_includesOnlyConfiguredProjectDirectories()
    {
        await PlantAsync(Path.Join(_root, "src", "App", "App.csproj"));
        await PlantAsync(Path.Join(_root, "src", "Other", "Other.csproj"));
        var matcher = new ScopeProjectSetPathMatcher(
            _root,
            new ScopeProjectSet.Projects(
                ["src/App/App.csproj"],
                Array.Empty<string>()));

        matcher.Includes(Path.Join(_root, "src", "App", "Bridge.cpp")).Should().BeTrue();
        matcher.Includes(Path.Join(_root, "src", "Other", "Bridge.cpp")).Should().BeFalse();
    }

    [Fact]
    public void Paths_scope_withoutMatchingProject_failsClosed()
    {
        var matcher = new ScopeProjectSetPathMatcher(
            _root,
            new ScopeProjectSet.Paths(["missing/**/*.csproj"], Array.Empty<string>()));

        matcher.Includes(Path.Join(_root, "missing", "Bridge.cpp")).Should().BeFalse();
    }

    [Fact]
    public async Task Solutions_scope_includesOnlySolutionProjectDirectories()
    {
        await PlantAsync(Path.Join(_root, "src", "App", "App.csproj"));
        await PlantAsync(Path.Join(_root, "src", "Other", "Other.csproj"));
        var solution = Path.Join(_root, "App.sln");
        await File.WriteAllTextAsync(
            solution,
            """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "src\App\App.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Global
            EndGlobal
            """);
        var matcher = new ScopeProjectSetPathMatcher(
            _root,
            new ScopeProjectSet.Solutions(["App.sln"], Array.Empty<string>()));

        matcher.Includes(Path.Join(_root, "src", "App", "app.ts")).Should().BeTrue();
        matcher.Includes(Path.Join(_root, "src", "Other", "other.ts")).Should().BeFalse();
        matcher.Includes(Path.Join(Path.GetTempPath(), "outside.cpp")).Should().BeFalse();
        matcher.IsProjectAnchorCandidate(solution).Should().BeTrue();
        matcher.IsProjectAnchorCandidate(
            Path.Join(_root, "src", "App", "App.csproj")).Should().BeTrue();
    }

    [Fact]
    public void Empty_solutions_scope_retains_synthetic_repository_discovery()
    {
        var matcher = new ScopeProjectSetPathMatcher(
            _root,
            new ScopeProjectSet.Solutions([], []));

        matcher.Includes(Path.Join(_root, "native", "Bridge.cpp")).Should().BeTrue();
        matcher.Includes(Path.Join(Path.GetTempPath(), "outside.cpp")).Should().BeFalse();
    }

    [SkippableFact]
    public async Task SelectedProject_rejectsLinkWhosePhysicalTargetIsUnselectedSibling()
    {
        var selectedRoot = Path.Join(_root, "src", "App");
        var siblingRoot = Path.Join(_root, "src", "Vendor");
        await PlantAsync(Path.Join(selectedRoot, "App.csproj"));
        await PlantAsync(Path.Join(siblingRoot, "Vendor.csproj"));
        var siblingFile = Path.Join(siblingRoot, "vendor.ts");
        await File.WriteAllTextAsync(siblingFile, "private");
        var link = Path.Join(selectedRoot, "LinkedVendor");
        Skip.IfNot(
            PhysicalPathTestSupport.TryCreateDirectoryLink(link, siblingRoot),
            "This environment does not permit symbolic-link or junction creation.");
        var linkedFile = Path.Join(link, "vendor.ts");
        var matcher = new ScopeProjectSetPathMatcher(
            _root,
            new ScopeProjectSet.Projects(
                ["src/App/App.csproj"],
                Array.Empty<string>()));

        matcher.Includes(linkedFile).Should().BeFalse();
        matcher.ShouldTraverseDirectory(link).Should().BeFalse();
        matcher.Includes(siblingFile).Should().BeFalse();
    }

    [SkippableFact]
    public async Task PathsAnchorDiscovery_neverFollowsDirectoryReparseAliases()
    {
        var selectedRoot = Path.Join(_root, "selected");
        var allowedRoot = Path.Join(selectedRoot, "App");
        var siblingRoot = Path.Join(_root, "Vendor");
        var outsideRoot = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-project-set-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideRoot);
        try
        {
            await PlantAsync(Path.Join(allowedRoot, "App.csproj"));
            await PlantAsync(Path.Join(siblingRoot, "Vendor.csproj"));
            await PlantAsync(Path.Join(outsideRoot, "Outside.csproj"));
            var siblingLink = Path.Join(selectedRoot, "LinkedVendor");
            var outsideLink = Path.Join(selectedRoot, "LinkedOutside");
            Skip.IfNot(
                PhysicalPathTestSupport.TryCreateDirectoryLink(
                    siblingLink,
                    siblingRoot)
                && PhysicalPathTestSupport.TryCreateDirectoryLink(
                    outsideLink,
                    outsideRoot),
                "This environment does not permit symbolic-link or junction creation.");

            var matcher = new ScopeProjectSetPathMatcher(
                _root,
                new ScopeProjectSet.Paths(
                    ["selected/**/*.csproj"],
                    Array.Empty<string>()));

            matcher.DiscoveryRoots.Should().Equal(allowedRoot);
            matcher.Includes(Path.Join(allowedRoot, "app.ts")).Should().BeTrue();
            matcher.Includes(Path.Join(siblingLink, "vendor.ts")).Should().BeFalse();
            matcher.Includes(Path.Join(outsideLink, "outside.ts")).Should().BeFalse();
        }
        finally
        {
            try { Directory.Delete(outsideRoot, recursive: true); } catch { }
        }
    }

    private static async Task PlantAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "<Project />");
    }
}
