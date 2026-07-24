using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class RoslynCommandExecutionTests
{
    [Fact]
    public async Task CommandExecutes_followsExactDelegateAcrossColdEditRemovalAndDelete()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var solutionPath = await WriteProjectAsync(root);
            var commandPath = Path.Join(root, "App", "RelayCommand.cs");
            var viewModelPath = Path.Join(root, "App", "ViewModel.cs");
            await File.WriteAllTextAsync(commandPath, RelayCommandSource);
            await File.WriteAllTextAsync(
                viewModelPath,
                ViewModelSource("Run", includeInitializer: true));

            await using var store =
                new SqliteGraphStore(Path.Join(root, "graph.db"));
            await using var indexer = new RoslynIndexer(
                store,
                logger: null,
                embeddingsSink: null,
                privacyRoot: root);
            await indexer.OpenAsync(solutionPath);

            var cold = await indexer.IndexAllAsync();

            cold.FailedFiles.Should().BeEmpty();
            var property = await SymbolNamedAsync(
                store,
                viewModelPath,
                "RunCommand",
                SymbolKinds.Property);
            var initializedProperty = await SymbolNamedAsync(
                store,
                viewModelPath,
                "InitializedCommand",
                SymbolKinds.Property);
            var run = await SymbolNamedAsync(
                store,
                viewModelPath,
                "Run",
                SymbolKinds.Method);
            var initialize = await SymbolNamedAsync(
                store,
                viewModelPath,
                "Initialize",
                SymbolKinds.Method);
            await AssertSingleCommandTargetAsync(store, property.Id, run.Id);
            await AssertSingleCommandTargetAsync(
                store,
                initializedProperty.Id,
                initialize.Id);

            var evidence = await store.ListEdgeEvidenceAsync(
                property.Id,
                run.Id,
                EdgeKinds.CommandExecutes);
            evidence.Should().ContainSingle();
            evidence[0].Confidence.Should().Be(
                DevBitsLab.Mcp.SourceGraph.Core.EvidenceConfidence.Semantic);
            evidence[0].Producer.Should().Be("roslyn");
            evidence[0].Location.FilePath.Should().Be(viewModelPath);
            evidence[0].Location.StartLine.Should().Be(9);
            var evidenceLine = (await File.ReadAllLinesAsync(viewModelPath))[8];
            evidenceLine.Substring(
                    evidence[0].Location.StartColumn - 1,
                    evidence[0].Location.EndColumn
                    - evidence[0].Location.StartColumn)
                .Should().Be("Run");

            var viewModelConstructor = (await store
                    .ListSymbolsInFileAsync(viewModelPath))
                .Single(symbol => symbol.Kind == SymbolKinds.Constructor);
            var commandConstructor = (await store
                    .ListSymbolsInFileAsync(commandPath))
                .Single(symbol => symbol.Kind == SymbolKinds.Constructor);
            (await store.ListCalleesAsync(
                    viewModelConstructor.Id,
                    edgeKind: EdgeKinds.Calls))
                .Should().ContainSingle(symbol =>
                    symbol.Id == commandConstructor.Id,
                    "the specialized edge must not replace the ordinary constructor call");

            await File.WriteAllTextAsync(
                viewModelPath,
                ViewModelSource("Stop", includeInitializer: true));
            var edited = await indexer.IndexChangedFilesAsync([viewModelPath]);

            edited.FailedFiles.Should().BeEmpty();
            var stop = await SymbolNamedAsync(
                store,
                viewModelPath,
                "Stop",
                SymbolKinds.Method);
            await AssertSingleCommandTargetAsync(store, property.Id, stop.Id);
            (await store.ListCalleesAsync(
                    property.Id,
                    edgeKind: EdgeKinds.CommandExecutes))
                .Should().NotContain(symbol => symbol.Id == run.Id);

            await File.WriteAllTextAsync(
                viewModelPath,
                ViewModelSource("Stop", includeInitializer: false));
            var removed = await indexer.IndexChangedFilesAsync([viewModelPath]);

            removed.FailedFiles.Should().BeEmpty();
            (await store.ListCalleesAsync(
                    property.Id,
                    edgeKind: EdgeKinds.CommandExecutes))
                .Should().BeEmpty();

            File.Delete(viewModelPath);
            var deleted = await indexer.IndexChangedFilesAsync([viewModelPath]);

            deleted.FailedFiles.Should().BeEmpty();
            (await store.GetSymbolByIdAsync(property.Id)).Should().BeNull();
            (await store.ListEdgeEvidenceAsync(
                    property.Id,
                    stop.Id,
                    EdgeKinds.CommandExecutes))
                .Should().BeEmpty();
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task CommandExecutes_rejectsAmbiguousLambdaAndNonCommandProperty()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var solutionPath = await WriteProjectAsync(root);
            var sourcePath = Path.Join(root, "App", "NegativeCommands.cs");
            await File.WriteAllTextAsync(sourcePath, """
                using System;
                using System.Windows.Input;

                namespace CommandFixture;

                public sealed class NegativeCommands
                {
                    public NegativeCommands()
                    {
                        dynamic handler = (Action)DynamicTarget;
                        DynamicCommand = new RelayCommand(handler);
                    }

                    public object NotACommand { get; } =
                        new RelayCommand(NonCommandTarget);
                    public ICommand LambdaCommand { get; } =
                        new RelayCommand(() => LambdaTarget());
                    public ICommand AmbiguousCommand { get; } =
                        new AmbiguousRelayCommand(Handle);
                    public ICommand MultipleDelegateCommand { get; } =
                        new MultipleDelegateRelayCommand(First, Second);
                    public ICommand DynamicCommand { get; }

                    private static void NonCommandTarget() { }
                    private static void LambdaTarget() { }
                    private static void DynamicTarget() { }
                    private static void Handle(int value) { }
                    private static void Handle(string value) { }
                    private static void First() { }
                    private static void Second() { }
                }

                public sealed class RelayCommand(Action execute) : ICommand
                {
                    public event EventHandler? CanExecuteChanged;
                    public bool CanExecute(object? parameter) => true;
                    public void Execute(object? parameter) => execute();
                }

                public sealed class AmbiguousRelayCommand : ICommand
                {
                    public AmbiguousRelayCommand(Action<int> execute) { }
                    public AmbiguousRelayCommand(Action<string> execute) { }
                    public event EventHandler? CanExecuteChanged;
                    public bool CanExecute(object? parameter) => true;
                    public void Execute(object? parameter) { }
                }

                public sealed class MultipleDelegateRelayCommand(
                    Action first,
                    Action second) : ICommand
                {
                    public event EventHandler? CanExecuteChanged;
                    public bool CanExecute(object? parameter) => true;
                    public void Execute(object? parameter) => first();
                }
                """);

            await using var store =
                new SqliteGraphStore(Path.Join(root, "graph.db"));
            await using var indexer = new RoslynIndexer(
                store,
                logger: null,
                embeddingsSink: null,
                privacyRoot: root);
            await indexer.OpenAsync(solutionPath);

            await indexer.IndexAllAsync();

            foreach (var propertyName in new[]
                     {
                         "NotACommand",
                         "LambdaCommand",
                         "AmbiguousCommand",
                         "MultipleDelegateCommand",
                         "DynamicCommand",
                     })
            {
                var property = await SymbolNamedAsync(
                    store,
                    sourcePath,
                    propertyName,
                    SymbolKinds.Property);
                (await store.ListCalleesAsync(
                        property.Id,
                        edgeKind: EdgeKinds.CommandExecutes))
                    .Should().BeEmpty(
                        $"{propertyName} does not have one semantically proven command handler");
            }
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static readonly string RelayCommandSource = """
        using System;
        using System.Windows.Input;

        namespace CommandFixture;

        public sealed class RelayCommand : ICommand
        {
            private readonly Action _execute;
            public RelayCommand(
                Action execute,
                Func<bool>? canExecute = null) => _execute = execute;
            public event EventHandler? CanExecuteChanged;
            public bool CanExecute(object? parameter) => true;
            public void Execute(object? parameter) => _execute();
        }
        """;

    private static string ViewModelSource(
        string assignedHandler,
        bool includeInitializer) => $$"""
        using System.Windows.Input;

        namespace CommandFixture;

        public sealed class ViewModel
        {
            public ViewModel()
            {
                RunCommand = {{(includeInitializer
                    ? $"new RelayCommand({assignedHandler})"
                    : "null!")}};
            }
            public ICommand RunCommand { get; }
            public ICommand InitializedCommand { get; } =
                {{(includeInitializer
                    ? "new RelayCommand(Initialize)"
                    : "null!")}};
            private static void Run() { }
            private static void Stop() { }
            private static void Initialize() { }
        }
        """;

    private static async Task<SymbolHit> SymbolNamedAsync(
        IGraphStore store,
        string path,
        string name,
        string kind) =>
        (await store.ListSymbolsInFileAsync(path))
        .Single(symbol => symbol.Name == name && symbol.Kind == kind);

    private static async Task AssertSingleCommandTargetAsync(
        IGraphStore store,
        long propertyId,
        long methodId) =>
        (await store.ListCalleesAsync(
                propertyId,
                edgeKind: EdgeKinds.CommandExecutes))
            .Should().ContainSingle(symbol => symbol.Id == methodId);

    private static string CreateTemporaryRoot()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-command-executes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task<string> WriteProjectAsync(string root)
    {
        var projectDirectory = Path.Join(root, "App");
        Directory.CreateDirectory(projectDirectory);
        var projectPath = Path.Join(projectDirectory, "App.csproj");
        await File.WriteAllTextAsync(projectPath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);

        var solutionPath = Path.Join(root, "Fixture.sln");
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
            // Best effort: MSBuild can briefly retain a file handle on Windows.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort: antivirus can briefly retain a file handle on Windows.
        }
    }
}
