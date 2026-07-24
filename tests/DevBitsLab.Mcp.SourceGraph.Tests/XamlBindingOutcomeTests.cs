using System.Collections.ObjectModel;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using DevBitsLab.Mcp.SourceGraph.Indexing.Xaml;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class XamlBindingOutcomeTests
{
    private const string PresentationNamespace =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private const string XamlNamespace =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public async Task CompleteContextEmitsSemanticEdgeAndOnlyProvenMissingFinding()
    {
        const string csharp = """
            namespace Test;
            public sealed class Vm
            {
                public string Existing { get; } = "";
            }
            """;
        var xaml = $$$"""
            <Window xmlns="{{{PresentationNamespace}}}"
                    xmlns:x="{{{XamlNamespace}}}"
                    xmlns:vm="clr-namespace:Test"
                    x:DataType="vm:Vm">
                <TextBlock x:Name="Resolved" Text="{Binding Existing}" />
                <TextBlock x:Name="Missing" Text="{Binding Absent}" />
                <TextBlock x:Name="Unsupported" Text="{Binding Items[0]}" />
            </Window>
            """;

        var events = await IndexAsync(csharp, xaml);

        var resolvedKey = "xaml:element:View.xaml#Resolved";
        var edge = events.OfType<IndexEvent.EdgeEmitted>()
            .Should().ContainSingle(item =>
                item.SourceCanonicalKey == resolvedKey
                && item.EdgeKindName == "binds-path").Subject;
        edge.TargetCanonicalKey.Should().Be("csharp:P:Test.Vm.Existing");
        edge.Evidence.Should().NotBeNull();
        edge.Evidence!.Confidence.Should().Be(EvidenceConfidence.Semantic);

        var missing = AnnotationFor(events, "Missing", "xaml-binding-finding");
        using (var json = JsonDocument.Parse(missing.ArgsJson!))
        {
            json.RootElement.GetProperty("status").GetString().Should().Be("missing");
            json.RootElement.GetProperty("reason").GetString().Should()
                .Be("property-not-found");
            json.RootElement.GetProperty("code").GetString().Should()
                .Be("XAMLBINDING001");
            json.RootElement.GetProperty("severity").GetString().Should()
                .Be("warning");
        }
        AnnotationFor(events, "Unsupported", "xaml-binding-outcome")
            .FullName.Should().Be("unsupported");
        events.OfType<IndexEvent.SymbolDeclared>().Should().NotContain(symbol =>
            symbol.CanonicalKey.Contains("binding-target", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnknownContextAndIncompleteCompilationAreOutcomesNotFindings()
    {
        const string completeCsharp = """
            namespace Test;
            public sealed class Vm { }
            """;
        var noContextXaml = $$"""
            <Window xmlns="{{PresentationNamespace}}"
                    xmlns:x="{{XamlNamespace}}">
                <TextBlock x:Name="NoContext" Text="{Binding Absent}" />
            </Window>
            """;
        var unknownEvents = await IndexAsync(completeCsharp, noContextXaml);
        var unknown = AnnotationFor(
            unknownEvents,
            "NoContext",
            "xaml-binding-outcome");
        unknown.FullName.Should().Be("unknown");
        unknownEvents.OfType<IndexEvent.AnnotationAttached>().Should().NotContain(annotation =>
            annotation.Flavor == "xaml-binding-finding");

        const string incompleteCsharp = """
            namespace Test;
            public sealed class Vm
            {
                public object Broken => DoesNotExist.Value;
            }
            """;
        var incompleteXaml = $$"""
            <Window xmlns="{{PresentationNamespace}}"
                    xmlns:x="{{XamlNamespace}}"
                    xmlns:vm="clr-namespace:Test"
                    x:DataType="vm:Vm">
                <TextBlock x:Name="Incomplete" Text="{Binding Absent}" />
            </Window>
            """;
        var incompleteEvents = await IndexAsync(incompleteCsharp, incompleteXaml);
        var incomplete = AnnotationFor(
            incompleteEvents,
            "Incomplete",
            "xaml-binding-outcome");
        incomplete.FullName.Should().Be("incomplete");
        incompleteEvents.OfType<IndexEvent.AnnotationAttached>().Should().NotContain(annotation =>
            annotation.Flavor == "xaml-binding-finding");
    }

    [Fact]
    public async Task AmbiguousMemberAndTypeAreExplicitWithoutTargetsOrFindings()
    {
        const string duplicateMemberCsharp = """
            namespace Test;
            public sealed class Vm
            {
                public string Value { get; } = "";
                public int Value { get; } = 0;
            }
            """;
        var memberXaml = $$"""
            <Window xmlns="{{PresentationNamespace}}"
                    xmlns:x="{{XamlNamespace}}"
                    xmlns:vm="clr-namespace:Test"
                    x:DataType="vm:Vm">
                <TextBlock x:Name="AmbiguousMember" Text="{Binding Value}" />
            </Window>
            """;
        var memberEvents = await IndexAsync(duplicateMemberCsharp, memberXaml);
        AnnotationFor(
                memberEvents,
                "AmbiguousMember",
                "xaml-binding-outcome")
            .FullName.Should().Be("ambiguous");
        memberEvents.OfType<IndexEvent.EdgeEmitted>().Should().NotContain(edge =>
            edge.SourceCanonicalKey.EndsWith(
                "#AmbiguousMember",
                StringComparison.Ordinal)
            && edge.EdgeKindName == "binds-path");
        memberEvents.OfType<IndexEvent.AnnotationAttached>().Should().NotContain(annotation =>
            annotation.Flavor == "xaml-binding-finding");

        var firstVm = CompileReference(
            "FirstVm",
            "namespace Duplicate; public sealed class Vm { public string Value => \"\"; }");
        var secondVm = CompileReference(
            "SecondVm",
            "namespace Duplicate; public sealed class Vm { public string Value => \"\"; }");
        var typeXaml = $$"""
            <Window xmlns="{{PresentationNamespace}}"
                    xmlns:x="{{XamlNamespace}}"
                    xmlns:vm="clr-namespace:Duplicate"
                    x:DataType="vm:Vm">
                <TextBlock x:Name="AmbiguousType" Text="{Binding Value}" />
            </Window>
            """;
        var typeEvents = await IndexAsync(
            "namespace Consumer; public sealed class Anchor { }",
            typeXaml,
            firstVm,
            secondVm);
        AnnotationFor(
                typeEvents,
                "AmbiguousType",
                "xaml-binding-outcome")
            .FullName.Should().Be("ambiguous");
        typeEvents.OfType<IndexEvent.AnnotationAttached>().Should().NotContain(annotation =>
            annotation.Flavor == "xaml-binding-finding");
    }

    [Fact]
    public async Task CommandResolutionDistinguishesResolvedMissingAndUnsupported()
    {
        const string csharp = """
            using System.Windows.Input;
            namespace Test;
            public sealed class Vm
            {
                public ICommand Save { get; } = null!;
                public string NotACommand { get; } = "";
            }
            """;
        var xaml = $$"""
            <Window xmlns="{{PresentationNamespace}}"
                    xmlns:x="{{XamlNamespace}}"
                    xmlns:vm="clr-namespace:Test"
                    x:DataType="vm:Vm">
                <Button x:Name="ResolvedCommand" Command="{Binding Save}" />
                <Button x:Name="MissingCommand" Command="{Binding Absent}" />
                <Button x:Name="UnsupportedCommand" Command="{Binding NotACommand}" />
            </Window>
            """;

        var events = await IndexAsync(csharp, xaml);

        events.OfType<IndexEvent.EdgeEmitted>().Should().ContainSingle(edge =>
            edge.SourceCanonicalKey.EndsWith(
                "#ResolvedCommand",
                StringComparison.Ordinal)
            && edge.TargetCanonicalKey == "csharp:P:Test.Vm.Save"
            && edge.EdgeKindName == "binds-path"
            && edge.Evidence!.Confidence == EvidenceConfidence.Semantic);
        AnnotationFor(events, "MissingCommand", "xaml-command-finding")
            .FullName.Should().Be("XAMLCOMMAND001");
        AnnotationFor(events, "UnsupportedCommand", "xaml-command-outcome")
            .FullName.Should().Be("unsupported");
        events.OfType<IndexEvent.AnnotationAttached>().Should().NotContain(annotation =>
            annotation.SymbolCanonicalKey.EndsWith(
                "#UnsupportedCommand",
                StringComparison.Ordinal)
            && annotation.Flavor == "xaml-command-finding");
    }

    [Fact]
    public async Task ExplicitBindingSourcesAreUnsupportedAndNeverReuseDataContext()
    {
        const string csharp = """
            namespace Test;
            public sealed class Vm
            {
                public string Existing { get; } = "";
            }
            """;
        var xaml = $$$"""
            <Window xmlns="{{{PresentationNamespace}}}"
                    xmlns:x="{{{XamlNamespace}}}"
                    xmlns:vm="clr-namespace:Test"
                    x:DataType="vm:Vm">
                <TextBlock x:Name="Other" />
                <TextBlock x:Name="Element"
                           Text="{Binding Existing, ElementName=Other}" />
                <TextBlock x:Name="Relative"
                           Text="{Binding Existing, RelativeSource={RelativeSource Self}}" />
                <TextBlock x:Name="Explicit"
                           Text="{Binding Existing, Source={StaticResource Source}}" />
                <TextBlock x:Name="Compiled"
                           Text="{x:Bind Existing}" />
            </Window>
            """;

        var events = await IndexAsync(csharp, xaml);

        foreach (var name in new[] { "Element", "Relative", "Explicit", "Compiled" })
        {
            AnnotationFor(events, name, "xaml-binding-outcome")
                .FullName.Should().Be("unsupported");
            events.OfType<IndexEvent.EdgeEmitted>().Should().NotContain(edge =>
                edge.SourceCanonicalKey.EndsWith(
                    "#" + name,
                    StringComparison.Ordinal)
                && edge.EdgeKindName == "binds-path");
        }
        events.OfType<IndexEvent.AnnotationAttached>().Should().NotContain(annotation =>
            annotation.Flavor == "xaml-binding-finding");
    }

    [Fact]
    public async Task InheritedInterfacePropertyIsResolvedInsteadOfReportedMissing()
    {
        const string csharp = """
            namespace Test;
            public interface IBaseVm
            {
                string Existing { get; }
            }
            public interface IDerivedVm : IBaseVm { }
            """;
        var xaml = $$"""
            <Window xmlns="{{PresentationNamespace}}"
                    xmlns:x="{{XamlNamespace}}"
                    xmlns:vm="clr-namespace:Test"
                    x:DataType="vm:IDerivedVm">
                <TextBlock x:Name="Inherited" Text="{Binding Existing}" />
            </Window>
            """;

        var events = await IndexAsync(csharp, xaml);

        events.OfType<IndexEvent.EdgeEmitted>().Should().ContainSingle(edge =>
            edge.SourceCanonicalKey.EndsWith("#Inherited", StringComparison.Ordinal)
            && edge.EdgeKindName == "binds-path"
            && edge.TargetCanonicalKey == "csharp:P:Test.IBaseVm.Existing");
        events.OfType<IndexEvent.AnnotationAttached>().Should().NotContain(annotation =>
            annotation.SymbolCanonicalKey.EndsWith(
                "#Inherited",
                StringComparison.Ordinal)
            && annotation.Flavor == "xaml-binding-finding");
    }

    [Fact]
    public async Task MultiTargetProjectSelectsTheOnlyProvenWpfCompilation()
    {
        var xaml = $$"""
            <Window xmlns="{{PresentationNamespace}}"
                    xmlns:x="{{XamlNamespace}}"
                    xmlns:vm="clr-namespace:Test"
                    x:DataType="vm:Vm">
                <TextBlock x:Name="WpfOnly" Text="{Binding Existing}" />
            </Window>
            """;
        var events = await IndexMultiTargetAsync(
            [
                """
                namespace Test
                {
                    public sealed class Vm { }
                }
                """,
                """
                namespace System.Windows
                {
                    public class Application { }
                }
                namespace Test
                {
                    public sealed class Vm
                    {
                        public string Existing { get; } = "";
                    }
                }
                """,
            ],
            xaml,
            semanticInputComplete: true);

        events.OfType<IndexEvent.EdgeEmitted>().Should().ContainSingle(edge =>
            edge.SourceCanonicalKey.EndsWith("#WpfOnly", StringComparison.Ordinal)
            && edge.EdgeKindName == "binds-path"
            && edge.TargetCanonicalKey == "csharp:P:Test.Vm.Existing");
        events.OfType<IndexEvent.AnnotationAttached>().Should().NotContain(annotation =>
            annotation.SymbolCanonicalKey.EndsWith(
                "#WpfOnly",
                StringComparison.Ordinal)
            && (annotation.Flavor == "xaml-binding-finding"
                || annotation.Flavor == "xaml-binding-outcome"));
    }

    [Fact]
    public async Task MultipleWpfTargetIterationsFailClosedAsIncomplete()
    {
        var xaml = $$"""
            <Window xmlns="{{PresentationNamespace}}"
                    xmlns:x="{{XamlNamespace}}"
                    xmlns:vm="clr-namespace:Test"
                    x:DataType="vm:Vm">
                <TextBlock x:Name="Divergent" Text="{Binding Existing}" />
            </Window>
            """;
        var events = await IndexMultiTargetAsync(
            [
                """
                namespace System.Windows { public class Application { } }
                namespace Test
                {
                    public sealed class Vm
                    {
                        public string Existing { get; } = "";
                    }
                }
                """,
                """
                namespace System.Windows { public class Application { } }
                namespace Test { public sealed class Vm { } }
                """,
            ],
            xaml,
            semanticInputComplete: true);

        AnnotationFor(events, "Divergent", "xaml-binding-outcome")
            .FullName.Should().Be("incomplete");
        events.OfType<IndexEvent.EdgeEmitted>().Should().NotContain(edge =>
            edge.SourceCanonicalKey.EndsWith("#Divergent", StringComparison.Ordinal)
            && edge.EdgeKindName == "binds-path");
        events.OfType<IndexEvent.AnnotationAttached>().Should().NotContain(annotation =>
            annotation.SymbolCanonicalKey.EndsWith(
                "#Divergent",
                StringComparison.Ordinal)
            && annotation.Flavor == "xaml-binding-finding");
    }

    [Fact]
    public async Task SanitizedSemanticInputCannotProveMissingBindingMember()
    {
        var xaml = $$"""
            <Window xmlns="{{PresentationNamespace}}"
                    xmlns:x="{{XamlNamespace}}"
                    xmlns:vm="clr-namespace:Test"
                    x:DataType="vm:Vm">
                <TextBlock x:Name="Sanitized" Text="{Binding ExcludedMember}" />
            </Window>
            """;
        var events = await IndexMultiTargetAsync(
            ["namespace Test { public sealed class Vm { } }"],
            xaml,
            semanticInputComplete: false);

        AnnotationFor(events, "Sanitized", "xaml-binding-outcome")
            .FullName.Should().Be("incomplete");
        events.OfType<IndexEvent.AnnotationAttached>().Should().NotContain(annotation =>
            annotation.SymbolCanonicalKey.EndsWith(
                "#Sanitized",
                StringComparison.Ordinal)
            && annotation.Flavor == "xaml-binding-finding");
    }

    [Fact]
    public async Task AttachedPropertyBindingStillEmitsSemanticEdge()
    {
        const string csharp = """
            namespace Test;

            public sealed class Vm
            {
                public string Existing { get; } = "";
            }

            public static class Probe
            {
                public static object? Value { get; set; }
            }
            """;
        var xaml = $$"""
            <Window xmlns="{{PresentationNamespace}}"
                    xmlns:x="{{XamlNamespace}}"
                    xmlns:local="clr-namespace:Test"
                    x:DataType="local:Vm">
                <TextBlock x:Name="AttachedBinding"
                           local:Probe.Value="{Binding Existing}" />
            </Window>
            """;

        var events = await IndexAsync(csharp, xaml);

        events.OfType<IndexEvent.EdgeEmitted>().Should().ContainSingle(edge =>
            edge.SourceCanonicalKey.EndsWith(
                "#AttachedBinding",
                StringComparison.Ordinal)
            && edge.EdgeKindName == "binds-path"
            && edge.TargetCanonicalKey == CanonicalKeys.ForProperty(
                "Test.Vm",
                "Existing"));
        events.OfType<IndexEvent.AnnotationAttached>().Should().Contain(
            annotation =>
                annotation.SymbolCanonicalKey.EndsWith(
                    "#AttachedBinding",
                    StringComparison.Ordinal)
                && annotation.Flavor == "xaml-attached-property");
    }

    [Fact]
    public async Task RoslynFactoryWithoutCompletenessProbeFailsClosed()
    {
        var xaml = $$"""
            <Window xmlns="{{PresentationNamespace}}"
                    xmlns:x="{{XamlNamespace}}"
                    xmlns:vm="clr-namespace:Test"
                    x:DataType="vm:Vm">
                <TextBlock x:Name="UnprovenInputs"
                           Text="{Binding Existing}" />
            </Window>
            """;
        var events = await IndexMultiTargetAsync(
            [
                """
                namespace Test
                {
                    public sealed class Vm
                    {
                        public string Existing { get; } = "";
                    }
                }
                """,
            ],
            xaml,
            semanticInputComplete: null);

        AnnotationFor(events, "UnprovenInputs", "xaml-binding-outcome")
            .FullName.Should().Be("incomplete");
        events.OfType<IndexEvent.EdgeEmitted>().Should().NotContain(edge =>
            edge.SourceCanonicalKey.EndsWith(
                "#UnprovenInputs",
                StringComparison.Ordinal)
            && edge.EdgeKindName == "binds-path");
    }

    [Fact]
    public async Task SanitizedSemanticInputCannotEmitAResolvedBaseMemberEdge()
    {
        var xaml = $$"""
            <Window xmlns="{{PresentationNamespace}}"
                    xmlns:x="{{XamlNamespace}}"
                    xmlns:vm="clr-namespace:Test"
                    x:DataType="vm:Vm">
                <TextBlock x:Name="Hidden" Text="{Binding Existing}" />
            </Window>
            """;
        var events = await IndexMultiTargetAsync(
            [
                """
                namespace Test
                {
                    public class BaseVm
                    {
                        public string Existing { get; } = "";
                    }
                    public sealed partial class Vm : BaseVm { }
                }
                """,
            ],
            xaml,
            semanticInputComplete: false,
            semanticPositiveResolutionSafe: true);

        AnnotationFor(events, "Hidden", "xaml-binding-outcome")
            .FullName.Should().Be("incomplete");
        events.OfType<IndexEvent.EdgeEmitted>().Should().NotContain(edge =>
            edge.SourceCanonicalKey.EndsWith("#Hidden", StringComparison.Ordinal)
            && edge.EdgeKindName == "binds-path");
    }

    [Fact]
    public async Task BuildGeneratedOmissionCanResolveDirectMemberButNotMissingMember()
    {
        var xaml = $$"""
            <Window xmlns="{{PresentationNamespace}}"
                    xmlns:x="{{XamlNamespace}}"
                    xmlns:vm="clr-namespace:Test"
                    x:DataType="vm:Vm">
                <TextBlock x:Name="Direct" Text="{Binding Existing}" />
                <TextBlock x:Name="Absent" Text="{Binding Missing}" />
            </Window>
            """;
        var events = await IndexMultiTargetAsync(
            [
                """
                namespace Test
                {
                    public sealed partial class Vm
                    {
                        public string Existing { get; } = "";
                    }
                }
                """,
            ],
            xaml,
            semanticInputComplete: false,
            semanticPositiveResolutionSafe: true);

        events.OfType<IndexEvent.EdgeEmitted>().Should().ContainSingle(edge =>
            edge.SourceCanonicalKey.EndsWith("#Direct", StringComparison.Ordinal)
            && edge.EdgeKindName == "binds-path"
            && edge.TargetCanonicalKey == CanonicalKeys.ForProperty(
                "Test.Vm",
                "Existing"));
        AnnotationFor(events, "Absent", "xaml-binding-outcome")
            .FullName.Should().Be("incomplete");
        events.OfType<IndexEvent.AnnotationAttached>().Should().NotContain(
            annotation =>
                annotation.SymbolCanonicalKey.EndsWith(
                    "#Absent",
                    StringComparison.Ordinal)
                && annotation.Flavor == "xaml-binding-finding");
    }

    [Fact]
    public async Task BuildGeneratedOmissionDoesNotAuthorizeEventHandlerInference()
    {
        var xaml = $$"""
            <Window xmlns="{{PresentationNamespace}}"
                    xmlns:x="{{XamlNamespace}}"
                    x:Class="Test.View">
                <Button x:Name="UnsafeEvent" Click="OnClick" />
            </Window>
            """;
        var events = await IndexMultiTargetAsync(
            [
                """
                namespace Test
                {
                    public partial class View
                    {
                        private void OnClick(
                            object sender,
                            System.EventArgs args) { }
                    }
                }
                """,
            ],
            xaml,
            semanticInputComplete: false,
            semanticPositiveResolutionSafe: true);

        events.OfType<IndexEvent.EdgeEmitted>().Should().NotContain(edge =>
            edge.SourceCanonicalKey.EndsWith(
                "#UnsafeEvent",
                StringComparison.Ordinal)
            && edge.EdgeKindName == "handles-event");
    }

    [Fact]
    public async Task ReferencedProjectErrorsMakeBindingSemanticsIncomplete()
    {
        var xaml = $$"""
            <Window xmlns="{{PresentationNamespace}}"
                    xmlns:x="{{XamlNamespace}}"
                    xmlns:vm="clr-namespace:Referenced;assembly=ViewModels"
                    x:DataType="vm:Vm">
                <TextBlock x:Name="ReferencedError"
                           Text="{Binding Existing}" />
            </Window>
            """;
        var events = await IndexReferencedProjectAsync(
            """
            namespace Referenced
            {
                public class BaseVm
                {
                    public string Existing { get; } = "";
                }
                public sealed class Vm : BaseVm
                {
                    public object Broken => MissingType.Value;
                }
            }
            """,
            xaml);

        AnnotationFor(events, "ReferencedError", "xaml-binding-outcome")
            .FullName.Should().Be("incomplete");
        events.OfType<IndexEvent.EdgeEmitted>().Should().NotContain(edge =>
            edge.SourceCanonicalKey.EndsWith(
                "#ReferencedError",
                StringComparison.Ordinal)
            && edge.EdgeKindName == "binds-path");
        events.OfType<IndexEvent.AnnotationAttached>().Should().NotContain(annotation =>
            annotation.SymbolCanonicalKey.EndsWith(
                "#ReferencedError",
                StringComparison.Ordinal)
            && annotation.Flavor == "xaml-binding-finding");
    }

    [Fact]
    public async Task ThrowingProjectGeneratorMakesBindingSemanticsIncomplete()
    {
        const string csharp = """
            namespace Test;
            public sealed class Vm
            {
                public string Existing { get; } = "";
            }
            """;
        var xaml = $$"""
            <Window xmlns="{{PresentationNamespace}}"
                    xmlns:x="{{XamlNamespace}}"
                    xmlns:vm="clr-namespace:Test"
                    x:DataType="vm:Vm">
                <TextBlock x:Name="GeneratorFailure"
                           Text="{Binding Existing}" />
            </Window>
            """;

        var events = await IndexAsync(
            csharp,
            xaml,
            includeThrowingGenerator: true);

        AnnotationFor(events, "GeneratorFailure", "xaml-binding-outcome")
            .FullName.Should().Be("incomplete");
        events.OfType<IndexEvent.EdgeEmitted>().Should().NotContain(edge =>
            edge.SourceCanonicalKey.EndsWith(
                "#GeneratorFailure",
                StringComparison.Ordinal)
            && edge.EdgeKindName == "binds-path");
        events.OfType<IndexEvent.AnnotationAttached>().Should().NotContain(annotation =>
            annotation.SymbolCanonicalKey.EndsWith(
                "#GeneratorFailure",
                StringComparison.Ordinal)
            && annotation.Flavor == "xaml-binding-finding");
    }

    [Fact]
    public async Task WorkspaceGeneratorFailureIsNotHiddenByASecondRun()
    {
        const string csharp = """
            namespace Test;
            public sealed class Vm
            {
                public string Existing { get; } = "";
            }
            """;
        var xaml = $$"""
            <Window xmlns="{{PresentationNamespace}}"
                    xmlns:x="{{XamlNamespace}}"
                    xmlns:vm="clr-namespace:Test"
                    x:DataType="vm:Vm">
                <TextBlock x:Name="StatefulGeneratorFailure"
                           Text="{Binding Existing}" />
            </Window>
            """;
        var reference = new ThrowOnceGeneratorReference();

        var events = await IndexWithAnalyzerReferenceAsync(
            csharp,
            xaml,
            reference);

        reference.ExecutionCount.Should().Be(
            1,
            "semantic validation must inspect Roslyn's original workspace run");
        AnnotationFor(events, "StatefulGeneratorFailure", "xaml-binding-outcome")
            .FullName.Should().Be("incomplete");
        events.OfType<IndexEvent.EdgeEmitted>().Should().NotContain(edge =>
            edge.SourceCanonicalKey.EndsWith(
                "#StatefulGeneratorFailure",
                StringComparison.Ordinal)
            && edge.EdgeKindName == "binds-path");
        events.OfType<IndexEvent.AnnotationAttached>().Should().NotContain(annotation =>
            annotation.SymbolCanonicalKey.EndsWith(
                "#StatefulGeneratorFailure",
                StringComparison.Ordinal)
            && annotation.Flavor == "xaml-binding-finding");
    }

    [Fact]
    public async Task AnalyzerFileReferenceLoadFailureMakesBindingSemanticsIncomplete()
    {
        const string csharp = """
            namespace Test;
            public sealed class Vm
            {
                public string Existing { get; } = "";
            }
            """;
        var xaml = $$"""
            <Window xmlns="{{PresentationNamespace}}"
                    xmlns:x="{{XamlNamespace}}"
                    xmlns:vm="clr-namespace:Test"
                    x:DataType="vm:Vm">
                <TextBlock x:Name="AnalyzerLoadFailure"
                           Text="{Binding Existing}" />
            </Window>
            """;
        var root = Path.Combine(
            Path.GetTempPath(),
            "sourcegraph-failing-analyzer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var assemblyPath = Path.Combine(root, "Generator.dll");
            CompileGeneratorAssembly(assemblyPath);
            var loader = new FailingAnalyzerAssemblyLoader();
            var reference = new AnalyzerFileReference(assemblyPath, loader);

            var events = await IndexWithAnalyzerReferenceAsync(
                csharp,
                xaml,
                reference);

            loader.LoadAttemptCount.Should().BeGreaterThan(0);
            AnnotationFor(events, "AnalyzerLoadFailure", "xaml-binding-outcome")
                .FullName.Should().Be("incomplete");
            events.OfType<IndexEvent.EdgeEmitted>().Should().NotContain(edge =>
                edge.SourceCanonicalKey.EndsWith(
                    "#AnalyzerLoadFailure",
                    StringComparison.Ordinal)
                && edge.EdgeKindName == "binds-path");
            events.OfType<IndexEvent.AnnotationAttached>().Should().NotContain(annotation =>
                annotation.SymbolCanonicalKey.EndsWith(
                    "#AnalyzerLoadFailure",
                    StringComparison.Ordinal)
                && annotation.Flavor == "xaml-binding-finding");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ThrowingReferencedProjectGeneratorMakesBindingSemanticsIncomplete()
    {
        var xaml = $$"""
            <Window xmlns="{{PresentationNamespace}}"
                    xmlns:x="{{XamlNamespace}}"
                    xmlns:vm="clr-namespace:Referenced;assembly=ViewModels"
                    x:DataType="vm:Vm">
                <TextBlock x:Name="ReferencedGeneratorFailure"
                           Text="{Binding Existing}" />
            </Window>
            """;
        var events = await IndexReferencedProjectAsync(
            """
            namespace Referenced
            {
                public sealed class Vm
                {
                    public string Existing { get; } = "";
                }
            }
            """,
            xaml,
            includeThrowingGenerator: true);

        AnnotationFor(
                events,
                "ReferencedGeneratorFailure",
                "xaml-binding-outcome")
            .FullName.Should().Be("incomplete");
        events.OfType<IndexEvent.EdgeEmitted>().Should().NotContain(edge =>
            edge.SourceCanonicalKey.EndsWith(
                "#ReferencedGeneratorFailure",
                StringComparison.Ordinal)
            && edge.EdgeKindName == "binds-path");
        events.OfType<IndexEvent.AnnotationAttached>().Should().NotContain(annotation =>
            annotation.SymbolCanonicalKey.EndsWith(
                "#ReferencedGeneratorFailure",
                StringComparison.Ordinal)
            && annotation.Flavor == "xaml-binding-finding");
    }

    [Fact]
    public async Task ArrayDataContextBindingIsUnsupportedRatherThanResolvedWithoutTarget()
    {
        const string csharp = """
            namespace Test;
            public sealed class Vm
            {
                public string[] Items { get; } = [];
            }
            """;
        var xaml = $$"""
            <Window xmlns="{{PresentationNamespace}}"
                    xmlns:x="{{XamlNamespace}}"
                    xmlns:vm="clr-namespace:Test"
                    x:DataType="vm:Vm">
                <Grid DataContext="{Binding Items}">
                    <TextBlock x:Name="ArrayContext"
                               Text="{Binding Length}" />
                </Grid>
            </Window>
            """;

        var events = await IndexAsync(csharp, xaml);

        AnnotationFor(events, "ArrayContext", "xaml-binding-outcome")
            .FullName.Should().Be("unsupported");
        events.OfType<IndexEvent.AnnotationAttached>().Should().NotContain(annotation =>
            annotation.SymbolCanonicalKey.EndsWith(
                "#ArrayContext",
                StringComparison.Ordinal)
            && annotation.FullName == "resolved");
        events.OfType<IndexEvent.EdgeEmitted>().Should().NotContain(edge =>
            edge.SourceCanonicalKey.EndsWith(
                "#ArrayContext",
                StringComparison.Ordinal)
            && edge.EdgeKindName == "binds-path");
    }

    private static IndexEvent.AnnotationAttached AnnotationFor(
        IReadOnlyList<IndexEvent> events,
        string elementName,
        string flavor) =>
        events.OfType<IndexEvent.AnnotationAttached>()
            .Should().ContainSingle(annotation =>
                annotation.SymbolCanonicalKey.EndsWith(
                    "#" + elementName,
                    StringComparison.Ordinal)
                && annotation.Flavor == flavor).Subject;

    private static Task<IReadOnlyList<IndexEvent>> IndexAsync(
        string csharp,
        string xaml,
        params MetadataReference[] additionalReferences) =>
        IndexAsync(
            csharp,
            xaml,
            includeThrowingGenerator: false,
            additionalReferences);

    private static async Task<IReadOnlyList<IndexEvent>> IndexAsync(
        string csharp,
        string xaml,
        bool includeThrowingGenerator,
        params MetadataReference[] additionalReferences)
    {
        return await IndexCoreAsync(
            csharp,
            xaml,
            includeThrowingGenerator
                ? new ThrowingGeneratorReference()
                : null,
            additionalReferences);
    }

    private static Task<IReadOnlyList<IndexEvent>>
        IndexWithAnalyzerReferenceAsync(
            string csharp,
            string xaml,
            AnalyzerReference analyzerReference) =>
        IndexCoreAsync(
            csharp,
            xaml,
            analyzerReference,
            Array.Empty<MetadataReference>());

    private static async Task<IReadOnlyList<IndexEvent>> IndexCoreAsync(
        string csharp,
        string xaml,
        AnalyzerReference? analyzerReference,
        IReadOnlyList<MetadataReference> additionalReferences)
    {
        using var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var references = PlatformReferences()
            .Concat(additionalReferences)
            .ToArray();
        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                "XamlOutcomeFixture",
                "XamlOutcomeFixture",
                LanguageNames.CSharp,
                filePath: Path.Combine(Path.GetTempPath(), "XamlOutcomeFixture.csproj"),
                compilationOptions: new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary),
                parseOptions: CSharpParseOptions.Default,
                metadataReferences: references))
            .AddDocument(
                DocumentId.CreateNewId(projectId),
                "Fixture.cs",
                csharp,
                filePath: Path.Combine(Path.GetTempPath(), "Fixture.cs"));
        if (analyzerReference is not null)
        {
            solution = solution.AddAnalyzerReference(
                projectId,
                analyzerReference);
        }
        var roslynProject = solution.GetProject(projectId)!;
        var root = Path.Combine(
            Path.GetTempPath(),
            "sourcegraph-xaml-outcome-" + Guid.NewGuid().ToString("N"));
        var viewPath = Path.Combine(root, "View.xaml");
        var emptyResources =
            new ReadOnlyDictionary<string, IReadOnlyList<ResourceDefinition>>(
                new Dictionary<string, IReadOnlyList<ResourceDefinition>>(
                    StringComparer.Ordinal));
        var xamlProject = new XamlLanguageProject(
            Path.Combine(root, "Fixture.csproj"),
            new[] { viewPath },
            emptyResources,
            resourceSnapshotBuilder: null,
            () => roslynProject);

        return await new XamlLanguageIndexer().IndexAsync(
            new IndexContext(
                viewPath,
                Encoding.UTF8.GetBytes(xaml),
                "test",
                root,
                xamlProject),
            CancellationToken.None);
    }

    private static async Task<IReadOnlyList<IndexEvent>> IndexMultiTargetAsync(
        IReadOnlyList<string> targetSources,
        string xaml,
        bool? semanticInputComplete,
        bool? semanticPositiveResolutionSafe = null)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "sourcegraph-xaml-multitarget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var projectPath = Path.Combine(root, "Fixture.csproj");
        var viewPath = Path.Combine(root, "View.xaml");
        try
        {
            await File.WriteAllTextAsync(
                projectPath,
                "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            await File.WriteAllTextAsync(viewPath, xaml);

            using var workspace = new AdhocWorkspace();
            var solution = workspace.CurrentSolution;
            for (var index = 0; index < targetSources.Count; index++)
            {
                var projectId = ProjectId.CreateNewId();
                solution = solution
                    .AddProject(ProjectInfo.Create(
                        projectId,
                        VersionStamp.Create(),
                        "Target" + index,
                        "Target" + index,
                        LanguageNames.CSharp,
                        filePath: projectPath,
                        compilationOptions: new CSharpCompilationOptions(
                            OutputKind.DynamicallyLinkedLibrary),
                        parseOptions: CSharpParseOptions.Default,
                        metadataReferences: PlatformReferences()))
                    .AddDocument(
                        DocumentId.CreateNewId(projectId),
                        "Target" + index + ".cs",
                        targetSources[index],
                        filePath: Path.Combine(root, "Target" + index + ".cs"));
            }

            var factory = semanticInputComplete is { } isComplete
                ? new XamlLanguageProjectFactory(
                    () => solution,
                    _ => isComplete,
                    _ => semanticPositiveResolutionSafe ?? isComplete)
                : new XamlLanguageProjectFactory(() => solution);
            var projects = await factory.DiscoverAsync(root, default);
            var xamlProject = projects.Should()
                .ContainSingle()
                .Subject.Should()
                .BeOfType<XamlLanguageProject>()
                .Subject;

            return await new XamlLanguageIndexer().IndexAsync(
                new IndexContext(
                    viewPath,
                    Encoding.UTF8.GetBytes(xaml),
                    "test",
                    root,
                    xamlProject),
                CancellationToken.None);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task<IReadOnlyList<IndexEvent>> IndexReferencedProjectAsync(
        string referencedSource,
        string xaml,
        bool includeThrowingGenerator = false)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "sourcegraph-xaml-project-reference-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var appProjectPath = Path.Combine(root, "Fixture.csproj");
        var referencedProjectPath = Path.Combine(root, "ViewModels.csproj");
        var viewPath = Path.Combine(root, "View.xaml");
        try
        {
            await File.WriteAllTextAsync(
                appProjectPath,
                "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            await File.WriteAllTextAsync(viewPath, xaml);

            using var workspace = new AdhocWorkspace();
            var appId = ProjectId.CreateNewId();
            var referencedId = ProjectId.CreateNewId();
            var solution = workspace.CurrentSolution
                .AddProject(ProjectInfo.Create(
                    appId,
                    VersionStamp.Create(),
                    "App",
                    "App",
                    LanguageNames.CSharp,
                    filePath: appProjectPath,
                    compilationOptions: new CSharpCompilationOptions(
                        OutputKind.DynamicallyLinkedLibrary),
                    metadataReferences: PlatformReferences()))
                .AddProject(ProjectInfo.Create(
                    referencedId,
                    VersionStamp.Create(),
                    "ViewModels",
                    "ViewModels",
                    LanguageNames.CSharp,
                    filePath: referencedProjectPath,
                    compilationOptions: new CSharpCompilationOptions(
                        OutputKind.DynamicallyLinkedLibrary),
                    metadataReferences: PlatformReferences()))
                .AddProjectReference(appId, new ProjectReference(referencedId))
                .AddDocument(
                    DocumentId.CreateNewId(appId),
                    "App.cs",
                    "internal sealed class AppAnchor { }",
                    filePath: Path.Combine(root, "App.cs"))
                .AddDocument(
                    DocumentId.CreateNewId(referencedId),
                    "ViewModels.cs",
                    referencedSource,
                    filePath: Path.Combine(root, "ViewModels.cs"));
            if (includeThrowingGenerator)
            {
                solution = solution.AddAnalyzerReference(
                    referencedId,
                    new ThrowingGeneratorReference());
            }

            var factory = new XamlLanguageProjectFactory(
                () => solution,
                _ => true);
            var xamlProject = (await factory.DiscoverAsync(root, default))
                .Should().ContainSingle().Subject.Should()
                .BeOfType<XamlLanguageProject>().Subject;
            return await new XamlLanguageIndexer().IndexAsync(
                new IndexContext(
                    viewPath,
                    Encoding.UTF8.GetBytes(xaml),
                    "test",
                    root,
                    xamlProject),
                CancellationToken.None);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private sealed class ThrowingGeneratorReference : AnalyzerReference
    {
        private static readonly ImmutableArray<ISourceGenerator> Generators =
            ImmutableArray.Create<ISourceGenerator>(new ThrowingGenerator());

        public override string FullPath => "throwing-generator";

        public override object Id => FullPath;

        public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(
            string language) =>
            ImmutableArray<DiagnosticAnalyzer>.Empty;

        public override ImmutableArray<DiagnosticAnalyzer>
            GetAnalyzersForAllLanguages() =>
            ImmutableArray<DiagnosticAnalyzer>.Empty;

        public override ImmutableArray<ISourceGenerator> GetGenerators(
            string language) =>
            language == LanguageNames.CSharp
                ? Generators
                : ImmutableArray<ISourceGenerator>.Empty;

        public override ImmutableArray<ISourceGenerator>
            GetGeneratorsForAllLanguages() =>
            Generators;
    }

    private sealed class ThrowOnceGeneratorReference : AnalyzerReference
    {
        private readonly ThrowOnceGenerator _generator = new();

        public int ExecutionCount => _generator.ExecutionCount;

        public override string FullPath => "throw-once-generator";

        public override object Id => FullPath;

        public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(
            string language) =>
            ImmutableArray<DiagnosticAnalyzer>.Empty;

        public override ImmutableArray<DiagnosticAnalyzer>
            GetAnalyzersForAllLanguages() =>
            ImmutableArray<DiagnosticAnalyzer>.Empty;

        public override ImmutableArray<ISourceGenerator> GetGenerators(
            string language) =>
            language == LanguageNames.CSharp
                ? ImmutableArray.Create<ISourceGenerator>(_generator)
                : ImmutableArray<ISourceGenerator>.Empty;

        public override ImmutableArray<ISourceGenerator>
            GetGeneratorsForAllLanguages() =>
            ImmutableArray.Create<ISourceGenerator>(_generator);
    }

    private sealed class FailingAnalyzerAssemblyLoader : IAnalyzerAssemblyLoader
    {
        private int _loadAttemptCount;

        public int LoadAttemptCount => Volatile.Read(ref _loadAttemptCount);

        public void AddDependencyLocation(string fullPath)
        {
        }

        public System.Reflection.Assembly LoadFromPath(string fullPath)
        {
            Interlocked.Increment(ref _loadAttemptCount);
            throw new FileLoadException(
                "Intentional analyzer load failure.",
                fullPath);
        }
    }

#pragma warning disable RS1042 // Test-only in-memory generator; it is never shipped or loaded from this net10.0 assembly.
    private sealed class ThrowingGenerator : ISourceGenerator
    {
        public void Initialize(GeneratorInitializationContext context)
        {
        }

        public void Execute(GeneratorExecutionContext context) =>
            throw new InvalidOperationException(
                "Intentional source-generator failure.");
    }

    private sealed class ThrowOnceGenerator : ISourceGenerator
    {
        private int _executionCount;

        public int ExecutionCount => Volatile.Read(ref _executionCount);

        public void Initialize(GeneratorInitializationContext context)
        {
        }

        public void Execute(GeneratorExecutionContext context)
        {
            if (Interlocked.Increment(ref _executionCount) == 1)
            {
                throw new InvalidOperationException(
                    "Intentional first-run source-generator failure.");
            }
        }
    }
#pragma warning restore RS1042

    private static PortableExecutableReference CompileReference(
        string assemblyName,
        string source)
    {
        var compilation = CSharpCompilation.Create(
            assemblyName,
            new[] { CSharpSyntaxTree.ParseText(source) },
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var image = new MemoryStream();
        var result = compilation.Emit(image);
        result.Success.Should().BeTrue(
            string.Join(
                Environment.NewLine,
                result.Diagnostics.Select(diagnostic => diagnostic.ToString())));
        return MetadataReference.CreateFromImage(image.ToArray());
    }

    private static void CompileGeneratorAssembly(string assemblyPath)
    {
        var codeAnalysisPath = typeof(ISourceGenerator).Assembly.Location;
        var references = PlatformReferences()
            .Where(reference => !string.Equals(
                reference.Display,
                codeAnalysisPath,
                StringComparison.OrdinalIgnoreCase))
            .Append(MetadataReference.CreateFromFile(codeAnalysisPath));
        var compilation = CSharpCompilation.Create(
            Path.GetFileNameWithoutExtension(assemblyPath),
            new[]
            {
                CSharpSyntaxTree.ParseText(
                    """
                    using Microsoft.CodeAnalysis;

                    [Generator]
                    public sealed class FixtureGenerator : ISourceGenerator
                    {
                        public void Initialize(GeneratorInitializationContext context)
                        {
                        }

                        public void Execute(GeneratorExecutionContext context)
                        {
                        }
                    }
                    """),
            },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var result = compilation.Emit(assemblyPath);
        result.Success.Should().BeTrue(
            string.Join(
                Environment.NewLine,
                result.Diagnostics.Select(diagnostic => diagnostic.ToString())));
    }

    private static IReadOnlyList<MetadataReference> PlatformReferences() =>
        ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
         ?? throw new InvalidOperationException(
             "Trusted platform assemblies are unavailable."))
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(path => MetadataReference.CreateFromFile(path))
        .ToArray();
}
