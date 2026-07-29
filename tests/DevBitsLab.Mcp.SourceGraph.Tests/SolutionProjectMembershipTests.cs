using DevBitsLab.Mcp.SourceGraph.Core;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class SolutionProjectMembershipTests : IDisposable
{
    private readonly string _root = Path.Join(
        Path.GetTempPath(),
        "sg-solution-membership-" + Guid.NewGuid().ToString("N"));

    public SolutionProjectMembershipTests() =>
        Directory.CreateDirectory(_root);

    [Fact]
    public void Sln_readsProjectMembershipAndActiveConfiguration()
    {
        Plant("native/Bridge/Bridge.vcxproj", "<Project />");
        Plant("outside/Other.vcxproj", "<Project />");
        Plant(
            "Product.sln",
            """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{8BC9CEB8-8B4A-11D0-8D11-00A0C91BC942}") = "Bridge", "native\Bridge\Bridge.vcxproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "Folder", "Folder", "{22222222-2222-2222-2222-222222222222}"
            EndProject
            Global
                GlobalSection(SolutionConfigurationPlatforms) = preSolution
                    Release|x64 = Release|x64
                EndGlobalSection
                GlobalSection(ProjectConfigurationPlatforms) = postSolution
                    {11111111-1111-1111-1111-111111111111}.Release|x64.ActiveCfg = ReleaseNative|x64
                EndGlobalSection
            EndGlobal
            """);

        var result = SolutionProjectMembershipResolver.Resolve(
            _root,
            new ScopeProjectSet.Solutions(["Product.sln"], []));

        result.Failures.Should().BeEmpty();
        var project = result.VisualCppProjects.Should().ContainSingle().Subject;
        project.ProjectPath.Should().Be("native/Bridge/Bridge.vcxproj");
        project.ActiveConfigurations["Release|x64"].Should().Be(
            "ReleaseNative|x64");
        result.SolutionConfigurations.Should().Equal("Release|x64");
    }

    [Fact]
    public void Slnx_rejectsProjectOutsideRepository()
    {
        var outside = Path.Join(
            Path.GetTempPath(),
            "sg-outside-" + Guid.NewGuid().ToString("N"),
            "Outside.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllText(outside, "<Project />");
        try
        {
            var relative = Path.GetRelativePath(_root, outside)
                .Replace('\\', '/');
            Plant(
                "Product.slnx",
                $"<Solution><Project Path=\"{relative}\" /></Solution>");

            var result = SolutionProjectMembershipResolver.Resolve(
                _root,
                new ScopeProjectSet.Solutions(["Product.slnx"], []));

            result.Projects.Should().BeEmpty();
            result.Failures.Should().ContainSingle(failure =>
                failure.Code == "solution-project-path-rejected");
        }
        finally
        {
            Directory.Delete(
                Path.GetDirectoryName(outside)!,
                recursive: true);
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best-effort test cleanup.
        }
    }

    private void Plant(string relativePath, string content)
    {
        var path = Path.Join(
            _root,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
