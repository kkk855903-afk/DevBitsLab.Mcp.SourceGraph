using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class WpfRiskIndexingTests
{
    [Fact]
    public async Task WpfRisks_reconcileAcrossUnchangedFiles()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var solutionPath = await WriteProjectAsync(root);
            var projectRoot = Path.Join(root, "App");
            var subscriberPath = Path.Join(projectRoot, "Subscriber.cs");
            var detachPath = Path.Join(projectRoot, "Detach.cs");
            var viewPath = Path.Join(projectRoot, "View.cs");
            var workerPath = Path.Join(projectRoot, "Worker.cs");

            await File.WriteAllTextAsync(
                Path.Join(projectRoot, "WpfStubs.cs"),
                WpfStubs);
            await File.WriteAllTextAsync(
                Path.Join(projectRoot, "AppLifetime.cs"),
                AppLifetimeSource);
            await File.WriteAllTextAsync(subscriberPath, SubscriberSource);
            await File.WriteAllTextAsync(detachPath, NoRemovalSource);
            await File.WriteAllTextAsync(
                viewPath,
                ViewSource(derivesFromDispatcherObject: true));
            await File.WriteAllTextAsync(workerPath, WorkerSource);

            await using var store =
                new SqliteGraphStore(Path.Join(root, "graph.db"));
            await using var indexer = new RoslynIndexer(
                store,
                logger: null,
                embeddingsSink: null,
                privacyRoot: root);
            await indexer.OpenAsync(solutionPath);

            var cold = await indexer.IndexAllAsync();

            cold.FailedFiles.Should().BeEmpty();
            indexer.IsProjectSemanticInputComplete(
                    Path.Join(projectRoot, "App.csproj"))
                .Should().BeTrue();
            var eventRisks = await DiagnosticsAsync(store, "WPFEVENT001");
            eventRisks.Should().ContainSingle();
            eventRisks[0].FilePath.Should().Be(subscriberPath);
            eventRisks[0].SymbolId.Should().NotBeNull();
            eventRisks[0].SymbolFqn.Should()
                .Contain("WpfRiskFixture.Subscriber.Attach");
            eventRisks[0].SymbolCanonicalKey.Should()
                .StartWith("csharp:M:WpfRiskFixture.Subscriber.Attach");
            eventRisks[0].Message.Should()
                .Contain("AppLifetime.Changed")
                .And.Contain("Subscriber.OnChanged");

            var threadRisks = await DiagnosticsAsync(store, "WPFTHREAD001");
            threadRisks.Should().ContainSingle();
            threadRisks[0].FilePath.Should().Be(workerPath);
            threadRisks[0].SymbolId.Should().NotBeNull();
            threadRisks[0].SymbolFqn.Should()
                .Contain("WpfRiskFixture.Worker.Run");
            threadRisks[0].SymbolCanonicalKey.Should()
                .StartWith("csharp:M:WpfRiskFixture.Worker.Run");
            threadRisks[0].Message.Should()
                .Contain("Task.Run")
                .And.Contain("View.Text");

            // The exact removal is added in a different file. The subscription file is byte
            // identical, so clearing its old diagnostic proves project-wide reconciliation.
            await File.WriteAllTextAsync(detachPath, ExactRemovalSource);
            var removal = await indexer.IndexChangedFilesAsync([detachPath]);

            removal.FailedFiles.Should().BeEmpty();
            (await DiagnosticsAsync(store, "WPFEVENT001")).Should().BeEmpty();
            (await DiagnosticsAsync(store, "WPFTHREAD001"))
                .Should().ContainSingle();

            // The risky access is also in an unchanged file. Changing only the receiver's base
            // type must remove the stale UI-thread warning from Worker.cs.
            await File.WriteAllTextAsync(
                viewPath,
                ViewSource(derivesFromDispatcherObject: false));
            var baseTypeChange =
                await indexer.IndexChangedFilesAsync([viewPath]);

            baseTypeChange.FailedFiles.Should().BeEmpty();
            (await DiagnosticsAsync(store, "WPFTHREAD001")).Should().BeEmpty();

            // Removing the separate exact removal makes the unchanged += provably risky again.
            await File.WriteAllTextAsync(detachPath, NoRemovalSource);
            var removalDeleted =
                await indexer.IndexChangedFilesAsync([detachPath]);

            removalDeleted.FailedFiles.Should().BeEmpty();
            (await DiagnosticsAsync(store, "WPFEVENT001"))
                .Should().ContainSingle(diagnostic =>
                    diagnostic.FilePath == subscriberPath);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDeleteDirectory(root);
        }
    }

    private static Task<IReadOnlyList<DiagnosticHit>> DiagnosticsAsync(
        SqliteGraphStore store,
        string code) =>
        store.FindDiagnosticsAsync(
            severity: null,
            code,
            symbolId: null);

    private static string ViewSource(
        bool derivesFromDispatcherObject) => $$"""
        using System.Windows.Threading;

        namespace WpfRiskFixture;

        internal sealed class View{{(derivesFromDispatcherObject
            ? " : DispatcherObject"
            : string.Empty)}}
        {
            internal string Text { get; set; } = "";
        }
        """;

    private const string WpfStubs = """
        using System;
        using System.Threading.Tasks;

        namespace System.Windows.Threading
        {
            public class DispatcherObject
            {
                public Dispatcher Dispatcher { get; } = new();
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

    private const string AppLifetimeSource = """
        using System;

        namespace WpfRiskFixture;

        internal static class AppLifetime
        {
            internal static event EventHandler? Changed;
            internal static void Raise() => Changed?.Invoke(null, EventArgs.Empty);
        }
        """;

    private const string SubscriberSource = """
        using System;

        namespace WpfRiskFixture;

        internal sealed partial class Subscriber
        {
            internal void Attach() =>
                AppLifetime.Changed += OnChanged;

            private void OnChanged(object? sender, EventArgs args) { }
        }
        """;

    private const string NoRemovalSource = """
        namespace WpfRiskFixture;

        internal static class Unrelated
        {
            internal const int Value = 1;
        }
        """;

    private const string ExactRemovalSource = """
        namespace WpfRiskFixture;

        internal sealed partial class Subscriber
        {
            internal void Detach() =>
                AppLifetime.Changed -= OnChanged;
        }
        """;

    private const string WorkerSource = """
        using System.Threading.Tasks;

        namespace WpfRiskFixture;

        internal static class Worker
        {
            internal static void Run(View view) =>
                Task.Run(() => view.Text = "unsafe");
        }
        """;

    private static string CreateTemporaryRoot()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-wpf-risk-indexing-"
            + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task<string> WriteProjectAsync(string root)
    {
        var projectDirectory = Path.Join(root, "App");
        Directory.CreateDirectory(projectDirectory);
        await File.WriteAllTextAsync(
            Path.Join(projectDirectory, "App.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>disable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
                <GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>
                <GenerateMSBuildEditorConfigFile>false</GenerateMSBuildEditorConfigFile>
                <EnableNETAnalyzers>false</EnableNETAnalyzers>
                <AnalysisLevel>none</AnalysisLevel>
              </PropertyGroup>
            </Project>
            """);

        var solutionPath = Path.Join(root, "Fixture.sln");
        await File.WriteAllTextAsync(
            solutionPath,
            """
            Microsoft Visual Studio Solution File, Format Version 12.00
            # Visual Studio Version 17
            VisualStudioVersion = 17.0.31903.59
            MinimumVisualStudioVersion = 10.0.40219.1
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "App\App.csproj", "{9EA601E0-EE65-49FE-81E2-C9A89F0B3E22}"
            EndProject
            Global
                GlobalSection(SolutionConfigurationPlatforms) = preSolution
                    Debug|Any CPU = Debug|Any CPU
                    Release|Any CPU = Release|Any CPU
                EndGlobalSection
                GlobalSection(ProjectConfigurationPlatforms) = postSolution
                    {9EA601E0-EE65-49FE-81E2-C9A89F0B3E22}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
                    {9EA601E0-EE65-49FE-81E2-C9A89F0B3E22}.Debug|Any CPU.Build.0 = Debug|Any CPU
                    {9EA601E0-EE65-49FE-81E2-C9A89F0B3E22}.Release|Any CPU.ActiveCfg = Release|Any CPU
                    {9EA601E0-EE65-49FE-81E2-C9A89F0B3E22}.Release|Any CPU.Build.0 = Release|Any CPU
                EndGlobalSection
            EndGlobal
            """);
        return solutionPath;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: MSBuild or antivirus can briefly retain handles on Windows.
        }
    }
}
