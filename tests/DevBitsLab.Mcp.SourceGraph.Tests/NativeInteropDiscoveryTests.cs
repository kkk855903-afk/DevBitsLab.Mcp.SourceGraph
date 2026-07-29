using DevBitsLab.Mcp.SourceGraph.Server.Interop;
using DevBitsLab.Mcp.SourceGraph.Core;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class NativeInteropDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Join(
        Path.GetTempPath(),
        "sourcegraph-native-discovery-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Discover_reportsInputsAndRefusesAmbiguousTargetSelection()
    {
        Directory.CreateDirectory(Path.Join(_root, "native"));
        Directory.CreateDirectory(Path.Join(_root, "artifacts", "x64", "Release"));
        Directory.CreateDirectory(Path.Join(_root, "artifacts", "x86", "Debug"));
        File.WriteAllText(Path.Join(_root, "native", "Native.vcxproj"), "<Project />");
        File.WriteAllText(Path.Join(_root, "native", "CMakeLists.txt"), "project(Native)");
        File.WriteAllText(Path.Join(_root, "native", "compile_commands.json"), "[]");
        File.WriteAllBytes(
            Path.Join(_root, "artifacts", "x64", "Release", "Native.dll"),
            [1]);
        File.WriteAllBytes(
            Path.Join(_root, "artifacts", "x86", "Debug", "Native.dll"),
            [1]);

        var result = NativeInteropDiscovery.Discover(_root);

        result.VcxProjects.Should().ContainSingle("native/Native.vcxproj");
        result.CMakeProjects.Should().ContainSingle("native/CMakeLists.txt");
        result.CompilationDatabases.Should()
            .ContainSingle("native/compile_commands.json");
        result.Architectures.Should().BeEquivalentTo(["x64", "x86"]);
        result.Configurations.Should().BeEquivalentTo(["Debug", "Release"]);
        result.ToDiagnostic().Should()
            .Contain("architecture=ambiguous")
            .And.Contain("configuration=ambiguous")
            .And.Contain("ambiguous targets are not selected");
    }

    [Fact]
    public void Solution_discovery_reportsOnlyMemberVcxProjects()
    {
        Directory.CreateDirectory(Path.Join(_root, "member"));
        Directory.CreateDirectory(Path.Join(_root, "outside"));
        File.WriteAllText(
            Path.Join(_root, "member", "Member.vcxproj"),
            "<Project />");
        File.WriteAllText(
            Path.Join(_root, "outside", "Outside.vcxproj"),
            "<Project />");
        File.WriteAllText(
            Path.Join(_root, "Fixture.sln"),
            """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{BC8A1FFA-BEE3-4634-8014-F334798102B3}") = "Member", "member\Member.vcxproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Global
            EndGlobal
            """);
        var scope = new Scope(
            "fixture",
            "Fixture",
            _root,
            new ScopeProjectSet.Solutions(["Fixture.sln"], []),
            Isolated: false,
            DateTimeOffset.MinValue);

        var result = NativeInteropDiscovery.Discover(scope);

        result.VcxProjects.Should().Equal("member/Member.vcxproj");
        result.IsSolutionScoped.Should().BeTrue();
        result.ToDiagnostic().Should()
            .Contain("contains 1 native project")
            .And.Contain("outside the solution are excluded");
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
}
