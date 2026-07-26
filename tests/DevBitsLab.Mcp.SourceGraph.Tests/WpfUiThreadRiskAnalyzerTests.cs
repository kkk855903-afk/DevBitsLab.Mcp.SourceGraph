using DevBitsLab.Mcp.SourceGraph.Indexing.Wpf;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class WpfUiThreadRiskAnalyzerTests
{
    private const string WpfStubs = """
        using System;
        using System.Threading.Tasks;

        namespace System.Windows.Threading
        {
            public class DispatcherObject
            {
                public Dispatcher Dispatcher { get; } = new();
                public void VerifyAccess() { }
            }

            public sealed class Dispatcher
            {
                public void Invoke(Action callback) => callback();
                public void BeginInvoke(Action callback) => callback();
                public Task InvokeAsync(Action callback)
                {
                    callback();
                    return Task.CompletedTask;
                }
            }
        }
        """;

    [Fact]
    public void TaskRun_reportsUiSinkAndPreservesBackgroundEntryLocation()
    {
        const string source = """
            using System.Threading.Tasks;
            using System.Windows.Threading;

            namespace Fixture;

            public class View : DispatcherObject
            {
                public string Text { get; set; } = "";
                public void Focus() { }
            }

            public static class Worker
            {
                public static void Run(View view)
                {
                    Task.Run(() =>
                    {
                        view.Text = "unsafe";
                        view.Focus();
                    });
                }
            }
            """;

        var diagnostics = Analyze(source);

        diagnostics.Should().HaveCount(2);
        diagnostics.Should().OnlyContain(diagnostic =>
            diagnostic.Id == WpfUiThreadRiskAnalyzer.DiagnosticId
            && diagnostic.Severity == DiagnosticSeverity.Warning
            && diagnostic.AdditionalLocations.Count == 1
            && LineText(diagnostic.AdditionalLocations[0]).Contains("Task.Run"));
        diagnostics.Select(LineText).Should().BeEquivalentTo(
            ["view.Text = \"unsafe\";", "view.Focus();"]);
        diagnostics[0].GetMessage().Should()
            .Contain("Fixture.View.Text")
            .And.Contain("Task.Run")
            .And.Contain("line 16");
    }

    [Fact]
    public void KnownBclBackgroundEntries_reportOnlyDirectCallbacks()
    {
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            using System.Windows.Threading;

            namespace Fixture;

            public class View : DispatcherObject
            {
                public string Text { get; set; } = "";
            }

            public static class Worker
            {
                public static void Run(View view)
                {
                    Task.Run(() => view.Text = "task");
                    ThreadPool.QueueUserWorkItem(_ => view.Text = "pool");
                    ThreadPool.UnsafeQueueUserWorkItem(
                        _ => view.Text = "unsafe-pool",
                        state: (object?)null);
                    new Thread(() => view.Text = "thread").Start();
                }
            }
            """;

        var diagnostics = Analyze(source);

        diagnostics.Should().HaveCount(4);
        diagnostics.Select(diagnostic => diagnostic.GetMessage()).Should()
            .Contain(message => message.Contains("Task.Run", StringComparison.Ordinal))
            .And.Contain(message => message.Contains(
                "ThreadPool.QueueUserWorkItem",
                StringComparison.Ordinal))
            .And.Contain(message => message.Contains(
                "ThreadPool.UnsafeQueueUserWorkItem",
                StringComparison.Ordinal))
            .And.Contain(message => message.Contains(
                "Thread.Start",
                StringComparison.Ordinal));
    }

    [Fact]
    public void DispatcherDirectCallbacksSuppressMarshaledUiAccess()
    {
        const string source = """
            using System;
            using System.Threading.Tasks;
            using System.Windows.Threading;

            namespace Fixture;

            public class View : DispatcherObject
            {
                public string Text { get; set; } = "";
                public void Focus() { }
            }

            public static class Worker
            {
                public static void Run(View view)
                {
                    Task.Run(() =>
                    {
                        view.Dispatcher.Invoke(() => view.Text = "safe");
                        view.Dispatcher.BeginInvoke(
                            (Action)(() => view.Text = "also-safe"));
                        view.Dispatcher.InvokeAsync(() => view.Focus());
                        view.Text = "unsafe";
                    });
                }
            }
            """;

        var diagnostics = Analyze(source);

        diagnostics.Should().ContainSingle();
        LineText(diagnostics[0]).Should().Be("view.Text = \"unsafe\";");
    }

    [Fact]
    public void UnknownExecutionOrReceiverProofDoesNotReport()
    {
        const string source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using System.Windows.Threading;

            namespace Fixture;

            public class View : DispatcherObject
            {
                public string Text { get; set; } = "";
            }

            public sealed class Worker
            {
                private readonly View _view = new();

                private void Update() => _view.Text = "method-group";
                private static void Register(Action callback) { }

                public void Run()
                {
                    object weaklyTyped = _view;
                    Task.Run(Update);

                    var thread = new Thread(() => _view.Text = "indirect-start");
                    thread.Start();

                    Task.Run(() =>
                        Register(() => _view.Text = "unknown-nested-callback"));
                    Task.Run(() => weaklyTyped.ToString());
                }
            }
            """;

        Analyze(source).Should().BeEmpty();
    }

    [Fact]
    public void ReceiverMustStaticallyDeriveFromDispatcherObject()
    {
        const string source = """
            using System.Threading.Tasks;
            using System.Windows.Threading;

            namespace Fixture;

            public sealed class Worker
            {
                public static void Run(DispatcherObject provenUi, object unknown)
                {
                    Task.Run(() => provenUi.VerifyAccess());
                    Task.Run(() => unknown.ToString());
                }
            }
            """;

        var diagnostics = Analyze(source);

        diagnostics.Should().ContainSingle();
        LineText(diagnostics[0]).Should().Be(
            "Task.Run(() => provenUi.VerifyAccess());");
    }

    [Fact]
    public void DispatcherObjectContainingTypeDoesNotTaintOrdinaryMembers()
    {
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            using System.Windows.Threading;

            namespace Fixture;

            public sealed class View : DispatcherObject
            {
                public string Text { get; set; } = "";
            }

            public sealed class WindowLike : DispatcherObject
            {
                private readonly CancellationTokenSource _cancellation = new();
                private readonly View _view = new();

                public void Start()
                {
                    Task.Run(() => CameraLoopAsync(_cancellation.Token));
                    Task.Run(() => _view.Text = "unsafe");
                }

                private Task CameraLoopAsync(CancellationToken token) =>
                    Task.CompletedTask;
            }
            """;

        var diagnostics = Analyze(source);

        diagnostics.Should().ContainSingle(
            "only the access through the actual DispatcherObject field is UI-bound");
        LineText(diagnostics[0]).Should().Be(
            "Task.Run(() => _view.Text = \"unsafe\");");
        diagnostics[0].GetMessage().Should().Contain("Fixture.View.Text");
    }

    private static IReadOnlyList<Diagnostic> Analyze(string source)
    {
        var trees = new[]
        {
            CSharpSyntaxTree.ParseText(WpfStubs, path: "WpfStubs.cs"),
            CSharpSyntaxTree.ParseText(source, path: "Scenario.cs"),
        };
        var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
                          ?? throw new InvalidOperationException(
                              "Trusted platform assemblies are unavailable."))
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            "WpfUiThreadRiskFixture",
            trees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

        return WpfUiThreadRiskAnalyzer.Analyze(compilation)
            .OrderBy(diagnostic => diagnostic.Location.SourceSpan.Start)
            .ToArray();
    }

    private static string LineText(Diagnostic diagnostic) =>
        LineText(diagnostic.Location);

    private static string LineText(Location location)
    {
        var tree = location.SourceTree
            ?? throw new InvalidOperationException("Expected a source diagnostic.");
        var line = tree.GetText().Lines.GetLineFromPosition(location.SourceSpan.Start);
        return line.ToString().Trim();
    }
}
