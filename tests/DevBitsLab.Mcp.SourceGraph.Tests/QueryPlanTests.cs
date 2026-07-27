using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Index-selection regression for the open-language-contract schema flip
/// (<c>edges.kind</c> / <c>symbols.kind</c> moved from <c>INTEGER</c> to <c>TEXT</c>).
/// New indexes <c>idx_edges_kind_name</c>, <c>idx_edges_dst (dst, kind_name)</c>, and
/// <c>idx_symbols_kind_name</c> were added to keep query plans stable; the risk is
/// SQLite silently picking a <c>SCAN edges</c> instead of <c>SEARCH ... USING INDEX ...</c>
/// for one of the hot kind-filtered queries. This is NOT a perf benchmark — it runs
/// <c>EXPLAIN QUERY PLAN</c> against the four hot SQL strings (plus a symbols-kind probe)
/// and asserts each plan picks an expected index. A regression here is an early-warning
/// signal: an index was dropped, a column type drifted, or a refactor changed the SQL
/// in a way that defeats the planner.
///
/// <para>The SQL strings are duplicated verbatim from
/// <see cref="SqliteGraphStore.ListCallersAsync"/>, <see cref="SqliteGraphStore.ListCalleesAsync"/>,
/// <see cref="SqliteGraphStore.ImpactOfChangeAsync"/>, and
/// <see cref="SqliteGraphStore.FindByAnnotationAsync"/>. The store inlines them as locals
/// (the <c>kindClause</c> branch is computed at call time), so reflection isn't viable;
/// keeping a verbatim copy here is the cleanest path. If the store ever gains an
/// <c>internal const</c> for the SQL fragments, this test should switch to that.
/// Maintainers: when changing the SQL in <see cref="SqliteGraphStore"/>, update the
/// duplicate literal below to match.</para>
/// </summary>
public sealed class QueryPlanTests : IAsyncLifetime
{
    private string _dbPath = string.Empty;
    private SqliteGraphStore? _store;

    public async Task InitializeAsync()
    {
        // Path.Join over Path.Combine — Combine silently drops earlier args when a later one
        // looks absolute; Join always concatenates with a separator regardless.
        var tmp = Path.Join(Path.GetTempPath(), "sourcegraph-queryplan-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        _dbPath = Path.Join(tmp, "graph.db");

        // Temp file (not :memory:) so a second SqliteConnection opened in the test methods
        // shares the same on-disk schema; in-memory DBs don't outlive the originating
        // connection, which would defeat the EXPLAIN QUERY PLAN assertions.
        _store = new SqliteGraphStore(_dbPath);
        await _store.EnsureSchemaAsync();
        await SeedAsync();
        // ANALYZE refreshes sqlite_stat1 so the planner has accurate row counts to choose
        // between the two competing indexes (kind_name vs (dst, kind_name)). Without it the
        // planner falls back to "rule-based" choices that are correct but less representative
        // of what production looks like after the indexer has filled the DB.
        await using var conn = OpenSharedConnection(_dbPath);
        await conn.ExecuteAsync("ANALYZE;");
    }

    public async Task DisposeAsync()
    {
        if (_store is not null) await _store.DisposeAsync();
        try
        {
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
            var dbDir = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrEmpty(dbDir) && Directory.Exists(dbDir))
            {
                // Recursive delete swallows residual `-wal` / `-shm` SQLite sidecar files plus
                // any nested temp content the test or store may have created; without this the
                // per-test temp directory leaks under the system temp folder across many runs.
                Directory.Delete(dbDir, recursive: true);
            }
        }
        catch (IOException) { /* best-effort cleanup; another handle may still hold a file */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup; readonly bit or ACL drift */ }
    }

    /// <summary>
    /// Populate ~50 symbols across two kinds (<c>method</c>, <c>class</c>) and ~120 edges
    /// across three kinds (<c>calls</c>, <c>inherits</c>, <c>uses-type</c>) so the planner
    /// has a real choice between <c>idx_edges_kind_name</c>, <c>idx_edges_dst</c>, and a
    /// full-scan fallback. Plus a single annotation row so <see cref="FindByAnnotationAsync"/>'s
    /// plan probe has a non-empty result set to consider.
    /// </summary>
    private async Task SeedAsync()
    {
        var fileId = await _store!.UpsertFileAsync(
            path: "/virtual/queryplan-fixture.cs",
            contentSha256: new byte[32],
            indexedAt: DateTimeOffset.UtcNow,
            isGenerated: false);

        var symbolIds = await SeedSymbolsAsync(fileId);
        await SeedEdgesAsync(symbolIds);
        await SeedAnnotationsAsync(symbolIds);
    }

    /// <summary>
    /// 50 symbols: 10 classes + 40 methods. Mixed kinds give <c>idx_symbols_kind_name</c> a
    /// non-trivial selectivity story (otherwise the planner might prefer a full scan).
    /// </summary>
    private async Task<List<long>> SeedSymbolsAsync(long fileId)
    {
        var symbolIds = new List<long>(capacity: 50);
        for (int i = 0; i < 10; i++)
        {
            var classId = await _store!.UpsertSymbolAsync($"csharp:T:Sample.Cls{i}", new Symbol(
                Id: 0,
                Name: $"Cls{i}",
                Fqn: $"Sample.Cls{i}",
                Kind: SymbolKinds.Class,
                FileId: fileId,
                StartLine: i * 100, StartCol: 1,
                EndLine: i * 100 + 50, EndCol: 1,
                Signature: $"class Cls{i}",
                ContainerId: null,
                Modifiers: null,
                Accessibility: 6,
                XmlSummary: null,
                TestFramework: null));
            symbolIds.Add(classId);
        }
        for (int i = 0; i < 40; i++)
        {
            var methodId = await _store!.UpsertSymbolAsync($"csharp:M:Sample.M{i}", new Symbol(
                Id: 0,
                Name: $"M{i}",
                Fqn: $"Sample.M{i}",
                Kind: SymbolKinds.Method,
                FileId: fileId,
                StartLine: 1000 + i, StartCol: 1,
                EndLine: 1010 + i, EndCol: 1,
                Signature: $"void M{i}()",
                ContainerId: null,
                Modifiers: null,
                Accessibility: 6,
                XmlSummary: null,
                TestFramework: null));
            symbolIds.Add(methodId);
        }
        return symbolIds;
    }

    /// <summary>
    /// 120 edges across 3 kinds. Pattern: methods call methods, classes inherit classes,
    /// methods use-type classes. Density gives the planner enough rows per index that a
    /// SCAN fallback would be obviously worse on cost.
    /// </summary>
    private async Task SeedEdgesAsync(List<long> symbolIds)
    {
        var edges = new List<Edge>(capacity: 120);
        // 60 calls (method -> method)
        for (int i = 0; i < 60; i++)
        {
            var src = symbolIds[10 + (i % 40)];
            var dst = symbolIds[10 + ((i + 7) % 40)];
            if (src == dst) dst = symbolIds[10 + ((i + 8) % 40)];
            edges.Add(new Edge(src, dst, EdgeKinds.Calls));
        }
        // 30 inherits (class -> class)
        for (int i = 0; i < 30; i++)
        {
            var src = symbolIds[i % 10];
            var dst = symbolIds[(i + 3) % 10];
            if (src == dst) dst = symbolIds[(i + 4) % 10];
            edges.Add(new Edge(src, dst, EdgeKinds.Inherits));
        }
        // 30 uses-type (method -> class)
        for (int i = 0; i < 30; i++)
        {
            var src = symbolIds[10 + (i % 40)];
            var dst = symbolIds[i % 10];
            edges.Add(new Edge(src, dst, EdgeKinds.UsesType));
        }
        await _store!.BulkInsertEdgesAsync(edges);
    }

    /// <summary>
    /// One annotation so <see cref="FindByAnnotationAsync"/>'s planner choice has a non-empty
    /// data set to consider.
    /// </summary>
    private Task SeedAnnotationsAsync(List<long> symbolIds) =>
        _store!.BulkInsertAnnotationsAsync(new[]
        {
            new AnnotationRecord(
                SymbolId: symbolIds[10],
                Name: "HttpGet",
                FullName: "Microsoft.AspNetCore.Mvc.HttpGetAttribute",
                Flavor: "csharp-attribute",
                ArgsJson: null,
                AttributeSymbolId: null),
        });

    /// <summary>
    /// Open a fresh <see cref="SqliteConnection"/> against the same DB file the store
    /// uses. <c>Cache=Shared</c> matches the store's connection-string options so we
    /// see the same in-flight WAL and PRAGMA-tuned page cache; otherwise the planner
    /// can produce subtly different statistics.
    /// </summary>
    private static SqliteConnection OpenSharedConnection(string dbPath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        };
        var conn = new SqliteConnection(builder.ConnectionString);
        conn.Open();
        return conn;
    }

    /// <summary>
    /// Run <c>EXPLAIN QUERY PLAN</c> against the supplied SQL with the given parameters and
    /// return the concatenated plan text. SQLite emits one row per plan node with a
    /// <c>detail</c> column; we join them on newlines to make assertions readable when a
    /// test fails.
    /// </summary>
    private static async Task<string> ExplainAsync(SqliteConnection conn, string sql, object parameters)
    {
        var rows = await conn.QueryAsync<(int id, int parent, int notused, string detail)>(
            "EXPLAIN QUERY PLAN " + sql, parameters);
        return string.Join("\n", rows.Select(r => r.detail));
    }

    /// <summary>
    /// Shared assertion: every plan line that reads the <c>edges</c> table must be a
    /// <c>SEARCH ... USING ...</c> (index seek), never a <c>SCAN ... </c> (full scan).
    /// We accept three flavours of index for the edges table because the four hot SQL
    /// strings legitimately split between them depending on which side of the (src, dst,
    /// kind_name) PK the WHERE clause anchors:
    /// <list type="bullet">
    /// <item><c>idx_edges_dst (dst, kind_name)</c> — the composite for "incoming edges to
    ///   <c>dst</c>, optionally filtered by kind". Hot for <c>ListCallersAsync</c> and
    ///   the recursive CTE in <c>ImpactOfChangeAsync</c>.</item>
    /// <item><c>sqlite_autoindex_edges_1</c> — the auto-index over the PRIMARY KEY
    ///   <c>(src, dst, kind_name)</c>. Hot for <c>ListCalleesAsync</c> where the WHERE
    ///   anchors on <c>src</c>.</item>
    /// <item><c>idx_edges_kind_name</c> — the single-column kind index. Used by
    ///   kind-only fan-outs (e.g. <c>SELECT DISTINCT kind_name FROM edges</c>).</item>
    /// </list>
    /// A regression here means a column-type drift (TEXT&lt;-&gt;INTEGER) or an index
    /// rename made the planner abandon every index for the edges table.
    /// </summary>
    private static void PlanShouldUseEdgeIndex(string plan, string queryName)
    {
        plan.Should().NotBeNullOrEmpty($"EXPLAIN QUERY PLAN for {queryName} produced no rows");
        var lines = plan.Split('\n');

        // Find every line that touches the edges table. SQLite's EXPLAIN format is
        // "<verb> <name-or-alias> [USING ...]"; we identify edges-side lines by the
        // verb token followed by either the literal "edges" or a single-letter alias
        // we control in the test SQL ("e ").
        var edgeLines = lines.Where(l =>
            l.Contains(" edges ") || l.Contains(" edges\t") || l.EndsWith(" edges")
            || l.Contains(" e ") || l.EndsWith(" e")).ToList();

        edgeLines.Should().NotBeEmpty(
            $"the {queryName} plan never references the edges table — wrong test fixture? Full plan:\n{plan}");

        foreach (var line in edgeLines)
        {
            // Each edges-touching line must be a SEARCH (not SCAN) and must name an
            // acceptable index. "USING INDEX <name>" is the explicit case; "USING
            // COVERING INDEX <name>" is what SQLite prints when the column set is
            // fully covered (sqlite_autoindex_edges_1 over the PK is a covering case
            // for ListCallees). We accept any of the three known index names.
            line.Should().StartWith("SEARCH",
                $"the {queryName} plan contains a non-SEARCH line for edges: \"{line}\". Full plan:\n{plan}");
            var usesAcceptableIndex =
                line.Contains("idx_edges_dst")
                || line.Contains("idx_edges_kind_name")
                || line.Contains("idx_edges_kind_src")
                || line.Contains("idx_edges_kind_dst_src")
                || line.Contains("sqlite_autoindex_edges_"); // PK auto-index
            usesAcceptableIndex.Should().BeTrue(
                $"the {queryName} plan reads edges via an unexpected index: \"{line}\". Full plan:\n{plan}");
        }
    }

    [Fact]
    public async Task ListCallersAsync_usesEdgeIndex_forKindFilter()
    {
        // Verbatim copy of SqliteGraphStore.ListCallersAsync's SQL when edgeKind != null,
        // including the `e.payload AS PayloadJson` projection added by harden-sdk-pre-xaml.
        // Maintainer: keep this in sync with the source-of-truth method.
        const string sql = """
            SELECT s.id, s.name, s.fqn, s.kind_name AS Kind, f.path AS FilePath, s.start_line AS StartLine, s.start_col AS StartCol,
                   s.end_line AS EndLine, s.end_col AS EndCol, s.signature,
                   s.modifiers AS Modifiers, s.accessibility AS Accessibility, s.xml_summary AS XmlSummary,
                   f.is_generated AS IsGenerated, s.test_framework AS TestFramework,
                   s.canonical_key AS CanonicalKey,
                   e.payload     AS PayloadJson
            FROM edges e
            JOIN symbols s ON s.id = e.src
            JOIN files   f ON f.id = s.file_id
            WHERE e.dst = @id AND e.kind_name = @kind
            ORDER BY f.path, s.start_line
            LIMIT @limit;
            """;

        await using var conn = OpenSharedConnection(_dbPath);
        var plan = await ExplainAsync(conn, sql, new { id = 1L, kind = "calls", limit = 50 });
        PlanShouldUseEdgeIndex(plan, nameof(ListCallersAsync_usesEdgeIndex_forKindFilter));
    }

    [Fact]
    public async Task ListCalleesAsync_usesEdgeIndex_forKindFilter()
    {
        // Verbatim copy of SqliteGraphStore.ListCalleesAsync's SQL when edgeKind != null,
        // including the `e.payload AS PayloadJson` projection added by harden-sdk-pre-xaml.
        // Note the (src, dst, kind_name) PK gives this query a perfect-match plan via the
        // PK's leading-column match on src. We accept either the PK plan or one of the
        // secondary indexes; both are non-scan.
        const string sql = """
            SELECT s.id, s.name, s.fqn, s.kind_name AS Kind, f.path AS FilePath, s.start_line AS StartLine, s.start_col AS StartCol,
                   s.end_line AS EndLine, s.end_col AS EndCol, s.signature,
                   s.modifiers AS Modifiers, s.accessibility AS Accessibility, s.xml_summary AS XmlSummary,
                   f.is_generated AS IsGenerated, s.test_framework AS TestFramework,
                   s.canonical_key AS CanonicalKey,
                   e.payload     AS PayloadJson
            FROM edges e
            JOIN symbols s ON s.id = e.dst
            JOIN files   f ON f.id = s.file_id
            WHERE e.src = @id AND e.kind_name = @kind
            ORDER BY f.path, s.start_line
            LIMIT @limit;
            """;

        await using var conn = OpenSharedConnection(_dbPath);
        var plan = await ExplainAsync(conn, sql, new { id = 11L, kind = "calls", limit = 50 });
        PlanShouldUseEdgeIndex(plan, nameof(ListCalleesAsync_usesEdgeIndex_forKindFilter));
    }

    [Fact]
    public async Task ImpactOfChangeAsync_recursiveCte_usesEdgeIndex()
    {
        // Verbatim copy of SqliteGraphStore.ImpactOfChangeAsync when edgeKind != null. The
        // recursive CTE pulls upstream(src) from edges twice: once seeded by dst = @id, once
        // joining e.dst = u.id. Both inner SELECTs are kind-filtered. We assert the planner
        // never falls back to a scan on either pass.
        const string sql = """
            WITH RECURSIVE upstream(id, depth) AS (
                SELECT src, 1 FROM edges WHERE dst = @id AND kind_name = @kind
                UNION
                SELECT e.src, u.depth + 1
                FROM edges e
                JOIN upstream u ON e.dst = u.id
                WHERE u.depth < @maxDepth AND e.kind_name = @kind
            )
            SELECT s.id, s.name, s.fqn, s.kind_name AS Kind, f.path AS FilePath, s.start_line AS StartLine, s.start_col AS StartCol,
                   s.end_line AS EndLine, s.end_col AS EndCol, s.signature,
                   s.modifiers AS Modifiers, s.accessibility AS Accessibility, s.xml_summary AS XmlSummary,
                   f.is_generated AS IsGenerated, s.test_framework AS TestFramework,
                   s.canonical_key AS CanonicalKey,
                   MIN(u.depth) AS Depth
            FROM upstream u
            JOIN symbols s ON s.id = u.id
            JOIN files   f ON f.id = s.file_id
            GROUP BY s.id
            ORDER BY Depth, s.fqn
            LIMIT @limit;
            """;

        await using var conn = OpenSharedConnection(_dbPath);
        var plan = await ExplainAsync(conn, sql, new { id = 1L, kind = "calls", maxDepth = 4, limit = 100 });
        PlanShouldUseEdgeIndex(plan, nameof(ImpactOfChangeAsync_recursiveCte_usesEdgeIndex));
    }

    [Fact]
    public async Task FindByAnnotationAsync_kindFilter_planIsIndexAware()
    {
        // Verbatim copy of SqliteGraphStore.FindByAnnotationAsync's SQL with `kindFilter`
        // non-null and `argSubstring` not used (so no FTS join). The query joins
        // annotations -> symbols -> files. Two valid plans exist depending on row counts:
        // (1) drive from annotations, lookup symbols by integer-PK rowid, lookup files
        //     by integer-PK rowid — this is what the planner picks when annotations is
        //     small (the production reality, since one production scan still beats a
        //     filtered hash for a few-row table); or
        // (2) drive from symbols via idx_symbols_kind_name, lookup annotations by the
        //     idx_annotations_symbol index, lookup files by integer-PK rowid — this
        //     fires when annotations grows large vs. the kind-filtered symbol slice.
        // Either is correct. The assertion guards against the regression we actually
        // care about: the symbols-side row lookup falling back to a SCAN. The
        // annotations side may legitimately scan a tiny table; we assert it doesn't
        // SCAN if there's a non-trivial row count, which the seed guarantees there
        // isn't (one row), so we let it pass.
        //
        // Divergence note: this query does NOT touch the edges table, so the
        // edges-index assertion in PlanShouldUseEdgeIndex doesn't apply. The original
        // task asked for "USING INDEX idx_edges_*" assertions; for FindByAnnotation
        // that test would never apply. We probe the symbols-side index health instead.
        const string sql = """
            SELECT s.id, s.name, s.fqn, s.kind_name AS Kind, f.path AS FilePath, s.start_line AS StartLine, s.start_col AS StartCol,
                   s.end_line AS EndLine, s.end_col AS EndCol, s.signature,
                   s.modifiers AS Modifiers, s.accessibility AS Accessibility, s.xml_summary AS XmlSummary,
                   f.is_generated AS IsGenerated, s.test_framework AS TestFramework,
                   s.canonical_key AS CanonicalKey
            FROM annotations a
            JOIN symbols s ON s.id = a.symbol_id
            JOIN files   f ON f.id = s.file_id
            WHERE a.name = @name
              AND s.kind_name = @kind
            GROUP BY s.id
            ORDER BY f.path, s.start_line
            LIMIT @limit;
            """;

        await using var conn = OpenSharedConnection(_dbPath);
        var plan = await ExplainAsync(conn, sql, new { name = "HttpGet", kind = SymbolKinds.Class, limit = 50 });

        plan.Should().NotBeNullOrEmpty();
        var lines = plan.Split('\n');
        // For each line touching the symbols table (alias `s` or full name `symbols`),
        // assert it's a SEARCH (index seek) — never a SCAN. The legitimate plans both
        // satisfy this: rowid-PK lookup or idx_symbols_kind_name seek.
        var symbolLines = lines.Where(l =>
            l.Contains(" symbols ") || l.Contains(" symbols\t") || l.EndsWith(" symbols")
            || l.Contains(" s ") || l.EndsWith(" s")).ToList();
        symbolLines.Should().NotBeEmpty(
            $"the FindByAnnotation plan never references symbols — wrong test fixture? Full plan:\n{plan}");
        foreach (var line in symbolLines)
        {
            line.Should().StartWith("SEARCH",
                $"FindByAnnotation plan contains a non-SEARCH line for symbols: \"{line}\". Full plan:\n{plan}");
        }
    }

    [Fact]
    public async Task SymbolsKindFilter_usesSymbolsKindIndex()
    {
        // Direct probe of idx_symbols_kind_name. A simple `SELECT id FROM symbols WHERE
        // kind_name = ?` is the canonical shape for any caller that wants to fan out over
        // every method/class/interface in the index — for example, the future `vocabulary
        // list` CLI's per-kind COUNT. The planner has two valid choices: full scan (cheap
        // when the table is tiny) or the dedicated index. With ~50 rows across 2 kinds,
        // SQLite usually picks the index, but we accept any plan that avoids `SCAN symbols`
        // because a covering scan over a tiny table is also defensible. The contract here
        // is: when the table grows, the index is available; the planner will use it.
        const string sql = "SELECT id FROM symbols WHERE kind_name = @kind;";

        await using var conn = OpenSharedConnection(_dbPath);
        var plan = await ExplainAsync(conn, sql, new { kind = SymbolKinds.Method });

        plan.Should().NotBeNullOrEmpty();
        // Strict assertion: the index must be picked. A SCAN here would mean the planner
        // never considered idx_symbols_kind_name, which is the regression we're guarding.
        var usesIndex = plan.Contains("USING INDEX idx_symbols_kind_name")
            || plan.Contains("USING COVERING INDEX idx_symbols_kind_name");
        usesIndex.Should().BeTrue(
            $"a SELECT WHERE kind_name = ? against symbols should pick idx_symbols_kind_name; full plan was:\n{plan}");
        plan.Should().NotContain(
            "SCAN symbols",
            $"a SCAN symbols line means idx_symbols_kind_name was ignored. Full plan:\n{plan}");
    }
}
