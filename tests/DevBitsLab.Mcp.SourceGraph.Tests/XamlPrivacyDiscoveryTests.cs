using DevBitsLab.Mcp.SourceGraph.Indexing.Xaml;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class XamlPrivacyDiscoveryTests : IDisposable
{
    private readonly string _root =
        Path.Join(Path.GetTempPath(), "sourcegraph-xaml-privacy-" + Guid.NewGuid().ToString("N"));

    public XamlPrivacyDiscoveryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Discover_prunesExcludedProjectsAndXamlFiles()
    {
        var projectDir = Path.Join(_root, "ManagedApp");
        var projectPath = await PlantAsync(
            Path.Join(projectDir, "ManagedApp.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"></Project>""");
        var mainView = await PlantAsync(
            Path.Join(projectDir, "Views", "MainWindow.xaml"),
            """<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" />""");
        var prefixBoundaryView = await PlantAsync(
            Path.Join(projectDir, "PatientDataModels", "Editor.xaml"),
            """<UserControl xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" />""");

        await PlantAsync(
            Path.Join(projectDir, "PatientData", "PatientView.xaml"),
            """<Window Tag="PATIENT-CANARY" />""");
        await PlantAsync(
            Path.Join(projectDir, "Images", "ImageViewer.xaml"),
            """<Window Tag="IMAGE-CANARY" />""");
        await PlantAsync(
            Path.Join(_root, "PatientData", "Hidden.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"></Project>""");
        await PlantAsync(
            Path.Join(_root, "PatientData", "Hidden.xaml"),
            """<Window Tag="HIDDEN-PROJECT-CANARY" />""");

        var projects = await new XamlLanguageProjectFactory().DiscoverAsync(_root, default);

        projects.Should().ContainSingle();
        projects[0].Id.Should().Be(projectPath);
        projects[0].FilePaths.Should().BeEquivalentTo(new[] { mainView, prefixBoundaryView });
    }

    [Fact]
    public async Task Discover_appliesScopeExcludes_beforeProjectAndXamlDiscovery()
    {
        var projectDir = Path.Join(_root, "ManagedApp");
        var projectPath = await PlantAsync(
            Path.Join(projectDir, "ManagedApp.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"></Project>""");
        var mainView = await PlantAsync(
            Path.Join(projectDir, "Views", "MainWindow.xaml"),
            """<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" />""");
        await PlantAsync(
            Path.Join(projectDir, "Generated", "GeneratedView.xaml"),
            """<Window Tag="SCOPE-EXCLUDE-CANARY" />""");
        await PlantAsync(
            Path.Join(_root, "Generated", "Hidden.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"></Project>""");
        await PlantAsync(
            Path.Join(_root, "Generated", "Hidden.xaml"),
            """<Window Tag="HIDDEN-SCOPE-PROJECT-CANARY" />""");

        var factory = (IExclusionAwareLanguageProjectFactory)new XamlLanguageProjectFactory();
        var projects = await factory.DiscoverAsync(
            _root,
            ["**/generated/**"],
            CancellationToken.None);

        projects.Should().ContainSingle();
        projects[0].Id.Should().Be(projectPath);
        projects[0].FilePaths.Should().Equal(mainView);
    }

    private static async Task<string> PlantAsync(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, contents);
        return path;
    }
}
