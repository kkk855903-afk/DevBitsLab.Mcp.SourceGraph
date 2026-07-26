using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class RoslynNativeSolutionCompatibilityTests
{
    [Theory]
    [InlineData(".slnx")]
    [InlineData(".sln")]
    public async Task Mixed_solution_indexes_managed_projects_and_skips_vcxproj(
        string solutionExtension)
    {
        var root = CreateFixture();
        try
        {
            var solutionPath = await WriteSolutionAsync(
                root,
                includeManaged: true,
                solutionExtension);
            await using var store = new SqliteGraphStore(Path.Join(root, "graph.db"));

            var result = await RoslynIndexer.IndexSolutionOnceAsync(solutionPath, store);

            result.FilesIndexed.Should().BeGreaterThan(0);
            (await store.FindSymbolsAsync("ManagedEntry"))
                .Should().Contain(symbol =>
                    symbol.Name == "ManagedEntry"
                    && symbol.Kind == "class");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Theory]
    [InlineData(".slnx")]
    [InlineData(".sln")]
    public async Task Native_only_solution_opens_as_an_empty_roslyn_workspace(
        string solutionExtension)
    {
        var root = CreateFixture();
        try
        {
            var solutionPath = await WriteSolutionAsync(
                root,
                includeManaged: false,
                solutionExtension);
            await using var store = new SqliteGraphStore(Path.Join(root, "graph.db"));
            await using var indexer = new RoslynIndexer(store);

            await indexer.OpenAsync(solutionPath);
            var result = await indexer.IndexAllAsync();

            indexer.SanitizedSolution.Should().NotBeNull();
            indexer.SanitizedSolution!.ProjectIds.Should().BeEmpty();
            result.FilesIndexed.Should().Be(0);
            result.FailedProjects.Should().BeEmpty();
            result.FailedFiles.Should().BeEmpty();
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string CreateFixture()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-native-solution-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task<string> WriteSolutionAsync(
        string root,
        bool includeManaged,
        string solutionExtension)
    {
        var nativeDirectory = Path.Join(root, "Native");
        Directory.CreateDirectory(nativeDirectory);
        await File.WriteAllTextAsync(
            Path.Join(nativeDirectory, "Native.vcxproj"),
            """
            <Project DefaultTargets="Build"
                     xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <ItemGroup Label="ProjectConfigurations">
                <ProjectConfiguration Include="Debug|x64">
                  <Configuration>Debug</Configuration>
                  <Platform>x64</Platform>
                </ProjectConfiguration>
              </ItemGroup>
              <PropertyGroup Label="Globals">
                <ProjectGuid>{2F868B0C-7092-46F7-BC48-2653094E3088}</ProjectGuid>
                <Keyword>Win32Proj</Keyword>
              </PropertyGroup>
              <Import Project="$(VCTargetsPath)\Microsoft.Cpp.Default.props" />
              <PropertyGroup Condition="'$(Configuration)|$(Platform)'=='Debug|x64'"
                             Label="Configuration">
                <ConfigurationType>DynamicLibrary</ConfigurationType>
                <UseDebugLibraries>true</UseDebugLibraries>
                <PlatformToolset>v143</PlatformToolset>
              </PropertyGroup>
              <Import Project="$(VCTargetsPath)\Microsoft.Cpp.props" />
              <Import Project="$(VCTargetsPath)\Microsoft.Cpp.targets" />
            </Project>
            """);
        await File.WriteAllTextAsync(
            Path.Join(nativeDirectory, "native.cpp"),
            "int native_value() { return 42; }");

        var slnxProjectElement = string.Empty;
        var slnProjectElement = string.Empty;
        if (includeManaged)
        {
            var managedDirectory = Path.Join(root, "Managed");
            Directory.CreateDirectory(managedDirectory);
            await File.WriteAllTextAsync(
                Path.Join(managedDirectory, "Managed.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="..\Native\Native.vcxproj"
                                      ReferenceOutputAssembly="false" />
                  </ItemGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(
                Path.Join(managedDirectory, "ManagedEntry.cs"),
                """
                namespace MixedFixture;

                public static class ManagedEntry
                {
                    public static int Value => 42;
                }
                """);
            slnxProjectElement = """
                <Project Path="Managed/Managed.csproj" />
            """;
            slnProjectElement = """
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Managed", "Managed\Managed.csproj", "{53612C42-DA41-44C8-94A2-FD837020EBDB}"
            EndProject
            """;
        }

        var solutionPath = Path.Join(root, "Mixed" + solutionExtension);
        if (solutionExtension == ".slnx")
        {
            await File.WriteAllTextAsync(
                solutionPath,
                $"""
                <Solution>
                {slnxProjectElement}
                  <Project Path="Native/Native.vcxproj" />
                </Solution>
                """);
        }
        else
        {
            await File.WriteAllTextAsync(
                solutionPath,
                $$"""
                Microsoft Visual Studio Solution File, Format Version 12.00
                # Visual Studio Version 17
                {{slnProjectElement}}
                Project("{BC8A1FFA-BEE3-4634-8014-F334798102B3}") = "Native", "Native\Native.vcxproj", "{2F868B0C-7092-46F7-BC48-2653094E3088}"
                EndProject
                Global
                EndGlobal
                """);
        }
        return solutionPath;
    }

    private static void TryDelete(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch
        {
            // Best-effort test cleanup.
        }
    }
}
