using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Server;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// End-to-end coverage for the <c>describe_schema</c> + <c>query_graph</c> MCP tools added by
/// the <c>add-graph-query</c> change. Each test owns a multi-scope fixture (frontend + backend
/// + isolated vendor) so it can exercise the multi-scope ATTACH + view-layer code paths from
/// <see cref="MultiScopeReadOnlyConnection"/> through the tool body.
///
/// <para>The fixture seeds three symbols (<c>Calculator</c> class, <c>Add</c> method,
/// <c>Logger</c> class) and one <c>uses-type</c> edge per scope so SELECTs against the views
/// return non-empty results — enough to assert on column shape, parameter binding, truncation,
/// and the read_only / multi_statement / sqlite error paths.</para>
///
/// <para>Joins the <c>LeafFormatterState</c> xunit collection (shared with the other
/// suppression-aware suites) so tests that flip <see cref="LeafFormatter.Suppressed"/> don't
/// race with the brand-mark assertions in this file.</para>
/// </summary>
[Collection("LeafFormatterState")]
public sealed class GraphQueryToolTests : IAsyncLifetime, IDisposable
{
    public GraphQueryToolTests() => LeafFormatter.Suppressed = false;
    public void Dispose() => LeafFormatter.Suppressed = false;

    private string _repoRoot = string.Empty;
    private SqliteScopeRegistry? _registry;

    public async Task InitializeAsync()
    {
        _repoRoot = Path.Join(Path.GetTempPath(), "sourcegraph-graphquery-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ScopeLayout.SourcegraphDir(_repoRoot));
        Directory.CreateDirectory(ScopeLayout.ScopesDirectory(_repoRoot));

        _registry = new SqliteScopeRegistry(ScopeLayout.MetaDbPath(_repoRoot));
        await _registry.EnsureSchemaAsync();

        // Two non-isolated scopes (frontend, backend) + one isolated scope (vendor). Every scope
        // gets the same seeded shape: Calculator (class) + Add (method) + Logger (class) + a
        // uses-type edge from Add → Logger. That's enough rows to populate v_symbols, v_edges,
        // and the kind-vocabulary lists for describe_schema.
        await SeedScopeWithDataAsync("frontend", isolated: false);
        await SeedScopeWithDataAsync("backend", isolated: false);
        await SeedScopeWithDataAsync("vendor", isolated: true);
    }

    public async Task DisposeAsync()
    {
        if (_registry is not null) await _registry.DisposeAsync();
        try { Directory.Delete(_repoRoot, recursive: true); }
        catch (IOException) { /* best-effort */ }
        catch (UnauthorizedAccessException) { /* best-effort */ }
    }

    // ─── describe_schema ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task DescribeSchema_returnsAllViewsAndLiveKinds()
    {
        var result = await GraphTools.DescribeSchemaAsync(
            _registry!, new RepoRootInfo(_repoRoot), scope: "*", ct: CancellationToken.None);

        result.Should().NotBeNull();
        result.IsError.Should().NotBe(true);
        result.StructuredContent.Should().NotBeNull();

        var dto = result.StructuredContent!.Value.Deserialize<DescribeSchemaResult>(
            ToolOutputJsonContext.Default.DescribeSchemaResult)!;
        dto.ViewSchemaVersion.Should().Be(3,
            "v_edge_evidence adds an agent-queryable occurrence-evidence contract");
        dto.Views.Should().HaveCount(9);
        dto.Views.Select(v => v.Name).Should().BeEquivalentTo(
            new[]
            {
                "v_symbols", "v_files", "v_edges", "v_edge_evidence", "v_references", "v_scopes",
                "v_annotations", "v_diagnostics", "v_history",
            });

        // Each view must list at least one column so agents can see the shape; and v_symbols
        // must surface the well-known is_public / is_type convenience booleans.
        var vSymbols = dto.Views.Single(v => v.Name == "v_symbols");
        vSymbols.Columns.Select(c => c.Name).Should().Contain(new[] { "scope", "id", "name", "kind", "is_public", "is_type" });
        var vEvidence = dto.Views.Single(v => v.Name == "v_edge_evidence");
        vEvidence.Columns.Select(c => c.Name).Should().Contain(
            new[] { "src", "dst", "kind", "file_path", "start_line", "confidence", "producer" });

        // Live kind vocabularies populated from SELECT DISTINCT against the view layer.
        dto.SymbolKinds.Should().Contain("class").And.Contain("method");
        dto.EdgeKinds.Should().Contain("uses-type");
        // Annotation flavors vocabulary — the seed fixture inserts one csharp-attribute
        // per non-isolated scope (Calculator decorated with [Obsolete]).
        dto.AnnotationFlavors.Should().Contain("csharp-attribute");
    }

    [Fact]
    public async Task DescribeSchema_emitsBrandedProseAndAudienceMetadata()
    {
        var result = await GraphTools.DescribeSchemaAsync(
            _registry!, new RepoRootInfo(_repoRoot), scope: "*", ct: CancellationToken.None);

        // First user-visible text block should carry the brand mark + the summary line.
        var first = result.Content!.OfType<TextContentBlock>().First(IsUserVisible);
        first.Text.Should().StartWith("\U0001F33F ", "the leaf chokepoint should brand the first text block");
        first.Text.Should().Contain("describe_schema");
        first.Text.Should().Contain("view_schema_version=3");
        first.Text.Should().Contain("v_symbols");

        // Audience-restricted metadata block at the tail.
        var assistantBlocks = result.Content!.OfType<TextContentBlock>()
            .Where(b => b.Annotations?.Audience is { Count: > 0 } a && a.Contains(Role.Assistant))
            .ToList();
        assistantBlocks.Should().HaveCount(1);
        assistantBlocks[0].Text.Should().Contain("symbol_kinds=");
        assistantBlocks[0].Text.Should().Contain("edge_kinds=");
        assistantBlocks[0].Text.Should().Contain("annotation_flavors=");
    }

    [Fact]
    public async Task DescribeSchema_excludesIsolatedScopeKindsByDefault()
    {
        // Add a vendor-only kind to confirm `*` (no scope) excludes vendor's vocabulary, then
        // explicitly request scope=vendor to confirm the kind shows up there.
        await SeedSymbolAsync("vendor", "VendorOnlyType", SymbolKinds.Interface);

        var defaultResult = await GraphTools.DescribeSchemaAsync(
            _registry!, new RepoRootInfo(_repoRoot), scope: "*", ct: CancellationToken.None);
        var defaultDto = defaultResult.StructuredContent!.Value.Deserialize<DescribeSchemaResult>(
            ToolOutputJsonContext.Default.DescribeSchemaResult)!;
        defaultDto.SymbolKinds.Should().NotContain("interface", "vendor is isolated, so its `interface` kind is excluded from `*`");

        var explicitResult = await GraphTools.DescribeSchemaAsync(
            _registry!, new RepoRootInfo(_repoRoot), scope: "vendor", ct: CancellationToken.None);
        var explicitDto = explicitResult.StructuredContent!.Value.Deserialize<DescribeSchemaResult>(
            ToolOutputJsonContext.Default.DescribeSchemaResult)!;
        explicitDto.SymbolKinds.Should().Contain("interface", "explicit scope=vendor includes the isolated scope's kinds");
    }

    // ─── query_graph: success paths ───────────────────────────────────────────────────

    [Fact]
    public async Task QueryGraph_runsSimpleSelect()
    {
        var opts = new GraphQueryOptions(TimeoutSeconds: 5, RowLimit: 5000);
        var result = await GraphTools.QueryGraphAsync(
            _registry!, new RepoRootInfo(_repoRoot), opts,
            sql: "SELECT name, kind FROM v_symbols ORDER BY name LIMIT 10",
            parameters: null, scope: "*", ct: CancellationToken.None);

        result.IsError.Should().NotBe(true, "query should succeed");
        var dto = result.StructuredContent!.Value.Deserialize<QueryGraphResult>(
            ToolOutputJsonContext.Default.QueryGraphResult)!;
        dto.RowCount.Should().BeGreaterThan(0);
        dto.Truncated.Should().BeFalse();
        dto.Columns.Should().HaveCount(2);
        dto.Columns[0].Name.Should().Be("name");
        dto.Columns[1].Name.Should().Be("kind");
        dto.Columns[0].Type.Should().Be("TEXT");
        dto.Columns[1].Type.Should().Be("TEXT");

        // Each row is a JsonElement array; first column is the symbol name.
        var firstRow = dto.Rows[0];
        firstRow.ValueKind.Should().Be(JsonValueKind.Array);
        firstRow[0].GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task QueryGraph_emitsBrandedProseTableAndAudienceMetadata()
    {
        var opts = new GraphQueryOptions(TimeoutSeconds: 5, RowLimit: 5000);
        var result = await GraphTools.QueryGraphAsync(
            _registry!, new RepoRootInfo(_repoRoot), opts,
            sql: "SELECT name, kind FROM v_symbols ORDER BY name LIMIT 5",
            parameters: null, scope: "*", ct: CancellationToken.None);

        var first = result.Content!.OfType<TextContentBlock>().First(IsUserVisible);
        first.Text.Should().StartWith("\U0001F33F ");
        first.Text.Should().Contain("query_graph");
        first.Text.Should().Contain("| name");
        first.Text.Should().Contain("| kind");

        var assistantBlocks = result.Content!.OfType<TextContentBlock>()
            .Where(b => b.Annotations?.Audience is { Count: > 0 } a && a.Contains(Role.Assistant))
            .ToList();
        assistantBlocks.Should().HaveCount(1);
        assistantBlocks[0].Text.Should().Contain("rows=");
        assistantBlocks[0].Text.Should().Contain("truncated=false");
    }

    [Fact]
    public async Task QueryGraph_parameterBinding_works()
    {
        var opts = new GraphQueryOptions(TimeoutSeconds: 5, RowLimit: 5000);
        var parameters = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["@name"] = JsonSerializer.SerializeToElement("Calculator"),
        };

        var result = await GraphTools.QueryGraphAsync(
            _registry!, new RepoRootInfo(_repoRoot), opts,
            sql: "SELECT name, kind FROM v_symbols WHERE name = @name LIMIT 5",
            parameters: parameters, scope: "*", ct: CancellationToken.None);

        result.IsError.Should().NotBe(true);
        var dto = result.StructuredContent!.Value.Deserialize<QueryGraphResult>(
            ToolOutputJsonContext.Default.QueryGraphResult)!;
        dto.RowCount.Should().BeGreaterThan(0, "Calculator was seeded into both non-isolated scopes");
        // Every row must literally match Calculator (no SQL-injection-style match-everything fallback).
        foreach (var row in dto.Rows)
        {
            row[0].GetString().Should().Be("Calculator");
        }
    }

    [Fact]
    public async Task QueryGraph_parameterBinding_typedInteger()
    {
        // Bind a numeric parameter (long) — exercises the JsonValueKind.Number → long path.
        var opts = new GraphQueryOptions(TimeoutSeconds: 5, RowLimit: 5000);
        var parameters = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["@accessibility"] = JsonSerializer.SerializeToElement(6L),
        };

        var result = await GraphTools.QueryGraphAsync(
            _registry!, new RepoRootInfo(_repoRoot), opts,
            sql: "SELECT COUNT(*) AS n FROM v_symbols WHERE accessibility = @accessibility",
            parameters: parameters, scope: "*", ct: CancellationToken.None);

        result.IsError.Should().NotBe(true);
        var dto = result.StructuredContent!.Value.Deserialize<QueryGraphResult>(
            ToolOutputJsonContext.Default.QueryGraphResult)!;
        dto.RowCount.Should().Be(1);
        var n = dto.Rows[0][0].GetInt64();
        n.Should().BeGreaterThan(0, "every seeded symbol is Public (accessibility = 6)");
    }

    [Fact]
    public async Task QueryGraph_rowCap_surfacesTruncation()
    {
        // Row cap = 2; the seeded fixture has at least 3 symbols per non-isolated scope so a
        // SELECT * FROM v_symbols LIMIT-less query yields at least 6 rows. Truncation should
        // fire and rowCount should equal the cap.
        var opts = new GraphQueryOptions(TimeoutSeconds: 5, RowLimit: 2);
        var result = await GraphTools.QueryGraphAsync(
            _registry!, new RepoRootInfo(_repoRoot), opts,
            sql: "SELECT name, kind FROM v_symbols ORDER BY name",
            parameters: null, scope: "*", ct: CancellationToken.None);

        result.IsError.Should().NotBe(true);
        var dto = result.StructuredContent!.Value.Deserialize<QueryGraphResult>(
            ToolOutputJsonContext.Default.QueryGraphResult)!;
        dto.Truncated.Should().BeTrue();
        dto.RowCount.Should().Be(2);
        dto.RowCap.Should().Be(2);
        dto.Rows.Should().HaveCount(2);

        // The prose surfaces the truncation footer.
        var firstText = result.Content!.OfType<TextContentBlock>().First(IsUserVisible);
        firstText.Text.Should().Contain("truncated at 2 rows");
    }

    [Fact]
    public async Task QueryGraph_excludesIsolatedScopeFromDefaultStar()
    {
        // GROUP BY scope on a multi-scope fixture: default `*` returns rows for the two
        // non-isolated scopes only; vendor (isolated) is absent.
        var opts = new GraphQueryOptions(TimeoutSeconds: 5, RowLimit: 5000);
        var result = await GraphTools.QueryGraphAsync(
            _registry!, new RepoRootInfo(_repoRoot), opts,
            sql: "SELECT scope, COUNT(*) AS n FROM v_symbols GROUP BY scope ORDER BY scope",
            parameters: null, scope: "*", ct: CancellationToken.None);

        var dto = result.StructuredContent!.Value.Deserialize<QueryGraphResult>(
            ToolOutputJsonContext.Default.QueryGraphResult)!;
        var scopeIds = dto.Rows.Select(r => r[0].GetString()).ToList();
        scopeIds.Should().BeEquivalentTo(new[] { "backend", "frontend" });
        scopeIds.Should().NotContain("vendor");
    }

    [Fact]
    public async Task QueryGraph_explicitScope_includesIsolatedScope()
    {
        var opts = new GraphQueryOptions(TimeoutSeconds: 5, RowLimit: 5000);
        var result = await GraphTools.QueryGraphAsync(
            _registry!, new RepoRootInfo(_repoRoot), opts,
            sql: "SELECT scope, COUNT(*) AS n FROM v_symbols GROUP BY scope",
            parameters: null, scope: "vendor", ct: CancellationToken.None);

        var dto = result.StructuredContent!.Value.Deserialize<QueryGraphResult>(
            ToolOutputJsonContext.Default.QueryGraphResult)!;
        dto.Rows.Should().HaveCount(1);
        dto.Rows[0][0].GetString().Should().Be("vendor");
    }

    // ─── query_graph: error paths ─────────────────────────────────────────────────────

    [Fact]
    public async Task QueryGraph_writeAttempt_returnsStructuredReadOnlyError()
    {
        var opts = new GraphQueryOptions(TimeoutSeconds: 5, RowLimit: 5000);
        var result = await GraphTools.QueryGraphAsync(
            _registry!, new RepoRootInfo(_repoRoot), opts,
            sql: "INSERT INTO v_symbols(name) VALUES ('evil')",
            parameters: null, scope: "*", ct: CancellationToken.None);

        result.IsError.Should().BeTrue();
        var error = result.StructuredContent!.Value;
        error.GetProperty("error").GetString().Should().Be("read_only");
        error.GetProperty("hint").GetString().Should().Contain("read-only");
    }

    [Fact]
    public async Task QueryGraph_multiStatement_returnsStructuredMultiStatementError()
    {
        var opts = new GraphQueryOptions(TimeoutSeconds: 5, RowLimit: 5000);
        var result = await GraphTools.QueryGraphAsync(
            _registry!, new RepoRootInfo(_repoRoot), opts,
            sql: "SELECT 1; ATTACH 'evil.db' AS evil;",
            parameters: null, scope: "*", ct: CancellationToken.None);

        result.IsError.Should().BeTrue();
        var error = result.StructuredContent!.Value;
        error.GetProperty("error").GetString().Should().Be("multi_statement");
        error.GetProperty("hint").GetString().Should().Contain("one SELECT/WITH");
        error.GetProperty("leftover").GetString().Should().Contain("ATTACH");
    }

    [Fact]
    public async Task QueryGraph_multiStatement_toleratesTrailingSemicolonAndComments()
    {
        // A trailing `;` (with optional whitespace / comments) is part of the first statement —
        // SQLite's own parser accepts it, and so should our single-statement check.
        var opts = new GraphQueryOptions(TimeoutSeconds: 5, RowLimit: 5000);
        var result = await GraphTools.QueryGraphAsync(
            _registry!, new RepoRootInfo(_repoRoot), opts,
            sql: "SELECT 1 AS one;   -- trailing comment\n  /* and a block comment */  ",
            parameters: null, scope: "*", ct: CancellationToken.None);

        result.IsError.Should().NotBe(true);
        var dto = result.StructuredContent!.Value.Deserialize<QueryGraphResult>(
            ToolOutputJsonContext.Default.QueryGraphResult)!;
        dto.RowCount.Should().Be(1);
        dto.Rows[0][0].GetInt64().Should().Be(1);
    }

    [Fact]
    public async Task QueryGraph_semicolonInStringLiteral_isNotTreatedAsStatementBoundary()
    {
        // The single-statement check must skip semicolons inside string literals; this query
        // SELECTs a literal string containing a semicolon and expects success, not a
        // multi_statement error.
        var opts = new GraphQueryOptions(TimeoutSeconds: 5, RowLimit: 5000);
        var result = await GraphTools.QueryGraphAsync(
            _registry!, new RepoRootInfo(_repoRoot), opts,
            sql: "SELECT 'a;b' AS x",
            parameters: null, scope: "*", ct: CancellationToken.None);

        result.IsError.Should().NotBe(true, "semicolons inside single-quoted strings are not statement separators");
        var dto = result.StructuredContent!.Value.Deserialize<QueryGraphResult>(
            ToolOutputJsonContext.Default.QueryGraphResult)!;
        dto.Rows[0][0].GetString().Should().Be("a;b");
    }

    [Fact]
    public async Task QueryGraph_invalidSql_returnsStructuredSqliteError()
    {
        var opts = new GraphQueryOptions(TimeoutSeconds: 5, RowLimit: 5000);
        var result = await GraphTools.QueryGraphAsync(
            _registry!, new RepoRootInfo(_repoRoot), opts,
            sql: "SELECT * FROM no_such_view",
            parameters: null, scope: "*", ct: CancellationToken.None);

        result.IsError.Should().BeTrue();
        var error = result.StructuredContent!.Value;
        error.GetProperty("error").GetString().Should().Be("sqlite");
        error.GetProperty("message").GetString().Should().Contain("no_such_view");
    }

    [Theory]
    [InlineData("PRAGMA table_info(v_symbols)", "PRAGMA")]
    [InlineData("EXPLAIN QUERY PLAN SELECT * FROM v_symbols", "EXPLAIN")]
    [InlineData("VACUUM", "VACUUM")]
    [InlineData("ATTACH DATABASE 'evil.db' AS evil", "ATTACH")]
    [InlineData("INSERT INTO v_symbols(name) VALUES ('x')", "INSERT")]
    [InlineData("UPDATE v_symbols SET name='x'", "UPDATE")]
    [InlineData("DROP VIEW v_symbols", "DROP")]
    public async Task QueryGraph_nonSelectFirstKeyword_isRejectedAsReadOnly(string sql, string expectedKeyword)
    {
        // The contract is "SELECT or WITH". Anything else surfaces as `read_only` with a
        // hint that names the offending keyword — even if PRAGMA query_only would let
        // some of these statements through at the engine level.
        var opts = new GraphQueryOptions(TimeoutSeconds: 5, RowLimit: 5000);
        var result = await GraphTools.QueryGraphAsync(
            _registry!, new RepoRootInfo(_repoRoot), opts,
            sql: sql, parameters: null, scope: "*", ct: CancellationToken.None);

        result.IsError.Should().BeTrue();
        var error = result.StructuredContent!.Value;
        error.GetProperty("error").GetString().Should().Be("read_only");
        error.GetProperty("first_keyword").GetString().Should().Be(expectedKeyword);
        error.GetProperty("message").GetString().Should().Contain(expectedKeyword);
    }

    [Theory]
    [InlineData("SELECT 1")]
    [InlineData("select 1")]
    [InlineData("SeLeCt 1")]
    [InlineData("WITH x AS (SELECT 1) SELECT * FROM x")]
    [InlineData("with X as (select 1) select * from X")]
    [InlineData("   SELECT 1   ")]
    [InlineData("-- leading comment\nSELECT 1")]
    [InlineData("/* block */ SELECT 1")]
    [InlineData("/* nested\n   multi-line\n   block */ WITH t(x) AS (VALUES(1)) SELECT * FROM t")]
    public async Task QueryGraph_validFirstKeywordVariants_arePassedThrough(string sql)
    {
        // SELECT and WITH (case-insensitive) survive the keyword gate. Leading whitespace,
        // line comments, and block comments are skipped before keyword detection.
        var opts = new GraphQueryOptions(TimeoutSeconds: 5, RowLimit: 5000);
        var result = await GraphTools.QueryGraphAsync(
            _registry!, new RepoRootInfo(_repoRoot), opts,
            sql: sql, parameters: null, scope: "*", ct: CancellationToken.None);

        result.IsError.Should().NotBe(true,
            "SELECT / WITH (any case, optionally preceded by whitespace and comments) is the contract");
    }

    [Fact]
    public async Task QueryGraph_emptyOrCommentOnly_returnsReadOnlyError()
    {
        var opts = new GraphQueryOptions(TimeoutSeconds: 5, RowLimit: 5000);
        var result = await GraphTools.QueryGraphAsync(
            _registry!, new RepoRootInfo(_repoRoot), opts,
            sql: "   -- only a comment\n  /* and another */  ",
            parameters: null, scope: "*", ct: CancellationToken.None);

        result.IsError.Should().BeTrue();
        var error = result.StructuredContent!.Value;
        error.GetProperty("error").GetString().Should().Be("read_only");
        error.GetProperty("message").GetString().Should().Contain("Empty SQL statement");
    }

    // ─── proposal worked example + multi-scope shape ──────────────────────────────────

    [Fact]
    public async Task QueryGraph_workedExample_publicTypesUsingType()
    {
        // Single-scope variant of the proposal's worked example (proposal.md "Why",
        // specs/mcp-tools/spec.md "Count public types that use a given type"). The
        // fixture seeds: Calculator (public class) → contains Add (public method) →
        // uses-type → Logger (public class). So "how many public types use Logger" = 1
        // (the public type is Calculator, which contains the using member).
        var opts = new GraphQueryOptions(TimeoutSeconds: 5, RowLimit: 5000);
        const string sql = """
            SELECT COUNT(DISTINCT t.id) AS public_user_count
            FROM v_edges e
            JOIN v_symbols m ON m.id = e.src AND m.scope = e.scope
            JOIN v_symbols t ON t.id = m.container_id AND t.scope = m.scope
            WHERE e.dst = (SELECT id FROM v_symbols WHERE fqn = @fqn LIMIT 1)
              AND e.kind = 'uses-type'
              AND t.is_public = 1
              AND t.is_type = 1
            """;
        var parameters = new Dictionary<string, JsonElement>
        {
            ["@fqn"] = JsonSerializer.SerializeToElement("Sample.frontend.Logger"),
        };

        var result = await GraphTools.QueryGraphAsync(
            _registry!, new RepoRootInfo(_repoRoot), opts,
            sql: sql, parameters: parameters, scope: "frontend",
            ct: CancellationToken.None);

        result.IsError.Should().NotBe(true);
        var dto = result.StructuredContent!.Value.Deserialize<QueryGraphResult>(
            ToolOutputJsonContext.Default.QueryGraphResult)!;
        dto.RowCount.Should().Be(1, "the aggregate returns one row");
        dto.Truncated.Should().BeFalse();
        dto.Columns.Should().HaveCount(1);
        dto.Columns[0].Name.Should().Be("public_user_count");

        // The single value: 1 (Calculator is the only public type whose member uses Logger).
        var rowJson = dto.Rows[0];
        rowJson.ValueKind.Should().Be(JsonValueKind.Array);
        rowJson[0].GetInt64().Should().Be(1L);
    }

    [Fact]
    public async Task QueryGraph_commaListScope_includesEachNamedScope()
    {
        // scope = "frontend,vendor" → both attached (vendor's isolation is overridden by
        // explicit naming); backend NOT attached. v_symbols rows enumerate both scopes only.
        var opts = new GraphQueryOptions(TimeoutSeconds: 5, RowLimit: 5000);
        var result = await GraphTools.QueryGraphAsync(
            _registry!, new RepoRootInfo(_repoRoot), opts,
            sql: "SELECT scope, COUNT(*) AS n FROM v_symbols GROUP BY scope ORDER BY scope",
            parameters: null, scope: "frontend,vendor", ct: CancellationToken.None);

        result.IsError.Should().NotBe(true);
        var dto = result.StructuredContent!.Value.Deserialize<QueryGraphResult>(
            ToolOutputJsonContext.Default.QueryGraphResult)!;
        dto.RowCount.Should().Be(2);
        var scopes = dto.Rows.Select(r => r[0].GetString()).ToList();
        scopes.Should().BeEquivalentTo(new[] { "frontend", "vendor" });
        scopes.Should().NotContain("backend");
    }

    // ─── extended-views composability ────────────────────────────────────────────────

    [Fact]
    public async Task QueryGraph_annotationsJoinSymbols_findsDecoratedTypes()
    {
        // Composability test for v_annotations + v_symbols. The fixture seeds one
        // [Obsolete] annotation tagged on Calculator per scope; this query in scope=frontend
        // returns exactly the frontend Calculator. Validates that the (scope, id) tuple
        // convention works across the new view the same way it does for v_edges.
        var opts = new GraphQueryOptions(TimeoutSeconds: 5, RowLimit: 5000);
        const string sql = """
            SELECT s.fqn FROM v_annotations a
            JOIN v_symbols s ON s.id = a.symbol_id AND s.scope = a.scope
            WHERE a.name = @name AND s.is_type = 1
            """;
        var parameters = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["@name"] = JsonSerializer.SerializeToElement("Obsolete"),
        };

        var result = await GraphTools.QueryGraphAsync(
            _registry!, new RepoRootInfo(_repoRoot), opts,
            sql: sql, parameters: parameters, scope: "frontend",
            ct: CancellationToken.None);

        result.IsError.Should().NotBe(true);
        var dto = result.StructuredContent!.Value.Deserialize<QueryGraphResult>(
            ToolOutputJsonContext.Default.QueryGraphResult)!;
        dto.RowCount.Should().Be(1, "frontend's Calculator is the only [Obsolete] type in scope");
        dto.Rows[0][0].GetString().Should().Be("Sample.frontend.Calculator");
    }

    [Fact]
    public async Task QueryGraph_diagnosticsJoinSymbols_findsSymbolsWithWarnings()
    {
        // Composability test for v_diagnostics + v_symbols. The fixture seeds one CS0612
        // warning (severity=2) tagged on Calculator per scope; this query filters via
        // severity_name='warning' and is_public=1/is_type=1 to surface exactly the
        // frontend Calculator. Validates the severity_name CASE mapping + the (scope, id)
        // join in one shot.
        var opts = new GraphQueryOptions(TimeoutSeconds: 5, RowLimit: 5000);
        const string sql = """
            SELECT DISTINCT s.fqn FROM v_diagnostics d
            JOIN v_symbols s ON s.id = d.symbol_id AND s.scope = d.scope
            WHERE d.severity_name = 'warning' AND s.is_public = 1 AND s.is_type = 1
            """;

        var result = await GraphTools.QueryGraphAsync(
            _registry!, new RepoRootInfo(_repoRoot), opts,
            sql: sql, parameters: null, scope: "frontend",
            ct: CancellationToken.None);

        result.IsError.Should().NotBe(true);
        var dto = result.StructuredContent!.Value.Deserialize<QueryGraphResult>(
            ToolOutputJsonContext.Default.QueryGraphResult)!;
        dto.RowCount.Should().Be(1, "frontend's Calculator is the only public type with a warning");
        dto.Rows[0][0].GetString().Should().Be("Sample.frontend.Calculator");
    }

    [Fact]
    public async Task QueryGraph_historyView_returnsExpectedColumns_evenWithEmptyData()
    {
        // v_history shape verification. The fixture doesn't seed git-blame rows, so this
        // query returns zero rows but must execute cleanly and surface the documented
        // column shape. Composability against populated history rides on the existing
        // who_authored / recent_changes curated-tool integration tests (out of scope here).
        var opts = new GraphQueryOptions(TimeoutSeconds: 5, RowLimit: 5000);
        var result = await GraphTools.QueryGraphAsync(
            _registry!, new RepoRootInfo(_repoRoot), opts,
            sql: "SELECT * FROM v_history",
            parameters: null, scope: "frontend",
            ct: CancellationToken.None);

        result.IsError.Should().NotBe(true,
            "v_history must execute cleanly even when symbol_history is empty");
        var dto = result.StructuredContent!.Value.Deserialize<QueryGraphResult>(
            ToolOutputJsonContext.Default.QueryGraphResult)!;
        dto.RowCount.Should().Be(0, "no symbol_history rows are seeded by the fixture");
        var columnNames = dto.Columns.Select(c => c.Name).ToList();
        columnNames.Should().Contain(new[] { "scope", "symbol_id", "last_commit_sha", "last_authored_at" });
    }

    // ─── seeding helpers ──────────────────────────────────────────────────────────────

    private async Task SeedScopeWithDataAsync(string scopeId, bool isolated)
    {
        var dbPath = ScopeLayout.ScopeDbPath(_repoRoot, scopeId);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        await using (var store = new SqliteGraphStore(dbPath))
        {
            await store.EnsureSchemaAsync();

            var fileId = await store.UpsertFileAsync(
                path: $"/virtual/{scopeId}/Calculator.cs",
                contentSha256: new byte[32],
                indexedAt: DateTimeOffset.UtcNow,
                isGenerated: false);

            // Class: Calculator
            var classId = await store.UpsertSymbolAsync($"csharp:T:Sample.{scopeId}.Calculator", new Symbol(
                Id: 0,
                Name: "Calculator",
                Fqn: $"Sample.{scopeId}.Calculator",
                Kind: SymbolKinds.Class,
                FileId: fileId,
                StartLine: 1, StartCol: 1, EndLine: 30, EndCol: 1,
                Signature: "public class Calculator",
                ContainerId: null,
                Modifiers: null,
                Accessibility: 6,
                XmlSummary: null,
                TestFramework: null));

            // Method: Add (contained in Calculator)
            var methodId = await store.UpsertSymbolAsync($"csharp:M:Sample.{scopeId}.Calculator.Add", new Symbol(
                Id: 0,
                Name: "Add",
                Fqn: $"Sample.{scopeId}.Calculator.Add",
                Kind: SymbolKinds.Method,
                FileId: fileId,
                StartLine: 5, StartCol: 5, EndLine: 8, EndCol: 5,
                Signature: "public int Add(int a, int b)",
                ContainerId: classId,
                Modifiers: null,
                Accessibility: 6,
                XmlSummary: null,
                TestFramework: null));

            // Class: Logger
            var loggerId = await store.UpsertSymbolAsync($"csharp:T:Sample.{scopeId}.Logger", new Symbol(
                Id: 0,
                Name: "Logger",
                Fqn: $"Sample.{scopeId}.Logger",
                Kind: SymbolKinds.Class,
                FileId: fileId,
                StartLine: 40, StartCol: 1, EndLine: 60, EndCol: 1,
                Signature: "public class Logger",
                ContainerId: null,
                Modifiers: null,
                Accessibility: 6,
                XmlSummary: null,
                TestFramework: null));

            // uses-type edge: Add → Logger
            await store.BulkInsertEdgesAsync(new[]
            {
                new Edge(methodId, loggerId, EdgeKinds.UsesType, null),
            });

            // One annotation tagged on Calculator: simulates `[Obsolete("use Foo")]` so
            // QueryGraph_annotationsJoinSymbols_findsDecoratedTypes can JOIN through to the
            // type. csharp-attribute is the documented flavor for .NET attributes.
            await store.BulkInsertAnnotationsAsync(new[]
            {
                new AnnotationRecord(
                    SymbolId: classId,
                    Name: "Obsolete",
                    FullName: "System.ObsoleteAttribute",
                    Flavor: "csharp-attribute",
                    ArgsJson: "[\"use Foo\"]",
                    AttributeSymbolId: null),
            });

            // One Roslyn-shaped warning against Calculator (severity=2 maps to severity_name
            // = 'warning' via the documented CASE expression in v_diagnostics). The line/col
            // matches the seeded class declaration so the diagnostic surfaces inside its span.
            await store.UpsertDiagnosticsForFileAsync(fileId, new[]
            {
                new DiagnosticRecord(
                    SymbolId: classId,
                    FileId: fileId,
                    Severity: 2,
                    Code: "CS0612",
                    Message: "Type is obsolete",
                    Line: 1,
                    Col: 1),
            });
        }

        await _registry!.UpsertAsync(new ScopeRow(
            Id: scopeId,
            Name: scopeId,
            Root: _repoRoot,
            ProjectSetJson: "{}",
            Isolated: isolated,
            LastIndexedAt: DateTimeOffset.UtcNow,
            Status: "ok"));
    }

    /// <summary>Add a single extra symbol to an existing scope. Used by the isolated-scope vocab test.</summary>
    private async Task SeedSymbolAsync(string scopeId, string name, string kind)
    {
        var dbPath = ScopeLayout.ScopeDbPath(_repoRoot, scopeId);
        await using var store = new SqliteGraphStore(dbPath);
        await store.EnsureSchemaAsync();
        var fileId = await store.UpsertFileAsync(
            path: $"/virtual/{scopeId}/{name}.cs",
            contentSha256: new byte[32],
            indexedAt: DateTimeOffset.UtcNow,
            isGenerated: false);
        await store.UpsertSymbolAsync($"csharp:T:Sample.{scopeId}.{name}", new Symbol(
            Id: 0,
            Name: name,
            Fqn: $"Sample.{scopeId}.{name}",
            Kind: kind,
            FileId: fileId,
            StartLine: 1, StartCol: 1, EndLine: 5, EndCol: 1,
            Signature: $"public {kind} {name}",
            ContainerId: null,
            Modifiers: null,
            Accessibility: 6,
            XmlSummary: null,
            TestFramework: null));
    }

    private static bool IsUserVisible(ContentBlock block)
    {
        var ann = block.Annotations;
        if (ann is null) return true;
        var audience = ann.Audience;
        if (audience is null || audience.Count == 0) return true;
        return audience.Contains(Role.User);
    }
}
