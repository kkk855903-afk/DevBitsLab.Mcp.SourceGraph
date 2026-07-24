using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using DevBitsLab.Mcp.SourceGraph.Indexing.Xaml;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class XamlResourceSnapshotTests
{
    [Fact]
    public void ConstructorCopiesEveryInputCollection()
    {
        var original = new ResourceDefinition("Accent", "App.xaml", 3, 5, "SolidColorBrush");
        var candidates = new List<ResourceDefinition> { original };
        var definitions = new Dictionary<string, IReadOnlyList<ResourceDefinition>>
        {
            ["Accent"] = candidates,
        };
        var contributorPaths = new List<string> { "App.xaml" };
        var unknownReasons = new List<string> { "initial-reason" };

        var snapshot = new XamlResourceSnapshot(
            definitions,
            contributorPaths,
            isComplete: true,
            unknownReasons);

        candidates.Clear();
        candidates.Add(new ResourceDefinition("Changed", "Changed.xaml", 1, 1, "Style"));
        definitions.Clear();
        contributorPaths.Clear();
        contributorPaths.Add("Changed.xaml");
        unknownReasons.Clear();
        unknownReasons.Add("changed-reason");

        snapshot.Definitions.Should().ContainSingle();
        snapshot.Definitions["Accent"].Should().ContainSingle().Which.Should().BeSameAs(original);
        snapshot.ContributorPaths.Should().Equal("App.xaml");
        snapshot.UnknownReasons.Should().Equal("initial-reason");
    }

    [Fact]
    public void ExposedCollectionsCannotBeMutatedOrChangeResourceResolution()
    {
        var definition = new ResourceDefinition("Accent", "App.xaml", 3, 5, "SolidColorBrush");
        var snapshot = new XamlResourceSnapshot(
            new Dictionary<string, IReadOnlyList<ResourceDefinition>>
            {
                ["Accent"] = new List<ResourceDefinition> { definition },
            },
            new List<string> { "App.xaml" },
            isComplete: true,
            new List<string> { "initial-reason" });
        var project = new XamlLanguageProject(
            "Sample.xaml.csproj",
            Array.Empty<string>(),
            snapshot,
            resourceSnapshotBuilder: null,
            roslynProjectProvider: null);

        snapshot.Definitions.Should().BeOfType<ReadOnlyDictionary<string, IReadOnlyList<ResourceDefinition>>>();
        snapshot.Definitions["Accent"].Should().BeOfType<ReadOnlyCollection<ResourceDefinition>>();
        snapshot.ContributorPaths.Should().BeOfType<ReadOnlyCollection<string>>();
        snapshot.UnknownReasons.Should().BeOfType<ReadOnlyCollection<string>>();

        Action replaceDefinitions = () =>
            ((IDictionary<string, IReadOnlyList<ResourceDefinition>>)snapshot.Definitions)["Changed"] =
                Array.Empty<ResourceDefinition>();
        Action replaceCandidate = () =>
            ((IList<ResourceDefinition>)snapshot.Definitions["Accent"])[0] =
                new ResourceDefinition("Changed", "Changed.xaml", 1, 1, "Style");
        Action addContributor = () =>
            ((IList<string>)snapshot.ContributorPaths).Add("Changed.xaml");
        Action replaceReason = () =>
            ((IList<string>)snapshot.UnknownReasons)[0] = "changed-reason";

        replaceDefinitions.Should().Throw<NotSupportedException>();
        replaceCandidate.Should().Throw<NotSupportedException>();
        addContributor.Should().Throw<NotSupportedException>();
        replaceReason.Should().Throw<NotSupportedException>();

        snapshot.Definitions.Should().ContainSingle();
        snapshot.Definitions["Accent"].Should().ContainSingle().Which.Should().BeSameAs(definition);
        snapshot.ContributorPaths.Should().Equal("App.xaml");
        snapshot.UnknownReasons.Should().Equal("initial-reason");
        project.ResolveResource("Accent").Status.Should().Be(ResourceResolutionStatus.Resolved);
    }
}
