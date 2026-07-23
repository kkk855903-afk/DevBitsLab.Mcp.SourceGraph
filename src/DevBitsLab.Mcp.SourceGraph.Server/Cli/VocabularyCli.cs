using System.Reflection;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Server.Plugins;
using DevBitsLab.Mcp.SourceGraph.Storage;
using Microsoft.Data.Sqlite;

namespace DevBitsLab.Mcp.SourceGraph.Server.Cli;

/// <summary>
/// CLI surface for the soft-registry diagnostics dump: <c>vocabulary list</c> reports, per scope,
/// the active kind vocabulary (every distinct edge_kind, symbol_kind, and annotation_flavor in
/// storage) with live emission counts and a Levenshtein-near-pair drift detector. Diagnostic-only
/// by default (exit 0); with <c>--strict</c> exits 2 when any drift candidate is reported.
///
/// <para>Source attribution is computed by reflection over <see cref="EdgeKinds"/> /
/// <see cref="SymbolKinds"/> static constants — kinds matching one of those are labelled
/// <c>[sdk]</c>; everything else is labelled <c>[unknown]</c> today. The plugin host doesn't yet
/// expose declared-kinds metadata, so we don't claim plugin attribution we can't substantiate.
/// When the host gains a declared-kinds API, the <see cref="ResolveSource"/> helper grows a
/// per-plugin lookup and returns <c>[plugin: id@version]</c> for matching kinds.</para>
///
/// <para>The DB probe deliberately mirrors <see cref="ServerVocabulary.ProbeScopeAsync"/>:
/// open the scope DB in read-only mode and run <c>SELECT kind_name, COUNT(*) ... GROUP BY ...</c>
/// to avoid <see cref="SqliteGraphStore"/>'s schema migration / journal-mode side effects. A scope
/// DB at an older schema version simply errors out on the missing columns; the outer loop logs to
/// stderr and continues.</para>
/// </summary>
internal static class VocabularyCli
{
    public static Task<int> RunSubcommandAsync(CommandLine cli, TextWriter? stdout = null, TextWriter? stderr = null)
    {
        var sub = cli.Positional.Count > 0 ? cli.Positional[0] : "list";
        return sub switch
        {
            "list" => RunListAsync(cli, stdout, stderr),
            _ => Task.FromResult(Unknown(sub, stderr)),
        };
    }

    /// <summary>
    /// Render the active kind vocabulary per scope, with drift detection. Returns 0 on success;
    /// 1 if the scope referenced via <c>--scope</c> is unknown; 2 with <c>--strict</c> when at
    /// least one drift candidate was reported.
    /// </summary>
    public static async Task<int> RunListAsync(CommandLine cli, TextWriter? stdout = null, TextWriter? stderr = null)
    {
        stdout ??= Console.Out;
        stderr ??= Console.Error;

        var repoRoot = cli.ResolvedRepoRoot();
        ScopeConfig config;
        try
        {
            config = ScopeConfigLoader.Load(repoRoot);
        }
        catch (ScopeConfigException ex)
        {
            await stderr.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 2;
        }

        // Plugin attribution wiring: keep the `PluginHost` instance threaded through to
        // `ResolveSource` so a future host API that surfaces declared-kinds metadata plugs in
        // without changing the call site. Skip the `LoadAllAsync` call until that API exists —
        // today `ResolveSource` always returns "unknown" for non-SDK kinds, so loading every
        // plugin (with the assembly-load + restore side effects that entails) buys nothing.
        var pluginHost = new PluginHost(repoRoot, config.Plugins);

        var scopes = config.Scopes;
        if (!string.IsNullOrEmpty(cli.ScopeId))
        {
            var match = scopes.FirstOrDefault(s => string.Equals(s.Id, cli.ScopeId, StringComparison.Ordinal));
            if (match is null)
            {
                await stderr.WriteLineAsync($"Unknown scope `{cli.ScopeId}`.").ConfigureAwait(false);
                return 1;
            }
            scopes = new[] { match };
        }

        var sdkEdges = EnumerateConstantStrings(typeof(EdgeKinds))
            .ToHashSet(StringComparer.Ordinal);
        var sdkSymbols = EnumerateConstantStrings(typeof(SymbolKinds))
            .ToHashSet(StringComparer.Ordinal);
        // No SDK constant for annotation flavors; the built-in C# emitter uses csharp-attribute.
        var sdkFlavors = new HashSet<string>(StringComparer.Ordinal) { "csharp-attribute" };

        var anyDrift = false;
        for (var i = 0; i < scopes.Count; i++)
        {
            var scope = scopes[i];
            if (i > 0) await stdout.WriteLineAsync().ConfigureAwait(false);

            await stdout.WriteLineAsync($"Scope: {scope.Id}").ConfigureAwait(false);

            var dbPath = ScopeLayout.ScopeDbPath(scope.Root, scope.Id);
            IReadOnlyList<KindCount> edges = Array.Empty<KindCount>();
            IReadOnlyList<KindCount> syms = Array.Empty<KindCount>();
            IReadOnlyList<KindCount> flavors = Array.Empty<KindCount>();
            if (File.Exists(dbPath))
            {
                try
                {
                    (edges, syms, flavors) = await ProbeScopeAsync(dbPath).ConfigureAwait(false);
                }
                catch (Exception ex) when (
                    ex is IOException or SqliteException or InvalidOperationException or UnauthorizedAccessException)
                {
                    await stderr.WriteLineAsync(
                        $"  (warning) failed to probe scope `{scope.Id}` at {dbPath}: {ex.Message}").ConfigureAwait(false);
                }
            }
            else
            {
                await stdout.WriteLineAsync($"  (no database at {dbPath} — never indexed)").ConfigureAwait(false);
            }

            await RenderListAsync(stdout, "edge_kinds", edges, sdkEdges, pluginHost).ConfigureAwait(false);
            await RenderListAsync(stdout, "symbol_kinds", syms, sdkSymbols, pluginHost).ConfigureAwait(false);
            await RenderListAsync(stdout, "annotation_flavors", flavors, sdkFlavors, pluginHost).ConfigureAwait(false);

            // Drift candidates: pairs within Levenshtein-distance ≤ 2 within the same kind list.
            await stdout.WriteLineAsync().ConfigureAwait(false);
            await stdout.WriteLineAsync("  Drift candidates:").ConfigureAwait(false);
            var driftPairs = new List<(string A, string B)>();
            driftPairs.AddRange(FindDriftPairs(edges));
            driftPairs.AddRange(FindDriftPairs(syms));
            driftPairs.AddRange(FindDriftPairs(flavors));
            if (driftPairs.Count == 0)
            {
                await stdout.WriteLineAsync("    (none)").ConfigureAwait(false);
            }
            else
            {
                anyDrift = true;
                foreach (var (a, b) in driftPairs)
                {
                    await stdout.WriteLineAsync($"    {a} ~ {b}").ConfigureAwait(false);
                }
            }
        }

        if (anyDrift && cli.Strict) return 2;
        return 0;
    }

    private static async Task RenderListAsync(
        TextWriter stdout,
        string heading,
        IReadOnlyList<KindCount> kinds,
        HashSet<string> sdkSet,
        PluginHost pluginHost)
    {
        await stdout.WriteLineAsync($"  {heading} ({kinds.Count}):").ConfigureAwait(false);
        if (kinds.Count == 0) return;

        // Two-column padded layout: kind on the left, attribution+count on the right. The kind
        // column auto-sizes to the longest identifier so multi-scope output stays readable.
        var widest = kinds.Max(k => k.Kind.Length);
        // Cap column width at 40 to keep extreme outliers from blowing out the line, but always
        // pad shorter identifiers at least as wide as the widest in this list.
        var col = Math.Min(Math.Max(widest, 12), 40);
        foreach (var kc in kinds)
        {
            var source = ResolveSource(kc.Kind, sdkSet, pluginHost);
            await stdout.WriteLineAsync($"    {kc.Kind.PadRight(col)}  [{source}, emitted: {kc.Count}]").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Open <paramref name="dbPath"/> in true read-only mode and run a single
    /// <c>GROUP BY kind_name</c> query against each of <c>edges</c>, <c>symbols</c>, and
    /// <c>annotations</c>. Mirrors <see cref="ServerVocabulary.ProbeScopeAsync"/>: bypasses
    /// <see cref="SqliteGraphStore"/> so the probe never triggers schema migration or journal-mode
    /// side effects. Throws on missing columns / older schemas; the caller catches and logs.
    /// </summary>
    private static async Task<(IReadOnlyList<KindCount> Edges, IReadOnlyList<KindCount> Symbols, IReadOnlyList<KindCount> Flavors)> ProbeScopeAsync(
        string dbPath, CancellationToken ct = default)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ConnectionString;

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var edges = await ReadGroupByAsync(connection,
            "SELECT kind_name, COUNT(*) FROM edges GROUP BY kind_name ORDER BY kind_name", ct).ConfigureAwait(false);
        var syms = await ReadGroupByAsync(connection,
            "SELECT kind_name, COUNT(*) FROM symbols GROUP BY kind_name ORDER BY kind_name", ct).ConfigureAwait(false);
        var flavors = await ReadGroupByAsync(connection,
            "SELECT flavor, COUNT(*) FROM annotations GROUP BY flavor ORDER BY flavor", ct).ConfigureAwait(false);
        return (edges, syms, flavors);
    }

    private static async Task<IReadOnlyList<KindCount>> ReadGroupByAsync(
        SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<KindCount>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (reader.IsDBNull(0)) continue;
            var kind = reader.GetString(0);
            if (kind.Length == 0) continue;
            var count = reader.GetInt64(1);
            rows.Add(new KindCount(kind.ToLowerInvariant(), count));
        }
        return rows;
    }

    /// <summary>
    /// Resolve the attribution label for <paramref name="kind"/>. Three buckets:
    /// <c>"sdk"</c> when the kind is one of the well-known SDK constants;
    /// <c>"plugin: id@version"</c> when a loaded plugin claims the kind (NB: the plugin host does
    /// not yet expose declared-kinds metadata, so this branch is reserved for a future API);
    /// <c>"unknown"</c> otherwise.
    /// </summary>
    private static string ResolveSource(string kind, HashSet<string> sdkSet, PluginHost pluginHost)
    {
        if (sdkSet.Contains(kind)) return "sdk";
        // Plugin declared-kinds API is not yet wired through PluginHost / PluginRecord. When it
        // lands, this branch returns `plugin: <id>@<version>` for the first plugin claiming the
        // kind. Until then, every non-SDK kind ends up in the unknown bucket — which is the
        // useful diagnostic anyway: the operator wants to spot kinds the SDK didn't sanction.
        _ = pluginHost;
        return "unknown";
    }

    /// <summary>
    /// Pairwise Levenshtein-distance scan within <paramref name="kinds"/>. Returns every
    /// <c>(a, b)</c> with distance ≤ 2; skips self-comparison and the symmetric <c>(b, a)</c>.
    /// Identical strings (distance 0) are skipped — that case can't happen here because the SQL
    /// already returns DISTINCT kind values, but the explicit check makes the helper safe to reuse.
    /// </summary>
    private static IEnumerable<(string A, string B)> FindDriftPairs(IReadOnlyList<KindCount> kinds)
    {
        for (var i = 0; i < kinds.Count; i++)
        {
            for (var j = i + 1; j < kinds.Count; j++)
            {
                var d = Levenshtein(kinds[i].Kind, kinds[j].Kind);
                if (d > 0 && d <= 2)
                {
                    yield return (kinds[i].Kind, kinds[j].Kind);
                }
            }
        }
    }

    /// <summary>
    /// Two-row dynamic-programming Levenshtein distance. O(|a|·|b|) time, O(min(|a|,|b|)) memory.
    /// Inline rather than via a NuGet dependency: distance computation on identifiers ≤ 64 chars
    /// is too tiny to justify a package surface.
    /// </summary>
    internal static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        // Ensure b is the shorter so we keep the smaller of the two row buffers.
        if (a.Length < b.Length)
        {
            (a, b) = (b, a);
        }
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                var del = prev[j] + 1;
                var ins = curr[j - 1] + 1;
                var sub = prev[j - 1] + cost;
                curr[j] = Math.Min(Math.Min(del, ins), sub);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }

    private static IEnumerable<string> EnumerateConstantStrings(Type constantsType) =>
        constantsType
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Select(f => f.GetRawConstantValue() as string)
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!.ToLowerInvariant());

    private static int Unknown(string sub, TextWriter? stderr)
    {
        var w = stderr ?? Console.Error;
        w.WriteLine($"Unknown vocabulary subcommand: {sub}");
        w.WriteLine("usage: sourcegraph-mcp vocabulary list [--scope <id>] [--strict]");
        return 2;
    }

    private readonly record struct KindCount(string Kind, long Count);
}
