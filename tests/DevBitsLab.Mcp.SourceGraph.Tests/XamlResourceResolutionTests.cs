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
                edge.SourceCanonicalKey.EndsWith("#NestedConsumer", StringComparison.Ordinal)
                && edge.TargetCanonicalKey == "xaml:resource:View.xaml#Converter"
                && edge.Metadata!["resource-lookup"] == "static");
            resourceEdges.Should().ContainSingle(edge =>
                edge.SourceCanonicalKey.EndsWith("#DynamicConsumer", StringComparison.Ordinal)
                && edge.TargetCanonicalKey == "xaml:resource:View.xaml#Accent"
                && edge.Metadata!["resource-lookup"] == "dynamic");
            resourceEdges.Should().OnlyContain(edge =>
                edge.Evidence != null
                && edge.Evidence.Confidence == EvidenceConfidence.Exact
                && edge.Evidence.Producer == "xaml-resource");
            events.OfType<IndexEvent.AnnotationAttached>().Should().NotContain(annotation =>
                annotation.Flavor == "xaml-resource-outcome");
            events.OfType<IndexEvent.AnnotationAttached>().Should().NotContain(annotation =>
                annotation.Flavor == "xaml-resource-finding");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task AttachedPropertyMarkupExtensionStillResolvesResource()
    {
        var root = CreateTempDirectory();
        try
        {
            var viewPath = Path.Combine(root, "View.xaml");
            await File.WriteAllTextAsync(
                viewPath,
                """
                <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                    <Window.Resources>
                        <ControlTemplate x:Key="ErrorTemplate" />
                    </Window.Resources>
                    <TextBox x:Name="AttachedConsumer"
                             Validation.ErrorTemplate="{StaticResource ErrorTemplate}" />
                </Window>
                """);

            var events = await new XamlLanguageIndexer().IndexAsync(
                new IndexContext(
                    viewPath,
                    await File.ReadAllBytesAsync(viewPath),
                    "test",
                    root),
                default);

            events.OfType<IndexEvent.AnnotationAttached>().Should().Contain(
                annotation =>
                    annotation.SymbolCanonicalKey.EndsWith(
                        "#AttachedConsumer",
                        StringComparison.Ordinal)
                    && annotation.Flavor == "xaml-attached-property");
            events.OfType<IndexEvent.EdgeEmitted>().Should().ContainSingle(edge =>
                edge.SourceCanonicalKey.EndsWith(
                    "#AttachedConsumer",
                    StringComparison.Ordinal)
                && edge.EdgeKindName == "uses-resource"
                && edge.TargetCanonicalKey
                    == "xaml:template:View.xaml#ErrorTemplate");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task StaticResourceWithoutLiteralKeyEmitsUnsupportedOutcome()
    {
        var root = CreateTempDirectory();
        try
        {
            var viewPath = Path.Combine(root, "View.xaml");
            await File.WriteAllTextAsync(
                viewPath,
                """
                <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                    <Border x:Name="EmptyKey"
                            Background="{StaticResource}" />
                </Window>
                """);

            var events = await new XamlLanguageIndexer().IndexAsync(
                new IndexContext(
                    viewPath,
                    await File.ReadAllBytesAsync(viewPath),
                    "test",
                    root),
                default);

            var outcome = events.OfType<IndexEvent.AnnotationAttached>()
                .Should().ContainSingle(annotation =>
                    annotation.SymbolCanonicalKey.EndsWith(
                        "#EmptyKey",
                        StringComparison.Ordinal)
                    && annotation.Flavor == "xaml-resource-outcome")
                .Subject;
            outcome.FullName.Should().Be("unsupported");
            using var json = JsonDocument.Parse(outcome.ArgsJson!);
            json.RootElement.GetProperty("reason").GetString().Should()
                .Be("resource-key-is-empty");
            events.OfType<IndexEvent.EdgeEmitted>().Should().NotContain(edge =>
                edge.SourceCanonicalKey.EndsWith(
                    "#EmptyKey",
                    StringComparison.Ordinal)
                && (edge.EdgeKindName == "uses-resource"
                    || edge.EdgeKindName == "applies-style"));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task SiblingResourceScopeDoesNotResolveUnrelatedConsumer()
    {
        var root = CreateTempDirectory();
        try
        {
            var viewPath = Path.Combine(root, "View.xaml");
            await File.WriteAllTextAsync(
                viewPath,
                """
                <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                    <StackPanel>
                        <StackPanel.Resources>
                            <SolidColorBrush x:Key="Accent" Color="Blue" />
                        </StackPanel.Resources>
                    </StackPanel>
                    <Border x:Name="Sibling"
                            Background="{StaticResource Accent}" />
                </Window>
                """);

            var events = await new XamlLanguageIndexer().IndexAsync(
                new IndexContext(
                    viewPath,
                    await File.ReadAllBytesAsync(viewPath),
                    "test",
                    root),
                default);

            events.OfType<IndexEvent.EdgeEmitted>().Should().NotContain(edge =>
                edge.EdgeKindName == "uses-resource"
                && edge.SourceCanonicalKey.EndsWith(
                    "#Sibling",
                    StringComparison.Ordinal));
            var outcome = events.OfType<IndexEvent.AnnotationAttached>()
                .Should().ContainSingle(annotation =>
                    annotation.SymbolCanonicalKey.EndsWith(
                        "#Sibling",
                        StringComparison.Ordinal)
                    && annotation.Flavor == "xaml-resource-outcome").Subject;
            outcome.FullName.Should().Be("unknown");
            events.OfType<IndexEvent.AnnotationAttached>().Should().NotContain(annotation =>
                annotation.SymbolCanonicalKey.EndsWith(
                    "#Sibling",
                    StringComparison.Ordinal)
                && annotation.Flavor == "xaml-resource-finding");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task SiblingScopesWithSameKeyEmitDistinctDeclarationsAndExactTargets()
    {
        var root = CreateTempDirectory();
        try
        {
            var viewPath = Path.Combine(root, "View.xaml");
            await File.WriteAllTextAsync(
                viewPath,
                """
                <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                    <StackPanel>
                        <StackPanel.Resources>
                            <SolidColorBrush x:Key="Accent" Color="Blue" />
                        </StackPanel.Resources>
                        <Border x:Name="FirstConsumer"
                                Background="{StaticResource Accent}" />
                    </StackPanel>
                    <StackPanel>
                        <StackPanel.Resources>
                            <SolidColorBrush x:Key="Accent" Color="Green" />
                        </StackPanel.Resources>
                        <Border x:Name="SecondConsumer"
                                Background="{StaticResource Accent}" />
                    </StackPanel>
                </Window>
                """);

            var events = await new XamlLanguageIndexer().IndexAsync(
                new IndexContext(
                    viewPath,
                    await File.ReadAllBytesAsync(viewPath),
                    "test",
                    root),
                default);

            var declarations = events.OfType<IndexEvent.SymbolDeclared>()
                .Where(symbol =>
                    symbol.Kind == "xaml-resource"
                    && symbol.CanonicalKey.StartsWith(
                        "xaml:resource:View.xaml#Accent",
                        StringComparison.Ordinal))
                .OrderBy(symbol => symbol.StartLine)
                .ToList();
            declarations.Should().HaveCount(2);
            declarations.Select(symbol => symbol.CanonicalKey).Should()
                .OnlyHaveUniqueItems();
            declarations.Should().OnlyContain(symbol =>
                symbol.CanonicalKey.StartsWith(
                    "xaml:resource:View.xaml#Accent@L",
                    StringComparison.Ordinal));

            var edges = events.OfType<IndexEvent.EdgeEmitted>()
                .Where(edge => edge.EdgeKindName == "uses-resource")
                .ToList();
            edges.Should().ContainSingle(edge =>
                edge.SourceCanonicalKey.EndsWith(
                    "#FirstConsumer",
                    StringComparison.Ordinal)
                && edge.TargetCanonicalKey == declarations[0].CanonicalKey);
            edges.Should().ContainSingle(edge =>
                edge.SourceCanonicalKey.EndsWith(
                    "#SecondConsumer",
                    StringComparison.Ordinal)
                && edge.TargetCanonicalKey == declarations[1].CanonicalKey);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task InlineLocalMergedDictionaryResolvesWithinOwningScope()
    {
        var root = CreateTempDirectory();
        try
        {
            var viewPath = Path.Combine(root, "View.xaml");
            await File.WriteAllTextAsync(
                viewPath,
                """
                <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                    <Window.Resources>
                        <ResourceDictionary>
                            <ResourceDictionary.MergedDictionaries>
                                <ResourceDictionary>
                                    <SolidColorBrush x:Key="Accent" Color="Blue" />
                                </ResourceDictionary>
                            </ResourceDictionary.MergedDictionaries>
                        </ResourceDictionary>
                    </Window.Resources>
                    <Border x:Name="Consumer"
                            Background="{StaticResource Accent}" />
                </Window>
                """);

            var events = await new XamlLanguageIndexer().IndexAsync(
                new IndexContext(
                    viewPath,
                    await File.ReadAllBytesAsync(viewPath),
                    "test",
                    root),
                default);

            events.OfType<IndexEvent.EdgeEmitted>().Should().ContainSingle(edge =>
                edge.EdgeKindName == "uses-resource"
                && edge.SourceCanonicalKey.EndsWith(
                    "#Consumer",
                    StringComparison.Ordinal)
                && edge.TargetCanonicalKey
                    == "xaml:resource:View.xaml#Accent");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task KeyedNestedDictionaryOwnsItsPrivateResourcesAndRemainsVisibleAsOuterResource()
    {
        var root = CreateTempDirectory();
        try
        {
            var viewPath = Path.Combine(root, "View.xaml");
            await File.WriteAllTextAsync(
                viewPath,
                """
                <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                    <Window.Resources>
                        <ResourceDictionary x:Key="Nested">
                            <ResourceDictionary.MergedDictionaries>
                                <ResourceDictionary>
                                    <SolidColorBrush x:Key="MergedInner" Color="Green" />
                                </ResourceDictionary>
                            </ResourceDictionary.MergedDictionaries>
                            <SolidColorBrush x:Key="DirectInner" Color="Blue" />
                            <Border x:Key="DirectConsumer"
                                    Background="{StaticResource DirectInner}" />
                            <Border x:Key="MergedConsumer"
                                    Background="{StaticResource MergedInner}" />
                        </ResourceDictionary>
                    </Window.Resources>
                    <Border x:Name="OuterDictionaryConsumer"
                            Tag="{StaticResource Nested}" />
                    <Border x:Name="OutsideConsumer"
                            Background="{StaticResource DirectInner}"
                            BorderBrush="{StaticResource MergedInner}" />
                </Window>
                """);

            var events = await new XamlLanguageIndexer().IndexAsync(
                new IndexContext(
                    viewPath,
                    await File.ReadAllBytesAsync(viewPath),
                    "test",
                    root),
                default);

            var resourceEdges = events.OfType<IndexEvent.EdgeEmitted>()
                .Where(edge => edge.EdgeKindName == "uses-resource")
                .ToList();
            resourceEdges.Should().ContainSingle(edge =>
                edge.SourceCanonicalKey.EndsWith(
                    "#OuterDictionaryConsumer",
                    StringComparison.Ordinal)
                && edge.TargetCanonicalKey
                    == "xaml:resource:View.xaml#Nested");
            resourceEdges.Should().ContainSingle(edge =>
                edge.SourceCanonicalKey.EndsWith(
                    "#DirectConsumer",
                    StringComparison.Ordinal)
                && edge.TargetCanonicalKey
                    == "xaml:resource:View.xaml#DirectInner");
            resourceEdges.Should().ContainSingle(edge =>
                edge.SourceCanonicalKey.EndsWith(
                    "#MergedConsumer",
                    StringComparison.Ordinal)
                && edge.TargetCanonicalKey
                    == "xaml:resource:View.xaml#MergedInner");
            resourceEdges.Should().NotContain(edge =>
                edge.SourceCanonicalKey.EndsWith(
                    "#OutsideConsumer",
                    StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task LocalMergedSourceMakesScopeIncompleteInsteadOfProjectMissing()
    {
        var root = CreateTempDirectory();
        try
        {
            await WriteProjectAsync(root);
            var viewPath = Path.Combine(root, "View.xaml");
            await File.WriteAllTextAsync(
                viewPath,
                """
                <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                    <Window.Resources>
                        <ResourceDictionary>
                            <ResourceDictionary.MergedDictionaries>
                                <ResourceDictionary Source="pack://application:,,,/Other;component/Colors.xaml" />
                            </ResourceDictionary.MergedDictionaries>
                        </ResourceDictionary>
                    </Window.Resources>
                    <Border x:Name="Consumer"
                            Background="{StaticResource Accent}" />
                </Window>
                """);
            var project = await DiscoverSingleProjectAsync(root);
            project.ResourceSnapshot.IsComplete.Should().BeTrue(
                "a view-local merge is not part of the project-global App/Generic cascade");

            var events = await new XamlLanguageIndexer().IndexAsync(
                new IndexContext(
                    viewPath,
                    await File.ReadAllBytesAsync(viewPath),
                    "test",
                    root,
                    project),
                default);

            events.OfType<IndexEvent.EdgeEmitted>().Should().NotContain(edge =>
                edge.EdgeKindName == "uses-resource");
            events.OfType<IndexEvent.AnnotationAttached>().Should().NotContain(annotation =>
                annotation.Flavor == "xaml-resource-finding");
            var outcome = events.OfType<IndexEvent.AnnotationAttached>()
                .Should().ContainSingle(annotation =>
                    annotation.Flavor == "xaml-resource-outcome").Subject;
            outcome.FullName.Should().Be("incomplete");
            using var json = JsonDocument.Parse(outcome.ArgsJson!);
            json.RootElement.GetProperty("reason").GetString().Should()
                .Be("local-resource-scope-incomplete");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task NestedStyleResourcesDoNotLeakIntoProjectGlobalSnapshot()
    {
        var root = CreateTempDirectory();
        try
        {
            await WriteProjectAsync(root);
            await File.WriteAllTextAsync(
                Path.Combine(root, "App.xaml"),
                """
                <Application xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                    <Application.Resources>
                        <Style x:Key="ContainerStyle">
                            <Style.Resources>
                                <SolidColorBrush x:Key="InnerBrush" Color="Blue" />
                            </Style.Resources>
                        </Style>
                    </Application.Resources>
                </Application>
                """);
            var viewPath = Path.Combine(root, "View.xaml");
            await File.WriteAllTextAsync(
                viewPath,
                ConsumerView("InnerBrush"));
            var project = await DiscoverSingleProjectAsync(root);

            project.ResolveResource("ContainerStyle").Status.Should()
                .Be(ResourceResolutionStatus.Resolved);
            project.ResolveResource("InnerBrush").Status.Should()
                .Be(ResourceResolutionStatus.Missing);
            var events = await new XamlLanguageIndexer().IndexAsync(
                new IndexContext(
                    viewPath,
                    await File.ReadAllBytesAsync(viewPath),
                    "test",
                    root,
                    project),
                default);

            events.OfType<IndexEvent.EdgeEmitted>().Should().NotContain(edge =>
                edge.EdgeKindName == "uses-resource");
            events.OfType<IndexEvent.AnnotationAttached>().Should()
                .ContainSingle(annotation =>
                    annotation.Flavor == "xaml-resource-finding"
                    && annotation.FullName == "XAMLRESOURCE001");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task ProjectGlobalCanonicalSurvivesPrivateStyleResourceWithSameKey()
    {
        var root = CreateTempDirectory();
        try
        {
            await WriteProjectAsync(root);
            var appPath = Path.Combine(root, "App.xaml");
            await File.WriteAllTextAsync(
                appPath,
                """
                <Application xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                    <Application.Resources>
                        <SolidColorBrush x:Key="Accent" Color="Blue" />
                        <Style x:Key="ContainerStyle">
                            <Style.Resources>
                                <SolidColorBrush x:Key="Accent" Color="Green" />
                            </Style.Resources>
                            <Setter Property="Background"
                                    Value="{StaticResource Accent}" />
                        </Style>
                    </Application.Resources>
                </Application>
                """);
            var viewPath = Path.Combine(root, "View.xaml");
            await File.WriteAllTextAsync(viewPath, ConsumerView("Accent"));
            var project = await DiscoverSingleProjectAsync(root);

            var resolution = project.ResolveResource("Accent");
            resolution.Status.Should().Be(ResourceResolutionStatus.Resolved);
            resolution.Definition!.ToCanonicalKey(root).Should()
                .Be("xaml:resource:App.xaml#Accent");

            var appEvents = await new XamlLanguageIndexer().IndexAsync(
                new IndexContext(
                    appPath,
                    await File.ReadAllBytesAsync(appPath),
                    "test",
                    root,
                    project),
                default);
            var declarations = appEvents.OfType<IndexEvent.SymbolDeclared>()
                .Where(symbol =>
                    symbol.Kind == "xaml-resource"
                    && symbol.CanonicalKey.StartsWith(
                        "xaml:resource:App.xaml#Accent",
                        StringComparison.Ordinal))
                .OrderBy(symbol => symbol.StartLine)
                .ToList();
            declarations.Should().HaveCount(2);
            declarations.Select(symbol => symbol.CanonicalKey).Should()
                .OnlyHaveUniqueItems();
            declarations[0].CanonicalKey.Should()
                .Be("xaml:resource:App.xaml#Accent");
            declarations[1].CanonicalKey.Should()
                .StartWith("xaml:resource:App.xaml#Accent@L");
            appEvents.OfType<IndexEvent.EdgeEmitted>().Should().ContainSingle(edge =>
                edge.EdgeKindName == "uses-resource"
                && edge.TargetCanonicalKey == declarations[1].CanonicalKey);

            var viewEvents = await new XamlLanguageIndexer().IndexAsync(
                new IndexContext(
                    viewPath,
                    await File.ReadAllBytesAsync(viewPath),
                    "test",
                    root,
                    project),
                default);
            viewEvents.OfType<IndexEvent.EdgeEmitted>().Should().ContainSingle(edge =>
                edge.EdgeKindName == "uses-resource"
                && edge.TargetCanonicalKey
                    == "xaml:resource:App.xaml#Accent");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task ApplicationDefinitionItemMakesCustomFileTheProjectResourceRoot()
    {
        var root = CreateTempDirectory();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, "Fixture.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <ApplicationDefinition Include="Bootstrap.xaml" />
                    <Page Include="View.xaml" />
                  </ItemGroup>
                </Project>
                """);
            var bootstrapPath = Path.Combine(root, "Bootstrap.xaml");
            await File.WriteAllTextAsync(
                bootstrapPath,
                """
                <Application xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                    <Application.Resources>
                        <SolidColorBrush x:Key="GlobalAccent" Color="Blue" />
                    </Application.Resources>
                </Application>
                """);
            var viewPath = Path.Combine(root, "View.xaml");
            await File.WriteAllTextAsync(viewPath, ConsumerView("GlobalAccent"));

            var project = await DiscoverSingleProjectAsync(root);

            var resolution = project.ResolveResource("GlobalAccent");
            resolution.Status.Should().Be(ResourceResolutionStatus.Resolved);
            resolution.Definition!.FilePath.Should().Be(bootstrapPath);
            project.ResourceSnapshot.ContributorPaths.Should().Equal(bootstrapPath);

            var events = await new XamlLanguageIndexer().IndexAsync(
                new IndexContext(
                    viewPath,
                    await File.ReadAllBytesAsync(viewPath),
                    "test",
                    root,
                    project),
                default);
            events.OfType<IndexEvent.EdgeEmitted>().Should().ContainSingle(edge =>
                edge.EdgeKindName == "uses-resource"
                && edge.TargetCanonicalKey
                    == "xaml:resource:Bootstrap.xaml#GlobalAccent");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task PageItemNamedAppXamlIsNotPromotedToProjectResourceRoot()
    {
        var root = CreateTempDirectory();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, "Fixture.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <Page Include="App.xaml" />
                    <Page Include="View.xaml" />
                  </ItemGroup>
                </Project>
                """);
            var appPath = Path.Combine(root, "App.xaml");
            await File.WriteAllTextAsync(
                appPath,
                """
                <Application xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                    <Application.Resources>
                        <SolidColorBrush x:Key="PageOnlyAccent" Color="Red" />
                    </Application.Resources>
                </Application>
                """);
            var viewPath = Path.Combine(root, "View.xaml");
            await File.WriteAllTextAsync(viewPath, ConsumerView("PageOnlyAccent"));

            var project = await DiscoverSingleProjectAsync(root);

            project.ResolveResource("PageOnlyAccent").Status.Should()
                .Be(ResourceResolutionStatus.Missing);
            project.ResourceSnapshot.ContributorPaths.Should().BeEmpty();

            var events = await new XamlLanguageIndexer().IndexAsync(
                new IndexContext(
                    viewPath,
                    await File.ReadAllBytesAsync(viewPath),
                    "test",
                    root,
                    project),
                default);
            events.OfType<IndexEvent.EdgeEmitted>().Should().NotContain(edge =>
                edge.EdgeKindName == "uses-resource");
            events.OfType<IndexEvent.AnnotationAttached>().Should().ContainSingle(annotation =>
                annotation.Flavor == "xaml-resource-finding"
                && annotation.FullName == "XAMLRESOURCE001");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task NestedFileNamedAppXamlIsNotPromotedByFilenameFallback()
    {
        var root = CreateTempDirectory();
        try
        {
            await WriteProjectAsync(root);
            Directory.CreateDirectory(Path.Combine(root, "Views"));
            await File.WriteAllTextAsync(
                Path.Combine(root, "Views", "App.xaml"),
                """
                <Application xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                    <Application.Resources>
                        <SolidColorBrush x:Key="NestedAppAccent" Color="Red" />
                    </Application.Resources>
                </Application>
                """);
            await File.WriteAllTextAsync(
                Path.Combine(root, "View.xaml"),
                ConsumerView("NestedAppAccent"));

            var project = await DiscoverSingleProjectAsync(root);

            project.ResourceSnapshot.IsComplete.Should().BeTrue();
            project.ResolveResource("NestedAppAccent").Status.Should()
                .Be(ResourceResolutionStatus.Missing);
            project.ResourceSnapshot.ContributorPaths.Should().BeEmpty();
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task UnrelatedPageUpdateDoesNotDisableImplicitAppXamlFallback()
    {
        var root = CreateTempDirectory();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, "Fixture.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <UseWPF>true</UseWPF>
                  </PropertyGroup>
                  <ItemGroup>
                    <Page Update="View.xaml">
                      <Generator>MSBuild:Compile</Generator>
                    </Page>
                  </ItemGroup>
                </Project>
                """);
            var appPath = Path.Combine(root, "App.xaml");
            await File.WriteAllTextAsync(
                appPath,
                """
                <Application xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                    <Application.Resources>
                        <SolidColorBrush x:Key="ImplicitAccent" Color="Blue" />
                    </Application.Resources>
                </Application>
                """);
            await File.WriteAllTextAsync(
                Path.Combine(root, "View.xaml"),
                ConsumerView("ImplicitAccent"));

            var project = await DiscoverSingleProjectAsync(root);

            project.ResourceSnapshot.IsComplete.Should().BeTrue();
            project.ResolveResource("ImplicitAccent").Status.Should()
                .Be(ResourceResolutionStatus.Resolved);
            project.ResourceSnapshot.ContributorPaths.Should().Equal(appPath);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task PageUpdateNamedAppXamlDoesNotSuppressImplicitApplicationDefinition()
    {
        var root = CreateTempDirectory();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, "Fixture.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <UseWPF>true</UseWPF>
                  </PropertyGroup>
                  <ItemGroup>
                    <Page Update="App.xaml">
                      <Generator>MSBuild:Compile</Generator>
                    </Page>
                  </ItemGroup>
                </Project>
                """);
            var appPath = Path.Combine(root, "App.xaml");
            await File.WriteAllTextAsync(
                appPath,
                """
                <Application xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                    <Application.Resources>
                        <SolidColorBrush x:Key="ImplicitAccent" Color="Blue" />
                    </Application.Resources>
                </Application>
                """);

            var project = await DiscoverSingleProjectAsync(root);

            project.ResourceSnapshot.IsComplete.Should().BeTrue();
            project.ResolveResource("ImplicitAccent").Status.Should()
                .Be(ResourceResolutionStatus.Resolved);
            project.ResourceSnapshot.ContributorPaths.Should().Equal(appPath);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task ApplicationDefinitionUpdateDoesNotPromoteOrdinaryFileToResourceRoot()
    {
        var root = CreateTempDirectory();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, "Fixture.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <ApplicationDefinition Update="Bootstrap.xaml" />
                  </ItemGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(
                Path.Combine(root, "Bootstrap.xaml"),
                """
                <Application xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                    <Application.Resources>
                        <SolidColorBrush x:Key="OrdinaryAccent" Color="Red" />
                    </Application.Resources>
                </Application>
                """);

            var project = await DiscoverSingleProjectAsync(root);

            project.ResourceSnapshot.IsComplete.Should().BeTrue();
            project.ResolveResource("OrdinaryAccent").Status.Should()
                .Be(ResourceResolutionStatus.Missing);
            project.ResourceSnapshot.ContributorPaths.Should().BeEmpty();
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Theory]
    [InlineData(
        """
        <ItemGroup>
          <Page Include="App.xaml" Condition="'$(Configuration)' == 'Debug'" />
        </ItemGroup>
        """)]
    [InlineData(
        """
        <ItemGroup>
          <Page Include="$(MarkupRoot)/App.xaml" />
        </ItemGroup>
        """)]
    [InlineData(
        """
        <ItemGroup>
          <Page Remove="App.xaml" />
        </ItemGroup>
        """)]
    [InlineData("""<Import Project="Markup.props" />""")]
    public async Task UnevaluatedProjectItemMetadataMakesResourceSnapshotIncomplete(
        string projectBody)
    {
        var root = CreateTempDirectory();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, "Fixture.csproj"),
                $"""
                 <Project Sdk="Microsoft.NET.Sdk">
                 {projectBody}
                 </Project>
                 """);
            await File.WriteAllTextAsync(
                Path.Combine(root, "App.xaml"),
                """
                <Application xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                    <Application.Resources>
                        <SolidColorBrush x:Key="UncertainAccent" Color="Blue" />
                    </Application.Resources>
                </Application>
                """);

            var project = await DiscoverSingleProjectAsync(root);

            project.ResourceSnapshot.IsComplete.Should().BeFalse();
            project.ResourceSnapshot.UnknownReasons.Should().Contain(reason =>
                reason.StartsWith(
                    "project-xaml-item-evaluation-unsupported:",
                    StringComparison.Ordinal));
            project.ResolveResource("UncertainAccent").Status.Should()
                .Be(ResourceResolutionStatus.Incomplete);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task MergedTargetWithNonDictionaryRootMakesSnapshotIncomplete()
    {
        var root = CreateTempDirectory();
        try
        {
            await WriteProjectAsync(root);
            await File.WriteAllTextAsync(
                Path.Combine(root, "App.xaml"),
                AppWithMergedDictionaries("Bad.xaml"));
            await File.WriteAllTextAsync(
                Path.Combine(root, "Bad.xaml"),
                """
                <UserControl xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                    <UserControl.Resources>
                        <SolidColorBrush x:Key="Accent" Color="Blue" />
                    </UserControl.Resources>
                </UserControl>
                """);
            var viewPath = Path.Combine(root, "View.xaml");
            await File.WriteAllTextAsync(viewPath, ConsumerView("Accent"));
            var project = await DiscoverSingleProjectAsync(root);

            project.ResourceSnapshot.IsComplete.Should().BeFalse();
            project.ResourceSnapshot.Definitions.Should().NotContainKey("Accent");
            project.ResourceSnapshot.UnknownReasons.Should().ContainSingle(reason =>
                reason
                    == "merged-dictionary-target-root-not-resource-dictionary:Bad.xaml");
            var events = await new XamlLanguageIndexer().IndexAsync(
                new IndexContext(
                    viewPath,
                    await File.ReadAllBytesAsync(viewPath),
                    "test",
                    root,
                    project),
                default);

            events.OfType<IndexEvent.EdgeEmitted>().Should().NotContain(edge =>
                edge.EdgeKindName == "uses-resource");
            events.OfType<IndexEvent.AnnotationAttached>().Should().NotContain(annotation =>
                annotation.Flavor == "xaml-resource-finding");
            events.OfType<IndexEvent.AnnotationAttached>().Should()
                .ContainSingle(annotation =>
                    annotation.Flavor == "xaml-resource-outcome"
                    && annotation.FullName == "incomplete");
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
                .Should().Equal("A.xaml", "App.xaml", "B.xaml");

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

            var outcome = events.OfType<IndexEvent.AnnotationAttached>()
                .Should().ContainSingle(annotation =>
                    annotation.Flavor == "xaml-resource-outcome"
                    && annotation.FullName == "ambiguous").Subject;
            events.OfType<IndexEvent.AnnotationAttached>().Should().NotContain(annotation =>
                annotation.Flavor == "xaml-resource-finding");
            using var json = JsonDocument.Parse(outcome.ArgsJson!);
            json.RootElement.GetProperty("status").GetString().Should().Be("ambiguous");
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

            await File.WriteAllTextAsync(
                sharedPath,
                """
                <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                    <SolidColorBrush x:Key="After" Color="Green" />
                    <SolidColorBrush x:Key="After" Color="Blue" />
                </ResourceDictionary>
                """);
            project.RebuildResourceCache();

            project.ResolveResource("After").Status.Should()
                .Be(ResourceResolutionStatus.Ambiguous);
            project.ResourceSnapshot.ContributorPaths
                .Select(Path.GetFileName)
                .Should().Equal("App.xaml", "Shared.xaml");
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
                .Be(ResourceResolutionStatus.Incomplete);
            project.ResourceSnapshot.IsComplete.Should().BeFalse();
            project.ResourceSnapshot.UnknownReasons.Should().ContainSingle(reason =>
                reason.StartsWith(
                    "merged-dictionary-target-excluded:",
                    StringComparison.Ordinal));
            project.FilePaths.Should().NotContain(path =>
                path.Contains("PatientData", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task CompleteStaticMissIsFindingButRuntimeLookupsAreOnlyOutcomes()
    {
        var root = CreateTempDirectory();
        try
        {
            await WriteProjectAsync(root);
            await File.WriteAllTextAsync(
                Path.Combine(root, "App.xaml"),
                """
                <Application xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                    <Application.Resources>
                        <SolidColorBrush x:Key="Known" Color="Blue" />
                    </Application.Resources>
                </Application>
                """);
            var viewPath = Path.Combine(root, "View.xaml");
            await File.WriteAllTextAsync(
                viewPath,
                """
                <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                    <Border x:Name="StaticMissing"
                            Background="{StaticResource Absent}" />
                    <Border x:Name="DynamicUnknown"
                            Background="{DynamicResource Absent}" />
                    <Border x:Name="ThemeUnknown"
                            Background="{ThemeResource Absent}" />
                </Window>
                """);

            var project = await DiscoverSingleProjectAsync(root);
            project.ResourceSnapshot.IsComplete.Should().BeTrue();
            var events = await new XamlLanguageIndexer().IndexAsync(
                new IndexContext(
                    viewPath,
                    await File.ReadAllBytesAsync(viewPath),
                    "test",
                    root,
                    project),
                default);

            var annotations = events.OfType<IndexEvent.AnnotationAttached>().ToArray();
            var finding = annotations.Should().ContainSingle(annotation =>
                annotation.Flavor == "xaml-resource-finding"
                && annotation.FullName == "XAMLRESOURCE001").Subject;
            using (var json = JsonDocument.Parse(finding.ArgsJson!))
            {
                json.RootElement.GetProperty("status").GetString().Should().Be("missing");
                json.RootElement.GetProperty("reason").GetString().Should()
                    .Be("resource-key-not-visible-in-complete-static-cascade");
            }
            var runtimeOutcomes = annotations.Where(annotation =>
                    annotation.Flavor == "xaml-resource-outcome")
                .ToArray();
            runtimeOutcomes.Should().HaveCount(2);
            var runtimeStates = runtimeOutcomes.Select(annotation =>
            {
                using var json = JsonDocument.Parse(annotation.ArgsJson!);
                return (
                    Status: json.RootElement.GetProperty("status").GetString(),
                    Reason: json.RootElement.GetProperty("reason").GetString());
            }).ToArray();
            runtimeStates.Should().ContainSingle(state =>
                state.Status == "unknown"
                && state.Reason == "dynamic-resource-not-present-in-indexed-cascade");
            runtimeStates.Should().ContainSingle(state =>
                state.Status == "unsupported"
                && state.Reason == "theme-resource-runtime-lookup");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Theory]
    [InlineData("pack://application:,,,/Other;component/Colors.xaml")]
    [InlineData("/Other;component/Colors.xaml")]
    [InlineData("https://example.invalid/Colors.xaml")]
    public async Task UnsupportedMergeMakesStaticMissIncompleteWithoutFinding(
        string source)
    {
        var root = CreateTempDirectory();
        try
        {
            await WriteProjectAsync(root);
            await File.WriteAllTextAsync(
                Path.Combine(root, "App.xaml"),
                AppWithMergedDictionaries(source));
            var viewPath = Path.Combine(root, "View.xaml");
            await File.WriteAllTextAsync(viewPath, ConsumerView("Absent"));

            var project = await DiscoverSingleProjectAsync(root);
            project.ResourceSnapshot.IsComplete.Should().BeFalse();
            project.ResolveResource("Absent").Status.Should()
                .Be(ResourceResolutionStatus.Incomplete);
            project.ResourceSnapshot.UnknownReasons.Should().NotBeEmpty();

            var events = await new XamlLanguageIndexer().IndexAsync(
                new IndexContext(
                    viewPath,
                    await File.ReadAllBytesAsync(viewPath),
                    "test",
                    root,
                    project),
                default);

            events.OfType<IndexEvent.AnnotationAttached>().Should().NotContain(annotation =>
                annotation.Flavor == "xaml-resource-finding");
            var outcome = events.OfType<IndexEvent.AnnotationAttached>()
                .Should().ContainSingle(annotation =>
                    annotation.Flavor == "xaml-resource-outcome").Subject;
            using var json = JsonDocument.Parse(outcome.ArgsJson!);
            json.RootElement.GetProperty("status").GetString().Should()
                .Be("incomplete");
            json.RootElement.GetProperty("snapshotComplete").GetBoolean().Should()
                .BeFalse();
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
