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
    public async Task RealWpfProductionIndexResolvesPositiveBindingsWithoutTrustingObj()
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
            var filteredGeneratedSources = rawProjects
                .SelectMany(rawProject =>
                {
                    var sanitizedProject = sanitized.GetProject(rawProject.Id);
                    return rawProject.Documents
                        .Where(document =>
                            sanitizedProject?.GetDocument(document.Id) is null)
                        .Select(document => Path.GetFileName(document.FilePath));
                })
                .ToArray();
            filteredGeneratedSources.Should().Contain("App.g.cs");
            filteredGeneratedSources.Should().Contain("MainWindow.g.cs");
            roslyn.IsProjectSemanticInputComplete(projectPath).Should().BeFalse(
                "privacy-filtered WPF generated sources are part of the raw compiler input");
            roslyn.IsProjectXamlPositiveResolutionSafe(projectPath)
                .Should().BeTrue(
                    "Roslyn build provenance permits direct positive facts without making absence authoritative");

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
                .Should().BeEmpty(
                    "positive-only safety does not authorize event-handler inference");

            var missing = (await store.FindSymbolsAsync("MissingBinding"))
                .Single(symbol => symbol.Kind == "xaml-element");
            (await store.ListCalleesAsync(
                    missing.Id,
                    limit: 10,
                    edgeKind: "binds-path"))
                .Should().BeEmpty(
                    "an omitted build output never authorizes a negative binding claim");
            var outcome = (await store.GetAnnotationsForSymbolAsync(missing.Id))
                .Should().ContainSingle(annotation =>
                    annotation.Flavor == "xaml-binding-outcome"
                    && annotation.FullName == "incomplete")
                .Subject;
            using var outcomeJson = JsonDocument.Parse(outcome.ArgsJson!);
            outcomeJson.RootElement.GetProperty("reason").GetString()
                .Should().Be("compilation-has-errors");
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
