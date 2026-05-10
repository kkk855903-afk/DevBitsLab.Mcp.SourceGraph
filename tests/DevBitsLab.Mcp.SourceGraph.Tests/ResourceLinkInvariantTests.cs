using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Server;
using DevBitsLab.Mcp.SourceGraph.Server.Resources;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Cross-cutting invariant for the resource-link emission introduced by
/// <c>tool-output-content-blocks</c>: every <see cref="ResourceLinkBlock.Uri"/> a converted tool
/// emits must resolve to a non-null, non-sentinel resource via
/// <see cref="Resources.GraphResources"/>. The "speculative URI" risk in the design is that a
/// tool fabricates a graph URI for an entity that no resource handler can serve; this suite
/// catches that across the full conversion sweep.
///
/// URI scheme (from <see cref="GraphResourceUris"/>):
/// <list type="bullet">
///   <item><c>graph://symbol/&lt;id&gt;</c> — resolved by <see cref="GraphResources.GetSymbolAsync"/>.</item>
///   <item><c>graph://file/&lt;path&gt;</c> — resolved by <see cref="GraphResources.GetFileOutlineAsync"/>.</item>
///   <item><c>graph://namespace/&lt;name&gt;</c> — resolved by <see cref="GraphResources.GetNamespaceSummaryAsync"/>.</item>
/// </list>
///
/// Pattern follows <see cref="FindDefinitionStructuredOutputTests"/> and
/// <see cref="StructuredContentInvariantTests"/>: cold-index <c>tests/fixtures/Sample.sln</c>
/// once via <see cref="IAsyncLifetime"/>, drive each link-emitting tool, walk the emitted URIs,
/// and assert each one dereferences cleanly.
/// </summary>
[Collection("LeafFormatterState")]
public sealed class ResourceLinkInvariantTests : IAsyncLifetime, IDisposable
{
    private string _tempDir = string.Empty;
    private string _dbPath = string.Empty;
    private SqliteGraphStore? _store;
    private ScopeRouter? _router;
    private ScopeHost? _host;

    public ResourceLinkInvariantTests() => LeafFormatter.Suppressed = false;
    public void Dispose() => LeafFormatter.Suppressed = false;

    public async Task InitializeAsync()
    {
        var slnPath = LocateSolution();
        _tempDir = Path.Join(Path.GetTempPath(), "resource-link-invariant-" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public async Task FindDefinition_emittedLinks_resolveCleanly()
    {
        var result = await GraphTools.FindDefinitionAsync(_router!, "Calculator");
        await AssertEveryLinkResolves(result);
    }

    [Fact]
    public async Task FindReferences_emittedLinks_resolveCleanly()
    {
        var result = await GraphTools.FindReferencesAsync(_router!, "Calculator.Add");
        await AssertEveryLinkResolves(result);
    }

    [Fact]
    public async Task SearchSymbols_emittedLinks_resolveCleanly()
    {
        var result = await GraphTools.SearchSymbolsAsync(_router!, "Add");
        await AssertEveryLinkResolves(result);
    }

    [Fact]
    public async Task FindByAnnotation_emittedLinks_resolveCleanly()
    {
        var result = await GraphTools.FindByAnnotationAsync(_router!, "Obsolete");
        await AssertEveryLinkResolves(result);
    }

    [Fact]
    public async Task ListCallers_emittedLinks_resolveCleanly()
    {
        var result = await GraphTools.ListCallersAsync(_router!, "Calculator.Add");
        await AssertEveryLinkResolves(result);
    }

    [Fact]
    public async Task ListCallees_emittedLinks_resolveCleanly()
    {
        var result = await GraphTools.ListCalleesAsync(_router!, "Sample.Domain.Greeter.Greet");
        await AssertEveryLinkResolves(result);
    }

    [Fact]
    public async Task FindImplementations_emittedLinks_resolveCleanly()
    {
        var result = await GraphTools.FindImplementationsAsync(_router!, "IGreeter.Greet");
        await AssertEveryLinkResolves(result);
    }

    [Fact]
    public async Task ListMembers_emittedLinks_resolveCleanly()
    {
        var result = await GraphTools.ListMembersAsync(_router!, "Sample.Domain.Calculator");
        await AssertEveryLinkResolves(result);
    }

    [Fact]
    public async Task ListSymbolsInFile_emittedLinks_resolveCleanly()
    {
        // The fixture's main domain file. Path probe is a suffix; storage finds it via LIKE.
        var result = await GraphTools.ListSymbolsInFileAsync(_router!, "Calculator.cs");
        await AssertEveryLinkResolves(result);
    }

    [Fact]
    public async Task Neighborhood_emittedLinks_resolveCleanly()
    {
        var result = await GraphTools.NeighborhoodAsync(_router!, "Calculator.Add");
        await AssertEveryLinkResolves(result);
    }

    [Fact]
    public async Task ModuleSummary_emittedLinks_resolveCleanly()
    {
        var result = await GraphTools.ModuleSummaryAsync(_router!, "Sample.Domain");
        await AssertEveryLinkResolves(result);
    }

    [Fact]
    public async Task ImpactOfChange_emittedLinks_resolveCleanly()
    {
        var result = await GraphTools.ImpactOfChangeAsync(_router!, "Calculator.Add");
        await AssertEveryLinkResolves(result);
    }

    [Fact]
    public async Task ListGeneratedFiles_emittedLinks_resolveCleanly()
    {
        // Sample.Generators emits a generated file via the source-gen attribute on Sample.Domain.
        // list_generated_files links each row with `graph://file/<path>`.
        var result = await GraphTools.ListGeneratedFilesAsync(_router!);
        await AssertEveryLinkResolves(result);
    }

    [Fact]
    public async Task ListTestsFor_emittedLinks_resolveCleanly()
    {
        var result = await HistoryTools.ListTestsForAsync(_router!, "Calculator.Add");
        await AssertEveryLinkResolves(result);
    }

    [Fact]
    public async Task WhoAuthored_emittedLinks_resolveCleanly()
    {
        // who_authored emits a singleton link to the resolved target symbol regardless of whether
        // git history is present (the no-history path still ships the link). The fixture doesn't
        // run the history pipeline at all, so no SymbolHistory row exists for Calculator.Add —
        // that absence is what triggers the "no git history yet" branch in the tool body. Pass
        // `Disabled: false` so the tool actually runs (Disabled: true short-circuits with an
        // unrelated diagnostic before reaching the link-emitting code path); the missing history
        // rows do the work of exercising the no-history branch.
        var options = new HistoryOptions(Disabled: false);
        var result = await HistoryTools.WhoAuthoredAsync(_router!, options, "Calculator.Add");
        await AssertEveryLinkResolves(result);
    }

    [Fact]
    public async Task RecentChanges_emittedLinks_resolveCleanly()
    {
        // Seed two synthesised SymbolHistory rows so recent_changes has rows to render and link.
        // The fixture doesn't run the history pipeline, so without the seed the tool returns
        // zero rows (no links to validate, but the test would silently no-op).
        var hits = await _store!.SearchSymbolsAsync("Calculator", kindFilter: null, limit: 5);
        hits.Should().HaveCountGreaterThanOrEqualTo(2);
        await _store!.UpsertSymbolHistoryAsync(new SymbolHistory(
            SymbolId: hits[0].Id,
            LastCommitSha: "abc1234",
            LastAuthor: "alice",
            LastAuthoredAt: DateTimeOffset.UtcNow.AddDays(-1),
            LineCount: 3,
            BlamedContentSha: null));
        await _store!.UpsertSymbolHistoryAsync(new SymbolHistory(
            SymbolId: hits[1].Id,
            LastCommitSha: "def5678",
            LastAuthor: "bob",
            LastAuthoredAt: DateTimeOffset.UtcNow.AddDays(-2),
            LineCount: 2,
            BlamedContentSha: null));

        var options = new HistoryOptions(Disabled: false);
        var result = await HistoryTools.RecentChangesAsync(_router!, options, days: 7);
        await AssertEveryLinkResolves(result);
    }

    /// <summary>
    /// Walk every <see cref="ResourceLinkBlock"/> in <paramref name="result"/>'s content and
    /// confirm each <see cref="ResourceLinkBlock.Uri"/> dereferences cleanly through
    /// <see cref="GraphResources"/>. Cards starting with <c>"# Symbol id N not found"</c>,
    /// <c>"# Invalid symbol id"</c>, <c>"# No scope"</c>, or the file/namespace not-found
    /// equivalents fail the assertion. Empty link list is tolerated — some tool calls produce
    /// zero rows for the chosen probe, which is itself a valid response (the spec scenarios
    /// allow zero-link responses; the invariant is "if a link is emitted, it resolves").
    /// </summary>
    private async Task AssertEveryLinkResolves(CallToolResult result)
    {
        // Map up-front to URIs — the loop body uses each link only via its Uri, so projecting
        // first keeps the loop variable focused on the value it actually inspects.
        var uris = result.Content?.OfType<ResourceLinkBlock>().Select(b => b.Uri).ToList()
            ?? new List<string>();
        foreach (var uri in uris)
        {
            uri.Should().StartWith("graph://", $"every emitted link must use the project's graph:// scheme; got: {uri}");

            string? card;
            if (uri.StartsWith("graph://symbol/", StringComparison.Ordinal))
            {
                var idStr = uri["graph://symbol/".Length..];
                card = await GraphResources.GetSymbolAsync(_router!, idStr);
            }
            else if (uri.StartsWith("graph://file/", StringComparison.Ordinal))
            {
                var pathSegment = uri["graph://file/".Length..];
                card = await GraphResources.GetFileOutlineAsync(_router!, pathSegment);
            }
            else if (uri.StartsWith("graph://namespace/", StringComparison.Ordinal))
            {
                var nameSegment = uri["graph://namespace/".Length..];
                card = await GraphResources.GetNamespaceSummaryAsync(_router!, nameSegment);
            }
            else
            {
                throw new Xunit.Sdk.XunitException($"Unrecognised graph:// URI shape: {uri}");
            }

            card.Should().NotBeNullOrEmpty($"resource handler returned empty for URI {uri}");
            card.Should().NotStartWith("# Symbol id ", $"URI {uri} dereferenced to a 'not found' sentinel");
            card.Should().NotStartWith("# Invalid symbol id", $"URI {uri} carries an invalid id segment");
            card.Should().NotStartWith("# No scope", $"URI {uri} reached the resource handler without a registered scope");
        }
    }
}
