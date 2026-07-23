using DevBitsLab.Mcp.SourceGraph.Indexing.Xaml;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using FluentAssertions;
using System.Xml;
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
        var excludedProjectDirectory = Path.Join(_root, "Generated");
        var excludedXamlDirectory = Path.Join(projectDir, "Generated");
        await PlantAsync(
            Path.Join(excludedProjectDirectory, "Hidden.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"></Project>""");
        await PlantAsync(
            Path.Join(excludedProjectDirectory, "Hidden.xaml"),
            """<Window Tag="HIDDEN-SCOPE-PROJECT-CANARY" />""");

        var factory = (IExclusionAwareLanguageProjectFactory)new XamlLanguageProjectFactory(
            (_, path) =>
            {
                if (IsSameOrDescendant(path, excludedProjectDirectory)
                    || IsSameOrDescendant(path, excludedXamlDirectory))
                {
                    throw new InvalidOperationException(
                        "Privacy-excluded path was accessed: " + path);
                }
            });
        var projects = await factory.DiscoverAsync(
            _root,
            ["**/generated/**"],
            CancellationToken.None);

        projects.Should().ContainSingle();
        projects[0].Id.Should().Be(projectPath);
        projects[0].FilePaths.Should().Equal(mainView);
    }

    [Fact]
    public async Task Discover_preCancelledEmptyRepository_propagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> discover = async () =>
            await new XamlLanguageProjectFactory().DiscoverAsync(
                _root,
                cts.Token);

        await discover.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Discover_cancellationDuringFirstProjectBuild_stopsBeforeSecondProject()
    {
        foreach (var projectName in new[] { "First", "Second" })
        {
            var projectDir = Path.Join(_root, projectName);
            await PlantAsync(
                Path.Join(projectDir, projectName + ".csproj"),
                """<Project Sdk="Microsoft.NET.Sdk"></Project>""");
            await PlantAsync(
                Path.Join(projectDir, "MainWindow.xaml"),
                """<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" />""");
        }

        using var cts = new CancellationTokenSource();
        var projectReads = 0;
        var factory = new XamlLanguageProjectFactory(
            (access, _) =>
            {
                if (access != XamlDiscoveryAccess.ReadProjectFile) return;
                projectReads++;
                cts.Cancel();
            });

        Func<Task> discover = async () =>
            await factory.DiscoverAsync(_root, cts.Token);

        await discover.Should().ThrowAsync<OperationCanceledException>();
        projectReads.Should().Be(1);
    }

    [Fact]
    public async Task Discover_singleProjectCancellationAtXamlRead_propagates()
    {
        var projectDir = Path.Join(_root, "ManagedApp");
        await PlantAsync(
            Path.Join(projectDir, "ManagedApp.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"></Project>""");
        await PlantAsync(
            Path.Join(projectDir, "MainWindow.xaml"),
            """<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" />""");

        using var cts = new CancellationTokenSource();
        var factory = new XamlLanguageProjectFactory(
            (access, _) =>
            {
                if (access == XamlDiscoveryAccess.ReadXamlFile)
                {
                    cts.Cancel();
                }
            });

        Func<Task> discover = async () =>
            await factory.DiscoverAsync(_root, cts.Token);

        await discover.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData("Include")]
    [InlineData("Update")]
    public async Task Discover_unionsExplicitItemWithSecondImplicitXaml(
        string itemAttribute)
    {
        var projectDir = Path.Join(_root, "ManagedApp");
        var projectPath = await PlantAsync(
            Path.Join(projectDir, "ManagedApp.csproj"),
            $"""
             <Project Sdk="Microsoft.NET.Sdk">
               <ItemGroup>
                 <Page {itemAttribute}="Views/AExplicit.xaml" />
               </ItemGroup>
             </Project>
             """);
        var explicitXaml = await PlantAsync(
            Path.Join(projectDir, "Views", "AExplicit.xaml"),
            """<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" />""");
        var implicitXaml = await PlantAsync(
            Path.Join(projectDir, "Views", "ZImplicit.xaml"),
            """<UserControl xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" />""");

        var projects = await new XamlLanguageProjectFactory().DiscoverAsync(
            _root,
            CancellationToken.None);

        projects.Should().ContainSingle();
        projects[0].Id.Should().Be(projectPath);
        projects[0].FilePaths.Should().BeEquivalentTo(
            new[] { explicitXaml, implicitXaml });
    }

    [Theory]
    [InlineData("Include")]
    [InlineData("Update")]
    public async Task Discover_explicitItemDoesNotHideMalformedImplicitXaml(
        string itemAttribute)
    {
        var projectDir = Path.Join(_root, "ManagedApp");
        await PlantAsync(
            Path.Join(projectDir, "ManagedApp.csproj"),
            $"""
             <Project Sdk="Microsoft.NET.Sdk">
               <ItemGroup>
                 <Page {itemAttribute}="Views/AExplicit.xaml" />
               </ItemGroup>
             </Project>
             """);
        await PlantAsync(
            Path.Join(projectDir, "Views", "AExplicit.xaml"),
            """<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" />""");
        await PlantAsync(
            Path.Join(projectDir, "Views", "ZImplicitMalformed.xaml"),
            """<Window>""");

        Func<Task> discover = async () =>
            await new XamlLanguageProjectFactory().DiscoverAsync(
                _root,
                CancellationToken.None);

        await discover.Should().ThrowAsync<XmlException>();
    }

    [Fact]
    public async Task Discover_validProjectWithoutXaml_returnsNoProjects()
    {
        await PlantAsync(
            Path.Join(_root, "ManagedApp", "ManagedApp.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"></Project>""");

        var projects = await new XamlLanguageProjectFactory().DiscoverAsync(
            _root,
            CancellationToken.None);

        projects.Should().BeEmpty();
    }

    [Fact]
    public async Task Discover_propagatesMalformedProjectXml()
    {
        var projectDir = Path.Join(_root, "ManagedApp");
        await PlantAsync(
            Path.Join(projectDir, "ManagedApp.csproj"),
            """<Project><ItemGroup>""");
        await PlantAsync(
            Path.Join(projectDir, "MainWindow.xaml"),
            """<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" />""");

        Func<Task> discover = async () =>
            await new XamlLanguageProjectFactory().DiscoverAsync(_root, default);

        await discover.Should().ThrowAsync<XmlException>();
    }

    [Fact]
    public async Task Discover_propagatesMalformedXamlXml()
    {
        var projectDir = Path.Join(_root, "ManagedApp");
        await PlantAsync(
            Path.Join(projectDir, "ManagedApp.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"></Project>""");
        await PlantAsync(
            Path.Join(projectDir, "MainWindow.xaml"),
            """<Window>""");

        Func<Task> discover = async () =>
            await new XamlLanguageProjectFactory().DiscoverAsync(_root, default);

        await discover.Should().ThrowAsync<XmlException>();
    }

    [Theory]
    [InlineData((int)XamlDiscoveryAccess.EnumerateProjectEntries)]
    [InlineData((int)XamlDiscoveryAccess.ReadProjectFile)]
    [InlineData((int)XamlDiscoveryAccess.EnumerateXamlEntries)]
    [InlineData((int)XamlDiscoveryAccess.ReadXamlFile)]
    public async Task Discover_propagatesIoFailureFromEveryDiscoveryAccess(
        int failingAccessValue)
    {
        var failingAccess = (XamlDiscoveryAccess)failingAccessValue;
        var projectDir = Path.Join(_root, "ManagedApp");
        await PlantAsync(
            Path.Join(projectDir, "ManagedApp.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"></Project>""");
        await PlantAsync(
            Path.Join(projectDir, "MainWindow.xaml"),
            """<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" />""");
        var factory = new XamlLanguageProjectFactory(
            (access, _) =>
            {
                if (access == failingAccess)
                {
                    throw new IOException("Injected discovery failure: " + access);
                }
            });

        Func<Task> discover = async () =>
            await factory.DiscoverAsync(_root, default);

        await discover.Should()
            .ThrowAsync<IOException>()
            .WithMessage("*" + failingAccess + "*");
    }

    [Fact]
    public async Task Discover_propagatesUnauthorizedProjectRead()
    {
        var projectDir = Path.Join(_root, "ManagedApp");
        await PlantAsync(
            Path.Join(projectDir, "ManagedApp.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"></Project>""");
        await PlantAsync(
            Path.Join(projectDir, "MainWindow.xaml"),
            """<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" />""");
        var factory = new XamlLanguageProjectFactory(
            (access, _) =>
            {
                if (access == XamlDiscoveryAccess.ReadProjectFile)
                {
                    throw new UnauthorizedAccessException(
                        "Injected unauthorized project read.");
                }
            });

        Func<Task> discover = async () =>
            await factory.DiscoverAsync(_root, default);

        await discover.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task Discover_propagatesMissingDiscoveryRoot()
    {
        var missingRoot = Path.Join(_root, "Missing");

        Func<Task> discover = async () =>
            await new XamlLanguageProjectFactory().DiscoverAsync(
                missingRoot,
                default);

        await discover.Should().ThrowAsync<DirectoryNotFoundException>();
    }

    [SkippableFact]
    public async Task Discover_danglingReparseRoot_failsInsteadOfReturningEmpty()
    {
        var target = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-xaml-dangling-target-" + Guid.NewGuid().ToString("N"));
        var link = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-xaml-dangling-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        try
        {
            Skip.IfNot(
                PhysicalPathTestSupport.TryCreateDirectoryLink(link, target),
                "This environment does not permit symbolic-link or junction creation.");
            Directory.Delete(target);

            Func<Task> discover = async () =>
                await new XamlLanguageProjectFactory().DiscoverAsync(
                    link,
                    CancellationToken.None);

            await discover.Should()
                .ThrowAsync<IOException>()
                .WithMessage("*cannot be resolved physically*");
        }
        finally
        {
            try { Directory.Delete(link); } catch { }
            try { Directory.Delete(target, recursive: true); } catch { }
        }
    }

    [SkippableFact]
    public async Task Discover_danglingReparseSubtree_failsInsteadOfPublishingPartialMap()
    {
        var projectDir = Path.Join(_root, "ManagedApp");
        await PlantAsync(
            Path.Join(projectDir, "ManagedApp.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"></Project>""");
        await PlantAsync(
            Path.Join(projectDir, "MainWindow.xaml"),
            """<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" />""");
        var target = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-xaml-bad-subtree-" + Guid.NewGuid().ToString("N"));
        var link = Path.Join(_root, "BrokenAlias");
        Directory.CreateDirectory(target);
        try
        {
            Skip.IfNot(
                PhysicalPathTestSupport.TryCreateDirectoryLink(link, target),
                "This environment does not permit symbolic-link or junction creation.");
            Directory.Delete(target);

            Func<Task> discover = async () =>
                await new XamlLanguageProjectFactory().DiscoverAsync(
                    _root,
                    CancellationToken.None);

            await discover.Should()
                .ThrowAsync<IOException>()
                .WithMessage("*cannot be resolved physically*");
        }
        finally
        {
            try { Directory.Delete(link); } catch { }
            try { Directory.Delete(target, recursive: true); } catch { }
        }
    }

    [SkippableFact]
    public async Task Discover_explicitlyExcludedDanglingSubtree_isPrunedBeforeResolution()
    {
        var target = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-xaml-excluded-bad-subtree-" + Guid.NewGuid().ToString("N"));
        var link = Path.Join(_root, "Generated");
        Directory.CreateDirectory(target);
        try
        {
            Skip.IfNot(
                PhysicalPathTestSupport.TryCreateDirectoryLink(link, target),
                "This environment does not permit symbolic-link or junction creation.");
            Directory.Delete(target);

            var projects = await new XamlLanguageProjectFactory().DiscoverAsync(
                _root,
                ["**/generated/**"],
                CancellationToken.None);

            projects.Should().BeEmpty();
        }
        finally
        {
            try { Directory.Delete(link); } catch { }
            try { Directory.Delete(target, recursive: true); } catch { }
        }
    }

    [SkippableFact]
    public async Task Discover_neverFollowsDirectoryLinkOutsideRepository()
    {
        var outside = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-xaml-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            var projectDir = Path.Join(_root, "ManagedApp");
            var projectPath = await PlantAsync(
                Path.Join(projectDir, "ManagedApp.csproj"),
                """<Project Sdk="Microsoft.NET.Sdk"></Project>""");
            var mainView = await PlantAsync(
                Path.Join(projectDir, "MainWindow.xaml"),
                """<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" />""");
            await PlantAsync(
                Path.Join(outside, "Hidden.csproj"),
                """<Project Sdk="Microsoft.NET.Sdk"></Project>""");
            await PlantAsync(
                Path.Join(outside, "Hidden.xaml"),
                """<Window Tag="OUTSIDE-CANARY" />""");
            var link = Path.Join(_root, "External");
            Skip.IfNot(
                PhysicalPathTestSupport.TryCreateDirectoryLink(link, outside),
                "This environment does not permit symbolic-link or junction creation.");

            var projects = await new XamlLanguageProjectFactory().DiscoverAsync(
                _root,
                CancellationToken.None);

            projects.Should().ContainSingle();
            projects[0].Id.Should().Be(projectPath);
            projects[0].FilePaths.Should().Equal(mainView);
        }
        finally
        {
            try { Directory.Delete(outside, recursive: true); } catch { }
        }
    }

    private static async Task<string> PlantAsync(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, contents);
        return path;
    }

    private static bool IsSameOrDescendant(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase)
               || fullPath.StartsWith(
                   fullRoot + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }
}
