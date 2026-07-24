using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Schema;
using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Embeddings;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Server.Observability;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Cross-cutting invariants for the structured-output evolution introduced by
/// <c>tool-output-content-blocks</c>:
///
/// <list type="number">
///   <item><b>Catalog invariant</b> (reflection): every built-in MCP tool method that opts into
///     <c>UseStructuredContent = true</c> must declare an <c>OutputSchemaType</c> typed DTO. The
///     SDK uses that type to generate the <c>outputSchema</c> exposed on <c>tools/list</c>; without
///     it the wire-level catalog entry can't carry the schema and downstream agents can't probe
///     the typed shape. Catches drift when a future tool conversion forgets to wire up the
///     attribute.</item>
///   <item><b>Runtime invariants</b> across a representative sample of converted tools:
///     <list type="bullet">
///       <item><c>tools/call</c> ships a non-null <c>StructuredContent</c> alongside renderable
///         prose.</item>
///       <item>The structured array length equals the number of result rows the prose enumerates
///         (single source of truth between the rendered text and the typed payload).</item>
///     </list>
///   </item>
/// </list>
///
/// Pattern follows <see cref="FindDefinitionStructuredOutputTests"/>: cold-index
/// <c>tests/fixtures/Sample.sln</c> once via <see cref="IAsyncLifetime"/>, drive each tool through
/// its public entry point, opt into the <c>LeafFormatterState</c> collection so suppression-aware
/// tests don't race.
/// </summary>
[Collection("LeafFormatterState")]
public sealed class StructuredContentInvariantTests : IAsyncLifetime, IDisposable
{
    private string _tempDir = string.Empty;
    private string _dbPath = string.Empty;
    private SqliteGraphStore? _store;
    private ScopeRouter? _router;
    private ScopeHost? _host;

    public StructuredContentInvariantTests() => LeafFormatter.Suppressed = false;
    public void Dispose() => LeafFormatter.Suppressed = false;

    public async Task InitializeAsync()
    {
        var slnPath = LocateSolution();
        _tempDir = Path.Join(Path.GetTempPath(), "structured-content-invariant-" + Guid.NewGuid().ToString("N"));
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
        // The fixture bypasses LiveIndexService, which is what normally calls MarkReady after
        // settling. Without it, every tool call through ScopedExecution.WaitUntilReadyAsync waits
        // forever. Surfaced post-merge of `improve-first-run-progress`.
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

    [Fact]
    public void EveryToolWithUseStructuredContent_hasOutputSchemaType()
    {
        // Reflection sweep over every [McpServerTool] method in the server assembly. When the
        // attribute opts into UseStructuredContent = true, OutputSchemaType MUST be set so the SDK
        // can generate the wire-level outputSchema. Tools that don't opt in (nothing left in our
        // catalog after Groups 3 & 4, but plugin-author future-proofing) are skipped — the rule
        // is about consistency between the two attribute fields, not a blanket requirement that
        // every tool ship structured content.
        var serverAsm = typeof(ToolMetrics).Assembly;
        var toolMethods = serverAsm.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .ToList();

        toolMethods.Should().NotBeEmpty(
            "the catalog invariant only matters if the assembly registers tools");

        var violations = new List<string>();
        foreach (var method in toolMethods)
        {
            var attr = method.GetCustomAttribute<McpServerToolAttribute>()!;
            if (!attr.UseStructuredContent) continue;
            if (attr.OutputSchemaType is null)
            {
                violations.Add($"{method.DeclaringType?.Name}.{method.Name}");
            }
        }

        violations.Should().BeEmpty(
            "every [McpServerTool(UseStructuredContent = true)] must also set OutputSchemaType so the SDK can generate the wire-level outputSchema");
    }

    [Fact]
    public void OutputSchemaTypes_areRegisteredOn_ToolOutputJsonContext()
    {
        // Source-gen invariant: every DTO referenced via OutputSchemaType must have a
        // [JsonSerializable(typeof(...))] entry on ToolOutputJsonContext, otherwise the source
        // generator can't emit the converter and the SDK falls back to reflection (or fails) at
        // serialise time. We assert by iterating the [JsonSerializable] attributes on the context
        // and confirming every OutputSchemaType is in that set.
        var serverAsm = typeof(ToolMetrics).Assembly;
        // JsonSerializableAttribute exposes the registered type only as a constructor argument
        // — it isn't reified as a public property — so we read it via the CustomAttributeData
        // surface, which preserves the constructor arguments verbatim from IL.
        var registered = typeof(ToolOutputJsonContext)
            .GetCustomAttributesData()
            .Where(d => d.AttributeType == typeof(System.Text.Json.Serialization.JsonSerializableAttribute))
            .Select(d => (Type)d.ConstructorArguments[0].Value!)
            .ToHashSet();

        registered.Should().NotBeEmpty(
            "ToolOutputJsonContext should have at least one [JsonSerializable] entry; the source generator requires one to compile");

        var schemaTypes = serverAsm.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>())
            .Where(a => a is not null && a.UseStructuredContent && a.OutputSchemaType is not null)
            .Select(a => a!.OutputSchemaType!)
            .Distinct()
            .ToList();

        schemaTypes.Should().NotBeEmpty();

        var unregistered = schemaTypes.Where(t => !registered.Contains(t)).ToList();
        unregistered.Should().BeEmpty(
            "every type referenced by OutputSchemaType must be registered with [JsonSerializable] on ToolOutputJsonContext");
    }

    [Fact]
    public void GeneratedOutputSchemas_publishSnakeCasePropertyNames_forMultiWordFields()
    {
        // Schema-payload casing alignment: the SDK derives a tool's outputSchema by handing the
        // OutputSchemaType to System.Text.Json's JsonSchemaExporter. The exporter doesn't pick
        // up the JsonContext's SnakeCaseLower naming policy, so without [JsonPropertyName]
        // overrides on each multi-word field the schema would publish camelCase property names
        // (filePath, xmlSummary) while the actual structuredContent payload uses snake_case
        // (file_path, xml_summary) per the JsonContext policy — strict-validating clients would
        // reject the mismatch.
        //
        // This test exercises the same exporter path with the same options shape the SDK uses
        // (JsonSerializerOptions.Web — camelCase for un-attributed properties, matching the
        // outputSchema observed on the JSON-RPC wire) so it fails fast and locally if anyone
        // removes a [JsonPropertyName] attribute. The relevant invariant is that ATTRIBUTED
        // multi-word fields publish their declared snake_case name, regardless of the
        // unattributed-property casing policy in effect.
        var options = JsonSerializerOptions.Web;

        // Probe FindDefinitionHit (nested) — carries both `file_path` and `xml_summary`, the two
        // snake_case names that motivated the fix.
        var schema = JsonSchemaExporter.GetJsonSchemaAsNode(options, typeof(FindDefinitionResult));
        Assert.NotNull(schema);
        // Drill through { properties: { hits: { items: { properties: {…} } } } }. Use xUnit's
        // Assert.NotNull so its [NotNull] annotation propagates through nullable flow analysis —
        // FluentAssertions' Should().NotBeNull() doesn't, so the subsequent .AsObject() access
        // would otherwise be flagged as a potential null dereference.
        var hitProps = schema["properties"]?["hits"]?["items"]?["properties"]
            ?? throw new Xunit.Sdk.XunitException(
                "FindDefinitionResult.Hits[<n>] should expose its element schema under .items.properties; actual schema was: " + schema.ToJsonString());
        var hitPropertyNames = hitProps.AsObject().Select(kv => kv.Key).ToList();
        hitPropertyNames.Should().Contain("file_path",
            "FindDefinitionHit.FilePath has [JsonPropertyName(\"file_path\")] so the schema must publish the snake_case name");
        hitPropertyNames.Should().Contain("xml_summary",
            "FindDefinitionHit.XmlSummary has [JsonPropertyName(\"xml_summary\")] so the schema must publish the snake_case name");
        hitPropertyNames.Should().NotContain("filePath",
            "schema must not regress to camelCase — would break alignment with structuredContent");
        hitPropertyNames.Should().NotContain("xmlSummary",
            "schema must not regress to camelCase — would break alignment with structuredContent");

        // Spot-check a top-level (non-nested) DTO too: ListCallersResult has TargetSymbolId,
        // TargetFqn, TargetKind, EdgeKind that all carry snake_case overrides.
        var callersSchema = JsonSchemaExporter.GetJsonSchemaAsNode(options, typeof(ListCallersResult));
        Assert.NotNull(callersSchema);
        var callersPropertiesNode = callersSchema["properties"]
            ?? throw new Xunit.Sdk.XunitException("ListCallersResult schema is missing top-level .properties");
        var callersProps = callersPropertiesNode.AsObject().Select(kv => kv.Key).ToList();
        callersProps.Should().Contain(new[] { "target_symbol_id", "target_fqn", "target_kind", "edge_kind" },
            "ListCallersResult's top-level fields must publish snake_case names matching the structuredContent payload");
        callersProps.Should().NotContain("targetSymbolId");
    }

    [Fact]
    public async Task FindDefinition_structuredArrayLength_matchesProseRowCount()
    {
        var result = await GraphTools.FindDefinitionAsync(_router!, "Calculator");
        AssertStructuredArrayMatchesProse(result, hitWord: "hits", proseHeader: "Calculator");
    }

    [Fact]
    public async Task SearchSymbols_structuredArrayLength_matchesProseRowCount()
    {
        var result = await GraphTools.SearchSymbolsAsync(_router!, "Add");
        AssertStructuredArrayMatchesProse(result, hitWord: "hits", proseHeader: "Add");

        var dto = JsonSerializer.Deserialize(
            result.StructuredContent!.Value.GetRawText(),
            ToolOutputJsonContext.Default.SearchSymbolsResult);
        dto.Should().NotBeNull();
        dto!.Hits.Should().NotBeEmpty();
        dto.Hits.Should().OnlyContain(hit =>
            hit.SymbolId > 0
            && hit.Relation == "defines"
            && hit.Confidence == "exact"
            && hit.StartLine > 0
            && hit.StartColumn > 0
            && hit.EndLine >= hit.StartLine,
            "fuzzy discovery must still return exact persisted declaration evidence");
    }

    [Fact]
    public async Task FindReferences_structuredArrayLength_matchesProseRowCount()
    {
        var result = await GraphTools.FindReferencesAsync(_router!, "Calculator.Add");
        result.StructuredContent.Should().NotBeNull("find_references opts into structuredContent");

        // find_references prose declares "<n> references:" before the table. Pull n from prose,
        // assert the structured array length matches.
        var prose = CallToolResultHelpers.ProseText(result);
        var match = System.Text.RegularExpressions.Regex.Match(prose, @"(\d+) references:");
        match.Success.Should().BeTrue($"prose should declare a reference count; got first line: {prose.Split('\n')[0]}");
        var proseCount = int.Parse(match.Groups[1].Value);

        var json = result.StructuredContent!.Value.GetRawText();
        var dto = JsonSerializer.Deserialize(json, ToolOutputJsonContext.Default.FindReferencesResult);
        dto.Should().NotBeNull();
        dto!.References.Count.Should().Be(proseCount, "structured array length equals prose row count");
        dto.References.Should().OnlyContain(reference =>
            reference.SymbolId == dto.TargetSymbolId
            && reference.TargetFqn == dto.TargetFqn
            && reference.Relation == reference.Kind
            && reference.Confidence == "semantic");
    }

    [Fact]
    public async Task ListMembers_structuredArrayLength_matchesProseRowCount()
    {
        var result = await GraphTools.ListMembersAsync(_router!, "Sample.Domain.Calculator");
        result.StructuredContent.Should().NotBeNull("list_members opts into structuredContent");

        var prose = CallToolResultHelpers.ProseText(result);
        var match = System.Text.RegularExpressions.Regex.Match(prose, @"(\d+) members of");
        match.Success.Should().BeTrue($"prose should declare a member count; got: {prose.Split('\n')[0]}");
        var proseCount = int.Parse(match.Groups[1].Value);

        var json = result.StructuredContent!.Value.GetRawText();
        var dto = JsonSerializer.Deserialize(json, ToolOutputJsonContext.Default.ListMembersResult);
        dto.Should().NotBeNull();
        dto!.Members.Count.Should().Be(proseCount);
    }

    [Fact]
    public async Task GraphStats_isSingletonStructuredContent_withFourCounts()
    {
        // Singleton-DTO smoke: graph_stats has no list, but still ships StructuredContent with the
        // four typed counts. Confirms the singleton-pattern path works end-to-end.
        var result = await GraphTools.GraphStatsAsync(_router!);
        result.StructuredContent.Should().NotBeNull();
        var json = result.StructuredContent!.Value.GetRawText();
        var dto = JsonSerializer.Deserialize(json, ToolOutputJsonContext.Default.GraphStatsResult);
        dto.Should().NotBeNull();
        dto!.Files.Should().BeGreaterThan(0, "Sample.sln has indexed files");
        dto.Symbols.Should().BeGreaterThan(0);
    }

    /// <summary>
    /// Common-shape assertion for tools whose prose header reads "<n> hits for '<probe>':" — both
    /// <c>find_definition</c> and <c>search_symbols</c> follow this pattern.
    /// </summary>
    private static void AssertStructuredArrayMatchesProse(CallToolResult result, string hitWord, string proseHeader)
    {
        result.StructuredContent.Should().NotBeNull("the tool opts into structuredContent");
        var prose = CallToolResultHelpers.ProseText(result);
        var pattern = @$"(\d+) {hitWord}";
        var match = System.Text.RegularExpressions.Regex.Match(prose, pattern);
        match.Success.Should().BeTrue($"prose should declare a {hitWord} count near the lead-in; got first line: {prose.Split('\n')[0]}");
        var proseCount = int.Parse(match.Groups[1].Value);

        // Both find_definition and search_symbols use a wrapping object with `.hits`. We probe via
        // JsonElement to stay agnostic about which concrete DTO type the tool used — this keeps
        // the helper reusable across both tools without taking a typed deserializer parameter.
        var hits = result.StructuredContent!.Value.GetProperty("hits");
        hits.GetArrayLength().Should().Be(proseCount, $"structured array length must equal the {hitWord} count declared in prose");
    }

    // ProseText / IsUserVisible live on the shared CallToolResultHelpers helper.
}
