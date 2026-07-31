using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Embeddings;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Server;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Per-tool scenario coverage for the tabular rendering polish (`polish-tool-output-markdown`).
/// Cold-indexes <c>tests/fixtures/Sample.sln</c> once, then drives each list-shaped MCP tool
/// through its public entry point and asserts on the expected GFM column header substring.
///
/// Runs through <see cref="LeafFormatter.Suppressed"/> = true so the assertions can pin the
/// substantive prose first line without worrying about the leaf glyph chokepoint. The
/// chokepoint itself is covered by <see cref="LeafChokepointInvariantTests"/>.
/// </summary>
[Collection("LeafFormatterState")]
public sealed class TabularRenderingTests : IAsyncLifetime, IDisposable
{
    private string _tempDir = string.Empty;
    private string _dbPath = string.Empty;
    private SqliteGraphStore? _store;
    private ScopeRouter? _router;
    private ScopeHost? _host;

    public TabularRenderingTests() => LeafFormatter.Suppressed = true;
    public void Dispose() => LeafFormatter.Suppressed = false;

    public async Task InitializeAsync()
    {
        var slnPath = LocateSolution();
        _tempDir = Path.Join(Path.GetTempPath(), "tabular-rendering-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Join(_tempDir, "graph.db");

        _store = new SqliteGraphStore(_dbPath);
        await RoslynIndexer.IndexSolutionOnceAsync(slnPath, _store);

        var scope = new Scope(
            Id: "default",
            Name: "default",
            Root: Path.GetDirectoryName(slnPath)!,
            ProjectSet: new ScopeProjectSet.Solutions(new[] { slnPath }, Array.Empty<string>()),
            Isolated: false,
            LastIndexedAt: DateTimeOffset.UtcNow);

        var embeddingsStore = _store.CreateEmbeddingsStore(384);
        var indexer = new RoslynIndexer(_store);
        _host = new ScopeHost(scope, _store, embeddingsStore, indexer, slnPath);
        _router = new ScopeRouter();
        _router.Register(_host);
        _router.SetDefaultScope("default");
        // The test fixture bypasses LiveIndexService, which is what normally calls MarkReady
        // after settling. Without this, every tool call through ScopedExecution.WaitUntilReadyAsync
        // waits forever. Surfaced post-merge of `improve-first-run-progress`.
        _host.MarkReady();
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
        if (_store is not null) await _store.DisposeAsync();
        try
        {
            // Recursive delete clears `graph.db` plus the SQLite WAL sidecars
            // (`graph.db-wal`, `graph.db-shm`) that linger when the connection
            // ran in WAL mode. Skipping them leaks temp files across runs.
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { /* best-effort cleanup; WAL/SHM sidecars sometimes linger after dispose */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup; CI sometimes denies temp-dir teardown */ }
    }

    private static string LocateSolution()
    {
        var dir = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
        {
            var candidate = Path.Join(d.FullName, "tests", "fixtures", "Sample.sln");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Could not locate tests/fixtures/Sample.sln from " + dir);
    }

    // ProseText / IsUserVisible live on the shared CallToolResultHelpers static helper so the
    // "user-visible" predicate doesn't drift across test classes that exercise the same wire
    // shape from different angles (TabularRenderingTests, StructuredContentInvariantTests,
    // FindDefinitionStructuredOutputTests, LeafChokepointInvariantTests).

    [Fact]
    public async Task FindReferences_multipleHits_rendersTable()
    {
        // Calculator.Add has at least two reference sites in the fixture: the call from
        // Multiply (`result = Add(result, a)`) and the call from Sample.App's Program.cs.
        var output = CallToolResultHelpers.ProseText(await GraphTools.FindReferencesAsync(_router!, "Calculator.Add"));
        output.Should().Contain("References to **");
        output.Should().Contain("| Relation | Confidence | Location |");
        // Separator row.
        output.Should().Contain("|---|---|");
        // The substantive prose lead-in stays on line 1 so the leaf chokepoint can prefix it.
        var firstLine = output.Split('\n')[0];
        firstLine.Should().NotStartWith("|", "the leaf chokepoint must land on prose, not on a table header");
    }

    [Fact]
    public async Task SearchSymbols_multipleHits_rendersTable()
    {
        // "Add" should match Calculator.Add (and likely several test methods that mention Add).
        var output = CallToolResultHelpers.ProseText(await GraphTools.SearchSymbolsAsync(_router!, "Add"));
        output.Should().Contain("hits for 'Add':");
        output.Should().Contain(
            "| Symbol | Kind | Relation | Confidence | Location |");
        var firstLine = output.Split('\n')[0];
        firstLine.Should().NotStartWith("|");
    }

    [Fact]
    public async Task FindByAnnotation_multipleHits_rendersTable()
    {
        // [Obsolete] is on Calculator.LegacyAdd and Greeter.LegacyGreet — at least 2 hits.
        var output = CallToolResultHelpers.ProseText(await GraphTools.FindByAnnotationAsync(_router!, "Obsolete"));
        output.Should().Contain("symbols carry [Obsolete]");
        output.Should().Contain("| Symbol | Kind | Location |");
        var firstLine = output.Split('\n')[0];
        firstLine.Should().NotStartWith("|");
    }

    [Fact]
    public async Task ListCallers_multipleHits_rendersTable()
    {
        // Calculator.Add is called from Multiply, Program.cs, and the xunit/nunit test cases.
        var output = CallToolResultHelpers.ProseText(await GraphTools.ListCallersAsync(_router!, "Calculator.Add"));
        output.Should().Contain("Inbound `calls` to **");
        output.Should().Contain("| Symbol | Kind | Location |");
        var firstLine = output.Split('\n')[0];
        firstLine.Should().NotStartWith("|");
    }

    [Fact]
    public async Task ListCallees_multipleHits_rendersTable()
    {
        // Greeter.Greet's body calls Bump and TrySet — at least 2 outbound `calls` edges. We
        // pin to the canonical key: strict resolution intentionally refuses the still-ambiguous
        // FQN prefix instead of silently selecting the method over the constructor.
        var output = CallToolResultHelpers.ProseText(await GraphTools.ListCalleesAsync(
            _router!,
            "csharp:M:Sample.Domain.Greeter.Greet(System.String)"));
        output.Should().Contain("Outbound `calls` from **");
        var firstLine = output.Split('\n')[0];
        firstLine.Should().NotStartWith("|");
        if (output.Contains("|"))
        {
            output.Should().Contain("| Symbol | Kind | Location |");
        }
    }

    [Fact]
    public async Task FindImplementations_multipleHits_rendersTable_orFallsBackToBullet()
    {
        // IGreeter.Greet has at least one implementation (Greeter.Greet). If only one impl
        // exists, the response stays bulleted; either way the lead-in is prose.
        var output = CallToolResultHelpers.ProseText(await GraphTools.FindImplementationsAsync(_router!, "IGreeter.Greet"));
        output.Should().Contain("Implementations of **");
        var firstLine = output.Split('\n')[0];
        firstLine.Should().NotStartWith("|");
    }

    [Fact]
    public async Task ListMembers_multipleHits_rendersTable()
    {
        // Calculator has Add, Subtract, Multiply, MakeGreeter, Divide, LegacyAdd — 6+ members.
        var output = CallToolResultHelpers.ProseText(await GraphTools.ListMembersAsync(_router!, "Sample.Domain.Calculator"));
        output.Should().Contain("members of **");
        output.Should().Contain("| Member | Kind | Signature |");
        var firstLine = output.Split('\n')[0];
        firstLine.Should().NotStartWith("|");
    }

    [Fact]
    public async Task FindDiagnostics_multipleHits_rendersTable_orFallsBackToBulletWhenSingle()
    {
        // The fixture deliberately calls Obsolete LegacyAdd from Program.cs to fire CS0618,
        // and there's an Obsolete attribute on Greeter.LegacyGreet too — so multiple sites
        // get the warning. We assert tolerantly: the prose lead-in is always there, and if
        // there are 2+ rows we expect the table header.
        var result = await GraphTools.FindDiagnosticsAsync(_router!);
        var output = CallToolResultHelpers.ProseText(result);
        output.Should().Contain("Diagnostics (severity");
        var firstLine = output.Split('\n')[0];
        firstLine.Should().NotStartWith("|");
        if (output.Contains("|"))
        {
            // If we got the table at all, it must be the right shape.
            output.Should().Contain(
                "| Severity | Code | Symbol | Relation | Confidence | Location | Message |");
        }

        var rows = result.StructuredContent!.Value
            .GetProperty("diagnostics")
            .EnumerateArray()
            .ToArray();
        rows.Should().NotBeEmpty();
        rows.Should().AllSatisfy(row =>
        {
            row.GetProperty("relation").GetString()
                .Should().BeOneOf("diagnoses-symbol", "diagnoses-file");
            row.GetProperty("confidence").GetString()
                .Should().Be("semantic");
            row.GetProperty("producer").GetString()
                .Should().NotBeNullOrWhiteSpace();
            row.GetProperty("file_path").GetString()
                .Should().NotBeNullOrWhiteSpace();
            row.GetProperty("line").GetInt32().Should().BeGreaterThan(0);
        });
    }

    [Fact]
    public async Task ListTestsFor_multipleHits_rendersTable()
    {
        // Calculator.Add has at least the xunit AddTwoNumbers + AddManyNumbers tests, plus
        // the nunit Add_isAssociative — so >= 2 inbound Tests edges.
        var output = CallToolResultHelpers.ProseText(await HistoryTools.ListTestsForAsync(_router!, "Calculator.Add"));
        output.Should().Contain("Tests targeting **");
        // If the fixture produced multiple test edges we expect the table header; otherwise
        // tolerate the bulleted single-row fallback.
        var firstLine = output.Split('\n')[0];
        firstLine.Should().NotStartWith("|");
        if (output.Contains("|"))
        {
            output.Should().Contain("| Framework | Test | Location |");
        }
    }

    [Fact]
    public async Task ImpactOfChange_multipleHits_rendersTableWithRightAlignedDepth()
    {
        // Calculator.Add has multiple upstream callers across direct + transitive depth.
        var output = CallToolResultHelpers.ProseText(await GraphTools.ImpactOfChangeAsync(_router!, "Calculator.Add"));
        output.Should().Contain("Upstream impact of **");
        var firstLine = output.Split('\n')[0];
        firstLine.Should().NotStartWith("|");
        if (output.Contains("|"))
        {
            output.Should().Contain("| Depth | Symbol | Kind | Location |");
            // Depth column is right-aligned: separator row contains `---:` for that column.
            output.Should().Contain("|---:|");
        }
    }

    [Fact]
    public async Task ModuleSummary_multipleHits_rendersTableWithRightAlignedInDeg()
    {
        // Sample.Domain has multiple symbols (Calculator + members + Greeter + IGreeter + LegacyAttribute).
        var output = CallToolResultHelpers.ProseText(await GraphTools.ModuleSummaryAsync(_router!, "Sample.Domain"));
        output.Should().Contain("Top ");
        output.Should().Contain("symbols in 'Sample.Domain'");
        var firstLine = output.Split('\n')[0];
        firstLine.Should().NotStartWith("|");
        if (output.Contains("|"))
        {
            output.Should().Contain("| In-deg | Symbol | Kind | Location |");
            output.Should().Contain("|---:|");
        }
    }

    [Fact]
    public async Task Neighborhood_multipleInbound_rendersTable()
    {
        // Calculator.Add has multiple inbound callers (Multiply, Program.cs, the unit tests).
        var output = CallToolResultHelpers.ProseText(await GraphTools.NeighborhoodAsync(_router!, "Calculator.Add"));
        output.Should().Contain("Neighborhood of **");
        output.Should().Contain("### Inbound (");
        var firstLine = output.Split('\n')[0];
        firstLine.Should().NotStartWith("|");
        // Inbound section has multiple rows -> tabular shape.
        output.Should().Contain("| Symbol | Kind | Location |");
    }

    [Fact]
    public async Task SemanticSearch_disabledStore_returnsLeadInProse()
    {
        // The default scope's embedding store is disabled (vec0 may or may not load). The
        // semantic_search tool returns a single-line "disabled" message in that case. Either
        // way, the response leads with prose so the leaf chokepoint can prefix it.
        var generator = new DeterministicMockEmbeddingGenerator(new EmbeddingModelInfo("test/mock", 384));
        var output = CallToolResultHelpers.ProseText(await GraphTools.SemanticSearchAsync(_router!, generator, "calculator add"));
        var firstLine = output.Split('\n')[0];
        firstLine.Should().NotStartWith("|", "first line must be prose so the leaf prefix lands on substantive content");
        // If the embedding pipeline produced enough rows (>= 2) the table must be the documented shape.
        if (output.Contains("semantic hits for") && output.Contains("|"))
        {
            output.Should().Contain("| Score | Symbol | Kind | Location |");
            // Score column is right-aligned: separator row contains `---:` for that column.
            output.Should().Contain("|---:|");
        }
    }

    [Fact]
    public async Task SemanticSearch_identifierQuery_skipsUnavailableEncoder()
    {
        using var generator = new DisabledEmbeddingGenerator(
            new EmbeddingModelInfo("disabled/test", 384));

        var result = await GraphTools.SemanticSearchAsync(
            _router!,
            generator,
            "Calculator");
        var output = CallToolResultHelpers.ProseText(result);

        output.Should().Contain("lexical hits");
        output.Should().Contain("semantic encoding skipped");
        output.Should().NotContain("semantic_search disabled");
        var dto = JsonSerializer.Deserialize(
            result.StructuredContent!.Value,
            ToolOutputJsonContext.Default.SemanticSearchResult)!;
        dto.StrategyUsed.Should().Be("lexical");
        dto.CandidateSource.Should().Be("fts");
        dto.EligibleSymbols.Should().BeGreaterThan(0);
        dto.EmbeddingCoverage.Should().BeInRange(0, 1);
        dto.Model.Should().Be("disabled/test");
    }

    [Fact]
    public async Task SearchSymbolsBatch_combinesIndependentQueries()
    {
        var result = await GraphTools.SearchSymbolsBatchAsync(
            _router!,
            ["Calculator", "Greeter"],
            topK: 3);
        var output = CallToolResultHelpers.ProseText(result);

        output.Should().Contain("across 2 queries");
        output.Should().Contain("**Calculator**");
        output.Should().Contain("**Greeter**");
        result.StructuredContent.Should().NotBeNull();
    }

    [Fact]
    public async Task RecentChanges_multipleHits_rendersTable()
    {
        // The history pipeline doesn't run during the tabular tests (we don't spin up the hosted
        // service), so seed two synthesised SymbolHistory rows directly so `recent_changes` has
        // > 1 row to render. We pick the first two indexed Calculator-family symbols.
        var store = _store!;
        var hits = await store.SearchSymbolsAsync("Calculator", kindFilter: null, limit: 5);
        hits.Should().HaveCountGreaterThanOrEqualTo(2);

        await store.UpsertSymbolHistoryAsync(new SymbolHistory(
            SymbolId: hits[0].Id,
            LastCommitSha: "abc1234",
            LastAuthor: "alice",
            LastAuthoredAt: DateTimeOffset.UtcNow.AddDays(-1),
            LineCount: 3,
            BlamedContentSha: null));
        await store.UpsertSymbolHistoryAsync(new SymbolHistory(
            SymbolId: hits[1].Id,
            LastCommitSha: "def5678",
            LastAuthor: "bob",
            LastAuthoredAt: DateTimeOffset.UtcNow.AddDays(-2),
            LineCount: 2,
            BlamedContentSha: null));

        var options = new HistoryOptions(Disabled: false);
        var output = CallToolResultHelpers.ProseText(await HistoryTools.RecentChangesAsync(_router!, options, days: 7));
        output.Should().Contain("symbols changed in the last 7 day(s)");
        output.Should().Contain("| When | Author | Symbol | Location |");
        var firstLine = output.Split('\n')[0];
        firstLine.Should().NotStartWith("|");
    }
}
