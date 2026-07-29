using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Server.Interop;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class SolutionNativeInteropResolverTests : IDisposable
{
    private readonly string _root = Path.Join(
        Path.GetTempPath(),
        "sg-solution-native-" + Guid.NewGuid().ToString("N"));

    public SolutionNativeInteropResolverTests() =>
        Directory.CreateDirectory(_root);

    [Fact]
    public void Resolve_usesSolutionAsHardBoundaryAndDoesNotNarrowSources()
    {
        Plant(
            "native/Bridge/Bridge.vcxproj",
            ProjectXml("BridgeRuntime"));
        Plant(
            "other/Other.vcxproj",
            ProjectXml("Other"));
        Plant(
            "Product.sln",
            """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{8BC9CEB8-8B4A-11D0-8D11-00A0C91BC942}") = "Bridge", "native\Bridge\Bridge.vcxproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Global
                GlobalSection(SolutionConfigurationPlatforms) = preSolution
                    Release|x64 = Release|x64
                EndGlobalSection
                GlobalSection(ProjectConfigurationPlatforms) = postSolution
                    {11111111-1111-1111-1111-111111111111}.Release|x64.ActiveCfg = Release|x64
                EndGlobalSection
            EndGlobal
            """);
        var authored = new ScopeInteropConfig(
            InteropTarget.WindowsX64Msvc,
            [])
        {
            VcxProjects =
            [
                new InteropVcxProjectConfig(
                    "native/Bridge/Bridge.vcxproj",
                    "Release",
                    "x64",
                    "BridgeOverride.dll",
                    ["Only.cpp"],
                    ["-DBRIDGE=1"],
                    null),
                new InteropVcxProjectConfig(
                    "other/Other.vcxproj",
                    "Release",
                    "x64",
                    "Other.dll",
                    [],
                    [],
                    null),
            ],
        };
        var scope = new Scope(
            "default",
            "default",
            _root,
            new ScopeProjectSet.Solutions(["Product.sln"], []),
            Isolated: false,
            DateTimeOffset.MinValue)
        {
            Interop = authored,
        };

        var result = SolutionNativeInteropResolver.Resolve(scope);

        result.DiscoveredProjects.Should().Be(1);
        var project = result.Configuration!.VcxProjects
            .Should().ContainSingle().Subject;
        project.Path.Should().Be("native/Bridge/Bridge.vcxproj");
        project.Configuration.Should().Be("Release");
        project.Platform.Should().Be("x64");
        project.Library.Should().Be("BridgeOverride.dll");
        project.SourceFiles.Should().BeEmpty();
        project.AdditionalArguments.Should().Equal("-DBRIDGE=1");
        result.Failures.Should().ContainSingle(failure =>
            failure.Code == "vcxproj-not-in-solution");
    }

    [Fact]
    public void Resolve_derivesLibraryNameFromTargetProperties()
    {
        Plant(
            "native/Bridge/Bridge.vcxproj",
            ProjectXml("BridgeRuntime"));
        Plant(
            "Product.slnx",
            """
            <Solution>
              <Project Path="native/Bridge/Bridge.vcxproj" />
            </Solution>
            """);
        var scope = new Scope(
            "default",
            "default",
            _root,
            new ScopeProjectSet.Solutions(["Product.slnx"], []),
            Isolated: false,
            DateTimeOffset.MinValue);

        var result = SolutionNativeInteropResolver.Resolve(scope);

        result.Failures.Should().BeEmpty();
        result.Configuration!.VcxProjects.Should()
            .ContainSingle()
            .Which.Library.Should().Be("BridgeRuntime.dll");
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

    private static string ProjectXml(string targetName) =>
        $$"""
        <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
          <ItemGroup Label="ProjectConfigurations">
            <ProjectConfiguration Include="Release|x64">
              <Configuration>Release</Configuration>
              <Platform>x64</Platform>
            </ProjectConfiguration>
          </ItemGroup>
          <PropertyGroup Condition="'$(Configuration)|$(Platform)'=='Release|x64'">
            <ConfigurationType>DynamicLibrary</ConfigurationType>
            <TargetName>{{targetName}}</TargetName>
          </PropertyGroup>
        </Project>
        """;
}
