using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class ManagedInteropProjectionIndexingTests
{
    [Fact]
    public async Task Cold_and_incremental_index_replace_real_managed_import_projection()
    {
        var root = CreateTempRoot();
        try
        {
            var (solutionPath, sourcePath) =
                await WriteSingleProjectSolutionAsync(root);
            await File.WriteAllTextAsync(sourcePath, ImportSource("run"));
            await using var store =
                new SqliteGraphStore(Path.Join(root, "graph.db"));
            await using var indexer = CreateIndexer(store, root);
            await indexer.OpenAsync(solutionPath);

            var cold = await indexer.IndexAllAsync();

            cold.FailedFiles.Should().BeEmpty();
            var row = (await ReadImportsAsync(store)).Should()
                .ContainSingle()
                .Subject;
            var import = InteropFactPayloadCodec.DecodeManagedImport(
                row.ArgsJson!,
                row.FileId);
            import.SymbolCanonicalKey.Should().Be(row.SymbolCanonicalKey);
            import.LibraryName.Should().Be("medalgo");
            import.EntryPoint.Should().Be("run");
            import.Target.Should().BeEquivalentTo(
                InteropTarget.WindowsX64Msvc);
            import.Evidence.ProducingFileId.Should().Be(row.FileId);
            import.Evidence.Location.FilePath.Should().Be(sourcePath);

            await File.WriteAllTextAsync(
                sourcePath,
                """
                namespace Fixture;

                internal static class NativeMethods
                {
                    internal static int Run(int value) => value;
                }
                """);
            var changed = await indexer.IndexChangedFilesAsync([sourcePath]);

            changed.FailedFiles.Should().BeEmpty();
            (await ReadImportsAsync(store)).Should().BeEmpty();
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Unchanged_cold_start_regenerates_missing_projection()
    {
        var root = CreateTempRoot();
        try
        {
            var (solutionPath, sourcePath) =
                await WriteSingleProjectSolutionAsync(root);
            await File.WriteAllTextAsync(sourcePath, ImportSource("run"));
            var dbPath = Path.Join(root, "graph.db");
            await using var store = new SqliteGraphStore(dbPath);

            await using (var first = CreateIndexer(store, root))
            {
                await first.OpenAsync(solutionPath);
                (await first.IndexAllAsync()).FailedFiles.Should().BeEmpty();
            }
            var original = (await ReadImportsAsync(store))
                .Should().ContainSingle().Subject;
            await store.ReplaceAnnotationsForFileByFlavorAsync(
                original.FilePath,
                InteropAnnotationFlavors.ManagedImport,
                []);
            (await ReadImportsAsync(store)).Should().BeEmpty();

            await using (var restarted = CreateIndexer(store, root))
            {
                await restarted.OpenAsync(solutionPath);
                var result = await restarted.IndexAllAsync();
                result.FailedFiles.Should().BeEmpty();
                result.FilesIndexed.Should().BeGreaterThan(0);
            }

            var regenerated = (await ReadImportsAsync(store))
                .Should().ContainSingle().Subject;
            InteropFactPayloadCodec.DecodeManagedImport(
                    regenerated.ArgsJson!,
                    regenerated.FileId)
                .EntryPoint.Should().Be("run");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Disabling_target_clears_stale_projection_without_source_edit()
    {
        var root = CreateTempRoot();
        try
        {
            var (solutionPath, sourcePath) =
                await WriteSingleProjectSolutionAsync(root);
            await File.WriteAllTextAsync(sourcePath, ImportSource("run"));
            await using var store =
                new SqliteGraphStore(Path.Join(root, "graph.db"));

            await using (var enabled = CreateIndexer(store, root))
            {
                await enabled.OpenAsync(solutionPath);
                (await enabled.IndexAllAsync()).FailedFiles.Should().BeEmpty();
            }
            (await ReadImportsAsync(store)).Should().ContainSingle();

            await using (var disabled = new RoslynIndexer(
                             store,
                             logger: null,
                             embeddingsSink: null,
                             privacyRoot: root,
                             excludePatterns: []))
            {
                await disabled.OpenAsync(solutionPath);
                (await disabled.IndexAllAsync()).FailedFiles.Should().BeEmpty();
            }

            (await ReadImportsAsync(store)).Should().BeEmpty();
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Conflicting_target_framework_projections_fail_closed()
    {
        var root = CreateTempRoot();
        try
        {
            var (solutionPath, sourcePath) =
                await WriteSingleProjectSolutionAsync(
                    root,
                    multiTarget: true);
            await File.WriteAllTextAsync(
                sourcePath,
                ImportSource("stable"));
            await using var store =
                new SqliteGraphStore(Path.Join(root, "graph.db"));
            await using var indexer = CreateIndexer(store, root);
            await indexer.OpenAsync(solutionPath);
            (await indexer.IndexAllAsync()).FailedFiles.Should().BeEmpty();
            var oldPayload = (await ReadImportsAsync(store))
                .Should().ContainSingle().Subject.ArgsJson;

            await File.WriteAllTextAsync(
                sourcePath,
                """
                using System.Runtime.InteropServices;

                namespace Fixture;

                internal static class NativeMethods
                {
                #if SECOND_TFM
                    [DllImport("medalgo", EntryPoint = "second", ExactSpelling = true)]
                #else
                    [DllImport("medalgo", EntryPoint = "first", ExactSpelling = true)]
                #endif
                    internal static extern int Run(int value);
                }
                """);

            var changed = await indexer.IndexChangedFilesAsync([sourcePath]);

            changed.FailedFiles.Should().ContainSingle(failure =>
                failure.Path == sourcePath
                && failure.Reason.Contains(
                    "conflicting target-framework projections",
                    StringComparison.Ordinal));
            (await ReadImportsAsync(store)).Should().ContainSingle()
                .Which.ArgsJson.Should().Be(oldPayload);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static RoslynIndexer CreateIndexer(
        IGraphStore store,
        string root) =>
        new(
            store,
            logger: null,
            embeddingsSink: null,
            privacyRoot: root,
            excludePatterns: [],
            interopTarget: InteropTarget.WindowsX64Msvc);

    private static async Task<IReadOnlyList<StoredAnnotationRow>>
        ReadImportsAsync(IGraphStore store) =>
        await store.ListAnnotationsByFlavorAsync(
            InteropAnnotationFlavors.ManagedImport,
            afterId: 0,
            limit: 1000);

    private static string ImportSource(string entryPoint) =>
        $$"""
        using System.Runtime.InteropServices;

        namespace Fixture;

        internal static class NativeMethods
        {
            [DllImport(
                "medalgo",
                EntryPoint = "{{entryPoint}}",
                CallingConvention = CallingConvention.Cdecl,
                ExactSpelling = true)]
            internal static extern int Run(int value);
        }
        """;

    private static string CreateTempRoot()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-managed-interop-projection-"
            + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task<(string SolutionPath, string SourcePath)>
        WriteSingleProjectSolutionAsync(
            string root,
            bool multiTarget = false)
    {
        var projectDirectory = Path.Join(root, "App");
        Directory.CreateDirectory(projectDirectory);
        var projectPath = Path.Join(projectDirectory, "App.csproj");
        var sourcePath = Path.Join(projectDirectory, "NativeMethods.cs");
        var targetProperties = multiTarget
            ? """
              <TargetFrameworks>net10.0;net10.0-windows</TargetFrameworks>
              <EnableWindowsTargeting>true</EnableWindowsTargeting>
              """
            : "<TargetFramework>net10.0</TargetFramework>";
        var secondTargetProperties = multiTarget
            ? """
              <PropertyGroup Condition="'$(TargetFramework)' == 'net10.0-windows'">
                <DefineConstants>$(DefineConstants);SECOND_TFM</DefineConstants>
              </PropertyGroup>
              """
            : string.Empty;
        await File.WriteAllTextAsync(
            projectPath,
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                {{targetProperties}}
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              {{secondTargetProperties}}
            </Project>
            """);
        var solutionPath = Path.Join(root, "Fixture.sln");
        await File.WriteAllTextAsync(
            solutionPath,
            """
            Microsoft Visual Studio Solution File, Format Version 12.00
            # Visual Studio Version 17
            VisualStudioVersion = 17.0.31903.59
            MinimumVisualStudioVersion = 10.0.40219.1
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "App\App.csproj", "{D269EB0B-1CA9-4D1C-BF7D-F620BF78E299}"
            EndProject
            Global
                GlobalSection(SolutionConfigurationPlatforms) = preSolution
                    Debug|Any CPU = Debug|Any CPU
                    Release|Any CPU = Release|Any CPU
                EndGlobalSection
                GlobalSection(ProjectConfigurationPlatforms) = postSolution
                    {D269EB0B-1CA9-4D1C-BF7D-F620BF78E299}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
                    {D269EB0B-1CA9-4D1C-BF7D-F620BF78E299}.Debug|Any CPU.Build.0 = Debug|Any CPU
                    {D269EB0B-1CA9-4D1C-BF7D-F620BF78E299}.Release|Any CPU.ActiveCfg = Release|Any CPU
                    {D269EB0B-1CA9-4D1C-BF7D-F620BF78E299}.Release|Any CPU.Build.0 = Release|Any CPU
                EndGlobalSection
            EndGlobal
            """);
        return (solutionPath, sourcePath);
    }

    private static void TryDeleteDirectory(string path)
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
