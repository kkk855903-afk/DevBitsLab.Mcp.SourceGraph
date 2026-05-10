using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// End-to-end coverage for <see cref="MultiScopeReadOnlyConnection.OpenAsync"/> against a
/// hand-built repo layout (<c>.sourcegraph/_meta.db</c> + <c>.sourcegraph/scopes/&lt;id&gt;.db</c>).
/// Exercises every scenario from the live storage spec's
/// <i>Read-only multi-scope attached connection helper</i> requirement (folded in by the
/// archived <c>add-graph-query</c> change):
///   - default <c>"*"</c> excludes isolated scopes
///   - explicit naming includes isolated scopes
///   - comma-list filter is honoured exactly
///   - <c>PRAGMA query_only = 1</c> on the connection rejects writes (<c>SQLITE_READONLY</c>)
///   - resolved-count overflow throws <see cref="ScopeAttachLimitExceededException"/>
///   - empty resolved set throws <see cref="ArgumentException"/>(<c>scopeFilter</c>)
///   - missing per-scope DB file throws <see cref="FileNotFoundException"/>
///   - per-call connections do not share TEMP-view state
///
/// <para>The test layout is throwaway temp directories per fixture — each scope DB is built
/// by <see cref="SqliteGraphStore"/>, the registry by <see cref="SqliteScopeRegistry"/>. This
/// avoids needing the multi-scope fixture under <c>tests/fixtures/MultiScope/</c> for cases
/// that don't depend on real index data.</para>
/// </summary>
public sealed class MultiScopeReadOnlyConnectionTests : IAsyncLifetime
{
    private string _repoRoot = string.Empty;
    private SqliteScopeRegistry? _registry;

    public async Task InitializeAsync()
    {
        // Path.Join over Path.Combine — Combine silently drops earlier args when a later one
        // looks absolute; Join always concatenates with a separator regardless.
        _repoRoot = Path.Join(Path.GetTempPath(), "sourcegraph-multiscope-conn-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ScopeLayout.SourcegraphDir(_repoRoot));
        Directory.CreateDirectory(ScopeLayout.ScopesDirectory(_repoRoot));

        // Build a registry against the standard <repo>/.sourcegraph/_meta.db location and a
        // canonical 3-scope shape used by the spec scenarios:
        //   - frontend: not isolated
        //   - backend:  not isolated
        //   - vendor:   isolated (excluded from `*` fan-out)
        _registry = new SqliteScopeRegistry(ScopeLayout.MetaDbPath(_repoRoot));
        await _registry.EnsureSchemaAsync();
        await SeedScopeAsync("frontend", isolated: false);
        await SeedScopeAsync("backend",  isolated: false);
        await SeedScopeAsync("vendor",   isolated: true);
    }

    public async Task DisposeAsync()
    {
        if (_registry is not null) await _registry.DisposeAsync();
        try { Directory.Delete(_repoRoot, recursive: true); }
        catch (IOException) { /* best-effort */ }
        catch (UnauthorizedAccessException) { /* best-effort */ }
    }

    [Fact]
    public async Task DefaultStarFilter_excludes_isolated_scopes()
    {
        await using var conn = await MultiScopeReadOnlyConnection.OpenAsync(
            _registry!, _repoRoot, scopeFilter: "*");

        var attached = await ListAttachedAsync(conn);
        attached.Should().BeEquivalentTo(
            new[] { "main", "temp", "meta", "frontend", "backend" },
            "default `*` includes meta + every non-isolated scope; vendor (isolated) is omitted "
            + "(`temp` and `main` are SQLite's always-present database list entries)");
    }

    [Fact]
    public async Task ExplicitNaming_includes_isolated_scope()
    {
        await using var conn = await MultiScopeReadOnlyConnection.OpenAsync(
            _registry!, _repoRoot, scopeFilter: "vendor");

        var attached = await ListAttachedAsync(conn);
        attached.Should().BeEquivalentTo(
            new[] { "main", "temp", "meta", "vendor" },
            "explicit naming permits isolated scopes — the `isolated` flag only affects `*` fan-out");
    }

    [Fact]
    public async Task CommaListFilter_is_honored_exactly()
    {
        await using var conn = await MultiScopeReadOnlyConnection.OpenAsync(
            _registry!, _repoRoot, scopeFilter: "frontend, vendor");

        var attached = await ListAttachedAsync(conn);
        attached.Should().BeEquivalentTo(
            new[] { "main", "temp", "meta", "frontend", "vendor" },
            "comma-list filter attaches exactly the named scopes (whitespace tolerated); "
            + "backend is NOT attached even though it is non-isolated");
    }

    [Fact]
    public async Task ReadOnly_enforced_at_per_scope_ATTACH()
    {
        await using var conn = await MultiScopeReadOnlyConnection.OpenAsync(
            _registry!, _repoRoot, scopeFilter: "frontend");

        // Try a direct INSERT against the attached scope DB. `PRAGMA query_only = 1` set on
        // the connection (after the TEMP VIEW DDL is applied) is the safety bound for every
        // attached DB — the operation should be rejected with SQLITE_READONLY (8), not
        // silently succeed and not leak past the in-memory boundary.
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO frontend.symbols(id, canonical_key, name, fqn, kind_name, file_id, start_line, start_col, end_line, end_col, accessibility) "
                        + "VALUES (999, 'evil', 'evil', 'evil', 'class', 1, 1, 1, 1, 1, 6);";

        var act = async () => await cmd.ExecuteNonQueryAsync();
        var ex = await act.Should().ThrowAsync<SqliteException>();
        // SqliteErrorCode 8 = SQLITE_READONLY ("attempt to write a readonly database").
        ex.Which.SqliteErrorCode.Should().Be(8,
            "PRAGMA query_only = 1 makes the connection refuse writes against any attached scope DB");
    }

    [Fact]
    public async Task Overflow_throws_ScopeAttachLimitExceeded_with_resolved_payload()
    {
        // Add 12 extra non-isolated scopes (we already have 2: frontend, backend) so the
        // resolved set for `*` is 14, which exceeds the bundled SQLite cap of
        // SQLITE_MAX_ATTACHED=10 (one slot reserved for meta = 9 effective scope slots).
        // We pass a generous maxAttached=64; the helper's secondary check picks up the
        // SQLite-imposed ceiling and throws with a payload that surfaces every resolved id.
        for (var i = 0; i < 12; i++)
        {
            await SeedScopeAsync($"extra-{i:D2}", isolated: false);
        }

        var act = async () => await MultiScopeReadOnlyConnection.OpenAsync(
            _registry!, _repoRoot, scopeFilter: "*", maxAttached: 64);

        var ex = await act.Should().ThrowAsync<ScopeAttachLimitExceededException>();
        ex.Which.ResolvedScopes.Should().HaveCount(14, "frontend + backend + 12 extras");
        ex.Which.ResolvedScopes.Should().Contain("frontend");
        ex.Which.ResolvedScopes.Should().Contain("backend");
        ex.Which.ResolvedScopes.Should().NotContain("vendor", "vendor is isolated, never in `*`");
        ex.Which.Limit.Should().BeGreaterThan(0,
            "the limit reflects whichever ceiling is binding: maxAttached or SQLite's runtime cap");
        ex.Which.Limit.Should().BeLessThan(64,
            "the bundled SQLite caps SQLITE_LIMIT_ATTACHED at 10 today, so the runtime ceiling "
            + "(minus 1 for meta) is the binding constraint when the resolved count overflows");
    }

    [Fact]
    public async Task Overflow_with_explicit_low_maxAttached_uses_configured_limit()
    {
        // When maxAttached is the smaller bound, the exception's Limit reports that smaller
        // value verbatim. This is the path agents will hit on a server configured with a
        // tighter cap than SQLite's runtime ceiling.
        var act = async () => await MultiScopeReadOnlyConnection.OpenAsync(
            _registry!, _repoRoot, scopeFilter: "*", maxAttached: 1);

        var ex = await act.Should().ThrowAsync<ScopeAttachLimitExceededException>();
        ex.Which.ResolvedScopes.Should().HaveCount(2, "frontend + backend (vendor is isolated)");
        ex.Which.Limit.Should().Be(1, "the configured maxAttached is the binding constraint here");
    }

    [Fact]
    public async Task Open_twice_yields_independent_TEMP_views()
    {
        // TEMP views live for the duration of one connection. Opening two concurrently must
        // produce two independent in-memory main DBs, each with its own view set; closing one
        // must not affect the other. This nails down the per-call lifecycle promise from the
        // spec's `Connection is per-call and disposable` scenario.
        await using var connA = await MultiScopeReadOnlyConnection.OpenAsync(
            _registry!, _repoRoot, scopeFilter: "frontend");
        await using var connB = await MultiScopeReadOnlyConnection.OpenAsync(
            _registry!, _repoRoot, scopeFilter: "backend");

        // Both should see v_symbols (their own per-scope branch, regardless of the other).
        var aHasView = await connA.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM sqlite_temp_master WHERE type='view' AND name='v_symbols';");
        var bHasView = await connB.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM sqlite_temp_master WHERE type='view' AND name='v_symbols';");
        aHasView.Should().Be(1, "connection A has its own v_symbols TEMP view");
        bHasView.Should().Be(1, "connection B has its own v_symbols TEMP view");

        // The two connections see different attached sets; verify by inspecting pragma_database_list.
        var aAttached = await ListAttachedAsync(connA);
        var bAttached = await ListAttachedAsync(connB);
        aAttached.Should().Contain("frontend").And.NotContain("backend");
        bAttached.Should().Contain("backend").And.NotContain("frontend");
    }

    [Fact]
    public async Task Empty_scopes_db_yields_empty_views_but_views_exist()
    {
        // Per the seeded layout the per-scope DBs are empty (just the schema). Querying any
        // view should return zero rows, but the view itself must exist and be queryable.
        await using var conn = await MultiScopeReadOnlyConnection.OpenAsync(
            _registry!, _repoRoot, scopeFilter: "frontend");

        var symCount = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM v_symbols;");
        symCount.Should().Be(0, "the seeded scope DB is empty (schema only)");

        var scopeCount = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM v_scopes;");
        scopeCount.Should().Be(3, "the meta DB has 3 scopes registered (frontend, backend, vendor)");
    }

    [Fact]
    public async Task Unknown_scope_id_throws_ArgumentException()
    {
        var act = async () => await MultiScopeReadOnlyConnection.OpenAsync(
            _registry!, _repoRoot, scopeFilter: "no-such-scope");

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*not registered*");
    }

    [Fact]
    public async Task Star_filter_against_all_isolated_registry_throws_ArgumentException()
    {
        // Build a fresh fixture where every registered scope is isolated, then ask for `*`.
        // The resolved set is empty, which would silently produce no-ATTACH and a degenerate
        // view layer — a footgun. The helper must surface the empty-resolution case explicitly.
        var freshRoot = Path.Join(Path.GetTempPath(), "sourcegraph-empty-scopes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ScopeLayout.SourcegraphDir(freshRoot));
        Directory.CreateDirectory(ScopeLayout.ScopesDirectory(freshRoot));
        await using var registry = new SqliteScopeRegistry(ScopeLayout.MetaDbPath(freshRoot));
        await registry.EnsureSchemaAsync();
        await SeedScopeDbAsync(freshRoot, "vendor-a");
        await SeedScopeDbAsync(freshRoot, "vendor-b");
        await registry.UpsertAsync(new ScopeRow("vendor-a", "vendor-a", freshRoot, "{}", Isolated: true, DateTimeOffset.UtcNow, "ok"));
        await registry.UpsertAsync(new ScopeRow("vendor-b", "vendor-b", freshRoot, "{}", Isolated: true, DateTimeOffset.UtcNow, "ok"));

        try
        {
            var act = async () => await MultiScopeReadOnlyConnection.OpenAsync(
                registry, freshRoot, scopeFilter: "*");

            var ex = await act.Should().ThrowAsync<ArgumentException>();
            ex.Which.ParamName.Should().Be("scopeFilter");
            ex.Which.Message.Should().Contain("resolved to no scopes");
            ex.Which.Message.Should().Contain("isolated", "the diagnosis should hint at the cause");
        }
        finally
        {
            try { Directory.Delete(freshRoot, recursive: true); }
            catch (IOException) { /* best-effort */ }
        }
    }

    [Fact]
    public async Task Missing_per_scope_db_file_throws_FileNotFoundException()
    {
        // A scope is registered in _meta.db but its on-disk DB file has been deleted (manual rm,
        // failed re-index, etc.). ATTACH on a missing path would silently materialise an empty
        // file under default SQLite semantics; the helper validates File.Exists first and throws
        // a clear error so the agent (via tool-level catch) knows what to fix.
        var freshRoot = Path.Join(Path.GetTempPath(), "sourcegraph-missing-db-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ScopeLayout.SourcegraphDir(freshRoot));
        Directory.CreateDirectory(ScopeLayout.ScopesDirectory(freshRoot));
        await using var registry = new SqliteScopeRegistry(ScopeLayout.MetaDbPath(freshRoot));
        await registry.EnsureSchemaAsync();
        // Register the scope but DO NOT seed its DB file.
        await registry.UpsertAsync(new ScopeRow("frontend", "frontend", freshRoot, "{}", Isolated: false, DateTimeOffset.UtcNow, "ok"));

        try
        {
            var act = async () => await MultiScopeReadOnlyConnection.OpenAsync(
                registry, freshRoot, scopeFilter: "*");

            var ex = await act.Should().ThrowAsync<FileNotFoundException>();
            ex.Which.Message.Should().Contain("frontend", "the alias appears in the message");
            ex.Which.FileName.Should().Be(ScopeLayout.ScopeDbPath(freshRoot, "frontend"));
            // The phantom file must NOT have been materialised by the failed ATTACH attempt.
            File.Exists(ScopeLayout.ScopeDbPath(freshRoot, "frontend")).Should().BeFalse(
                "validating File.Exists before ATTACH prevents SQLite from creating an empty placeholder");
        }
        finally
        {
            try { Directory.Delete(freshRoot, recursive: true); }
            catch (IOException) { /* best-effort */ }
        }
    }

    /// <summary>
    /// Inspect SQLite's own database list (the in-memory main DB plus every ATTACH alias)
    /// via <c>pragma_database_list</c>. Returns the alias names sorted; the well-known
    /// <c>"main"</c> entry is the in-memory main DB.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ListAttachedAsync(SqliteConnection conn)
    {
        var rows = await conn.QueryAsync<string>(
            "SELECT name FROM pragma_database_list ORDER BY name;");
        return rows.ToList();
    }

    /// <summary>
    /// Create a scope's per-scope DB on disk (with the standard schema applied) and register
    /// the scope in the meta DB. The DB itself is empty after schema setup — these tests
    /// don't require seeded rows for the layout/attachment scenarios.
    /// </summary>
    private async Task SeedScopeAsync(string scopeId, bool isolated)
    {
        await SeedScopeDbAsync(_repoRoot, scopeId);
        await _registry!.UpsertAsync(new ScopeRow(
            Id: scopeId,
            Name: scopeId,
            Root: _repoRoot,
            ProjectSetJson: "{}",
            Isolated: isolated,
            LastIndexedAt: DateTimeOffset.UtcNow,
            Status: "ok"));
    }

    /// <summary>
    /// Just create the per-scope DB file (with schema). Does NOT touch the registry — used by
    /// the empty-resolution test that builds its own registry on a fresh repoRoot.
    /// </summary>
    private static async Task SeedScopeDbAsync(string repoRoot, string scopeId)
    {
        var dbPath = ScopeLayout.ScopeDbPath(repoRoot, scopeId);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        await using var store = new SqliteGraphStore(dbPath);
        await store.EnsureSchemaAsync();
    }
}
