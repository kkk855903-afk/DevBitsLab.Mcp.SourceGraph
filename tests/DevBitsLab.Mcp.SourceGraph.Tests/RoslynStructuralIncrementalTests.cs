using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class RoslynStructuralIncrementalTests
{
    [Fact]
    public async Task AddDeleteAndRenameReloadSolutionWhileOrdinaryEditStaysIncremental()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-roslyn-structure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var projectDirectory = Path.Join(root, "App");
            Directory.CreateDirectory(projectDirectory);
            var solutionPath = Path.Join(root, "StructuralFixture.sln");
            var projectPath = Path.Join(projectDirectory, "App.csproj");
            var existingPath = Path.Join(projectDirectory, "Existing.cs");
            var targetPath = Path.Join(projectDirectory, "Target.cs");
            var callerPath = Path.Join(projectDirectory, "Caller.cs");
            var addedPath = Path.Join(projectDirectory, "Added.cs");
            var renamedPath = Path.Join(projectDirectory, "Renamed.cs");

            await File.WriteAllTextAsync(projectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(solutionPath, """
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
            await File.WriteAllTextAsync(existingPath, """
                namespace StructuralFixture;

                public static class Existing
                {
                    public static int BeforeEdit() => 1;
                }
                """);
            await File.WriteAllTextAsync(targetPath, """
                namespace StructuralFixture;

                public static class Target
                {
                    public static void Hit() { }
                }
                """);
            await File.WriteAllTextAsync(callerPath, """
                namespace StructuralFixture;

                public static class Caller
                {
                    public static void Invoke() => AddedType.Call();
                }
                """);

            var injectPartialWorkspaceFailure = false;
            var failNextIncrementalRead = false;
            var replaceFileAfterNextIncrementalRead = false;
            var failNextIndexCoreRead = false;
            var indexCoreReadsForExisting = 0;
            const string snapshotBContents = """
                namespace StructuralFixture;

                public static class Existing
                {
                    public static int SnapshotB() => 5;
                }
                """;
            var openedWorkspaces = new List<MSBuildWorkspace>();
            var disposedWorkspaces = new List<MSBuildWorkspace>();
            var hooks = new RoslynIndexer.TestHooks(
                OpenWorkspaceAsync: async (workspace, path, ct) =>
                {
                    openedWorkspaces.Add(workspace);
                    var solution = await workspace.OpenSolutionAsync(
                        path,
                        cancellationToken: ct);
                    var diagnostics = new List<WorkspaceDiagnostic>
                    {
                        new(
                            WorkspaceDiagnosticKind.Warning,
                            "simulated non-fatal workspace warning"),
                    };
                    if (injectPartialWorkspaceFailure)
                    {
                        injectPartialWorkspaceFailure = false;
                        diagnostics.Add(new WorkspaceDiagnostic(
                            WorkspaceDiagnosticKind.Failure,
                            "simulated partial workspace load"));
                        foreach (var projectId in solution.ProjectIds.ToArray())
                        {
                            solution = solution.RemoveProject(projectId);
                        }
                    }
                    return new RoslynIndexer.WorkspaceOpenResult(
                        solution,
                        diagnostics);
                },
                ReadIncrementalBytesAsync: async (path, ct) =>
                {
                    if (failNextIncrementalRead
                        && string.Equals(
                            path,
                            existingPath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        failNextIncrementalRead = false;
                        throw new IOException("simulated transient read failure");
                    }
                    var bytes = await File.ReadAllBytesAsync(path, ct);
                    if (replaceFileAfterNextIncrementalRead
                        && string.Equals(
                            path,
                            existingPath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        replaceFileAfterNextIncrementalRead = false;
                        await File.WriteAllTextAsync(
                            path,
                            snapshotBContents,
                            ct);
                    }
                    return bytes;
                },
                ReadIndexCoreBytesAsync: async (path, ct) =>
                {
                    if (string.Equals(
                            path,
                            existingPath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        indexCoreReadsForExisting++;
                        if (failNextIndexCoreRead)
                        {
                            failNextIndexCoreRead = false;
                            throw new IOException(
                                "simulated Phase A snapshot read failure");
                        }
                    }
                    return await File.ReadAllBytesAsync(path, ct);
                },
                WorkspaceDisposed: workspace => disposedWorkspaces.Add(workspace));

            await using var store = new SqliteGraphStore(Path.Join(root, "graph.db"));
            var storeProxy = DispatchProxy.Create<IGraphStore, StructuralFailureProxy>();
            var failureControl = (StructuralFailureProxy)storeProxy;
            failureControl.Inner = store;
            await using var indexer = new RoslynIndexer(
                storeProxy,
                logger: null,
                embeddingsSink: null,
                privacyRoot: root,
                excludePatterns: ["excluded/**"],
                testHooks: hooks);

            injectPartialWorkspaceFailure = true;
            var initialOpenAct = () => indexer.OpenAsync(solutionPath);

            var initialOpenFailure = await initialOpenAct.Should()
                .ThrowAsync<InvalidOperationException>();
            initialOpenFailure.Which.Message
                .Should().Contain("simulated partial workspace load")
                .And.NotContain("simulated non-fatal workspace warning");
            indexer.Workspace.Should().BeNull();
            indexer.SanitizedSolution.Should().BeNull();
            disposedWorkspaces.Should().Contain(openedWorkspaces[0]);

            await indexer.OpenAsync(solutionPath);
            var workspaceBeforeFailedReopen = indexer.Workspace;
            var snapshotBeforeFailedReopen = indexer.SanitizedSolution;
            injectPartialWorkspaceFailure = true;
            var failedReopenAct = () => indexer.OpenAsync(solutionPath);

            await failedReopenAct.Should().ThrowAsync<InvalidOperationException>();
            indexer.Workspace.Should().BeSameAs(workspaceBeforeFailedReopen);
            indexer.SanitizedSolution.Should().BeSameAs(snapshotBeforeFailedReopen);
            disposedWorkspaces.Should().Contain(openedWorkspaces[^1]);
            disposedWorkspaces.Should().NotContain(workspaceBeforeFailedReopen!);

            await indexer.OpenAsync(solutionPath);
            indexer.Workspace.Should().NotBeSameAs(workspaceBeforeFailedReopen);
            disposedWorkspaces.Should().Contain(workspaceBeforeFailedReopen!);
            await indexer.IndexAllAsync();

            var targetBeforePartialWorkspace = (await store.ListSymbolsInFileAsync(targetPath))
                .Single(symbol => symbol.Name == "Hit");
            var snapshotBeforePartialWorkspace = indexer.SanitizedSolution;
            injectPartialWorkspaceFailure = true;
            var partialWorkspaceAct = () => indexer.ReloadAndIndexAllAsync();

            var partialWorkspaceFailure = await partialWorkspaceAct.Should()
                .ThrowAsync<InvalidOperationException>();
            partialWorkspaceFailure.Which.Message
                .Should().Contain("simulated partial workspace load")
                .And.NotContain("simulated non-fatal workspace warning");
            indexer.SanitizedSolution.Should().BeSameAs(snapshotBeforePartialWorkspace);
            (await store.GetAllFilesAsync()).Should().Contain(file =>
                string.Equals(file.Path, targetPath, StringComparison.OrdinalIgnoreCase));
            (await store.GetAllSymbolKeysAsync()).Should().Contain(symbol =>
                symbol.CanonicalKey == targetBeforePartialWorkspace.CanonicalKey);

            var workspaceRetry = await indexer.IndexChangedFilesAsync([callerPath]);

            workspaceRetry.FilesIndexed.Should().BeGreaterThan(0);
            workspaceRetry.FailedFiles.Should().BeEmpty();
            indexer.SanitizedSolution.Should().NotBeSameAs(snapshotBeforePartialWorkspace);

            var invalidPathResult = await indexer.IndexChangedFilesAsync(["\0.cs"]);

            invalidPathResult.FilesIndexed.Should().Be(0);
            invalidPathResult.FailedFiles.Should().ContainSingle();

            var staleGeneratedPath = Path.Join(root, "obj", "OldGenerated.g.cs");
            var staleGeneratedFileId = await store.UpsertFileAsync(
                staleGeneratedPath,
                new byte[32],
                DateTimeOffset.UtcNow,
                isGenerated: true);
            var staleGeneratedSymbolId = await store.UpsertSymbolAsync(
                "csharp:T:StructuralFixture.OldGenerated",
                new Symbol(
                    Id: 0,
                    Name: "OldGenerated",
                    Fqn: "StructuralFixture.OldGenerated",
                    Kind: "class",
                    FileId: staleGeneratedFileId,
                    StartLine: 1,
                    StartCol: 1,
                    EndLine: 1,
                    EndCol: 20,
                    Signature: "OldGenerated",
                    ContainerId: null));

            await indexer.ReloadAndIndexAllAsync();

            (await store.ListGeneratedFilesAsync(int.MaxValue))
                .Should().NotContain(row =>
                    string.Equals(
                        row.FilePath,
                        staleGeneratedPath,
                        StringComparison.OrdinalIgnoreCase));
            (await store.GetSymbolByIdAsync(staleGeneratedSymbolId)).Should().BeNull();

            var loosePath = Path.Join(root, "Loose.cs");
            await File.WriteAllTextAsync(loosePath, """
                namespace StructuralFixture;
                public static class Loose { }
                """);

            var firstLooseResult = await indexer.IndexChangedFilesAsync([loosePath]);
            var snapshotAfterLooseConfirmation = indexer.SanitizedSolution;
            var repeatedLooseResult = await indexer.IndexChangedFilesAsync([loosePath]);

            firstLooseResult.FilesIndexed.Should().BeGreaterThan(0);
            repeatedLooseResult.FilesIndexed.Should().Be(0);
            indexer.SanitizedSolution.Should().BeSameAs(snapshotAfterLooseConfirmation);

            await indexer.ReloadAndIndexAllAsync();
            var snapshotAfterExplicitReload = indexer.SanitizedSolution;

            var looseAfterExplicitReload = await indexer.IndexChangedFilesAsync([loosePath]);

            looseAfterExplicitReload.FilesIndexed.Should().BeGreaterThan(0);
            indexer.SanitizedSolution.Should().NotBeSameAs(snapshotAfterExplicitReload);

            var beforeEdit = (await store.ListSymbolsInFileAsync(existingPath))
                .Single(symbol => symbol.Name == "BeforeEdit");
            var hashBeforeReadFailure = await store.GetFileContentHashAsync(existingPath);
            hashBeforeReadFailure.Should().NotBeNull();

            await File.WriteAllTextAsync(existingPath, """
                namespace StructuralFixture;

                public static class Existing
                {
                    public static int AfterReadRetry() => 2;
                }
                """);
            failNextIncrementalRead = true;

            var failedRead = await indexer.IndexChangedFilesAsync([existingPath]);

            failedRead.FilesIndexed.Should().Be(0);
            failedRead.FailedFiles.Should().ContainSingle(failure =>
                string.Equals(
                    failure.Path,
                    existingPath,
                    StringComparison.OrdinalIgnoreCase));
            (await store.GetFileContentHashAsync(existingPath))
                .Should().Equal(hashBeforeReadFailure!);
            (await store.GetAllSymbolKeysAsync()).Should().Contain(symbol =>
                symbol.CanonicalKey == beforeEdit.CanonicalKey);
            (await store.ListSymbolsInFileAsync(existingPath))
                .Should().NotContain(symbol => symbol.Name == "AfterReadRetry");

            var workspaceBeforePendingReopen = indexer.Workspace;
            await indexer.OpenAsync(solutionPath);

            indexer.Workspace.Should().NotBeSameAs(workspaceBeforePendingReopen);
            disposedWorkspaces.Should().Contain(workspaceBeforePendingReopen!);

            var readRetry = await indexer.IndexChangedFilesAsync([callerPath]);

            readRetry.FilesIndexed.Should().BeGreaterThan(1);
            readRetry.FailedFiles.Should().BeEmpty();
            (await store.GetAllSymbolKeysAsync()).Should().NotContain(symbol =>
                symbol.CanonicalKey == beforeEdit.CanonicalKey);
            var afterReadRetry = (await store.ListSymbolsInFileAsync(existingPath))
                .Single(symbol => symbol.Name == "AfterReadRetry");
            (await store.GetFileContentHashAsync(existingPath))
                .Should().NotEqual(hashBeforeReadFailure!);

            var existingDocumentId = indexer.SanitizedSolution!
                .GetDocumentIdsWithFilePath(existingPath)
                .Single();
            await File.WriteAllTextAsync(existingPath, """
                namespace StructuralFixture;

                public static class Existing
                {
                    public static int AfterEdit() => 3;
                }
                """);

            var ordinaryEdit = await indexer.IndexChangedFilesAsync([existingPath]);

            ordinaryEdit.FilesIndexed.Should().Be(1);
            indexer.SanitizedSolution!
                .GetDocumentIdsWithFilePath(existingPath)
                .Single()
                .Should().Be(existingDocumentId);
            (await store.GetAllSymbolKeysAsync()).Should().NotContain(symbol =>
                symbol.CanonicalKey == afterReadRetry.CanonicalKey);
            (await store.ListSymbolsInFileAsync(existingPath))
                .Should().ContainSingle(symbol => symbol.Name == "AfterEdit");

            const string snapshotAContents = """
                namespace StructuralFixture;

                public static class Existing
                {
                    public static int SnapshotA() => 4;
                }
                """;
            var snapshotABytes = Encoding.UTF8
                .GetPreamble()
                .Concat(Encoding.UTF8.GetBytes(snapshotAContents))
                .ToArray();
            await File.WriteAllBytesAsync(existingPath, snapshotABytes);
            var indexCoreReadsBeforeSnapshotRace = indexCoreReadsForExisting;
            replaceFileAfterNextIncrementalRead = true;

            var snapshotAResult = await indexer.IndexChangedFilesAsync([existingPath]);

            snapshotAResult.FilesIndexed.Should().Be(1);
            snapshotAResult.FailedFiles.Should().BeEmpty();
            indexCoreReadsForExisting.Should().Be(
                indexCoreReadsBeforeSnapshotRace,
                "incremental Phase A must reuse the exact pre-read byte snapshot");
            (await store.GetFileContentHashAsync(existingPath))
                .Should().Equal(SHA256.HashData(snapshotABytes));
            (await store.ListSymbolsInFileAsync(existingPath))
                .Should().ContainSingle(symbol => symbol.Name == "SnapshotA");
            (await store.ListSymbolsInFileAsync(existingPath))
                .Should().NotContain(symbol => symbol.Name == "SnapshotB");
            (await File.ReadAllTextAsync(existingPath))
                .Should().Contain("SnapshotB");

            var snapshotBResult = await indexer.IndexChangedFilesAsync([existingPath]);

            snapshotBResult.FilesIndexed.Should().Be(1);
            snapshotBResult.FailedFiles.Should().BeEmpty();
            (await store.ListSymbolsInFileAsync(existingPath))
                .Should().ContainSingle(symbol => symbol.Name == "SnapshotB");
            (await store.ListSymbolsInFileAsync(existingPath))
                .Should().NotContain(symbol => symbol.Name == "SnapshotA");

            var hashBeforeIndexCoreReadFailure =
                await store.GetFileContentHashAsync(existingPath);
            failNextIndexCoreRead = true;

            var indexCoreReadFailure = await indexer.ReloadAndIndexAllAsync();

            indexCoreReadFailure.FilesIndexed.Should().Be(0);
            indexCoreReadFailure.FailedFiles.Should().ContainSingle(failure =>
                string.Equals(
                    failure.Path,
                    existingPath,
                    StringComparison.OrdinalIgnoreCase));
            (await store.GetFileContentHashAsync(existingPath))
                .Should().Equal(hashBeforeIndexCoreReadFailure!);
            (await store.ListSymbolsInFileAsync(existingPath))
                .Should().ContainSingle(symbol => symbol.Name == "SnapshotB");

            var indexCoreReadRetry =
                await indexer.IndexChangedFilesAsync([callerPath]);

            indexCoreReadRetry.FilesIndexed.Should().BeGreaterThan(1);
            indexCoreReadRetry.FailedFiles.Should().BeEmpty();
            (await store.ListSymbolsInFileAsync(existingPath))
                .Should().ContainSingle(symbol => symbol.Name == "SnapshotB");

            var excludedDirectory = Path.Join(root, "excluded");
            Directory.CreateDirectory(excludedDirectory);
            var excludedPath = Path.Join(excludedDirectory, "Secret.cs");
            await File.WriteAllTextAsync(excludedPath, """
                namespace StructuralFixture;
                public static class Secret { }
                """);
            var snapshotBeforeExcludedChange = indexer.SanitizedSolution;

            var excludedResult = await indexer.IndexChangedFilesAsync([excludedPath]);

            excludedResult.FilesIndexed.Should().Be(0);
            indexer.SanitizedSolution.Should().BeSameAs(snapshotBeforeExcludedChange);
            (await store.ListSymbolsInFileAsync(excludedPath)).Should().BeEmpty();

            var callerMethod = (await store.ListSymbolsInFileAsync(callerPath))
                .Single(symbol => symbol.Name == "Invoke");
            (await store.ListCalleesAsync(
                    callerMethod.Id,
                    edgeKind: EdgeKinds.Calls))
                .Should().BeEmpty("AddedType does not exist in the original solution snapshot");

            await File.WriteAllTextAsync(addedPath, """
                namespace StructuralFixture;

                public static class AddedType
                {
                    public static void Call() => Target.Hit();
                }
                """);

            var addResult = await indexer.IndexChangedFilesAsync([addedPath]);

            addResult.FilesIndexed.Should().BeGreaterThan(1);
            var addedSymbols = await store.ListSymbolsInFileAsync(addedPath);
            var addedType = addedSymbols.Single(symbol => symbol.Name == "AddedType");
            var addedMethod = addedSymbols.Single(symbol => symbol.Name == "Call");
            var targetMethod = (await store.ListSymbolsInFileAsync(targetPath))
                .Single(symbol => symbol.Name == "Hit");
            (await store.ListCalleesAsync(
                    callerMethod.Id,
                    edgeKind: EdgeKinds.Calls))
                .Should().ContainSingle(symbol => symbol.Id == addedMethod.Id);
            (await store.ListCalleesAsync(
                    addedMethod.Id,
                    edgeKind: EdgeKinds.Calls))
                .Should().ContainSingle(symbol => symbol.Id == targetMethod.Id);

            File.Move(addedPath, renamedPath);
            await File.WriteAllTextAsync(renamedPath, """
                namespace StructuralFixture;

                public static class RenamedType
                {
                    public static void Call() => Target.Hit();
                }
                """);

            var snapshotBeforeIncompleteRename = indexer.SanitizedSolution;
            failureControl.FailNextEdgeInsert = true;

            var incompleteRename = await indexer.IndexChangedFilesAsync(
                [addedPath, renamedPath]);

            incompleteRename.FailedFiles.Should().NotBeEmpty();
            failureControl.EdgeFailures.Should().Be(1);
            indexer.SanitizedSolution.Should().NotBeSameAs(snapshotBeforeIncompleteRename);
            indexer.SanitizedSolution!
                .GetDocumentIdsWithFilePath(addedPath)
                .Should().BeEmpty();
            indexer.SanitizedSolution
                .GetDocumentIdsWithFilePath(renamedPath)
                .Should().NotBeEmpty();

            var renameResult = await indexer.IndexChangedFilesAsync([existingPath]);

            renameResult.FilesIndexed.Should().BeGreaterThan(1);
            renameResult.FailedFiles.Should().BeEmpty();
            var keysAfterRename = await store.GetAllSymbolKeysAsync();
            keysAfterRename.Should().NotContain(symbol =>
                symbol.CanonicalKey == addedType.CanonicalKey);
            keysAfterRename.Should().NotContain(symbol =>
                symbol.CanonicalKey == addedMethod.CanonicalKey);
            (await store.GetAllFilesAsync())
                .Should().NotContain(file =>
                    string.Equals(file.Path, addedPath, StringComparison.OrdinalIgnoreCase));
            var renamedMethod = (await store.ListSymbolsInFileAsync(renamedPath))
                .Single(symbol => symbol.Name == "Call");
            (await store.ListCalleesAsync(
                    callerMethod.Id,
                    edgeKind: EdgeKinds.Calls))
                .Should().BeEmpty("the unchanged caller still names the removed AddedType");
            (await store.ListCalleesAsync(
                    renamedMethod.Id,
                    edgeKind: EdgeKinds.Calls))
                .Should().ContainSingle(symbol => symbol.Id == targetMethod.Id);

            File.Delete(targetPath);
            var snapshotBeforeFailedDelete = indexer.SanitizedSolution;
            failureControl.FailNextPathDelete = true;
            var failedDeleteAct = () =>
                indexer.IndexChangedFilesAsync([targetPath]);

            await failedDeleteAct.Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage("simulated structural delete failure");
            failureControl.DeleteFailures.Should().Be(1);
            indexer.SanitizedSolution.Should().BeSameAs(snapshotBeforeFailedDelete);

            var deleteResult = await indexer.IndexChangedFilesAsync([callerPath]);

            deleteResult.FilesIndexed.Should().BeGreaterThan(0);
            deleteResult.FailedFiles.Should().BeEmpty();
            (await store.GetSymbolByIdAsync(targetMethod.Id)).Should().BeNull();
            (await store.FindReferencesAsync(targetMethod.Id)).Should().BeEmpty();
            (await store.ListCalleesAsync(
                    renamedMethod.Id,
                    edgeKind: EdgeKinds.Calls))
                .Should().BeEmpty();
            (await store.GetAllFilesAsync())
                .Should().NotContain(file =>
                    string.Equals(file.Path, targetPath, StringComparison.OrdinalIgnoreCase));

            var cancelledPath = Path.Join(projectDirectory, "Cancelled.cs");
            await File.WriteAllTextAsync(cancelledPath, """
                namespace StructuralFixture;
                public static class Cancelled { }
                """);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var cancelledAct = () =>
                indexer.IndexChangedFilesAsync([cancelledPath], cancellation.Token);

            await cancelledAct.Should().ThrowAsync<OperationCanceledException>();
            (await store.ListSymbolsInFileAsync(cancelledPath)).Should().BeEmpty();
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup: Windows may briefly retain an MSBuild file handle.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup: antivirus may transiently hold an evaluated project file.
            }
        }
    }

    [Fact]
    public async Task ChangedFileUnionsDeclarationsFromEveryTargetFrameworkBeforeReconciling()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-roslyn-multitfm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var solutionPath = await WriteSingleProjectSolutionAsync(
                root,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFrameworks>net10.0;net10.0-windows</TargetFrameworks>
                    <EnableWindowsTargeting>true</EnableWindowsTargeting>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                  <PropertyGroup Condition="'$(TargetFramework)' == 'net10.0-windows'">
                    <DefineConstants>$(DefineConstants);SECOND_TFM</DefineConstants>
                  </PropertyGroup>
                </Project>
                """);
            var sourcePath = Path.Join(root, "App", "Conditional.cs");
            await File.WriteAllTextAsync(
                sourcePath,
                "// The initial graph intentionally has a file row but no declarations.");

            await using var store =
                new SqliteGraphStore(Path.Join(root, "graph.db"));
            await using var indexer = new RoslynIndexer(
                store,
                logger: null,
                embeddingsSink: null,
                privacyRoot: root);
            await indexer.OpenAsync(solutionPath);

            indexer.SanitizedSolution!
                .GetDocumentIdsWithFilePath(sourcePath)
                .Should().HaveCount(2);
            await indexer.IndexAllAsync();
            (await store.ListSymbolsInFileAsync(sourcePath)).Should().BeEmpty();

            await File.WriteAllTextAsync(
                sourcePath,
                """
                #if SECOND_TFM
                public static class OnlyInSecondTarget
                {
                }
                #endif
                """);

            var result = await indexer.IndexChangedFilesAsync([sourcePath]);

            result.FilesIndexed.Should().Be(1);
            result.FailedFiles.Should().BeEmpty();
            var parseOptions = indexer.SanitizedSolution!
                .GetDocumentIdsWithFilePath(sourcePath)
                .Select(id => indexer.SanitizedSolution.GetDocument(id)!)
                .Select(document => (CSharpParseOptions)document.Project.ParseOptions!)
                .ToArray();
            parseOptions.Should().ContainSingle(options =>
                !options.PreprocessorSymbolNames.Contains("SECOND_TFM"));
            parseOptions.Should().ContainSingle(options =>
                options.PreprocessorSymbolNames.Contains("SECOND_TFM"));
            (await store.ListSymbolsInFileAsync(sourcePath))
                .Should().ContainSingle(symbol =>
                    symbol.Name == "OnlyInSecondTarget"
                    && symbol.Kind == SymbolKinds.Class);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task PassTwoCancellationPersistsRetryMarkerAndRestartRepairsGraph()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-roslyn-pass2-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var solutionPath = await WriteSingleProjectSolutionAsync(
                root,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);
            var targetPath = Path.Join(root, "App", "Target.cs");
            var victimPath = Path.Join(root, "App", "CancellationVictim.cs");
            await File.WriteAllTextAsync(
                targetPath,
                """
                namespace CancellationFixture;

                public static class Target
                {
                    public static int Value() => 42;
                }
                """);
            await File.WriteAllTextAsync(
                victimPath,
                """
                namespace CancellationFixture;

                public static class CancellationVictim
                {
                    public static int Invoke() => Target.Value();
                }
                """);

            await using var store =
                new SqliteGraphStore(Path.Join(root, "graph.db"));
            var storeProxy =
                DispatchProxy.Create<IGraphStore, StructuralFailureProxy>();
            var failureControl = (StructuralFailureProxy)storeProxy;
            failureControl.Inner = store;
            var indexer = new RoslynIndexer(
                storeProxy,
                logger: null,
                embeddingsSink: null,
                privacyRoot: root);
            try
            {
                await indexer.OpenAsync(solutionPath);
                await indexer.IndexAllAsync();

                var victimFile = (await store.GetAllFilesAsync()).Single(file =>
                    string.Equals(
                        file.Path,
                        victimPath,
                        StringComparison.OrdinalIgnoreCase));
                var realSha = SHA256.HashData(
                    await File.ReadAllBytesAsync(victimPath));
                (await store.HasOutgoingReferencesAsync(victimFile.Id))
                    .Should().BeTrue();

                using var cancellation = new CancellationTokenSource();
                failureControl.CancelAfterReferencesForFileId = victimFile.Id;
                failureControl.CancellationSource = cancellation;

                var cancelledAct = () =>
                    indexer.ReloadAndIndexAllAsync(cancellation.Token);

                await cancelledAct.Should()
                    .ThrowAsync<OperationCanceledException>();
                failureControl.ReferencesCommittedBeforeCancellationForFileId
                    .Should().Be(victimFile.Id);
                (await store.GetFileContentHashAsync(victimPath))
                    .Should().BeEmpty(
                        "an empty hash is the durable Pass-2 retry marker");
                (await store.HasOutgoingReferencesAsync(victimFile.Id))
                    .Should().BeFalse(
                        "the committed references must be cleared with any partial edges");

                await indexer.DisposeAsync();

                await using var restarted = new RoslynIndexer(
                    store,
                    logger: null,
                    embeddingsSink: null,
                    privacyRoot: root);
                await restarted.OpenAsync(solutionPath);
                var repaired = await restarted.IndexAllAsync();

                repaired.FilesIndexed.Should().BeGreaterThan(0);
                repaired.FailedFiles.Should().BeEmpty();
                (await store.GetFileContentHashAsync(victimPath))
                    .Should().Equal(realSha);
                (await store.HasOutgoingReferencesAsync(victimFile.Id))
                    .Should().BeTrue();
                var victimMethod =
                    (await store.ListSymbolsInFileAsync(victimPath))
                    .Single(symbol => symbol.Name == "Invoke");
                var targetMethod =
                    (await store.ListSymbolsInFileAsync(targetPath))
                    .Single(symbol => symbol.Name == "Value");
                (await store.ListCalleesAsync(
                        victimMethod.Id,
                        edgeKind: EdgeKinds.Calls))
                    .Should().ContainSingle(symbol =>
                        symbol.Id == targetMethod.Id);
            }
            finally
            {
                await indexer.DisposeAsync();
            }
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task ConcurrentOpenDisposeAndQueuedIndexAreSerializedByOneLifetimeLock()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-roslyn-concurrency-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var solutionPath = await WriteSingleProjectSolutionAsync(
                root,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(
                Path.Join(root, "App", "Program.cs"),
                "public static class Program { public static int Value => 1; }");

            var firstOpenEntered = NewCompletionSource();
            var releaseFirstOpen = NewCompletionSource();
            var secondOpenEntered = NewCompletionSource();
            var thirdOpenEntered = NewCompletionSource();
            var releaseThirdOpen = NewCompletionSource();
            var disposeEntered = NewCompletionSource();
            var releaseDispose = NewCompletionSource();
            var openedWorkspaces = new ConcurrentQueue<MSBuildWorkspace>();
            var disposedWorkspaces = new ConcurrentQueue<MSBuildWorkspace>();
            var openCalls = 0;
            var hooks = new RoslynIndexer.TestHooks(
                OpenWorkspaceAsync: async (workspace, path, ct) =>
                {
                    openedWorkspaces.Enqueue(workspace);
                    var call = Interlocked.Increment(ref openCalls);
                    if (call == 1)
                    {
                        firstOpenEntered.TrySetResult();
                        await releaseFirstOpen.Task.WaitAsync(ct);
                    }
                    else if (call == 2)
                    {
                        secondOpenEntered.TrySetResult();
                    }
                    else if (call == 3)
                    {
                        thirdOpenEntered.TrySetResult();
                        await releaseThirdOpen.Task.WaitAsync(ct);
                    }

                    var solution = await workspace.OpenSolutionAsync(
                        path,
                        cancellationToken: ct);
                    return new RoslynIndexer.WorkspaceOpenResult(
                        solution,
                        Array.Empty<WorkspaceDiagnostic>());
                },
                WorkspaceDisposed: workspace =>
                    disposedWorkspaces.Enqueue(workspace),
                DisposeAsyncEntered: async () =>
                {
                    disposeEntered.TrySetResult();
                    await releaseDispose.Task;
                });

            await using var store =
                new SqliteGraphStore(Path.Join(root, "graph.db"));
            var indexer = new RoslynIndexer(
                store,
                logger: null,
                embeddingsSink: null,
                privacyRoot: root,
                excludePatterns: Array.Empty<string>(),
                testHooks: hooks);
            try
            {
                var firstOpen = indexer.OpenAsync(solutionPath);
                await firstOpenEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
                var secondOpen = indexer.OpenAsync(solutionPath);

                secondOpenEntered.Task.IsCompleted.Should().BeFalse(
                    "the second candidate may not open until the first OpenAsync publishes");
                secondOpen.IsCompleted.Should().BeFalse();

                releaseFirstOpen.TrySetResult();
                await firstOpen.WaitAsync(TimeSpan.FromSeconds(30));
                await secondOpenEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
                await secondOpen.WaitAsync(TimeSpan.FromSeconds(30));

                var firstWorkspace = openedWorkspaces.ElementAt(0);
                var secondWorkspace = openedWorkspaces.ElementAt(1);
                indexer.Workspace.Should().BeSameAs(secondWorkspace);
                disposedWorkspaces.Count(workspace =>
                    ReferenceEquals(workspace, firstWorkspace)).Should().Be(1);

                var thirdOpen = indexer.OpenAsync(solutionPath);
                await thirdOpenEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
                var dispose = indexer.DisposeAsync().AsTask();

                releaseThirdOpen.TrySetResult();
                await thirdOpen.WaitAsync(TimeSpan.FromSeconds(30));
                await disposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
                var queuedIndex = indexer.IndexAllAsync();

                queuedIndex.IsCompleted.Should().BeFalse(
                    "DisposeAsync still owns the lifetime lock");
                releaseDispose.TrySetResult();
                await dispose.WaitAsync(TimeSpan.FromSeconds(30));
                var queuedIndexAct = async () => await queuedIndex;
                await queuedIndexAct.Should()
                    .ThrowAsync<ObjectDisposedException>();

                openedWorkspaces.Should().HaveCount(3);
                foreach (var workspace in openedWorkspaces)
                {
                    disposedWorkspaces.Count(disposed =>
                        ReferenceEquals(disposed, workspace)).Should().Be(1);
                }
            }
            finally
            {
                releaseFirstOpen.TrySetResult();
                releaseThirdOpen.TrySetResult();
                releaseDispose.TrySetResult();
                await indexer.DisposeAsync();
            }
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task GeneratedOwnersRemainDistinctWhenProjectsGeneratorsAndRegularDocumentShareDisplayPath()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-roslyn-generated-owner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var analyzerPath = LocateFixtureGeneratorAssembly();
            var solutionPath = await WriteGeneratedCollisionSolutionAsync(
                root,
                analyzerPath);
            var appAPath = Path.Join(root, "AppA", "Marker.cs");
            var appBPath = Path.Join(root, "AppB", "Marker.cs");
            var sharedDisplayPath = Path.Join(
                root,
                "AppA",
                "SharedOwner.g.cs");
            await File.WriteAllTextAsync(
                appAPath,
                "// GEN_VERSION_1");
            await File.WriteAllTextAsync(
                appBPath,
                "// GEN_VERSION_1");
            await File.WriteAllTextAsync(
                sharedDisplayPath,
                "public static class RegularCollisionV1 { }");

            var ownerGeneration = 1;
            var historyPaths = new ConcurrentQueue<string>();
            var hooks = new RoslynIndexer.TestHooks(
                GeneratedOwnerIdentity: document =>
                    $"{ownerGeneration}:{document.Id.Id:N}",
                GeneratedDisplayPath: _ => sharedDisplayPath);
            await using var store =
                new SqliteGraphStore(Path.Join(root, "graph.db"));
            await using var indexer = new RoslynIndexer(
                store,
                logger: null,
                embeddingsSink: null,
                privacyRoot: root,
                excludePatterns: Array.Empty<string>(),
                testHooks: hooks);
            indexer.OnFileIndexed = (_, path, _) =>
            {
                historyPaths.Enqueue(path);
                return Task.CompletedTask;
            };
            await indexer.OpenAsync(solutionPath);

            var initial = await indexer.IndexAllAsync();

            initial.FailedFiles.Should().BeEmpty();
            var generatedDocuments = await GetGeneratedDocumentsAsync(indexer);
            generatedDocuments.Should().HaveCount(6);
            generatedDocuments.Count(document =>
                    document.HintName == "SharedOwner.g.cs")
                .Should().Be(4);
            generatedDocuments
                .Select(_ => sharedDisplayPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Should().ContainSingle(
                    "the hook reproduces four generated documents with one display path");
            var generatedRows = await store.ListGeneratedFilesAsync(int.MaxValue);
            generatedRows.Should().HaveCount(6);
            generatedRows.Select(row => row.FilePath)
                .Should().OnlyHaveUniqueItems();
            generatedRows.Should().OnlyContain(row =>
                row.FilePath.Contains(
                    Path.Join("obj", ".sourcegraph-generated"),
                    StringComparison.OrdinalIgnoreCase));
            generatedRows.Count(row => row.FilePath.EndsWith(
                    "SharedOwner.g.cs",
                    StringComparison.OrdinalIgnoreCase))
                .Should().Be(4);

            var regularRow = (await store.GetAllFilesAsync()).Single(row =>
                string.Equals(
                    row.Path,
                    sharedDisplayPath,
                    StringComparison.OrdinalIgnoreCase));
            (await store.IsGeneratedFileAsync(regularRow.Id)).Should().BeFalse();
            (await store.ListSymbolsInFileAsync(sharedDisplayPath))
                .Should().ContainSingle(symbol =>
                    symbol.Name == "RegularCollisionV1");
            (await store.FindSymbolsAsync("GeneratedStateV1"))
                .Should().Contain(symbol =>
                    symbol.Fqn == "Generated.AppA.GeneratedStateV1")
                .And.Contain(symbol =>
                    symbol.Fqn == "Generated.AppB.GeneratedStateV1");
            (await store.FindSymbolsAsync("SecondGeneratedState"))
                .Should().Contain(symbol =>
                    symbol.Fqn == "Generated.AppA.SecondGeneratedState")
                .And.Contain(symbol =>
                    symbol.Fqn == "Generated.AppB.SecondGeneratedState");
            historyPaths.Should().NotBeEmpty();
            historyPaths.Should().OnlyContain(path => File.Exists(path),
                "virtual generated owners must never enter the disk/git history callback");
            await AssertGeneratedHashesMatchDocumentsAsync(indexer, store);

            await File.WriteAllTextAsync(
                appAPath,
                "// GEN_VERSION_2");
            var appAUpdate = await indexer.IndexChangedFilesAsync([appAPath]);

            appAUpdate.FailedFiles.Should().BeEmpty();
            (await store.FindSymbolsAsync("GeneratedStateV2"))
                .Should().ContainSingle(symbol =>
                    symbol.Fqn == "Generated.AppA.GeneratedStateV2");
            (await store.FindSymbolsAsync("GeneratedStateV1"))
                .Should().ContainSingle(symbol =>
                    symbol.Fqn == "Generated.AppB.GeneratedStateV1");
            await AssertGeneratedHashesMatchDocumentsAsync(indexer, store);

            await File.WriteAllTextAsync(
                sharedDisplayPath,
                "public static class RegularCollisionV2 { }");
            var regularUpdate = await indexer.IndexChangedFilesAsync(
                [sharedDisplayPath]);

            regularUpdate.FailedFiles.Should().BeEmpty();
            (await store.ListSymbolsInFileAsync(sharedDisplayPath))
                .Should().ContainSingle(symbol =>
                    symbol.Name == "RegularCollisionV2");
            (await store.ListGeneratedFilesAsync(int.MaxValue))
                .Should().HaveCount(6);
            await AssertGeneratedHashesMatchDocumentsAsync(indexer, store);

            await File.WriteAllTextAsync(
                appBPath,
                "// GEN_VERSION_2");
            var appBUpdate = await indexer.IndexChangedFilesAsync([appBPath]);

            appBUpdate.FailedFiles.Should().BeEmpty();
            (await store.FindSymbolsAsync("GeneratedStateV2"))
                .Should().Contain(symbol =>
                    symbol.Fqn == "Generated.AppA.GeneratedStateV2")
                .And.Contain(symbol =>
                    symbol.Fqn == "Generated.AppB.GeneratedStateV2");

            var oldGeneratedPaths = (await store.ListGeneratedFilesAsync(int.MaxValue))
                .Select(row => row.FilePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            ownerGeneration = 2;
            await indexer.OpenAsync(solutionPath);
            var reopened = await indexer.IndexAllAsync();

            reopened.FailedFiles.Should().BeEmpty();
            var reopenedGeneratedRows =
                await store.ListGeneratedFilesAsync(int.MaxValue);
            reopenedGeneratedRows.Should().HaveCount(6);
            reopenedGeneratedRows.Should().OnlyContain(row =>
                !oldGeneratedPaths.Contains(row.FilePath),
                "a complete discovery must remove owners from the prior workspace identity");
            (await store.GetAllFilesAsync()).Count(row =>
                    string.Equals(
                        row.Path,
                        sharedDisplayPath,
                        StringComparison.OrdinalIgnoreCase))
                .Should().Be(1);
            await AssertGeneratedHashesMatchDocumentsAsync(indexer, store);

            await File.WriteAllTextAsync(
                appAPath,
                "// GEN_VERSION_1");
            var postReconcileIncremental =
                await indexer.IndexChangedFilesAsync([appAPath]);

            postReconcileIncremental.FailedFiles.Should().BeEmpty();
            var secondValue = (await store.GetAllSymbolKeysAsync())
                .Single(symbol => symbol.CanonicalKey.Contains(
                    "Generated.AppA.SecondGeneratedState.Value",
                    StringComparison.Ordinal));
            (await store.FindReferencesAsync(
                    secondValue.Id,
                    includeGenerated: true))
                .Should().Contain(reference =>
                    reference.IsGenerated,
                    "stale-owner cleanup must retain symbol maps now owned by the new owner");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task GeneratedOwnerReconcileWaitsForACompleteSuccessfulPass()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-roslyn-generated-reconcile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var solutionPath = await WriteGeneratedCollisionSolutionAsync(
                root,
                LocateFixtureGeneratorAssembly());
            await File.WriteAllTextAsync(
                Path.Join(root, "AppA", "Marker.cs"),
                "// collision-reconcile fixture");
            await File.WriteAllTextAsync(
                Path.Join(root, "AppB", "Marker.cs"),
                "// collision-reconcile fixture");

            var ownerGeneration = 0;
            var sharedDisplayPath = Path.Join(root, "SharedOwner.g.cs");
            var hooks = new RoslynIndexer.TestHooks(
                GeneratedOwnerIdentity: document => ownerGeneration switch
                {
                    // The two SharedOwner.g.cs documents in each project deliberately collapse
                    // to one owner during the failed pass. Hello remains at its initial owner so
                    // no unrelated owner churn obscures the stale-reconcile assertion.
                    1 when document.HintName == "SharedOwner.g.cs" => "collision",
                    1 => "initial-hello",
                    2 => $"replacement:{document.HintName}:{document.Id.Id:N}",
                    _ when document.HintName == "GeneratedHello.g.cs" => "initial-hello",
                    _ => $"initial:{document.Id.Id:N}",
                },
                GeneratedDisplayPath: _ => sharedDisplayPath);
            await using var store =
                new SqliteGraphStore(Path.Join(root, "graph.db"));
            await using var indexer = new RoslynIndexer(
                store,
                logger: null,
                embeddingsSink: null,
                privacyRoot: root,
                excludePatterns: Array.Empty<string>(),
                testHooks: hooks);
            await indexer.OpenAsync(solutionPath);

            var initial = await indexer.IndexAllAsync();

            initial.FailedProjects.Should().BeEmpty();
            initial.FailedFiles.Should().BeEmpty();
            var oldGeneratedRows = (await store.ListGeneratedFilesAsync(int.MaxValue))
                .OrderBy(row => row.FilePath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            oldGeneratedRows.Should().HaveCount(6);
            var oldFileIds = oldGeneratedRows
                .Select(row => row.FileId)
                .ToHashSet();
            var oldGeneratedPaths = oldGeneratedRows
                .Select(row => row.FilePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var oldSymbols = (await store.GetAllSymbolKeysAsync())
                .Where(row => oldFileIds.Contains(row.FileId))
                .OrderBy(row => row.CanonicalKey, StringComparer.Ordinal)
                .ToArray();
            oldSymbols.Should().NotBeEmpty();
            var referenceTargets = oldSymbols
                .Where(row => row.CanonicalKey.Contains(
                    "SecondGeneratedState.Value",
                    StringComparison.Ordinal))
                .ToArray();
            referenceTargets.Should().HaveCount(2);
            var oldReferences = new List<ReferenceHit>();
            foreach (var target in referenceTargets)
            {
                oldReferences.AddRange(
                    (await store.FindReferencesAsync(
                        target.Id,
                        includeGenerated: true))
                    .Where(reference =>
                        oldGeneratedPaths.Contains(reference.FilePath)));
            }
            oldReferences.Should().NotBeEmpty()
                .And.OnlyContain(reference => reference.IsGenerated);
            var orderedOldReferences = oldReferences
                .OrderBy(reference => reference.Id)
                .ToArray();

            ownerGeneration = 1;
            var collided = await indexer.IndexAllAsync();

            collided.FailedProjects.Should().BeEmpty();
            collided.FailedFiles.Should().ContainSingle(failure =>
                failure.Reason.Contains(
                    "same stable owner",
                    StringComparison.Ordinal));
            (await store.ListGeneratedFilesAsync(int.MaxValue))
                .OrderBy(row => row.FilePath, StringComparer.OrdinalIgnoreCase)
                .Should().Equal(oldGeneratedRows,
                    "a failed current-owner pass must not reconcile prior generated rows");
            (await store.GetAllSymbolKeysAsync())
                .Where(row => oldFileIds.Contains(row.FileId))
                .OrderBy(row => row.CanonicalKey, StringComparer.Ordinal)
                .Should().Equal(oldSymbols,
                    "the prior generated declarations remain the last usable graph");
            var referencesAfterCollision = new List<ReferenceHit>();
            foreach (var target in referenceTargets)
            {
                referencesAfterCollision.AddRange(
                    (await store.FindReferencesAsync(
                        target.Id,
                        includeGenerated: true))
                    .Where(reference =>
                        oldGeneratedPaths.Contains(reference.FilePath)));
            }
            referencesAfterCollision
                .OrderBy(reference => reference.Id)
                .Should().Equal(orderedOldReferences,
                    "the failed pass must retain prior generated references");

            ownerGeneration = 2;
            var recovered = await indexer.IndexAllAsync();

            recovered.FailedProjects.Should().BeEmpty();
            recovered.FailedFiles.Should().BeEmpty();
            var replacementRows =
                await store.ListGeneratedFilesAsync(int.MaxValue);
            replacementRows.Should().HaveCount(6);
            replacementRows.Should().OnlyContain(row =>
                !oldGeneratedPaths.Contains(row.FilePath),
                "the first complete successful pass may now remove prior owners");
            var replacementPaths = replacementRows
                .Select(row => row.FilePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var replacementSymbols = (await store.GetAllSymbolKeysAsync())
                .Where(row => referenceTargets.Any(target =>
                    target.Id == row.Id))
                .ToArray();
            replacementSymbols.Should().OnlyContain(row =>
                replacementRows.Any(file => file.FileId == row.FileId));
            foreach (var target in referenceTargets)
            {
                (await store.FindReferencesAsync(
                        target.Id,
                        includeGenerated: true))
                    .Should().Contain(reference =>
                        reference.IsGenerated
                        && replacementPaths.Contains(reference.FilePath));
            }
            await AssertGeneratedHashesMatchDocumentsAsync(indexer, store);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task WindowsCasingOnlyReloadAndIncrementalEditReuseOnePersistedFileIdentity()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-roslyn-path-case-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var solutionPath = await WriteSingleProjectSolutionAsync(
                root,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);
            var targetPath = Path.Join(root, "App", "Target.cs");
            var originalPath = Path.Join(root, "App", "CaseConsumer.cs");
            var casedPath = Path.Join(root, "App", "CASECONSUMER.cs");
            await File.WriteAllTextAsync(
                targetPath,
                """
                public static class Target
                {
                    public static int One() => 1;
                    public static int Two() => 2;
                }
                """);
            await File.WriteAllTextAsync(
                originalPath,
                """
                public static class CaseConsumer
                {
                    public static int Invoke() => Target.One();
                }
                """);

            await using var store =
                new SqliteGraphStore(Path.Join(root, "graph.db"));
            var indexer = new RoslynIndexer(
                store,
                logger: null,
                embeddingsSink: null,
                privacyRoot: root);
            try
            {
                await indexer.OpenAsync(solutionPath);
                await indexer.IndexAllAsync();
                var originalRow = (await store.GetAllFilesAsync()).Single(row =>
                    string.Equals(
                        row.Path,
                        originalPath,
                        StringComparison.OrdinalIgnoreCase));
                var oneMethod = (await store.ListSymbolsInFileAsync(targetPath))
                    .Single(symbol => symbol.Name == "One");
                var twoMethod = (await store.ListSymbolsInFileAsync(targetPath))
                    .Single(symbol => symbol.Name == "Two");

                var temporaryPath = Path.Join(root, "App", "case-rename.tmp");
                File.Move(originalPath, temporaryPath);
                File.Move(temporaryPath, casedPath);
                await File.WriteAllTextAsync(
                    casedPath,
                    """
                    public static class CaseConsumer
                    {
                        public static int Invoke() => Target.Two();
                    }
                    """);

                var reloaded = await indexer.IndexChangedFilesAsync(
                    [originalPath, casedPath]);

                reloaded.FailedFiles.Should().BeEmpty();
                var rowsAfterReload = (await store.GetAllFilesAsync())
                    .Where(row => string.Equals(
                        row.Path,
                        casedPath,
                        StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                rowsAfterReload.Should().ContainSingle();
                rowsAfterReload[0].Id.Should().Be(originalRow.Id);
                (await store.GetFileContentHashAsync(rowsAfterReload[0].Path))
                    .Should().Equal(SHA256.HashData(
                        await File.ReadAllBytesAsync(casedPath)));
                var invoke = (await store.ListSymbolsInFileAsync(
                        rowsAfterReload[0].Path))
                    .Single(symbol => symbol.Name == "Invoke");
                (await store.ListCalleesAsync(
                        invoke.Id,
                        edgeKind: EdgeKinds.Calls))
                    .Should().ContainSingle(symbol => symbol.Id == twoMethod.Id)
                    .And.NotContain(symbol => symbol.Id == oneMethod.Id);

                await File.WriteAllTextAsync(
                    casedPath,
                    """
                    public static class CaseConsumer
                    {
                        public static int Invoke() => Target.One();
                        public static int AfterCaseIncremental() => 3;
                    }
                    """);
                var watcherVariant = casedPath.ToLowerInvariant();

                var incremental = await indexer.IndexChangedFilesAsync(
                    [watcherVariant]);

                incremental.FilesIndexed.Should().Be(1);
                incremental.FailedFiles.Should().BeEmpty();
                (await store.GetAllFilesAsync()).Count(row =>
                        string.Equals(
                            row.Path,
                            casedPath,
                            StringComparison.OrdinalIgnoreCase))
                    .Should().Be(1);
                (await store.ListSymbolsInFileAsync(rowsAfterReload[0].Path))
                    .Should().ContainSingle(symbol =>
                        symbol.Name == "AfterCaseIncremental");
                (await store.ListCalleesAsync(
                        invoke.Id,
                        edgeKind: EdgeKinds.Calls))
                    .Should().ContainSingle(symbol => symbol.Id == oneMethod.Id)
                    .And.NotContain(symbol => symbol.Id == twoMethod.Id);

                await indexer.DisposeAsync();
                await using var restarted = new RoslynIndexer(
                    store,
                    logger: null,
                    embeddingsSink: null,
                    privacyRoot: root);
                await restarted.OpenAsync(solutionPath);
                var restartResult = await restarted.IndexAllAsync();

                restartResult.FailedFiles.Should().BeEmpty();
                var rowsAfterRestart = (await store.GetAllFilesAsync())
                    .Where(row => string.Equals(
                        row.Path,
                        casedPath,
                        StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                rowsAfterRestart.Should().ContainSingle();
                rowsAfterRestart[0].Id.Should().Be(originalRow.Id);
                (await store.ListSymbolsInFileAsync(rowsAfterRestart[0].Path))
                    .Should().ContainSingle(symbol =>
                        symbol.Name == "AfterCaseIncremental");
            }
            finally
            {
                await indexer.DisposeAsync();
            }
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static TaskCompletionSource NewCompletionSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<IReadOnlyList<SourceGeneratedDocument>>
        GetGeneratedDocumentsAsync(RoslynIndexer indexer)
    {
        var documents = new List<SourceGeneratedDocument>();
        foreach (var project in indexer.SanitizedSolution!.Projects)
        {
            documents.AddRange(
                await project.GetSourceGeneratedDocumentsAsync());
        }
        return documents;
    }

    private static async Task AssertGeneratedHashesMatchDocumentsAsync(
        RoslynIndexer indexer,
        IGraphStore store)
    {
        var expected = new List<string>();
        foreach (var document in await GetGeneratedDocumentsAsync(indexer))
        {
            var text = await document.GetTextAsync();
            expected.Add(Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(text.ToString()))));
        }

        var actual = new List<string>();
        foreach (var row in await store.ListGeneratedFilesAsync(int.MaxValue))
        {
            var hash = await store.GetFileContentHashAsync(row.FilePath);
            hash.Should().NotBeNull();
            actual.Add(Convert.ToHexString(hash!));
        }

        actual.Should().BeEquivalentTo(
            expected,
            "every generated owner hash must describe its own Roslyn document text");
    }

    private static async Task<string> WriteGeneratedCollisionSolutionAsync(
        string root,
        string analyzerPath)
    {
        var appADirectory = Path.Join(root, "AppA");
        var appBDirectory = Path.Join(root, "AppB");
        Directory.CreateDirectory(appADirectory);
        Directory.CreateDirectory(appBDirectory);
        var escapedAnalyzerPath =
            System.Security.SecurityElement.Escape(analyzerPath)!;
        var projectContents = $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <Analyzer Include="{{escapedAnalyzerPath}}" />
              </ItemGroup>
            </Project>
            """;
        await File.WriteAllTextAsync(
            Path.Join(appADirectory, "AppA.csproj"),
            projectContents);
        await File.WriteAllTextAsync(
            Path.Join(appBDirectory, "AppB.csproj"),
            projectContents);

        var solutionPath = Path.Join(root, "GeneratedCollision.sln");
        await File.WriteAllTextAsync(
            solutionPath,
            """
            Microsoft Visual Studio Solution File, Format Version 12.00
            # Visual Studio Version 17
            VisualStudioVersion = 17.0.31903.59
            MinimumVisualStudioVersion = 10.0.40219.1
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "AppA", "AppA\AppA.csproj", "{A269EB0B-1CA9-4D1C-BF7D-F620BF78E291}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "AppB", "AppB\AppB.csproj", "{B269EB0B-1CA9-4D1C-BF7D-F620BF78E292}"
            EndProject
            Global
                GlobalSection(SolutionConfigurationPlatforms) = preSolution
                    Debug|Any CPU = Debug|Any CPU
                    Release|Any CPU = Release|Any CPU
                EndGlobalSection
                GlobalSection(ProjectConfigurationPlatforms) = postSolution
                    {A269EB0B-1CA9-4D1C-BF7D-F620BF78E291}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
                    {A269EB0B-1CA9-4D1C-BF7D-F620BF78E291}.Debug|Any CPU.Build.0 = Debug|Any CPU
                    {A269EB0B-1CA9-4D1C-BF7D-F620BF78E291}.Release|Any CPU.ActiveCfg = Release|Any CPU
                    {A269EB0B-1CA9-4D1C-BF7D-F620BF78E291}.Release|Any CPU.Build.0 = Release|Any CPU
                    {B269EB0B-1CA9-4D1C-BF7D-F620BF78E292}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
                    {B269EB0B-1CA9-4D1C-BF7D-F620BF78E292}.Debug|Any CPU.Build.0 = Debug|Any CPU
                    {B269EB0B-1CA9-4D1C-BF7D-F620BF78E292}.Release|Any CPU.ActiveCfg = Release|Any CPU
                    {B269EB0B-1CA9-4D1C-BF7D-F620BF78E292}.Release|Any CPU.Build.0 = Release|Any CPU
                EndGlobalSection
            EndGlobal
            """);
        return solutionPath;
    }

    private static string LocateFixtureGeneratorAssembly()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Join(
                directory.FullName,
                "tests",
                "fixtures",
                "Sample.Generators",
                "bin",
                "Debug",
                "netstandard2.0",
                "Sample.Generators.dll");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            "Could not locate the built Sample.Generators fixture assembly.");
    }

    private static async Task<string> WriteSingleProjectSolutionAsync(
        string root,
        string projectContents)
    {
        var projectDirectory = Path.Join(root, "App");
        Directory.CreateDirectory(projectDirectory);
        var projectPath = Path.Join(projectDirectory, "App.csproj");
        var solutionPath = Path.Join(root, "Fixture.sln");
        await File.WriteAllTextAsync(projectPath, projectContents);
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
        return solutionPath;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup: Windows may briefly retain an MSBuild file handle.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup: antivirus may transiently hold an evaluated project file.
        }
    }

    public class StructuralFailureProxy : DispatchProxy
    {
        public IGraphStore Inner { get; set; } = null!;
        public bool FailNextPathDelete { get; set; }
        public bool FailNextEdgeInsert { get; set; }
        public long CancelAfterReferencesForFileId { get; set; }
        public CancellationTokenSource? CancellationSource { get; set; }
        public int DeleteFailures { get; private set; }
        public int EdgeFailures { get; private set; }
        public long ReferencesCommittedBeforeCancellationForFileId { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null)
            {
                return null;
            }

            if (targetMethod.Name == nameof(IGraphStore.DeleteFileAsync)
                && args is [{ } path, _]
                && path is string
                && FailNextPathDelete)
            {
                FailNextPathDelete = false;
                DeleteFailures++;
                return Task.FromException<bool>(
                    new InvalidOperationException("simulated structural delete failure"));
            }

            if (targetMethod.Name == nameof(IGraphStore.BulkInsertEdgesAsync))
            {
                var edges = ((IEnumerable<Edge>?)args![0])?.ToList()
                    ?? new List<Edge>();
                args[0] = edges;
                if (edges.Count > 0 && FailNextEdgeInsert)
                {
                    FailNextEdgeInsert = false;
                    EdgeFailures++;
                    return Task.FromException(
                        new InvalidOperationException("simulated structural edge failure"));
                }
            }

            if (targetMethod.Name == nameof(IGraphStore.BulkInsertReferencesAsync))
            {
                var references =
                    ((IEnumerable<SymbolReference>?)args![0])?.ToList()
                    ?? new List<SymbolReference>();
                args[0] = references;
                if (CancelAfterReferencesForFileId != 0
                    && CancellationSource is { } cancellationSource
                    && references.Any(reference =>
                        reference.FileId == CancelAfterReferencesForFileId))
                {
                    var fileId = CancelAfterReferencesForFileId;
                    CancelAfterReferencesForFileId = 0;
                    CancellationSource = null;
                    var commit = (Task)targetMethod.Invoke(Inner, args)!;
                    return CommitReferencesThenCancelAsync(
                        commit,
                        cancellationSource,
                        fileId);
                }
            }

            return targetMethod.Invoke(Inner, args);
        }

        private async Task CommitReferencesThenCancelAsync(
            Task commit,
            CancellationTokenSource cancellationSource,
            long fileId)
        {
            await commit;
            ReferencesCommittedBeforeCancellationForFileId = fileId;
            cancellationSource.Cancel();
        }
    }
}
