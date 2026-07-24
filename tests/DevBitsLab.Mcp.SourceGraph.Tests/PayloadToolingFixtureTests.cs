using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Indexing.Xaml;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Server.Plugins;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Tool-level coverage for the payload-aware <c>find_data_bindings</c> /
/// <c>find_event_handlers</c> tools, run against the <c>SampleWpf</c> fixture so the assertions
/// reflect realistic XAML indexer output (and not just hand-crafted edges). Covers the happy
/// path for each tool, the leaf-brand chokepoint, plus a soft-empty regression on a scope whose
/// loaded indexers do not emit the queried edge kind.
///
/// Joins the <c>LeafFormatterState</c> collection (shared with other suppression-aware tests)
/// so a sibling test that flips <see cref="LeafFormatter.Suppressed"/> never races with the
/// leaf-presence assertions in this class.
/// </summary>
[Collection("LeafFormatterState")]
public sealed class PayloadToolingFixtureTests : IAsyncLifetime, IDisposable
{
    public PayloadToolingFixtureTests() => LeafFormatter.Suppressed = false;
    public void Dispose() => LeafFormatter.Suppressed = false;


    private string _wpfDbPath = string.Empty;
    private SqliteGraphStore? _wpfStore;
    private ScopeHost? _wpfHost;
    private ScopeRouter? _wpfRouter;

    private string _csOnlyDbPath = string.Empty;
    private SqliteGraphStore? _csOnlyStore;
    private ScopeHost? _csOnlyHost;
    private ScopeRouter? _csOnlyRouter;

    public async Task InitializeAsync()
    {
        // Happy-path scope: SampleWpf cold-indexed via Roslyn + XAML dispatcher (the same plumbing
        // as XamlIndexFixtureTests). The resulting store has both code-behind / handles-event /
        // binds-path edges populated.
        var wpfRoot = LocateFixture("SampleWpf");
        var wpfSln = Path.Join(wpfRoot, "SampleWpf.sln");
        (_wpfStore, _wpfDbPath) = await IndexWithXamlAsync(wpfRoot, wpfSln);
        (_wpfHost, _wpfRouter) = WrapInRouter("default", wpfRoot, wpfSln, _wpfStore);

        // Soft-empty scope: a fresh on-disk SQLite store with no edges at all. Mirrors a scope
        // whose loaded indexer set never emits binds-path / handles-event (e.g. a backend-only
        // scope without a XAML indexer).
        var tmp = Path.Join(Path.GetTempPath(), "sourcegraph-payload-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        _csOnlyDbPath = Path.Join(tmp, "graph.db");
        _csOnlyStore = new SqliteGraphStore(_csOnlyDbPath);
        await _csOnlyStore.EnsureSchemaAsync();
        (_csOnlyHost, _csOnlyRouter) = WrapInRouter("backend", tmp, slnPath: Path.Join(tmp, "stub.sln"), _csOnlyStore);
    }

    public async Task DisposeAsync()
    {
        if (_wpfHost is not null) await _wpfHost.DisposeAsync();
        if (_wpfStore is not null) await _wpfStore.DisposeAsync();
        if (_csOnlyHost is not null) await _csOnlyHost.DisposeAsync();
        if (_csOnlyStore is not null) await _csOnlyStore.DisposeAsync();
        SafeDeleteWithDir(_wpfDbPath);
        SafeDeleteWithDir(_csOnlyDbPath);
    }

    [Fact]
    public async Task FindDataBindings_resolvesBindingOnSampleWpf_andSurfacesPathAndModePayload()
    {
        // SampleWpf's MainWindow.xaml has `<TextBox Text="{Binding User.Name, Mode=TwoWay}" />`,
        // so a path-substring filter for "User.Name" should produce at least one row whose payload
        // carries the documented kebab-case keys (path = "User.Name", mode = "two-way"). This is
        // the spec scenario "Find every TwoWay binding to a property" run against a real fixture.
        var result = await GraphTools.FindDataBindingsAsync(
            router: _wpfRouter!,
            target: null,
            source: null,
            path: "User.Name",
            mode: "two-way",
            converter: null,
            scope: null,
            limit: 50);

        result.IsError.Should().NotBe(true, "the happy-path query must not surface as an error");
        result.StructuredContent.Should().NotBeNull();

        var dto = JsonSerializer.Deserialize<FindDataBindingsResult>(
            result.StructuredContent!.Value.GetRawText(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        dto.Should().NotBeNull();
        dto!.Note.Should().BeNull("at least one filter (path + mode) was supplied — no hint should fire");
        dto.EdgeKind.Should().Be("binds-path");
        dto.Bindings.Should().NotBeEmpty("SampleWpf MainWindow.xaml binds Text to User.Name TwoWay");
        dto.Bindings.Should().Contain(b =>
            b.PayloadJson != null
            && b.PayloadJson.Contains("\"path\":\"User.Name\"", StringComparison.Ordinal)
            && b.PayloadJson.Contains("\"mode\":\"two-way\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FindEventHandlers_resolvesClickHandlerOnSampleWpf()
    {
        // SampleWpf's SaveButton declares `Click="OnSave"`, which the XAML indexer resolves to
        // the codebehind `MainWindow.OnSave` method. find_event_handlers --event=Click must
        // surface that wiring, payload included.
        var result = await GraphTools.FindEventHandlersAsync(
            router: _wpfRouter!,
            handler: null,
            @event: "Click",
            element: null,
            command: null,
            scope: null,
            limit: 50);

        result.IsError.Should().NotBe(true);
        result.StructuredContent.Should().NotBeNull();

        var dto = JsonSerializer.Deserialize<FindEventHandlersResult>(
            result.StructuredContent!.Value.GetRawText(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        dto.Should().NotBeNull();
        dto!.EdgeKind.Should().Be("handles-event");
        dto.Handlers.Should().NotBeEmpty();
        dto.Handlers.Should().Contain(h =>
            h.EventName == "Click"
            && h.HandlerFqn.Contains("SampleWpf.Views.MainWindow.OnSave", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FindDataBindings_brandsFirstUserVisibleText_withLeaf()
    {
        // Sanity check that the new tool flows through ToolMetrics.TrackAsync's leaf chokepoint
        // (LeafFormatter.BrandFirstText) just like every other built-in. Asserts the first
        // user-visible TextContentBlock starts with the brand mark — the at-a-glance signal that
        // the answer came from sourcegraph and not from Grep+Read or another MCP server.
        var result = await GraphTools.FindDataBindingsAsync(
            router: _wpfRouter!,
            target: null, source: null,
            path: "User.Name", mode: null, converter: null,
            scope: null, limit: 50);
        result.Content.Should().NotBeNullOrEmpty();
        var firstUserText = result.Content!
            .OfType<TextContentBlock>()
            .First(b => b.Annotations is null
                || b.Annotations.Audience is null
                || b.Annotations.Audience.Count == 0
                || b.Annotations.Audience.Contains(Role.User));
        firstUserText.Text.Should().StartWith(LeafFormatter.Mark,
            "find_data_bindings must brand its first user-visible text block via the leaf chokepoint in ToolMetrics.TrackAsync");
    }

    [Fact]
    public async Task FindEventHandlers_brandsFirstUserVisibleText_withLeaf()
    {
        var result = await GraphTools.FindEventHandlersAsync(
            router: _wpfRouter!,
            handler: null, @event: "Click",
            element: null, command: null,
            scope: null, limit: 50);
        result.Content.Should().NotBeNullOrEmpty();
        var firstUserText = result.Content!
            .OfType<TextContentBlock>()
            .First(b => b.Annotations is null
                || b.Annotations.Audience is null
                || b.Annotations.Audience.Count == 0
                || b.Annotations.Audience.Contains(Role.User));
        firstUserText.Text.Should().StartWith(LeafFormatter.Mark,
            "find_event_handlers must brand its first user-visible text block via the leaf chokepoint in ToolMetrics.TrackAsync");
    }

    [Fact]
    public async Task FindDataBindings_explicitScope_suppressesAllFiltersNullHint()
    {
        // Regression for the Copilot review on PR #33: the all-filters-null hint used to fire
        // whenever target/source/path/mode/converter were empty, even when the caller restricted
        // `scope`. The spec scenario "All filters null returns hint" only calls for the note when
        // every filter is null AND no scope restriction is in effect — an explicit scope id (or
        // comma list, anything other than wildcard `*`) already narrows the query and makes the
        // hint noise.
        var result = await GraphTools.FindDataBindingsAsync(
            router: _wpfRouter!,
            target: null, source: null, path: null, mode: null, converter: null,
            scope: "default", // explicit non-wildcard scope counts as narrowing
            limit: 50);

        result.IsError.Should().NotBe(true);
        var dto = JsonSerializer.Deserialize<FindDataBindingsResult>(
            result.StructuredContent!.Value.GetRawText(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        dto.Should().NotBeNull();
        dto!.Note.Should().BeNull(
            "an explicit non-wildcard scope already narrows the query — the all-filters-null hint should be suppressed");
    }

    [Fact]
    public async Task FindDataBindings_resolvesColonBearingNonCanonicalTarget_viaNameLookup()
    {
        // Regression for the Copilot review feedback on PR #33: `ResolveCanonicalKeyAsync` used
        // to treat any input containing `:` as a canonical key and pass it through verbatim. XAML
        // v1 XAML placeholder FQNs used to look like
        // `binding-target:Views/MainWindow.xaml#Text@12:24`. Semantic XAML indexing no longer
        // emits those placeholders, but a legacy-looking, colon-bearing lookup must still fall
        // through to FindSymbolsAsync rather than being mistaken for a canonical key.
        var result = await GraphTools.FindDataBindingsAsync(
            router: _wpfRouter!,
            target: "binding-target:Views/MainWindow.xaml#Text@12:24", // unresolved-placeholder FQN-shape
            source: null, path: null, mode: null, converter: null,
            scope: null, limit: 50);

        result.IsError.Should().NotBe(true,
            "an unresolvable target should not surface as an error — the tool should still respond cleanly");

        var dto = JsonSerializer.Deserialize<FindDataBindingsResult>(
            result.StructuredContent!.Value.GetRawText(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        dto.Should().NotBeNull();
        // Semantic indexing intentionally emits no synthetic binding-target symbol, so
        // FindSymbolsAsync returns zero hits, the query fans wide, and the all-filters-null
        // `note:` fires. The point of this test isn't the row count — it's that we DIDN'T pass
        // the value through verbatim as if it were a canonical key.
        dto!.Note.Should().NotBeNullOrEmpty(
            "with no resolved filter, the all-filters-null hint should fire");
    }

    [Fact]
    public async Task FindEventHandlers_commandFilter_softEmpty_whenScopeHandlesEventsLackCommandPayload()
    {
        // The SampleWpf fixture has handles-event edges (Click → OnSave) but none of them carry
        // a `command` payload key — its XAML indexer doesn't record command-bound wirings on
        // event-attribute handlers. Calling find_event_handlers --command=… against this scope
        // must surface the documented `note:` line distinguishing "no command payload anywhere"
        // from "command name didn't match", so the agent stops trying other command names.
        var result = await GraphTools.FindEventHandlersAsync(
            router: _wpfRouter!,
            handler: null, @event: null,
            element: null, command: "AnyCommandName",
            scope: null, limit: 50);

        result.IsError.Should().NotBe(true);
        result.StructuredContent.Should().NotBeNull();
        var dto = JsonSerializer.Deserialize<FindEventHandlersResult>(
            result.StructuredContent!.Value.GetRawText(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        dto.Should().NotBeNull();
        dto!.Handlers.Should().BeEmpty();
        dto.Note.Should().NotBeNullOrEmpty();
        dto.Note!.Should().Contain("command", "the note must name the missing payload key");
        dto.Note.Should().Contain("handles-event", "the note must name the edge kind it walked");
    }

    [Fact]
    public async Task FindDataBindings_softEmpty_whenScopeHasNoBindsPathEmitter()
    {
        // The C#-only scope's storage holds no binds-path edges (and the SDK doesn't promote
        // binds-path to a built-in EdgeKinds constant), so the tool's vocabulary check fires and
        // the response carries the documented `note:` line plus an empty bindings list.
        var result = await GraphTools.FindDataBindingsAsync(
            router: _csOnlyRouter!,
            target: "csharp:P:Foo.Bar.Baz",
            source: null,
            path: null,
            mode: null,
            converter: null,
            scope: null,
            limit: 50);

        result.IsError.Should().NotBe(true,
            "soft-empty must telemeter as ok=true (mirroring list_callers --kind=unknown's no-error pattern)");

        var dto = JsonSerializer.Deserialize<FindDataBindingsResult>(
            result.StructuredContent!.Value.GetRawText(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        dto.Should().NotBeNull();
        dto!.Bindings.Should().BeEmpty("the scope has no binds-path emitter loaded");
        dto.Note.Should().NotBeNullOrEmpty();
        dto.Note!.Should().Contain("binds-path", "the note must name the missing edge kind");
        dto.Note.Should().Contain("backend", "the note must name the active scope id");

        // Prose surface mirrors the structured note so agents reading the rendered text see the
        // same hint they'd see by parsing structured_content.note.
        var prose = string.Join("\n", result.Content!.OfType<TextContentBlock>().Select(b => b.Text));
        prose.Should().Contain("binds-path", "the prose must surface the missing-kind diagnostic");
    }

    private static (ScopeHost host, ScopeRouter router) WrapInRouter(string scopeId, string root, string slnPath, SqliteGraphStore store)
    {
        // Tests don't need embeddings — pass a 384-dim store which falls back to the disabled
        // implementation when the vec0 extension isn't loaded (it isn't in the unit-test process).
        var embeddings = store.CreateEmbeddingsStore(384);
        var indexer = new RoslynIndexer(store);
        var scope = new Scope(
            Id: scopeId,
            Name: scopeId,
            Root: root,
            ProjectSet: new ScopeProjectSet.Solutions(new[] { slnPath }, Array.Empty<string>()),
            Isolated: false,
            LastIndexedAt: DateTimeOffset.UtcNow);
        var host = new ScopeHost(scope, store, embeddings, indexer, slnPath);
        var router = new ScopeRouter();
        router.Register(host);
        router.SetDefaultScope(scopeId);
        // The fixture bypasses LiveIndexService, which is what normally calls MarkReady after
        // settling. Without it, every tool call through ScopedExecution.WaitUntilReadyAsync waits
        // forever. Surfaced post-merge of `improve-first-run-progress`.
        host.MarkReady();
        return (host, router);
    }

    private static async Task<(SqliteGraphStore Store, string DbPath)> IndexWithXamlAsync(string fixtureRoot, string slnPath)
    {
        var tmp = Path.Join(Path.GetTempPath(), "sourcegraph-payload-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        var dbPath = Path.Join(tmp, "graph.db");
        var store = new SqliteGraphStore(dbPath);
        await using var roslyn = new RoslynIndexer(store);
        await roslyn.OpenAsync(slnPath);
        await roslyn.IndexAllAsync();

        var langRegistry = new LanguageIndexerRegistry();
        langRegistry.Register(new XamlLanguageIndexer());
        var factories = new LanguageProjectFactoryRegistry();
        factories.Register(new XamlLanguageProjectFactory(
            () => roslyn.SanitizedSolution,
            roslyn.IsProjectSemanticInputComplete));
        var dispatcher = new LanguageIndexerDispatcher(langRegistry, factories);

        var projectMap = new Dictionary<string, ILanguageProject>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in factories.All())
        {
            var projects = await f.DiscoverAsync(fixtureRoot, default);
            foreach (var p in projects)
            {
                foreach (var path in p.FilePaths)
                {
                    // TryAdd is the dictionary primitive for "insert if absent"; it makes the
                    // intent explicit and replaces a foreach + ContainsKey check that CodeQL
                    // flagged as an implicit filter.
                    projectMap.TryAdd(path, p);
                }
            }
        }
        await dispatcher.DispatchAllForTestAsync(store, "test", fixtureRoot, projectMap);
        return (store, dbPath);
    }

    private static string LocateFixture(string fixtureName)
    {
        // Path.Join over Path.Combine — Combine silently drops earlier args when a later one
        // looks absolute; Join always concatenates with a separator regardless. Matches the
        // pattern in EdgeMetadataRoundTripTests / FindDefinitionStructuredOutputTests.
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var candidate = Path.Join(d.FullName, "tests", "fixtures", fixtureName);
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("Could not locate tests/fixtures/" + fixtureName + " from " + AppContext.BaseDirectory);
    }

    private static void SafeDeleteWithDir(string path)
    {
        // Best-effort cleanup of both the SQLite DB file AND its parent temp directory so the
        // sourcegraph-payload-* folders don't accumulate on developer machines / CI agents.
        // Catch only the realistic disposal-path exceptions (file lock, ACL drift) — anything
        // else (e.g. NRE indicating a bug) propagates so it surfaces in the test run.
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { /* best-effort cleanup; another handle may still hold the file */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup; ACL drift / readonly bit */ }
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException) { /* best-effort cleanup; WAL/SHM sidecars sometimes linger */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup; ACL drift / readonly bit */ }
    }
}
