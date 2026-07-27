using DevBitsLab.Mcp.SourceGraph.Server.Interop;
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
