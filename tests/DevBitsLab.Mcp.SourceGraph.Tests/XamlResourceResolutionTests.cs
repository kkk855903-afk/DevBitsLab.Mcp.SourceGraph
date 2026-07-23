using System.Text.Json;
using DevBitsLab.Mcp.SourceGraph.Indexing.Xaml;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class XamlResourceResolutionTests
{
    [Fact]
    public async Task LocalResourcesResolveInSecondPassAndRetainLookupKinds()
    {
        var root = CreateTempDirectory();
        try
        {
            var viewPath = Path.Combine(root, "View.xaml");
            var source = """
                <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                    <Window.Resources>
                        <SolidColorBrush x:Key="Accent" Color="Blue" />
                        <ObjectDataProvider x:Key="Converter" />
                    </Window.Resources>
                    <Border x:Name="StaticConsumer"
                            Background="{StaticResource Accent}" />
                    <Border x:Name="DynamicConsumer"
                            Background="{DynamicResource Accent}" />
                    <TextBlock x:Name="NestedConsumer"
                               Text="{Binding Name, Converter={StaticResource Converter}}" />
                </Window>
                """;
            await File.WriteAllTextAsync(viewPath, source);

            var events = await new XamlLanguageIndexer().IndexAsync(
                new IndexContext(
                    viewPath,
                    await File.ReadAllBytesAsync(viewPath),
                    "test",
                    root),
                default);

            events.OfType<IndexEvent.SymbolDeclared>().Should().Contain(symbol =>
                symbol.CanonicalKey == "xaml:resource:View.xaml#Accent");
            var resourceEdges = events.OfType<IndexEvent.EdgeEmitted>()
                .Where(edge => edge.EdgeKindName == "uses-resource")
                .ToList();
            resourceEdges.Should().Contain(edge =>
                edge.SourceCanonicalKey.EndsWith("#StaticConsumer", StringComparison.Ordinal)
                && edge.TargetCanonicalKey == "xaml:resource:View.xaml#Accent"
                && edge.Metadata!["resource-lookup"] == "static");
            resourceEdges.Should().Contain(edge =>
                edge.SourceCanonicalKey.EndsWith("#DynamicConsumer", StringComparison.Ordinal)
                && edge.TargetCanonicalKey == "xaml:resource:View.xaml#Accent"
                && edge.Metadata!["resource-lookup"] == "dynamic");
            resourceEdges.Should().Contain(edge =>
                edge.SourceCanonicalKey.EndsWith("#NestedConsumer", StringComparison.Ordinal)
                && edge.TargetCanonicalKey == "xaml:resource:View.xaml#Converter"
                && edge.Metadata!["resource-lookup"] == "static");
            resourceEdges.Should().OnlyContain(edge =>
                edge.Evidence != null
                && edge.Evidence.Confidence == EvidenceConfidence.Exact
                && edge.Evidence.Producer == "xaml-resource");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task DuplicateMergedResourcesProduceStructuredAmbiguityWithoutEdge()
    {
        var root = CreateTempDirectory();
        try
        {
            await WriteProjectAsync(root);
            await File.WriteAllTextAsync(
                Path.Combine(root, "App.xaml"),
                AppWithMergedDictionaries("A.xaml", "B.xaml"));
            await File.WriteAllTextAsync(
                Path.Combine(root, "A.xaml"),
                ResourceDictionary("Duplicate", "Red"));
            await File.WriteAllTextAsync(
                Path.Combine(root, "B.xaml"),
                ResourceDictionary("Duplicate", "Blue"));
            var viewPath = Path.Combine(root, "View.xaml");
            await File.WriteAllTextAsync(
                viewPath,
                ConsumerView("Duplicate"));

            var project = await DiscoverSingleProjectAsync(root);
            var resolution = project.ResolveResource("Duplicate");
            resolution.Status.Should().Be(ResourceResolutionStatus.Ambiguous);
            resolution.Candidates.Should().HaveCount(2);
            project.Should().BeAssignableTo<IDeclarationFirstLanguageProject>();
            project.DeclarationFilePaths
                .Select(Path.GetFileName)
                .Should().Equal("A.xaml", "B.xaml");

            var events = await new XamlLanguageIndexer().IndexAsync(
                new IndexContext(
                    viewPath,
                    await File.ReadAllBytesAsync(viewPath),
                    "test",
                    root,
                    project),
                default);
            events.OfType<IndexEvent.EdgeEmitted>()
                .Should().NotContain(edge => edge.EdgeKindName == "uses-resource");
            events.OfType<IndexEvent.SymbolDeclared>()
                .Should().NotContain(symbol =>
                    symbol.CanonicalKey.Contains("__unresolved", StringComparison.Ordinal));

            var finding = events.OfType<IndexEvent.AnnotationAttached>()
                .Should().ContainSingle(annotation =>
                    annotation.Flavor == "xaml-resource-finding"
                    && annotation.FullName == "XAMLRESOURCE002"
                    && annotation.AnnotationName == "Resource不明确").Subject;
            using var json = JsonDocument.Parse(finding.ArgsJson!);
            json.RootElement.GetProperty("candidateCount").GetInt32().Should().Be(2);
            json.RootElement.GetProperty("candidates").GetArrayLength().Should().Be(2);
            json.RootElement.GetProperty("confidence").GetString().Should().Be("exact");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task ExplicitResourceCacheRebuildAtomicallyReflectsFileEdit()
    {
        var root = CreateTempDirectory();
        try
        {
            await WriteProjectAsync(root);
            await File.WriteAllTextAsync(
                Path.Combine(root, "App.xaml"),
                AppWithMergedDictionaries("Shared.xaml"));
            var sharedPath = Path.Combine(root, "Shared.xaml");
            await File.WriteAllTextAsync(
                sharedPath,
                ResourceDictionary("Before", "Red"));

            var project = await DiscoverSingleProjectAsync(root);
            project.ResolveResource("Before").Status.Should()
                .Be(ResourceResolutionStatus.Resolved);
            project.ResolveResource("After").Status.Should()
                .Be(ResourceResolutionStatus.Missing);

            await File.WriteAllTextAsync(
                sharedPath,
                ResourceDictionary("After", "Green"));
            project.RebuildResourceCache();

            project.ResolveResource("Before").Status.Should()
                .Be(ResourceResolutionStatus.Missing);
            project.ResolveResource("After").Status.Should()
                .Be(ResourceResolutionStatus.Resolved);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task PrivacyExcludedMergedDictionaryNeverEntersResourceCatalog()
    {
        var root = CreateTempDirectory();
        try
        {
            await WriteProjectAsync(root);
            await File.WriteAllTextAsync(
                Path.Combine(root, "App.xaml"),
                AppWithMergedDictionaries("PatientData/Secret.xaml"));
            var privateDirectory = Path.Combine(root, "PatientData");
            Directory.CreateDirectory(privateDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(privateDirectory, "Secret.xaml"),
                ResourceDictionary("PrivateCanary", "Red"));

            var project = await DiscoverSingleProjectAsync(root);

            project.ResolveResource("PrivateCanary").Status.Should()
                .Be(ResourceResolutionStatus.Missing);
            project.FilePaths.Should().NotContain(path =>
                path.Contains("PatientData", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static async Task<XamlLanguageProject> DiscoverSingleProjectAsync(string root)
    {
        var projects = await new XamlLanguageProjectFactory().DiscoverAsync(root, default);
        return projects.Should().ContainSingle().Subject.Should()
            .BeOfType<XamlLanguageProject>().Subject;
    }

    private static Task WriteProjectAsync(string root) =>
        File.WriteAllTextAsync(
            Path.Combine(root, "Fixture.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />");

    private static string AppWithMergedDictionaries(params string[] sources)
    {
        var dictionaries = string.Join(
            Environment.NewLine,
            sources.Select(source =>
                $"            <ResourceDictionary Source=\"{source}\" />"));
        return $$"""
            <Application xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <Application.Resources>
                    <ResourceDictionary>
                        <ResourceDictionary.MergedDictionaries>
            {{dictionaries}}
                        </ResourceDictionary.MergedDictionaries>
                    </ResourceDictionary>
                </Application.Resources>
            </Application>
            """;
    }

    private static string ResourceDictionary(string key, string color) => $$"""
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            <SolidColorBrush x:Key="{{key}}" Color="{{color}}" />
        </ResourceDictionary>
        """;

    private static string ConsumerView(string key) => $$"""
        <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            <Border x:Name="Consumer"
                    Background="{StaticResource {{key}}}" />
        </Window>
        """;

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "sourcegraph-xaml-resources-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort fixture cleanup.
        }
    }
}
