using System.Text.Json;
using System.Xml.Linq;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Indexing.Xaml;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Server.Plugins;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class WpfWindowsFixtureContractTests
{
    [SkippableFact]
    public void FixtureUsesRealWindowsDesktopWpfBuild()
    {
        Skip.IfNot(
            OperatingSystem.IsWindows(),
            "The real WindowsDesktop/WPF build fixture runs on Windows.");

        var fixtureRoot = LocateFixture("SampleWpfWindows");
        var project = XDocument.Load(
            Path.Combine(fixtureRoot, "SampleWpfWindows.csproj"));

        ProjectProperty(project, "TargetFramework").Should().Be(
            "net10.0-windows");
        ProjectProperty(project, "UseWPF").Should().Be("true");
        ProjectProperty(project, "EnableWindowsTargeting").Should().Be("true");
        ProjectProperty(project, "OutputType").Should().Be("WinExe");

        File.ReadAllText(Path.Combine(fixtureRoot, "App.xaml.cs"))
            .Should().Contain("App : Application")
            .And.Contain("InitializeComponent();");
        File.ReadAllText(
                Path.Combine(fixtureRoot, "Views", "MainWindow.xaml.cs"))
            .Should().Contain("MainWindow : Window")
            .And.Contain("InitializeComponent();");

        var buildOutputs = Directory
            .EnumerateFiles(
                Path.Combine(fixtureRoot, "bin"),
                "SampleWpfWindows.dll",
                SearchOption.AllDirectories)
            .ToArray();
        buildOutputs.Should().NotBeEmpty(
            "the test project has a build-only reference to the real WPF fixture");

        var generatedRoot = Path.Combine(fixtureRoot, "obj");
        var generatedView = Directory
            .EnumerateFiles(
                generatedRoot,
                "MainWindow.g.cs",
                SearchOption.AllDirectories)
            .Should().NotBeEmpty(
                "the WindowsDesktop markup compiler must generate the code-behind half")
            .And.Subject.First();
        File.ReadAllText(generatedView).Should()
            .Contain("partial class MainWindow")
            .And.Contain("void InitializeComponent()");
        Directory.EnumerateFiles(
                generatedRoot,
                "MainWindow.baml",
                SearchOption.AllDirectories)
            .Should().NotBeEmpty(
                "a real WPF Page is compiled into BAML");
    }

    [SkippableFact]
    public async Task RealWpfProductionIndexUsesGeneratedDocumentsForCompleteSemantics()
    {
        Skip.IfNot(
            OperatingSystem.IsWindows(),
            "The real WindowsDesktop/WPF semantic fixture runs on Windows.");

        var fixtureRoot = LocateFixture("SampleWpfWindows");
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "sourcegraph-real-wpf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            await using var store = new SqliteGraphStore(
                Path.Combine(tempRoot, "graph.db"));
            await using var roslyn = new RoslynIndexer(store);
            await roslyn.OpenAsync(
                Path.Combine(fixtureRoot, "SampleWpfWindows.sln"));
            await roslyn.IndexAllAsync();

            var projectPath = Path.Combine(
                fixtureRoot,
                "SampleWpfWindows.csproj");
            var rawProjects = roslyn.Workspace!.CurrentSolution.Projects
                .Where(project => ProjectPathMatches(
                    project.FilePath,
                    projectPath))
                .ToArray();
            rawProjects.Should().NotBeEmpty();
            var sanitized = roslyn.SanitizedSolution!;
            var retainedGeneratedSources = rawProjects
                .SelectMany(rawProject =>
                {
                    var sanitizedProject = sanitized.GetProject(rawProject.Id);
                    return rawProject.Documents
                        .Where(document =>
                            sanitizedProject?.GetDocument(document.Id) is not null
                            && SolutionPrivacySanitizer.IsBuildGeneratedDocument(
                                document))
                        .Select(document => Path.GetFileName(document.FilePath));
                })
                .ToArray();
            retainedGeneratedSources.Should().Contain(fileName =>
                fileName.EndsWith("GlobalUsings.g.cs", StringComparison.Ordinal));
            retainedGeneratedSources.Should().Contain("App.g.cs");
            retainedGeneratedSources.Should().Contain("MainWindow.g.cs");
            var retainedAnalyzerConfigs = rawProjects
                .SelectMany(rawProject =>
                {
                    var sanitizedProject = sanitized.GetProject(rawProject.Id);
                    return rawProject.AnalyzerConfigDocuments
                        .Where(document =>
                            sanitizedProject?.GetAnalyzerConfigDocument(
                                document.Id) is not null)
                        .Select(document => document.FilePath);
                })
                .OfType<string>()
                .ToArray();
            retainedAnalyzerConfigs.Should().Contain(path =>
                path.EndsWith(
                    "GeneratedMSBuildEditorConfig.editorconfig",
                    StringComparison.Ordinal));
            retainedAnalyzerConfigs.Should().Contain(path =>
                path.EndsWith(
                    ".globalconfig",
                    StringComparison.Ordinal));
            roslyn.IsProjectSemanticInputComplete(projectPath).Should().BeTrue(
                "SDK, WPF, and analyzer configuration inputs remain in the semantic compilation");
            roslyn.IsProjectXamlPositiveResolutionSafe(projectPath)
                .Should().BeTrue(
                    "the complete Roslyn compilation is authoritative");

            var compilation = await sanitized.GetProject(rawProjects.Single().Id)!
                .GetCompilationAsync();
            compilation.Should().NotBeNull();
            compilation!.GetDiagnostics()
                .Where(diagnostic =>
                    diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                .Should().BeEmpty(
                    "implicit usings and InitializeComponent must resolve as they do in dotnet build");

            await DispatchXamlAsync(
                store,
                fixtureRoot,
                "real-wpf-production",
                new XamlLanguageProjectFactory(
                    () => roslyn.SanitizedSolution,
                    roslyn.IsProjectSemanticInputComplete,
                    roslyn.IsProjectXamlPositiveResolutionSafe));

            var textBox = (await store.FindSymbolsAsync("QueryTextBox"))
                .Single(symbol => symbol.Kind == "xaml-element");
            var targets = await store.ListCalleesAsync(
                textBox.Id,
                limit: 10,
                edgeKind: "binds-path");

            targets.Should().ContainSingle(target =>
                target.CanonicalKey == CanonicalKeys.ForProperty(
                    "SampleWpfWindows.ViewModels.MainViewModel",
                    "QueryText"));

            var runButton = (await store.FindSymbolsAsync("RunButton"))
                .Single(symbol => symbol.Kind == "xaml-element");
            (await store.ListCalleesAsync(
                    runButton.Id,
                    limit: 10,
                    edgeKind: "binds-path"))
                .Should().ContainSingle(target =>
                    target.CanonicalKey == CanonicalKeys.ForProperty(
                        "SampleWpfWindows.ViewModels.MainViewModel",
                        "RunCommand"));
            (await store.ListCalleesAsync(
                    runButton.Id,
                    limit: 10,
                    edgeKind: "handles-event"))
                .Should().ContainSingle(target =>
                    target.CanonicalKey == CanonicalKeys.ForMethod(
                        "SampleWpfWindows.Views.MainWindow",
                        "OnRunClick",
                        new[]
                        {
                            "System.Object",
                            "System.Windows.RoutedEventArgs",
                        }));

            var missing = (await store.FindSymbolsAsync("MissingBinding"))
                .Single(symbol => symbol.Kind == "xaml-element");
            (await store.ListCalleesAsync(
                missing.Id,
                limit: 10,
                edgeKind: "binds-path"))
                .Should().BeEmpty(
                    "the view model does not declare the requested property");
            var outcome = (await store.GetAnnotationsForSymbolAsync(missing.Id))
                .Should().ContainSingle(annotation =>
                    annotation.Flavor == "xaml-binding-finding"
                    && annotation.FullName == "XAMLBINDING001")
                .Subject;
            using var outcomeJson = JsonDocument.Parse(outcome.ArgsJson!);
            outcomeJson.RootElement.GetProperty("reason").GetString()
                .Should().Be("property-not-found");

            (await store.ListGeneratedFilesAsync())
                .Should().NotContain(file =>
                    file.FilePath.EndsWith("GlobalUsings.g.cs", StringComparison.Ordinal)
                    || file.FilePath.EndsWith("App.g.cs", StringComparison.Ordinal)
                    || file.FilePath.EndsWith("MainWindow.g.cs", StringComparison.Ordinal),
                    "build-generated compiler inputs are semantic support, not ordinary search noise");
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // Best-effort cleanup; a failed assertion remains the useful signal.
            }
        }
    }

    [SkippableFact]
    public async Task SlnAndSlnxLoadTheSameCompleteWpfProjectUniverse()
    {
        Skip.IfNot(
            OperatingSystem.IsWindows(),
            "The real WindowsDesktop/WPF solution-format regression runs on Windows.");

        var fixtureRoot = LocateFixture("SampleWpfWindows");
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "sourcegraph-wpf-solution-formats-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var observations = new List<(
                string[] ProjectNames,
                int CompilationErrors,
                int FailedProjects,
                int FailedFiles)>();
            foreach (var extension in new[] { ".sln", ".slnx" })
            {
                await using var store = new SqliteGraphStore(
                    Path.Combine(tempRoot, $"graph-{extension[1..]}.db"));
                await using var roslyn = new RoslynIndexer(store);
                await roslyn.OpenAsync(
                    Path.Combine(
                        fixtureRoot,
                        "SampleWpfWindows" + extension));
                var result = await roslyn.IndexAllAsync();
                observations.Add((
                    roslyn.SanitizedSolution!.Projects
                        .Select(project => project.Name)
                        .OrderBy(name => name, StringComparer.Ordinal)
                        .ToArray(),
                    result.CompilationErrorCount,
                    result.FailedProjects.Count,
                    result.FailedFiles.Count));
            }

            observations.Should().HaveCount(2);
            observations[1].Should().BeEquivalentTo(
                observations[0],
                "the modern .slnx path must preserve the same projects and semantic completeness as .sln");
            observations[0].ProjectNames.Should().ContainSingle()
                .Which.Should().Be("SampleWpfWindows");
            observations[0].CompilationErrors.Should().Be(0);
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // Best-effort cleanup; a failed assertion remains the useful signal.
            }
        }
    }

    [SkippableFact]
    public async Task RealWindowsDesktopCompilationPublishesWpfRiskDiagnostics()
    {
        Skip.IfNot(
            OperatingSystem.IsWindows(),
            "The real WindowsDesktop/WPF risk fixture runs on Windows.");

        var fixtureRoot = LocateFixture("SampleWpfRisksWindows");
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "sourcegraph-real-wpf-risks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            await using var store = new SqliteGraphStore(
                Path.Combine(tempRoot, "graph.db"));
            await using var roslyn = new RoslynIndexer(store);
            await roslyn.OpenAsync(
                Path.Combine(fixtureRoot, "SampleWpfRisksWindows.sln"));

            var result = await roslyn.IndexAllAsync();
            result.FailedFiles.Should().BeEmpty();

            var projectPath = Path.Combine(
                fixtureRoot,
                "SampleWpfRisksWindows.csproj");
            roslyn.IsProjectSemanticInputComplete(projectPath).Should().BeTrue(
                "the real code-only WindowsDesktop project has no omitted generated documents");

            var eventRisks = await store.FindDiagnosticsAsync(
                severity: null,
                code: "WPFEVENT001",
                symbolId: null);
            eventRisks.Should().ContainSingle(diagnostic =>
                diagnostic.SymbolFqn != null
                && diagnostic.SymbolFqn.Contains(
                    "SampleWpfRisksWindows.Subscriber.Attach",
                    StringComparison.Ordinal)
                && diagnostic.Message.Contains(
                    "AppLifetime.Changed",
                    StringComparison.Ordinal));

            var threadRisks = await store.FindDiagnosticsAsync(
                severity: null,
                code: "WPFTHREAD001",
                symbolId: null);
            threadRisks.Should().ContainSingle(diagnostic =>
                diagnostic.SymbolFqn != null
                && diagnostic.SymbolFqn.Contains(
                    "SampleWpfRisksWindows.Worker.Run",
                    StringComparison.Ordinal)
                && diagnostic.Message.Contains(
                    "View.Text",
                    StringComparison.Ordinal));
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // Best-effort cleanup; a failed assertion remains the useful signal.
            }
        }
    }

    private static async Task DispatchXamlAsync(
        SqliteGraphStore store,
        string fixtureRoot,
        string scopeId,
        XamlLanguageProjectFactory factory)
    {
        var languages = new LanguageIndexerRegistry();
        languages.Register(new XamlLanguageIndexer());
        var factories = new LanguageProjectFactoryRegistry();
        factories.Register(factory);
        var dispatcher = new LanguageIndexerDispatcher(
            languages,
            factories);

        var projectMap = new Dictionary<string, ILanguageProject>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var project in await factory.DiscoverAsync(
                     fixtureRoot,
                     default))
        {
            foreach (var filePath in project.FilePaths)
            {
                projectMap.TryAdd(filePath, project);
            }
        }

        await dispatcher.DispatchAllForTestAsync(
            store,
            scopeId,
            fixtureRoot,
            projectMap);
    }

    private static bool ProjectPathMatches(
        string? candidatePath,
        string expectedPath) =>
        !string.IsNullOrWhiteSpace(candidatePath)
        && string.Equals(
            Path.GetFullPath(candidatePath),
            Path.GetFullPath(expectedPath),
            StringComparison.OrdinalIgnoreCase);

    private static string ProjectProperty(
        XDocument project,
        string propertyName) =>
        project.Descendants(propertyName).Single().Value.Trim();

    private static string LocateFixture(string fixtureName)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "tests",
                "fixtures",
                fixtureName);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not locate tests/fixtures/{fixtureName} from "
            + AppContext.BaseDirectory);
    }
}
