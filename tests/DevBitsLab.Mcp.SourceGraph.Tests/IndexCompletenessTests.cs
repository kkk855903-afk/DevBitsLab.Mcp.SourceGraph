using System.Security.Cryptography;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class IndexCompletenessTests
{
    [Theory]
    [InlineData(true, true, true, true, true)]
    [InlineData(false, true, true, true, false)]
    [InlineData(true, false, true, true, false)]
    [InlineData(true, true, false, true, false)]
    [InlineData(true, true, true, false, false)]
    public void Absence_isAuthoritative_onlyWhenEveryDimensionIsComplete(
        bool source,
        bool language,
        bool relations,
        bool traversal,
        bool expected)
    {
        var report = new IndexCompletenessReport(
            source,
            language,
            relations,
            traversal,
            IndexedFiles: 1,
            EligibleFiles: 1,
            MissingFiles: [],
            MissingFileCount: 0,
            MissingFilesTruncated: false,
            LoadedIndexers: ["roslyn"],
            IndexGeneration: 7,
            IndexedAt: null);

        report.AbsenceAuthoritative.Should().Be(expected);
    }

    [Fact]
    public async Task EligibleCppFileMissingFromGraphMakesSourceCoverageNonAuthoritative()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-completeness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var managedPath = Path.Join(root, "Managed.cs");
        var nativePath = Path.Join(root, "Native.cpp");
        await File.WriteAllTextAsync(managedPath, "internal sealed class Managed { }");
        await File.WriteAllTextAsync(nativePath, "int native_entry() { return 0; }");

        ScopeHost? host = null;
        try
        {
            var store = new SqliteGraphStore(Path.Join(root, "graph.db"));
            await store.EnsureSchemaAsync();
            await store.UpsertFileAsync(
                managedPath,
                SHA256.HashData(await File.ReadAllBytesAsync(managedPath)),
                DateTimeOffset.UtcNow);
            var scope = new Scope(
                "default",
                "default",
                root,
                new ScopeProjectSet.Paths(["**/*"], []),
                Isolated: false,
                DateTimeOffset.UtcNow);
            host = new ScopeHost(
                scope,
                store,
                store.CreateEmbeddingsStore(384),
                new RoslynIndexer(store),
                solutionPath: "")
            {
                LoadedIndexers = ["roslyn", "cpp-syntax"],
                RegisteredLanguageEligibleFiles = [nativePath],
                ProjectMapReady = true,
            };
            host.MarkReady();

            var report = await IndexCompleteness.BuildAsync(
                host,
                queryTraversalComplete: true,
                requireGrpcProjection: false,
                requireNativeInteropProjection: false,
                CancellationToken.None);

            report.SourceCoverageComplete.Should().BeFalse();
            report.LanguageProjectionComplete.Should().BeTrue();
            report.RelationProjectionComplete.Should().BeTrue();
            report.QueryTraversalComplete.Should().BeTrue();
            report.AbsenceAuthoritative.Should().BeFalse();
            report.IndexedFiles.Should().Be(1);
            report.EligibleFiles.Should().Be(2);
            report.MissingFiles.Should().ContainSingle(path =>
                path.EndsWith("/Native.cpp", StringComparison.Ordinal));
            report.LoadedIndexers.Should().Equal("roslyn", "cpp-syntax");
        }
        finally
        {
            if (host is not null) await host.DisposeAsync();
            try { Directory.Delete(root, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
