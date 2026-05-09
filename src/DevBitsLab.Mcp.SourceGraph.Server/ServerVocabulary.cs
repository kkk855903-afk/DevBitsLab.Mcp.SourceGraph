using System.Reflection;
using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DevBitsLab.Mcp.SourceGraph.Server;

/// <summary>
/// Computes the open-language vocabularies the MCP server publishes alongside its
/// <c>ServerInstructions</c>: every distinct edge-kind, symbol-kind, and annotation-flavor that
/// could be queried anywhere on this server, broken out per-scope. The published payload carries
/// both a top-level <b>server-wide union</b> across every configured scope (good for "what's
/// possible somewhere here?") and a per-scope map (good for "what's valid in scope X?"). Each
/// scope's lists are the union of the SDK's well-known constants (<see cref="EdgeKinds"/>,
/// <see cref="SymbolKinds"/>) with whatever that scope's DB currently contains, so plugin-defined
/// kinds (e.g. <c>"vue-directive"</c>, <c>"renders-component"</c>) surface alongside the built-in
/// C# kinds without a code change in the host.
///
/// <para>The lists are surfaced as JSON arrays of lower-case kebab-case identifiers in the
/// MCP <c>initialize</c> response. The wire shape is documented in
/// <see cref="VocabularyResult"/>; we publish through
/// <c>McpServerOptions.Capabilities.Experimental</c>, which the MCP SDK round-trips into the
/// <c>capabilities.experimental</c> object on the wire — the spec's stable extension point for
/// non-standard server capabilities. Clients that don't know to look ignore the field; clients
/// that do (the agent harness) read it once at session start and use the per-scope map to
/// validate the <c>kind</c> argument they pass to tools like <c>list_callers</c>.</para>
///
/// <para>Suppression mirrors <see cref="ServerInstructions"/>: pass <c>--no-instructions</c> or
/// set <c>SOURCEGRAPH_NO_INSTRUCTIONS=1</c> and the arrays are not added to the capabilities
/// payload at all.</para>
/// </summary>
internal static class ServerVocabulary
{
    /// <summary>The capability key under <c>capabilities.experimental</c> the vocabulary lives at.</summary>
    public const string CapabilityKey = "sourcegraph.vocabulary";

    /// <summary>
    /// Compute the vocabulary for every scope in <paramref name="scopes"/>. For each scope: open
    /// the DB in true <see cref="SqliteOpenMode.ReadOnly"/> mode, query the three distinct-kind
    /// columns, and union the result with the SDK constants. The result also exposes a
    /// server-wide union (every kind seen in any scope) for clients that don't yet know to read
    /// the per-scope map. The probe deliberately bypasses <see cref="SqliteGraphStore"/> so it
    /// never runs <c>EnsureSchemaAsync</c> (which would drop-and-rebuild a stale schema as a
    /// side effect of vocabulary collection) and never flips the journal_mode pragma. A scope DB
    /// at an older schema version simply errors out on the missing columns; the catch falls back
    /// to the SDK constants for that scope. Each missing or unreadable scope is skipped silently
    /// — the host stays up regardless.
    /// </summary>
    public static async Task<VocabularyResult> ComputeAsync(
        IReadOnlyList<Scope> scopes,
        ILogger logger,
        CancellationToken ct = default)
    {
        var unionEdge = new SortedSet<string>(StringComparer.Ordinal);
        var unionSymbol = new SortedSet<string>(StringComparer.Ordinal);
        var unionFlavor = new SortedSet<string>(StringComparer.Ordinal);
        var perScope = new Dictionary<string, ScopeVocabulary>(StringComparer.Ordinal);

        foreach (var scope in scopes)
        {
            ct.ThrowIfCancellationRequested();

            // Per-scope sets: SDK constants are always present even on a fresh / empty scope so a
            // built-in kind like "calls" never registers as "unknown" just because no edge of
            // that kind has been indexed yet.
            var edgeKinds = new SortedSet<string>(StringComparer.Ordinal);
            var symbolKinds = new SortedSet<string>(StringComparer.Ordinal);
            var annotationFlavors = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var v in EnumerateConstantStrings(typeof(EdgeKinds))) edgeKinds.Add(v);
            foreach (var v in EnumerateConstantStrings(typeof(SymbolKinds))) symbolKinds.Add(v);
            // No SDK constant for annotation flavors — flavor names are scoped to language
            // plugins. Built-in C# uses "csharp-attribute"; seed it so single-language scopes
            // still get a useful list before any indexed annotation has landed.
            annotationFlavors.Add("csharp-attribute");

            var dbPath = ScopeLayout.ScopeDbPath(scope.Root, scope.Id);
            if (File.Exists(dbPath))
            {
                try
                {
                    await ProbeScopeAsync(dbPath, edgeKinds, symbolKinds, annotationFlavors, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Caller cancelled — propagate cleanly rather than swallowing.
                    throw;
                }
                catch (Exception ex) when (
                    ex is IOException or SqliteException or InvalidOperationException or UnauthorizedAccessException)
                {
                    // File missing/locked, schema older than the columns we query, ambient state weird.
                    // Probe is best-effort; the SDK constants already cover the common case.
                    logger.LogDebug(ex, "Vocabulary probe failed for scope `{Id}` at {Path}", scope.Id, dbPath);
                }
            }

            perScope[scope.Id] = new ScopeVocabulary(
                edgeKinds.ToList(),
                symbolKinds.ToList(),
                annotationFlavors.ToList());

            unionEdge.UnionWith(edgeKinds);
            unionSymbol.UnionWith(symbolKinds);
            unionFlavor.UnionWith(annotationFlavors);
        }

        return new VocabularyResult(
            unionEdge.ToList(),
            unionSymbol.ToList(),
            unionFlavor.ToList(),
            perScope);
    }

    /// <summary>
    /// Open <paramref name="dbPath"/> in true read-only mode and union its three distinct-kind
    /// columns into the supplied sets. Uses raw <see cref="SqliteCommand"/> so we bypass
    /// <see cref="SqliteGraphStore"/>'s schema migration and journal-mode pragmas — this path must
    /// have zero side effects on the file. Throws on missing columns / older schemas; the caller
    /// catches and falls back to the SDK constants.
    /// </summary>
    private static async Task ProbeScopeAsync(
        string dbPath,
        SortedSet<string> edgeKinds,
        SortedSet<string> symbolKinds,
        SortedSet<string> annotationFlavors,
        CancellationToken ct)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            // ReadOnly mode prevents any writes, schema migration, or WAL/journal pragmas from
            // mutating the file. Pooling=false keeps this transient connection out of the shared
            // pool that the indexer-side SqliteGraphStore uses.
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ConnectionString;

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await ReadDistinctIntoAsync(connection, "SELECT DISTINCT kind_name FROM edges ORDER BY kind_name", edgeKinds, ct).ConfigureAwait(false);
        await ReadDistinctIntoAsync(connection, "SELECT DISTINCT kind_name FROM symbols ORDER BY kind_name", symbolKinds, ct).ConfigureAwait(false);
        await ReadDistinctIntoAsync(connection, "SELECT DISTINCT flavor FROM annotations ORDER BY flavor", annotationFlavors, ct).ConfigureAwait(false);
    }

    private static async Task ReadDistinctIntoAsync(
        SqliteConnection connection,
        string sql,
        SortedSet<string> sink,
        CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            // Filter+map inline rather than via Where() — async stream readers don't compose with
            // System.Linq cleanly without an extra IAsyncEnumerable adapter dependency.
            if (reader.IsDBNull(0)) continue;
            var v = reader.GetString(0);
            if (v.Length == 0) continue;
            sink.Add(v.ToLowerInvariant());
        }
    }

    /// <summary>
    /// SDK-declared edge kind constants (e.g. <c>"calls"</c>, <c>"tests"</c>) — kinds the host's
    /// built-in indexer is configured to emit, regardless of whether the active scope has any
    /// such edges in storage yet. Used by tool-side validators (<c>list_callers</c> /
    /// <c>list_callees</c>) so a fresh / never-indexed scope doesn't reject a built-in kind name
    /// as "unknown vocabulary". Computed once on first read.
    /// </summary>
    public static IReadOnlyCollection<string> SdkEdgeKinds { get; } =
        EnumerateConstantStrings(typeof(EdgeKinds)).ToHashSet(StringComparer.Ordinal);

    /// <summary>SDK-declared symbol kind constants. Same semantics as <see cref="SdkEdgeKinds"/>.</summary>
    public static IReadOnlyCollection<string> SdkSymbolKinds { get; } =
        EnumerateConstantStrings(typeof(SymbolKinds)).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Enumerate every <c>public const string</c> field declared on <paramref name="constantsType"/>.
    /// Used to pick up the SDK's well-known kind / flavor identifiers without a hand-maintained
    /// echo of the same list here.
    /// </summary>
    private static IEnumerable<string> EnumerateConstantStrings(Type constantsType) =>
        constantsType
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Select(f => f.GetRawConstantValue() as string)
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!.ToLowerInvariant());
}

/// <summary>
/// Wire shape published under <c>capabilities.experimental["sourcegraph.vocabulary"]</c> in the
/// MCP <c>initialize</c> response. The top-level <see cref="EdgeKinds"/> /
/// <see cref="SymbolKinds"/> / <see cref="AnnotationFlavors"/> lists are the <b>server-wide
/// union</b> across every configured scope — useful for clients that ask "what's possible
/// somewhere on this server?". <see cref="Scopes"/> carries the per-scope breakdown for clients
/// that need to validate a tool argument against a specific scope's vocabulary. Every list is
/// sorted, lower-case, and kebab-case.
/// </summary>
public sealed record VocabularyResult(
    IReadOnlyList<string> EdgeKinds,
    IReadOnlyList<string> SymbolKinds,
    IReadOnlyList<string> AnnotationFlavors,
    IReadOnlyDictionary<string, ScopeVocabulary> Scopes);

/// <summary>
/// Per-scope subset of <see cref="VocabularyResult"/> — the kebab-case kind / flavor identifiers
/// queryable against one scope specifically. A scope's lists union the SDK constants (so a
/// built-in kind like <c>"calls"</c> appears even on a fresh / never-indexed scope) with the
/// distinct values present in that scope's storage.
/// </summary>
public sealed record ScopeVocabulary(
    IReadOnlyList<string> EdgeKinds,
    IReadOnlyList<string> SymbolKinds,
    IReadOnlyList<string> AnnotationFlavors);
