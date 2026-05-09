using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Server.Resources;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Vertical-slice coverage for the <c>tool-output-content-blocks</c> change applied to
/// <c>find_definition</c>: confirms the tool returns a multi-content
/// <see cref="CallToolResult"/> with the documented shape — a leading user-visible
/// <see cref="TextContentBlock"/> (brand-marked when the leaf chokepoint isn't suppressed), one
/// <see cref="ResourceLinkBlock"/> per match pointing at <c>graph://symbol/&lt;id&gt;</c>, a
/// trailing audience-restricted <see cref="TextContentBlock"/> with scope/latency metadata, and a
/// typed <see cref="FindDefinitionResult"/> in <see cref="CallToolResult.StructuredContent"/>.
///
/// Cold-indexes <c>tests/fixtures/Sample.sln</c> once via <see cref="IAsyncLifetime"/>; opts
/// into the <c>LeafFormatterState</c> collection (shared with other suppression-aware tests) so
/// tests that flip <see cref="LeafFormatter.Suppressed"/> don't race with these.
/// </summary>
[Collection("LeafFormatterState")]
public sealed class FindDefinitionStructuredOutputTests : IAsyncLifetime, IDisposable
{
    private string _tempDir = string.Empty;
    private string _dbPath = string.Empty;
    private SqliteGraphStore? _store;
    private ScopeRouter? _router;
    private ScopeHost? _host;

    public FindDefinitionStructuredOutputTests() => LeafFormatter.Suppressed = false;
    public void Dispose() => LeafFormatter.Suppressed = false;

    public async Task InitializeAsync()
    {
        var slnPath = LocateSolution();
        _tempDir = Path.Join(Path.GetTempPath(), "find-definition-structured-" + Guid.NewGuid().ToString("N"));
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
    public async Task FindDefinition_returnsCallToolResult_withProseAndStructuredContent()
    {
        // "Calculator" matches the class plus its members in the Sample fixture (Add, Subtract,
        // Multiply, Divide, MakeGreeter, LegacyAdd) — at least 5 hits, enough to exercise multi-row
        // structured content + resource link enumeration.
        var result = await GraphTools.FindDefinitionAsync(_router!, "Calculator");
        result.Should().NotBeNull();

        // Content[0] is the user-visible prose, brand-marked because Suppressed = false.
        result.Content.Should().NotBeNullOrEmpty();
        var first = result.Content!.OfType<TextContentBlock>().FirstOrDefault(b => IsUserVisible(b));
        first.Should().NotBeNull();
        first!.Text.Should().StartWith("\U0001F33F ", "the leaf chokepoint should brand the first user-visible text block");
        first.Text.Should().Contain("hits for 'Calculator'");
    }

    [Fact]
    public async Task FindDefinition_emitsOneResourceLinkPerHit_pointingAtGraphSymbolUri()
    {
        var result = await GraphTools.FindDefinitionAsync(_router!, "Calculator");
        var links = result.Content!.OfType<ResourceLinkBlock>().ToList();
        links.Should().NotBeEmpty();

        // Each link targets the graph://symbol/<id> URI shape produced by the centralised helper.
        links.Should().OnlyContain(b => b.Uri.StartsWith("graph://symbol/", StringComparison.Ordinal));

        // Every emitted link's symbol id should resolve in the active scope's store. Drives the
        // "no speculative URIs" risk mitigation from the design.
        var hits = await _store!.FindSymbolsAsync("Calculator", filePathHint: null, limit: 25);
        var idToFqn = hits.ToDictionary(h => h.Id, h => h.Fqn);
        foreach (var link in links)
        {
            var idStr = link.Uri["graph://symbol/".Length..];
            long.TryParse(idStr, out var id).Should().BeTrue($"URI '{link.Uri}' should carry a numeric id");
            idToFqn.Should().ContainKey(id, $"resource link id {id} should match a hit returned by find_definition");
            // Each link's Name carries the matched FQN so card-rendering clients have something to display.
            link.Name.Should().Be(idToFqn[id]);
        }

        // The number of resource links matches the number of structured hits — single source of truth.
        var dto = DeserializeStructured(result);
        links.Count.Should().Be(dto.Hits.Count, "one resource link per structured hit");
    }

    [Fact]
    public async Task FindDefinition_emitsTrailingAudienceRestrictedMetadataBlock()
    {
        var result = await GraphTools.FindDefinitionAsync(_router!, "Calculator");
        // The audience-restricted block is the last text block with Audience = [Assistant].
        var assistantBlocks = result.Content!.OfType<TextContentBlock>()
            .Where(b => b.Annotations?.Audience is { Count: > 0 } a && a.Contains(Role.Assistant))
            .ToList();
        assistantBlocks.Should().HaveCount(1, "find_definition emits exactly one trailing audience-restricted metadata block");

        var meta = assistantBlocks[0];
        meta.Annotations!.Audience.Should().BeEquivalentTo(new[] { Role.Assistant });
        meta.Annotations!.Priority.Should().BeLessThan(0.5f, "metadata block is informational, deprioritised per design");
        meta.Text.Should().Contain("scope=`default`");
        meta.Text.Should().Contain("hits=");
        // The leaf must NOT brand audience-restricted blocks (per spec scenario).
        meta.Text.Should().NotStartWith("\U0001F33F ");
    }

    [Fact]
    public async Task FindDefinition_structuredContent_deserialisesToFindDefinitionResult_withHitCountMatchingProse()
    {
        var result = await GraphTools.FindDefinitionAsync(_router!, "Calculator");
        var dto = DeserializeStructured(result);
        dto.Hits.Should().NotBeEmpty();

        // The prose declares "<n> hits for 'Calculator':" — extract the count and assert parity.
        var firstUser = result.Content!.OfType<TextContentBlock>().First(IsUserVisible);
        var match = System.Text.RegularExpressions.Regex.Match(firstUser.Text, @"(\d+) hits for 'Calculator'");
        match.Success.Should().BeTrue($"prose should declare a hit count; got: {firstUser.Text.Split('\n')[0]}");
        var proseCount = int.Parse(match.Groups[1].Value);
        dto.Hits.Count.Should().Be(proseCount, "structured array length equals prose row count");

        // Every hit DTO carries the documented fields populated from the SymbolHit.
        foreach (var h in dto.Hits)
        {
            h.Fqn.Should().NotBeNullOrEmpty();
            h.Kind.Should().NotBeNullOrEmpty();
            h.FilePath.Should().NotBeNullOrEmpty();
            h.Line.Should().BeGreaterThan(0);
            // Column may legitimately be 0 (column origin is 0-based in our storage).
            h.Column.Should().BeGreaterThanOrEqualTo(0);
        }
    }

    [Fact]
    public async Task FindDefinition_emptyResult_stillShipsStructuredContentWithEmptyHitsArray()
    {
        // No matches case: the tool's "No matches for 'X'." prose still flows through the
        // CallToolResult shape, and the structured hits array is empty (not omitted). Spec scenario:
        // "Empty result populates structured content".
        var result = await GraphTools.FindDefinitionAsync(_router!, "ZzzNonexistentSymbolZzz");

        // Brand-marked prose first.
        var first = result.Content!.OfType<TextContentBlock>().First(IsUserVisible);
        first.Text.Should().StartWith("\U0001F33F ");
        first.Text.Should().Contain("No matches for");

        // No resource links.
        result.Content!.OfType<ResourceLinkBlock>().Should().BeEmpty();

        // Structured content present, with empty Hits array.
        var dto = DeserializeStructured(result);
        dto.Hits.Should().BeEmpty();
    }

    [Fact]
    public async Task FindDefinition_everyEmittedResourceLinkUri_resolvesViaGraphResources()
    {
        // Spec scenario "Tool-emitted resource links resolve via the resource handler": follow each
        // emitted link's URI through GraphResources.GetSymbolAsync and confirm we don't get the
        // "Symbol id N not found" sentinel.
        var result = await GraphTools.FindDefinitionAsync(_router!, "Calculator");
        var links = result.Content!.OfType<ResourceLinkBlock>().ToList();
        links.Should().NotBeEmpty();

        foreach (var link in links)
        {
            var idStr = link.Uri["graph://symbol/".Length..];
            // Drive the resource handler the same way an MCP client would — by passing the segment
            // captured out of the URI to GetSymbolAsync.
            var card = await GraphResources.GetSymbolAsync(_router!, idStr);
            card.Should().NotBeNullOrEmpty();
            card.Should().NotStartWith("# Symbol id ", "every emitted URI should dereference to a real graph symbol card");
            card.Should().NotStartWith("# Invalid symbol id");
            card.Should().NotStartWith("# No scope");
        }
    }

    private static FindDefinitionResult DeserializeStructured(CallToolResult result)
    {
        result.StructuredContent.Should().NotBeNull("find_definition opts into structuredContent");
        var json = result.StructuredContent!.Value.GetRawText();
        var dto = JsonSerializer.Deserialize(json, ToolOutputJsonContext.Default.FindDefinitionResult);
        dto.Should().NotBeNull("structured payload should round-trip through the source-gen context");
        return dto!;
    }

    private static bool IsUserVisible(TextContentBlock b)
    {
        var aud = b.Annotations?.Audience;
        return aud is null || aud.Count == 0 || aud.Contains(Role.User);
    }
}
