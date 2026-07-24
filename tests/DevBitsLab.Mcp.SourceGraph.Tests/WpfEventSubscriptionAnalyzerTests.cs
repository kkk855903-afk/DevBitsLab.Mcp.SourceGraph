using DevBitsLab.Mcp.SourceGraph.Indexing.Wpf;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class WpfEventSubscriptionAnalyzerTests
{
    [Fact]
    public void CompleteCompilation_emitsTypedFindingAtDirectSubscription()
    {
        var compilation = Compile(
            """
            using System;

            internal static class AppLifetime
            {
                internal static event EventHandler? Changed;
            }

            internal sealed class View
            {
                internal void Attach()
                {
                    AppLifetime.Changed += OnChanged;
                }

                private void OnChanged(object? sender, EventArgs args) { }
            }
            """);

        var result = WpfEventSubscriptionAnalyzer.Analyze(
            compilation,
            semanticInputComplete: true);

        result.IsComplete.Should().BeTrue();
        result.Facts.Should().ContainSingle()
            .Which.Kind.Should().Be(WpfEventAssignmentKind.Subscription);
        var finding = result.Findings.Should().ContainSingle().Subject;
        finding.EventCanonicalKey.Should().Contain("AppLifetime.Changed");
        finding.HandlerCanonicalKey.Should().Contain("View.OnChanged");
        finding.SubscriberTypeCanonicalKey.Should().Contain("View");
        finding.SyntaxTree.FilePath.Should().Be("Fixture.cs");

        var diagnostic = finding.ToDiagnosticRecord(17, symbolId: 23);
        diagnostic.Code.Should().Be("WPFEVENT001");
        diagnostic.Severity.Should().Be((int)DiagnosticSeverity.Warning);
        diagnostic.FileId.Should().Be(17);
        diagnostic.SymbolId.Should().Be(23);
        diagnostic.Line.Should().Be(12);
        diagnostic.Col.Should().Be(29);
        diagnostic.Message.Should().Contain(finding.EventCanonicalKey);
        diagnostic.Message.Should().Contain(finding.HandlerCanonicalKey);
    }

    [Fact]
    public void ExactNamedRemovalAnywhereInCompleteCompilation_suppressesFinding()
    {
        var compilation = Compile(
            """
            using System;

            internal static class AppLifetime
            {
                internal static event EventHandler? Changed;
            }

            internal sealed partial class View
            {
                internal void Attach() =>
                    AppLifetime.Changed += OnChanged;

                private void OnChanged(object? sender, EventArgs args) { }
            }
            """,
            """
            internal sealed partial class View
            {
                internal void Detach() =>
                    AppLifetime.Changed -= OnChanged;
            }
            """);

        var result = WpfEventSubscriptionAnalyzer.Analyze(
            compilation,
            semanticInputComplete: true);

        result.IsComplete.Should().BeTrue();
        result.Facts.Select(fact => fact.Kind).Should().BeEquivalentTo(
            [
                WpfEventAssignmentKind.Subscription,
                WpfEventAssignmentKind.Unsubscription,
            ]);
        result.Findings.Should().BeEmpty();
        result.Unknowns.Should().BeEmpty();
    }

    [Fact]
    public void AliasRemoval_isUnknownAndCannotProveMissingUnsubscription()
    {
        var compilation = Compile(
            """
            using System;

            internal static class AppLifetime
            {
                internal static event EventHandler? Changed;
            }

            internal sealed class View
            {
                internal void Attach() =>
                    AppLifetime.Changed += OnChanged;

                internal void Detach()
                {
                    EventHandler alias = OnChanged;
                    AppLifetime.Changed -= alias;
                }

                private void OnChanged(object? sender, EventArgs args) { }
            }
            """);

        var result = WpfEventSubscriptionAnalyzer.Analyze(
            compilation,
            semanticInputComplete: true);

        result.IsComplete.Should().BeTrue();
        result.Facts.Should().ContainSingle(fact =>
            fact.Kind == WpfEventAssignmentKind.Subscription);
        result.Findings.Should().BeEmpty();
        result.Unknowns.Select(unknown => unknown.Reason).Should().Contain(
            [
                "handler-not-direct-named-instance",
                "matching-removal-ambiguous",
            ]);
    }

    [Fact]
    public void LambdaAndExternalStaticEventShapes_areUnknownNotFindings()
    {
        var compilation = Compile(
            """
            using System;

            internal static class AppLifetime
            {
                internal static event EventHandler? Changed;
            }

            internal sealed class View
            {
                internal void Attach()
                {
                    AppLifetime.Changed += (_, _) => { };
                    Console.CancelKeyPress += OnCancel;
                }

                private void OnCancel(
                    object? sender,
                    ConsoleCancelEventArgs args) { }
            }
            """);

        var result = WpfEventSubscriptionAnalyzer.Analyze(
            compilation,
            semanticInputComplete: true);

        result.IsComplete.Should().BeTrue();
        result.Facts.Should().BeEmpty();
        result.Findings.Should().BeEmpty();
        result.Unknowns.Select(unknown => unknown.Reason).Should().BeEquivalentTo(
            [
                "handler-not-direct-named-instance",
                "event-declaration-outside-compilation",
            ]);
    }

    [Fact]
    public void IncompleteOrBrokenCompilation_failsClosed()
    {
        var valid = Compile(
            """
            using System;

            internal static class AppLifetime
            {
                internal static event EventHandler? Changed;
            }

            internal sealed class View
            {
                internal void Attach() =>
                    AppLifetime.Changed += OnChanged;

                private void OnChanged(object? sender, EventArgs args) { }
            }
            """);
        var broken = CompileWithoutErrorAssertion(
            """
            using System;

            internal static class AppLifetime
            {
                internal static event EventHandler? Changed;
            }

            internal sealed class View
            {
                internal void Attach()
                {
                    Missing();
                    AppLifetime.Changed += OnChanged;
                }

                private void OnChanged(object? sender, EventArgs args) { }
            }
            """);

        var incomplete = WpfEventSubscriptionAnalyzer.Analyze(
            valid,
            semanticInputComplete: false);
        var erroneous = WpfEventSubscriptionAnalyzer.Analyze(
            broken,
            semanticInputComplete: true);

        incomplete.IsComplete.Should().BeFalse();
        incomplete.Facts.Should().BeEmpty();
        incomplete.Findings.Should().BeEmpty();
        incomplete.Unknowns.Should().ContainSingle()
            .Which.Reason.Should().Be("semantic-input-incomplete");
        erroneous.IsComplete.Should().BeFalse();
        erroneous.Facts.Should().BeEmpty();
        erroneous.Findings.Should().BeEmpty();
        erroneous.Unknowns.Should().ContainSingle()
            .Which.Reason.Should().Be("compilation-contains-errors");
    }

    [Fact]
    public void InstanceEventsAndXamlHandlerNames_doNotImplyStaticLifetimeFacts()
    {
        var compilation = Compile(
            """
            using System;

            internal sealed class Button
            {
                internal event EventHandler? Click;
            }

            internal sealed class View
            {
                internal void Attach(Button button) =>
                    button.Click += OnClick;

                // A same-named XAML handler is not an event-lifetime fact by itself.
                private void OnClick(object? sender, EventArgs args) { }
            }
            """);

        var result = WpfEventSubscriptionAnalyzer.Analyze(
            compilation,
            semanticInputComplete: true);

        result.IsComplete.Should().BeTrue();
        result.Facts.Should().BeEmpty();
        result.Findings.Should().BeEmpty();
        result.Unknowns.Should().BeEmpty();
    }

    private static CSharpCompilation Compile(params string[] sources)
    {
        var compilation = CompileWithoutErrorAssertion(sources);
        compilation.GetDiagnostics()
            .Where(diagnostic =>
                diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
        return compilation;
    }

    private static CSharpCompilation CompileWithoutErrorAssertion(
        params string[] sources)
    {
        var trees = sources
            .Select((source, index) => CSharpSyntaxTree.ParseText(
                source,
                new CSharpParseOptions(LanguageVersion.Preview),
                path: index == 0 ? "Fixture.cs" : $"Fixture{index + 1}.cs"))
            .ToArray();
        var trustedPlatformAssemblies =
            (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InvalidOperationException(
                "The test host did not expose trusted platform assemblies.");
        var references = trustedPlatformAssemblies
            .Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries)
            .Select(path => MetadataReference.CreateFromFile(path));
        return CSharpCompilation.Create(
            "WpfEventLifetimeFixture",
            trees,
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary));
    }
}
