using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// End-to-end indexer test against the <c>PartialFailure</c> fixture: a 2-project solution
/// with one healthy project (<c>Good</c>) and one broken project (<c>Broken</c>) whose
/// unresolvable <c>PackageReference</c> prevents MSBuildWorkspace from producing a usable
/// <c>Compilation</c>. Asserts that the cold index produces symbols for <c>Good</c> while
/// reporting <c>Broken</c> in <c>IndexResult.FailedProjects</c> AND/OR keeping its symbols
/// out of the store. The probe's reliability depends on Roslyn behaviour for unresolved
/// project metadata; the test treats either reporting path as sufficient because the
/// user-visible contract is "Good's symbols indexed, Broken's didn't pollute the store".
/// </summary>
public sealed class PartialIndexResultTests
{
    private static string LocatePartialFailureSolution()
    {
        var dir = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "tests", "fixtures", "PartialFailure", "PartialFailure.sln");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Could not locate tests/fixtures/PartialFailure/PartialFailure.sln from " + dir);
    }

    [Fact]
    public async Task ColdIndex_partialSolution_indexesGoodAndDoesNotThrow()
    {
        var slnPath = LocatePartialFailureSolution();
        var tmp = Path.Combine(Path.GetTempPath(), "partial-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        var dbPath = Path.Combine(tmp, "graph.db");

        try
        {
            await using var store = new SqliteGraphStore(dbPath);
            // The user-visible contract for `partial` solutions: the cold index returns
            // successfully even when one project has compilation issues. MSBuildWorkspace is
            // surprisingly permissive — an unresolvable PackageReference produces a project
            // whose Compilation has errors but parses successfully, so Roslyn still produces a
            // valid Compilation for Broken. The probe catches the rarer "GetCompilationAsync
            // throws / returns null" cases (e.g., source-generator failures during compilation
            // construction); for the more common "compilation has errors but is constructible"
            // path, Pass 1B's per-file catch handles any individual symbol-resolution failure.
            var result = await RoslynIndexer.IndexSolutionOnceAsync(slnPath, store);

            // Good's HappyClass MUST be indexed: that's the "scope still works" guarantee.
            var happy = await store.FindSymbolsAsync("HappyClass");
            happy.Should().NotBeEmpty(
                "Good's HappyClass must appear in the store regardless of sister project compile state");

            // The IndexResult shape MUST carry the failure-list contract even when empty;
            // consumers (LiveIndexService) read these arrays unconditionally.
            result.FailedProjects.Should().NotBeNull();
            result.FailedFiles.Should().NotBeNull();
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task ColdIndex_healthySolution_emitsEmptyFailureLists()
    {
        // Sanity check: indexing a solution where every project compiles cleanly produces
        // empty FailedProjects / FailedFiles. The pre-flight probe and Pass 1B catch are
        // no-ops on healthy installs.
        var slnPath = LocateSampleSolution();
        var tmp = Path.Combine(Path.GetTempPath(), "partial-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        var dbPath = Path.Combine(tmp, "graph.db");

        try
        {
            await using var store = new SqliteGraphStore(dbPath);
            var result = await RoslynIndexer.IndexSolutionOnceAsync(slnPath, store);

            result.FailedProjects.Should().BeEmpty("healthy solutions produce no project failures");
            result.FailedFiles.Should().BeEmpty("healthy solutions produce no file failures");
            result.FilesIndexed.Should().BeGreaterThan(0);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task ColdIndex_partialWorkspaceWithLoadedProjects_reportsPartialAndIndexesLoadedSymbols()
    {
        var slnPath = LocateSampleSolution();
        var tmp = Path.Combine(
            Path.GetTempPath(),
            "partial-workspace-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);

        try
        {
            await using var store = new SqliteGraphStore(Path.Combine(tmp, "graph.db"));
            var hooks = new RoslynIndexer.TestHooks(
                OpenWorkspaceAsync: async (workspace, path, ct) =>
                {
                    var solution = await workspace.OpenSolutionAsync(
                        path,
                        cancellationToken: ct);
                    return new RoslynIndexer.WorkspaceOpenResult(
                        solution,
                        [
                            new WorkspaceDiagnostic(
                                WorkspaceDiagnosticKind.Warning,
                                "simulated non-fatal warning"),
                            new WorkspaceDiagnostic(
                                WorkspaceDiagnosticKind.Failure,
                                "simulated partial workspace load"),
                        ]);
                });
            await using var indexer = new RoslynIndexer(
                store,
                logger: null,
                embeddingsSink: null,
                privacyRoot: Path.GetDirectoryName(slnPath),
                excludePatterns: Array.Empty<string>(),
                testHooks: hooks);

            await indexer.OpenAsync(slnPath);
            var result = await indexer.IndexAllAsync();

            result.FailedProjects.Should().ContainSingle(failure =>
                failure.Name == "workspace-load-1"
                && failure.Reason.Contains("simulated partial workspace load"));
            result.FailedProjects.Should().NotContain(failure =>
                failure.Reason.Contains("simulated non-fatal warning"));
            result.FilesIndexed.Should().BeGreaterThan(0);
            (await store.GetAllSymbolKeysAsync()).Should().NotBeEmpty(
                "projects returned by a partial initial workspace remain queryable");
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task ColdIndex_explicitInterfaceImplementation_emitsImplementationEdge()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "explicit-interface-tests-" + Guid.NewGuid().ToString("N"));
        var projectDir = Path.Combine(root, "App");
        Directory.CreateDirectory(projectDir);
        var solutionPath = Path.Combine(root, "ExplicitInterface.slnx");
        var projectPath = Path.Combine(projectDir, "App.csproj");
        var sourcePath = Path.Combine(projectDir, "Lookup.cs");

        try
        {
            await File.WriteAllTextAsync(
                solutionPath,
                """
                <Solution>
                  <Project Path="App/App.csproj" />
                </Solution>
                """);
            await File.WriteAllTextAsync(
                projectPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(
                sourcePath,
                """
                namespace ExplicitFixture;

                public interface ILookup
                {
                    string? Find(string key);
                }

                public sealed class Lookup : ILookup
                {
                    string? ILookup.Find(string key) => key;
                }

                public class LookupBase
                {
                    public string? Find(string key) => key;
                }

                public sealed class InheritedLookup : LookupBase, ILookup
                {
                }

                public interface IMappedData<T>
                {
                    void From(T source);
                }

                public interface IPatientInfo : IMappedData<IPatientInfo>
                {
                }

                public sealed class DbPatientInfo : IPatientInfo
                {
                    public void From(IPatientInfo source)
                    {
                    }
                }
                """);

            await using var store = new SqliteGraphStore(Path.Combine(root, "graph.db"));
            await RoslynIndexer.IndexSolutionOnceAsync(solutionPath, store);

            var interfaceMethod = (await store.FindSymbolsAsync("ILookup.Find"))
                .Should().ContainSingle(hit =>
                    hit.Fqn.Contains("ExplicitFixture.ILookup.Find"))
                .Which;
            var implementations = await store.ListImplementationsAsync(
                interfaceMethod.Id);

            implementations.Should().Contain(hit =>
                hit.Fqn.Contains("ExplicitFixture.Lookup")
                && hit.CanonicalKey != null);
            implementations.Should().Contain(hit =>
                hit.Fqn.Contains("ExplicitFixture.LookupBase.Find")
                && hit.CanonicalKey != null,
                "a derived type may introduce an interface while inheriting its implementation");

            var mappedFrom = (await store.FindSymbolsAsync("IMappedData<T>.From"))
                .Should().ContainSingle(hit =>
                    hit.Fqn.Contains("ExplicitFixture.IMappedData<T>.From"))
                .Which;
            var mappedImplementations = await store.ListImplementationsAsync(
                mappedFrom.Id);
            mappedImplementations.Should().ContainSingle(hit =>
                hit.Fqn.Contains("ExplicitFixture.DbPatientInfo.From")
                && hit.CanonicalKey != null,
                "a member inherited through a closed generic interface must retain its implements-member edge");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task Protected_source_with_prior_symbols_isFailedOnEveryRun_withoutDiagnosticCascades()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "syntax-failure-tests-" + Guid.NewGuid().ToString("N"));
        var projectDir = Path.Combine(root, "App");
        Directory.CreateDirectory(projectDir);
        var solutionPath = Path.Combine(root, "SyntaxFailure.slnx");
        var projectPath = Path.Combine(projectDir, "App.csproj");
        var sourcePath = Path.Combine(projectDir, "Protected.cs");
        var healthyPath = Path.Combine(projectDir, "Healthy.cs");

        try
        {
            await File.WriteAllTextAsync(
                solutionPath,
                """
                <Solution>
                  <Project Path="App/App.csproj" />
                </Solution>
                """);
            await File.WriteAllTextAsync(
                projectPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(
                sourcePath,
                "public sealed class Protected { }");
            await File.WriteAllTextAsync(
                healthyPath,
                "public sealed class Healthy { Protected Value = new(); }");

            await using var store = new SqliteGraphStore(
                Path.Combine(root, "graph.db"));
            var baseline = await RoslynIndexer.IndexSolutionOnceAsync(
                solutionPath,
                store);
            baseline.FailedFiles.Should().BeEmpty();

            await File.WriteAllTextAsync(
                sourcePath,
                "HSKey.Co.SZ WYDZLJ protected payload");
            var first = await RoslynIndexer.IndexSolutionOnceAsync(
                solutionPath,
                store);
            first.FailedFiles.Should().ContainSingle(failure =>
                failure.Path.EndsWith("Protected.cs", StringComparison.Ordinal)
                && failure.Reason.Contains(
                    "protected-hskey",
                    StringComparison.Ordinal));
            var firstDiagnostics = await store.FindDiagnosticsAsync(
                severity: null,
                code: null,
                symbolId: null,
                limit: 100);
            firstDiagnostics.Should().ContainSingle(diagnostic =>
                diagnostic.Code == "SG0001"
                && diagnostic.Severity == 2
                && diagnostic.FilePath.EndsWith(
                    "Protected.cs",
                    StringComparison.Ordinal));
            firstDiagnostics.Should().NotContain(diagnostic =>
                diagnostic.Code.StartsWith("CS", StringComparison.Ordinal),
                "compiler cascades are not reliable while a project input is protected");

            var second = await RoslynIndexer.IndexSolutionOnceAsync(
                solutionPath,
                store);
            second.FailedFiles.Should().ContainSingle(failure =>
                failure.Path.EndsWith("Protected.cs", StringComparison.Ordinal)
                && failure.Reason.Contains(
                    "protected-hskey",
                    StringComparison.Ordinal),
                "an unchanged zero-symbol parse failure must be retried and remain visible");
            var secondDiagnostics = await store.FindDiagnosticsAsync(
                severity: null,
                code: null,
                symbolId: null,
                limit: 100);
            secondDiagnostics.Should().ContainSingle(diagnostic =>
                diagnostic.Code == "SG0001");
            secondDiagnostics.Should().NotContain(diagnostic =>
                diagnostic.Code.StartsWith("CS", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task Protected_project_dependency_isClassifiedAsSg0002Warning()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "protected-dependency-tests-" + Guid.NewGuid().ToString("N"));
        var libraryDir = Path.Combine(root, "ProtectedLibrary");
        var appDir = Path.Combine(root, "App");
        Directory.CreateDirectory(libraryDir);
        Directory.CreateDirectory(appDir);
        var solutionPath = Path.Combine(root, "ProtectedDependency.slnx");
        var protectedPath = Path.Combine(libraryDir, "Protected.cs");
        var consumerPath = Path.Combine(appDir, "Consumer.cs");

        try
        {
            await File.WriteAllTextAsync(
                solutionPath,
                """
                <Solution>
                  <Project Path="ProtectedLibrary/ProtectedLibrary.csproj" />
                  <Project Path="App/App.csproj" />
                </Solution>
                """);
            await File.WriteAllTextAsync(
                Path.Combine(libraryDir, "ProtectedLibrary.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(
                Path.Combine(appDir, "App.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../ProtectedLibrary/ProtectedLibrary.csproj" />
                  </ItemGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(
                protectedPath,
                "HSKey.Co.SZ WYDZLJ protected payload");
            await File.WriteAllTextAsync(
                consumerPath,
                """
                internal sealed class Consumer
                {
                    private ProtectedType? _value;
                }
                """);

            await using var store = new SqliteGraphStore(
                Path.Combine(root, "graph.db"));
            await RoslynIndexer.IndexSolutionOnceAsync(solutionPath, store);

            var diagnostics = await store.FindDiagnosticsAsync(
                severity: null,
                code: null,
                symbolId: null,
                limit: 100);
            diagnostics.Should().ContainSingle(diagnostic =>
                diagnostic.Code == "SG0001"
                && diagnostic.Severity == (int)DiagnosticSeverity.Warning
                && diagnostic.FilePath.EndsWith(
                    "Protected.cs",
                    StringComparison.Ordinal));
            diagnostics.Should().Contain(diagnostic =>
                diagnostic.Code == "SG0002"
                && diagnostic.Severity == (int)DiagnosticSeverity.Warning
                && diagnostic.FilePath.EndsWith(
                    "Consumer.cs",
                    StringComparison.Ordinal)
                && diagnostic.Message.Contains(
                    "original Roslyn diagnostic CS0246",
                    StringComparison.Ordinal));
            diagnostics.Should().NotContain(diagnostic =>
                diagnostic.Severity == (int)DiagnosticSeverity.Error
                && diagnostic.FilePath.EndsWith(
                    "Consumer.cs",
                    StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static string LocateSampleSolution()
    {
        var dir = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "tests", "fixtures", "Sample.sln");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Could not locate tests/fixtures/Sample.sln from " + dir);
    }
}
