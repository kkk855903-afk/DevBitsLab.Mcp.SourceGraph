using System.Text.Json;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;
using CoreEvidence = DevBitsLab.Mcp.SourceGraph.Core.Evidence;
using CoreEvidenceConfidence = DevBitsLab.Mcp.SourceGraph.Core.EvidenceConfidence;
using CoreSourceLocation = DevBitsLab.Mcp.SourceGraph.Core.SourceLocation;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class Phase1QueryEvidenceTests : IAsyncLifetime
{
    private string _tempDir = string.Empty;
    private string _databasePath = string.Empty;
    private SqliteGraphStore? _store;
    private ScopeHost? _host;
    private ScopeRouter? _router;

    public async Task InitializeAsync()
    {
        _tempDir = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-phase1-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _databasePath = Path.Join(_tempDir, "graph.db");
        _store = new SqliteGraphStore(_databasePath);
        await _store.EnsureSchemaAsync();

        var a = await SeedSymbolAsync(_store, "A");
        var b = await SeedSymbolAsync(_store, "B");
        var c = await SeedSymbolAsync(_store, "C");
        var d = await SeedSymbolAsync(_store, "D");

        await _store.BulkInsertEdgesAsync(new[]
        {
            Edge(a, b, EdgeKinds.Calls, 10, CoreEvidenceConfidence.Exact, "a-calls-b"),
            Edge(b, c, EdgeKinds.Calls, 20, CoreEvidenceConfidence.Semantic, "b-calls-c"),
            Edge(b, c, EdgeKinds.UsesType, 21, CoreEvidenceConfidence.Exact, "b-uses-c"),
            Edge(c, a, EdgeKinds.Calls, 30, CoreEvidenceConfidence.Exact, "cycle"),
            Edge(a, c, EdgeKinds.UsesType, 40, CoreEvidenceConfidence.Exact, "a-uses-c"),
        });

        // Deliberately malformed legacy row. Evidence-first tools must skip it instead of
        // presenting D's declaration as a fabricated D -> C call site.
        await using (var connection = new SqliteConnection($"Data Source={_databasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO edges(src, dst, kind_name) VALUES ($src, $dst, $kind);";
            command.Parameters.AddWithValue("$src", d.SymbolId);
            command.Parameters.AddWithValue("$dst", c.SymbolId);
            command.Parameters.AddWithValue("$kind", EdgeKinds.Calls);
            await command.ExecuteNonQueryAsync();
        }

        var scope = new Scope(
            "default",
            "default",
            _tempDir,
            new ScopeProjectSet.Paths(["**/*.cs"], Array.Empty<string>()),
            Isolated: false,
            DateTimeOffset.UtcNow);
        _host = new ScopeHost(
            scope,
            _store,
            _store.CreateEmbeddingsStore(384),
            new RoslynIndexer(_store),
            solutionPath: "");
        _host.MarkReady();
        _router = new ScopeRouter();
        _router.Register(_host);
        _router.SetDefaultScope("default");
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task CallerAndCalleeAliases_returnCanonicalAuditableRelations()
    {
        var aliasCallers = await Phase1CompatibilityTools.FindCallersAsync(
            _router!,
            "Graph.C",
            kind: "all");
        var listCallers = await GraphTools.ListCallersAsync(
            _router!,
            "Graph.C",
            kind: "all");
        var callerDto = Deserialize<ListCallersResult>(
            aliasCallers,
            ToolOutputJsonContext.Default.ListCallersResult);
        var listCallerDto = Deserialize<ListCallersResult>(
            listCallers,
            ToolOutputJsonContext.Default.ListCallersResult);

        callerDto.Should().BeEquivalentTo(listCallerDto);
        callerDto.TargetCanonicalKey.Should().Be("csharp:M:Graph.C");
        callerDto.Callers.Should().HaveCount(3);
        callerDto.Callers.Should().NotContain(row => row.Fqn == "Graph.D");
        callerDto.Callers.Select(row => row.Relation)
            .Should().BeEquivalentTo(
                EdgeKinds.Calls,
                EdgeKinds.UsesType,
                EdgeKinds.UsesType);
        listCallers.Content
            .OfType<ModelContextProtocol.Protocol.ResourceLinkBlock>()
            .Should().HaveCount(3, "each evidence-backed relation row gets its own resource link");
        AssertEveryRelationIsAuditable(callerDto.Callers.Select(row =>
            (row.Source, row.Target, row.Relation, row.Confidence, row.Evidence)));

        var aliasCallees = await Phase1CompatibilityTools.FindCalleesAsync(
            _router!,
            "Graph.A",
            kind: "all");
        var calleeDto = Deserialize<ListCalleesResult>(
            aliasCallees,
            ToolOutputJsonContext.Default.ListCalleesResult);
        calleeDto.Callees.Should().HaveCount(2);
        calleeDto.Callees.Select(row => row.Relation)
            .Should().BeEquivalentTo(EdgeKinds.Calls, EdgeKinds.UsesType);
        AssertEveryRelationIsAuditable(calleeDto.Callees.Select(row =>
            (row.Source, row.Target, row.Relation, row.Confidence, row.Evidence)));

        var duplicateTargetResult = await GraphTools.ListCalleesAsync(
            _router!,
            "Graph.B",
            kind: "all");
        var duplicateTargetDto = Deserialize<ListCalleesResult>(
            duplicateTargetResult,
            ToolOutputJsonContext.Default.ListCalleesResult);
        duplicateTargetDto.Callees.Should().HaveCount(2)
            .And.OnlyContain(row => row.Fqn == "Graph.C");
        duplicateTargetResult.Content
            .OfType<ModelContextProtocol.Protocol.ResourceLinkBlock>()
            .Should().HaveCount(2, "resource links are per relation row, not per target id");
    }

    [Fact]
    public async Task ImpactAlias_returnsCycleSafePredecessorPaths_withEvidence()
    {
        var result = await Phase1CompatibilityTools.ImpactAnalysisAsync(
            _router!,
            "Graph.C",
            maxDepth: 4,
            limit: 10,
            kind: EdgeKinds.Calls,
            evidence: "full");
        var dto = Deserialize<ImpactOfChangeResult>(
            result,
            ToolOutputJsonContext.Default.ImpactOfChangeResult);

        dto.Truncated.Should().BeFalse();
        dto.TargetCanonicalKey.Should().Be("csharp:M:Graph.C");
        dto.Upstream.Select(row => row.Fqn)
            .Should().Equal("Graph.B", "Graph.A");
        dto.Upstream.Should().OnlyHaveUniqueItems(row => row.SymbolId);
        dto.Upstream.Should().NotContain(row =>
            row.Fqn == "Graph.C" || row.Fqn == "Graph.D");

        var b = dto.Upstream.Single(row => row.Fqn == "Graph.B");
        b.Depth.Should().Be(1);
        b.Predecessor.CanonicalKey.Should().Be("csharp:M:Graph.C");
        b.Path.Should().ContainSingle();
        b.Confidence.Should().Be("semantic");

        var a = dto.Upstream.Single(row => row.Fqn == "Graph.A");
        a.Depth.Should().Be(2);
        a.Predecessor.CanonicalKey.Should().Be("csharp:M:Graph.B");
        a.Path.Should().HaveCount(2);
        a.Path.Select(hop => hop.From.Fqn)
            .Should().Equal("Graph.A", "Graph.B");
        a.Path.Select(hop => hop.To.Fqn)
            .Should().Equal("Graph.B", "Graph.C");
        a.Confidence.Should().Be("semantic", "the path uses its weakest hop confidence");
        a.Path.Should().AllSatisfy(AssertHopIsAuditable);
    }

    [Fact]
    public async Task Impact_reportsDepthAndResultBoundaries()
    {
        var shallow = await Phase1CompatibilityTools.ImpactAnalysisAsync(
            _router!,
            "Graph.C",
            maxDepth: 1,
            limit: 10);
        var shallowDto = Deserialize<ImpactOfChangeResult>(
            shallow,
            ToolOutputJsonContext.Default.ImpactOfChangeResult);
        shallowDto.Upstream.Select(row => row.Fqn).Should().Equal("Graph.B");
        shallowDto.Truncated.Should().BeTrue();

        var limited = await GraphTools.ImpactOfChangeAsync(
            _router!,
            "Graph.C",
            maxDepth: 4,
            limit: 1);
        var limitedDto = Deserialize<ImpactOfChangeResult>(
            limited,
            ToolOutputJsonContext.Default.ImpactOfChangeResult);
        limitedDto.Upstream.Should().ContainSingle();
        limitedDto.Evidence.Should().Be("summary");
        limitedDto.IncludePaths.Should().BeFalse();
        limitedDto.PathFormat.Should().Be("relative");
        limitedDto.Upstream.Should().OnlyContain(row => row.Path.Count == 0);
        limitedDto.Truncated.Should().BeTrue();

        var invalidDepth = await GraphTools.ImpactOfChangeAsync(
            _router!,
            "Graph.C",
            maxDepth: 13);
        invalidDepth.IsError.Should().BeTrue();
        CallToolResultHelpers.ProseText(invalidDepth).Should().Contain("between 1 and 12");

        var invalidLimit = await GraphTools.ListCallersAsync(
            _router!,
            "Graph.C",
            limit: 0);
        invalidLimit.IsError.Should().BeTrue();
        CallToolResultHelpers.ProseText(invalidLimit).Should().Contain("between 1 and 1000");
    }

    [Fact]
    public async Task Impact_summary_omits_repeated_paths_until_full_audit_is_requested()
    {
        var summaryResult = await GraphTools.ImpactOfChangeAsync(
            _router!,
            "Graph.C",
            maxDepth: 4,
            limit: 10);
        var fullResult = await GraphTools.ImpactOfChangeAsync(
            _router!,
            "Graph.C",
            maxDepth: 4,
            limit: 10,
            evidence: "full");
        var summary = Deserialize<ImpactOfChangeResult>(
            summaryResult,
            ToolOutputJsonContext.Default.ImpactOfChangeResult);
        var full = Deserialize<ImpactOfChangeResult>(
            fullResult,
            ToolOutputJsonContext.Default.ImpactOfChangeResult);

        summary.Upstream.Should().OnlyContain(row => row.Path.Count == 0);
        full.Upstream.Should().OnlyContain(row => row.Path.Count > 0);
        summaryResult.StructuredContent!.Value.GetRawText().Length.Should().BeLessThan(
            fullResult.StructuredContent!.Value.GetRawText().Length);
    }

    [Fact]
    public async Task ExhaustiveRelationTools_propagatePartialScopeCompleteness()
    {
        _host!.Status = "partial";
        _host.StatusMessage = "fixture source file could not be parsed";
        _host.FailedFiles =
        [
            new FileFailure("Protected.cs", "syntax-errors-with-no-declarations: CS1001"),
        ];

        var callersResult = await GraphTools.ListCallersAsync(
            _router!,
            "Graph.C",
            kind: EdgeKinds.Calls);
        var callers = Deserialize<ListCallersResult>(
            callersResult,
            ToolOutputJsonContext.Default.ListCallersResult);
        callers.Result.Should().Be("found");
        callers.ScopeStatus.Should().Be("partial");
        callers.Completeness.Should().Be("partial");
        callers.AbsenceAuthoritative.Should().BeFalse();
        callers.Reason.Should().Be("scope-partial");
        CallToolResultHelpers.ProseText(callersResult)
            .Should().Contain("absence_authoritative=false")
            .And.Contain("narrowed `rg` coverage check");

        var implementationsResult = await GraphTools.FindImplementationsAsync(
            _router!,
            "Graph.C");
        var implementations = Deserialize<FindImplementationsResult>(
            implementationsResult,
            ToolOutputJsonContext.Default.FindImplementationsResult);
        implementations.Result.Should().Be("unknown");
        implementations.ScopeStatus.Should().Be("partial");
        implementations.Completeness.Should().Be("partial");
        implementations.AbsenceAuthoritative.Should().BeFalse();

        var impactResult = await GraphTools.ImpactOfChangeAsync(
            _router!,
            "Graph.C",
            maxDepth: 4,
            limit: 10);
        var impact = Deserialize<ImpactOfChangeResult>(
            impactResult,
            ToolOutputJsonContext.Default.ImpactOfChangeResult);
        impact.Result.Should().Be("found");
        impact.ScopeStatus.Should().Be("partial");
        impact.Completeness.Should().Be("partial");
        impact.AbsenceAuthoritative.Should().BeFalse();
    }

    [Fact]
    public async Task Unrelated_project_failure_does_not_pollute_private_target_completeness()
    {
        var target = await SeedSymbolAsync(
            _store!,
            "PrivateTarget",
            accessibility: (int)Microsoft.CodeAnalysis.Accessibility.Private);
        _host!.Status = "partial";
        _host.FailedFiles =
        [
            new FileFailure(
                Path.Join(_tempDir, "UnrelatedProject", "Broken.cs"),
                "fixture failure"),
        ];
        _host.ProjectByFilePath[target.FilePath] = new FixtureLanguageProject(
            Path.Join(_tempDir, "Owner", "Owner.csproj"),
            [target.FilePath]);
        _host.ProjectMapReady = true;

        var result = await GraphTools.ListCallersAsync(
            _router!,
            "Graph.PrivateTarget",
            kind: EdgeKinds.Calls);
        var dto = Deserialize<ListCallersResult>(
            result,
            ToolOutputJsonContext.Default.ListCallersResult);

        dto.Result.Should().Be("absent");
        dto.ScopeStatus.Should().Be("partial");
        dto.Completeness.Should().Be("complete");
        dto.AbsenceAuthoritative.Should().BeTrue();
        dto.Reason.Should().BeNull();
    }

    [Fact]
    public async Task ExactFqnWithMultipleGraphCandidates_isExplicitlyAmbiguous()
    {
        var duplicatePath = Path.Join(_tempDir, "DuplicateC.cs");
        var duplicateFileId = await _store!.UpsertFileAsync(
            duplicatePath,
            [5, 6, 7, 8],
            DateTimeOffset.UtcNow);
        await _store.UpsertSymbolAsync(
            "csharp:M:Alternate.C",
            new Symbol(
                0,
                "C",
                "Graph.C",
                SymbolKinds.Method,
                duplicateFileId,
                1,
                1,
                2,
                1,
                "void C()",
                null));

        var result = await GraphTools.ListCallersAsync(
            _router!,
            "Graph.C",
            kind: EdgeKinds.Calls);
        var dto = Deserialize<ListCallersResult>(
            result,
            ToolOutputJsonContext.Default.ListCallersResult);

        dto.Result.Should().Be("ambiguous");
        dto.SelectionMode.Should().Be("exact-fqn");
        dto.CandidateCount.Should().Be(2);
        dto.SelectionAmbiguous.Should().BeTrue();
        dto.Completeness.Should().Be("partial");
        dto.AbsenceAuthoritative.Should().BeFalse();
        CallToolResultHelpers.ProseText(result)
            .Should().Contain("retry with an exact canonical key");
    }

    private async Task<SeededSymbol> SeedSymbolAsync(
        SqliteGraphStore store,
        string name,
        int accessibility = 0)
    {
        var path = Path.Join(_tempDir, $"{name}.cs");
        var fileId = await store.UpsertFileAsync(
            path,
            [1, 2, 3, 4],
            DateTimeOffset.UtcNow);
        var symbolId = await store.UpsertSymbolAsync(
            $"csharp:M:Graph.{name}",
            new Symbol(
                0,
                name,
                $"Graph.{name}",
                SymbolKinds.Method,
                fileId,
                1,
                1,
                50,
                1,
                $"void {name}()",
                null,
                Accessibility: accessibility));
        return new SeededSymbol(symbolId, fileId, path);
    }

    private sealed record FixtureLanguageProject(
        string Id,
        IReadOnlyCollection<string> FilePaths) : ILanguageProject;

    private static Edge Edge(
        SeededSymbol source,
        SeededSymbol target,
        string relation,
        int line,
        CoreEvidenceConfidence confidence,
        string marker) =>
        new(source.SymbolId, target.SymbolId, relation)
        {
            Evidence = new CoreEvidence(
                source.FileId,
                new CoreSourceLocation(source.FilePath, line, 3, line, 11),
                confidence,
                "phase1-fixture",
                new Dictionary<string, string> { ["marker"] = marker }),
        };

    private static T Deserialize<T>(
        ModelContextProtocol.Protocol.CallToolResult result,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
        result.StructuredContent!.Value.Deserialize(typeInfo)!;

    private static void AssertEveryRelationIsAuditable(
        IEnumerable<(
            TraceCallPathSymbol Source,
            TraceCallPathSymbol Target,
            string Relation,
            string Confidence,
            IReadOnlyList<TraceCallPathEvidence> Evidence)> relations)
    {
        relations.Should().AllSatisfy(relation =>
        {
            relation.Source.CanonicalKey.Should().NotBeNullOrWhiteSpace();
            relation.Target.CanonicalKey.Should().NotBeNullOrWhiteSpace();
            relation.Relation.Should().NotBeNullOrWhiteSpace();
            relation.Confidence.Should().BeOneOf("exact", "semantic", "inferred");
            relation.Evidence.Should().NotBeEmpty();
            relation.Evidence.Should().AllSatisfy(evidence =>
            {
                evidence.FilePath.Should().EndWith(".cs");
                evidence.StartLine.Should().BePositive();
                evidence.EndLine.Should().BeGreaterThanOrEqualTo(evidence.StartLine);
                evidence.EndColumn.Should().BeGreaterThan(evidence.StartColumn);
            });
        });
    }

    private static void AssertHopIsAuditable(TraceCallPathHop hop) =>
        AssertEveryRelationIsAuditable(
        [
            (hop.From, hop.To, hop.Relation, hop.Confidence, hop.Evidence),
        ]);

    private sealed record SeededSymbol(
        long SymbolId,
        long FileId,
        string FilePath);
}
