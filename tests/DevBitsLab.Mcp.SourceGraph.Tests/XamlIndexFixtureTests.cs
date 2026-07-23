using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Indexing.Xaml;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Server.Plugins;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Xunit;
using CoreEvidenceConfidence = DevBitsLab.Mcp.SourceGraph.Core.EvidenceConfidence;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// End-to-end smoke tests for the XAML indexer over the SampleWpf and SampleAvalonia fixtures.
/// Cold-indexes the C# side via Roslyn, then dispatches the .xaml files via the
/// <see cref="LanguageIndexerDispatcher"/>'s test-only overload, then queries the resulting
/// graph for the cross-language joins documented in the spec.
/// </summary>
public sealed class XamlIndexFixtureTests : IAsyncLifetime
{
    private string _wpfDbPath = string.Empty;
    private string _avaloniaDbPath = string.Empty;
    private SqliteGraphStore? _wpfStore;
    private SqliteGraphStore? _avaloniaStore;
    private string _wpfRoot = string.Empty;
    private string _avaloniaRoot = string.Empty;

    public async Task InitializeAsync()
    {
        _wpfRoot = LocateFixture("SampleWpf");
        _avaloniaRoot = LocateFixture("SampleAvalonia");

        var (wpfStore, wpfDb) = await IndexFixtureAsync(_wpfRoot, "SampleWpf.sln");
        _wpfStore = wpfStore;
        _wpfDbPath = wpfDb;

        var (avaloniaStore, avaloniaDb) = await IndexFixtureAsync(_avaloniaRoot, "SampleAvalonia.sln");
        _avaloniaStore = avaloniaStore;
        _avaloniaDbPath = avaloniaDb;
    }

    public async Task DisposeAsync()
    {
        if (_wpfStore is not null) await _wpfStore.DisposeAsync();
        if (_avaloniaStore is not null) await _avaloniaStore.DisposeAsync();
        SafeDeleteWithDir(_wpfDbPath);
        SafeDeleteWithDir(_avaloniaDbPath);
    }

    [Fact]
    public async Task SampleWpf_emitsXamlViewForMainWindow()
    {
        var hits = await _wpfStore!.FindSymbolsAsync("Views/MainWindow.xaml");
        hits.Should().Contain(h => h.Kind == "xaml-view"
            && h.Fqn.Contains("Views/MainWindow.xaml", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SampleWpf_codeBehindEdgeResolvesToCSharpType()
    {
        var view = (await _wpfStore!.FindSymbolsAsync("Views/MainWindow.xaml"))
            .First(h => h.Kind == "xaml-view");
        var callees = await _wpfStore.ListCalleesAsync(view.Id, limit: 50, edgeKind: "code-behind");
        callees.Should().Contain(c => c.Fqn.Contains("SampleWpf.Views.MainWindow", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SampleWpf_handlesEventEdgeResolvesToCSharpMethod()
    {
        // The Click="OnSave" attribute lives on the SaveButton element; emission attaches the edge
        // to that element symbol, so we look it up by name (not by walking from the view root).
        var saveButton = (await _wpfStore!.FindSymbolsAsync("SaveButton"))
            .FirstOrDefault(h => h.Kind == "xaml-element");
        saveButton.Should().NotBeNull();
        var callees = await _wpfStore.ListCalleesAsync(saveButton!.Id, limit: 50, edgeKind: "handles-event");
        var handler = callees.Should().ContainSingle(c =>
            c.CanonicalKey == CanonicalKeys.ForMethod(
                "SampleWpf.Views.MainWindow",
                "OnSave",
                new[] { "System.Object", "System.EventArgs" })).Subject;
        handler.Fqn.Contains(
            "SampleWpf.Views.MainWindow.OnSave",
            StringComparison.Ordinal).Should().BeTrue();

        var evidence = await _wpfStore.ListEdgeEvidenceAsync(
            saveButton.Id,
            handler.Id,
            "handles-event");
        evidence.Should().ContainSingle();
        evidence[0].Confidence.Should().Be(CoreEvidenceConfidence.Exact);
        evidence[0].Producer.Should().Be("xaml-semantic");
    }

    [Fact]
    public async Task SampleWpf_dataContextBindingResolvesToRealTerminalPropertyWithExactEvidence()
    {
        var box = (await _wpfStore!.FindSymbolsAsync("UserNameBox"))
            .First(h => h.Kind == "xaml-element");
        var callees = await _wpfStore.ListCalleesAsync(box.Id, limit: 50, edgeKind: "binds-path");
        var nameProperty = callees.Should().ContainSingle(c =>
            c.CanonicalKey == CanonicalKeys.ForProperty("SampleWpf.ViewModels.User", "Name")).Subject;
        var withPayload = nameProperty;
        var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(withPayload.PayloadJson!);
        dict.Should().ContainKey("path").WhoseValue.Should().Be("User.Name");
        dict.Should().ContainKey("mode").WhoseValue.Should().Be("two-way");
        dict.Should().ContainKey("data-type").WhoseValue.Should()
            .Be(CanonicalKeys.ForType("SampleWpf.ViewModels.MainViewModel"));

        var evidence = await _wpfStore.ListEdgeEvidenceAsync(
            box.Id,
            nameProperty.Id,
            "binds-path");
        evidence.Should().ContainSingle();
        evidence[0].Confidence.Should().Be(CoreEvidenceConfidence.Exact);
        evidence[0].Producer.Should().Be("xaml-semantic");
        evidence[0].Location.FilePath.Replace('\\', '/').Should()
            .EndWith("Views/MainWindow.xaml");
        evidence[0].Location.StartLine.Should().BeGreaterThan(0);
        evidence[0].Location.EndColumn.Should().BeGreaterThan(evidence[0].Location.StartColumn);
        var evidenceLine = File.ReadLines(evidence[0].Location.FilePath)
            .ElementAt(evidence[0].Location.StartLine - 1);
        evidenceLine.Substring(
            evidence[0].Location.StartColumn - 1,
            evidence[0].Location.EndColumn - evidence[0].Location.StartColumn)
            .Should().Be("Text");
    }

    [Fact]
    public async Task SampleWpf_xDataTypeBindingResolvesToRealProperty()
    {
        var emailProperty = (await _wpfStore!.FindSymbolsAsync("Email"))
            .First(h => h.CanonicalKey == CanonicalKeys.ForProperty("SampleWpf.ViewModels.User", "Email"));
        var callers = await _wpfStore.ListCallersAsync(
            emailProperty.Id,
            limit: 50,
            edgeKind: "binds-path");

        callers.Should().ContainSingle(c =>
            c.Kind == "xaml-element"
            && c.FilePath.Replace('\\', '/').EndsWith(
                "Views/UserView.xaml",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SampleWpf_commandBindingResolvesOnlyICommandProperty()
    {
        var saveButton = (await _wpfStore!.FindSymbolsAsync("SaveButton"))
            .First(h => h.Kind == "xaml-element");
        var commandTargets = await _wpfStore.ListCalleesAsync(
            saveButton.Id,
            limit: 50,
            edgeKind: "binds-path");

        commandTargets.Should().ContainSingle(c =>
            c.CanonicalKey == CanonicalKeys.ForProperty(
                "SampleWpf.ViewModels.MainViewModel",
                "SaveCommand"));

        var invalidButton = (await _wpfStore.FindSymbolsAsync("InvalidCommandButton"))
            .First(h => h.Kind == "xaml-element");
        (await _wpfStore.ListCalleesAsync(
            invalidButton.Id,
            limit: 50,
            edgeKind: "binds-path")).Should().BeEmpty(
                "a Command binding to a non-ICommand property must not be guessed as valid");
    }

    [Fact]
    public async Task SampleWpf_unresolvedBindingDoesNotCreateSyntheticTarget()
    {
        var broken = (await _wpfStore!.FindSymbolsAsync("BrokenBinding"))
            .First(h => h.Kind == "xaml-element");

        (await _wpfStore.ListCalleesAsync(
            broken.Id,
            limit: 50,
            edgeKind: "binds-path")).Should().BeEmpty();
        (await _wpfStore.FindSymbolsAsync("binding-target")).Should().BeEmpty(
            "unresolved paths must degrade without manufacturing symbols");
    }

    [Theory]
    [InlineData("RelativeSourceText")]
    [InlineData("ExplicitSourceText")]
    [InlineData("CompiledBindingText")]
    public async Task SampleWpf_unsupportedBindingSourcesDoNotReuseInheritedDataContext(
        string elementName)
    {
        var element = (await _wpfStore!.FindSymbolsAsync(elementName))
            .First(h => h.Kind == "xaml-element");

        (await _wpfStore.ListCalleesAsync(
            element.Id,
            limit: 50,
            edgeKind: "binds-path")).Should().BeEmpty(
                "explicit, ElementName, RelativeSource, and compiled bindings require their own source-type resolver");
    }

    [Fact]
    public async Task SampleWpf_attachedPropertyAnnotationEmitted()
    {
        var box = (await _wpfStore!.FindSymbolsAsync("UserNameBox"))
            .First(h => h.Kind == "xaml-element");
        var anns = await _wpfStore.GetAnnotationsForSymbolAsync(box.Id);
        anns.Should().Contain(a => a.Flavor == "xaml-attached-property" && a.Name == "Grid.Row");
    }

    [Fact]
    public async Task SampleWpf_findReferencesOnCSharpTypeReturnsXamlView()
    {
        // Cross-language join: list_callers on the C# class with edge_kind = "code-behind" must
        // return the XAML view symbol. This is the spec scenario "find me the codebehind for this
        // view" run in reverse — the view points at the type, so callers of the type via the
        // code-behind edge are the views.
        var typeSymbol = (await _wpfStore!.FindSymbolsAsync("SampleWpf.Views.MainWindow"))
            .First(h => h.Kind == "class");
        var inbound = await _wpfStore.ListCallersAsync(typeSymbol.Id, limit: 50, edgeKind: "code-behind");
        inbound.Should().Contain(i => i.Kind == "xaml-view");
    }

    [Fact]
    public async Task SampleWpf_resourceCacheResolvesAccentBrush()
    {
        // The SaveButton uses Background="{StaticResource AccentBrush}". The XAML factory's
        // resource cache walked App.xaml; the indexer should have emitted a uses-resource edge.
        var saveButton = (await _wpfStore!.FindSymbolsAsync("SaveButton"))
            .First(h => h.Kind == "xaml-element");
        var callees = await _wpfStore.ListCalleesAsync(saveButton.Id, limit: 50, edgeKind: "uses-resource");
        callees.Should().Contain(c => c.Fqn.Contains("AccentBrush", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SampleAvalonia_profileDetectedFromAvaloniaNamespace()
    {
        // Avalonia's profile runs the same dispatcher; smoke is that the indexer didn't crash and
        // the same xaml-view + code-behind edge land for the Avalonia fixture root.
        var view = (await _avaloniaStore!.FindSymbolsAsync("Views/MainWindow.xaml"))
            .First(h => h.Kind == "xaml-view");
        var callees = await _avaloniaStore.ListCalleesAsync(view.Id, limit: 50, edgeKind: "code-behind");
        callees.Should().Contain(c => c.Fqn.Contains("SampleAvalonia.Views.MainWindow", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SampleAvalonia_attachedPropertyEmittedUnderXamlFlavor()
    {
        var box = (await _avaloniaStore!.FindSymbolsAsync("UserNameBox"))
            .First(h => h.Kind == "xaml-element");
        var anns = await _avaloniaStore.GetAnnotationsForSymbolAsync(box.Id);
        anns.Should().Contain(a => a.Flavor == "xaml-attached-property" && a.Name == "Grid.Row");
    }

    private static async Task<(SqliteGraphStore Store, string DbPath)> IndexFixtureAsync(string fixtureRoot, string slnName)
    {
        var slnPath = Path.Combine(fixtureRoot, slnName);
        var tmp = Path.Combine(Path.GetTempPath(), "sourcegraph-xaml-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        var dbPath = Path.Combine(tmp, "graph.db");
        var store = new SqliteGraphStore(dbPath);
        await using var roslyn = new RoslynIndexer(store);
        await roslyn.OpenAsync(slnPath);
        await roslyn.IndexAllAsync();

        var langRegistry = new LanguageIndexerRegistry();
        langRegistry.Register(new XamlLanguageIndexer());
        var factories = new LanguageProjectFactoryRegistry();
        factories.Register(new XamlLanguageProjectFactory(() => roslyn.SanitizedSolution));
        var dispatcher = new LanguageIndexerDispatcher(langRegistry, factories);

        var projectMap = new Dictionary<string, ILanguageProject>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in factories.All())
        {
            var projects = await f.DiscoverAsync(fixtureRoot, default);
            foreach (var p in projects)
            {
                foreach (var path in p.FilePaths)
                {
                    if (!projectMap.ContainsKey(path)) projectMap[path] = p;
                }
            }
        }
        await dispatcher.DispatchAllForTestAsync(store, "test", fixtureRoot, projectMap);
        return (store, dbPath);
    }

    private static string LocateFixture(string fixtureName)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "tests", "fixtures", fixtureName);
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("Could not locate tests/fixtures/" + fixtureName + " from " + AppContext.BaseDirectory);
    }

    private static void SafeDeleteWithDir(string path)
    {
        // Best-effort cleanup of both the SQLite DB file AND its parent temp directory so the
        // sourcegraph-xaml-tests-* folders don't accumulate on developer machines / CI agents.
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch { /* best-effort */ }
    }
}
