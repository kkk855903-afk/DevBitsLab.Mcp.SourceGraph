using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using DevBitsLab.Mcp.SourceGraph.Storage;

namespace DevBitsLab.Mcp.SourceGraph.Server.Cli;

/// <summary>
/// Implementation of the <c>sourcegraph-mcp demo</c> subcommand: runs four canned tool-shaped
/// operations against the active scope's DB and prints leaf-stamped markdown — the same shape an
/// MCP client would see. Provides the "ah, it works" confidence moment without an agent loop.
/// </summary>
internal static class DemoCli
{
    public static async Task<int> RunAsync(CommandLine cli)
    {
        var root = cli.ResolvedRepoRoot();
        var scopeId = cli.ScopeId ?? ResolveDefaultScopeId(root);
        var noColor = cli.NoColor
            || Environment.GetEnvironmentVariables().Contains("NO_COLOR")
            || LeafFormatter.Suppressed;

        var dbPath = ScopeLayout.ScopeDbPath(root, scopeId);
        if (!File.Exists(dbPath))
        {
            await Console.Error.WriteLineAsync(
                $"error: no graph database for scope '{scopeId}' at {dbPath}\n" +
                $"hint: run `sourcegraph-mcp index <solution>` first, or `sourcegraph-mcp init --prewarm`")
                .ConfigureAwait(false);
            return 2;
        }

        await using var store = new SqliteGraphStore(dbPath);
        await store.EnsureSchemaAsync().ConfigureAwait(false);

        Console.WriteLine("🌿 SourceGraph demo");
        Console.WriteLine($"  scope: {scopeId}");
        Console.WriteLine($"  db:    {dbPath}");
        Console.WriteLine();

        // 1. ping
        PrintSectionHeader("ping");
        Console.WriteLine(LeafPrefix(noColor) + $"pong @ {DateTimeOffset.UtcNow:O}");
        Console.WriteLine();

        // 2. graph_stats
        PrintSectionHeader("graph_stats");
        var stats = await store.GetStatsAsync().ConfigureAwait(false);
        Console.WriteLine(LeafPrefix(noColor) +
            $"files={stats.FileCount} symbols={stats.SymbolCount} references={stats.ReferenceCount} edges={stats.EdgeCount}");
        Console.WriteLine();

        if (stats.SymbolCount == 0)
        {
            await Console.Error.WriteLineAsync(
                "demo: scope has 0 symbols indexed.\n" +
                "      run `sourcegraph-mcp index <solution>` first, or `sourcegraph-mcp init --prewarm`.")
                .ConfigureAwait(false);
            return 2;
        }

        // 3. search_symbols + 4. find_definition — pick a probe symbol from the store directly
        // (FTS5 trigram matching needs 3+ chars, which we may not have without knowing the
        // codebase). GetAllSymbolKeysAsync is cheap and gives us a known-existing target.
        var allKeys = await store.GetAllSymbolKeysAsync().ConfigureAwait(false);
        SymbolHit? probeSymbol = null;
        if (allKeys.Count > 0)
        {
            probeSymbol = await store.GetSymbolByIdAsync(allKeys[0].Id).ConfigureAwait(false);
        }

        PrintSectionHeader("search_symbols");
        if (probeSymbol is null)
        {
            Console.WriteLine(LeafPrefix(noColor) + "(skipped: no symbols available for probe)");
        }
        else
        {
            var fragment = PickQueryFragment(probeSymbol.Name);
            var hits = await store.SearchSymbolsAsync(fragment, kindFilter: null, limit: 5).ConfigureAwait(false);
            if (hits.Count == 0)
            {
                Console.WriteLine(LeafPrefix(noColor) + $"No symbols match '{fragment}'.");
            }
            else
            {
                Console.WriteLine(LeafPrefix(noColor) + $"{hits.Count} hits for '{fragment}':");
                foreach (var h in hits)
                {
                    Console.WriteLine($"  - {h.Fqn} ({h.Kind})");
                }
            }
        }
        Console.WriteLine();

        PrintSectionHeader("find_definition");
        if (probeSymbol is null)
        {
            Console.WriteLine(LeafPrefix(noColor) + "(skipped: no symbols available)");
        }
        else
        {
            var target = probeSymbol.Fqn;
            var defs = await store.FindSymbolsAsync(target, filePathHint: null, limit: 5).ConfigureAwait(false);
            if (defs.Count == 0)
            {
                Console.WriteLine(LeafPrefix(noColor) + $"No matches for '{target}'.");
            }
            else
            {
                Console.WriteLine(LeafPrefix(noColor) + $"{defs.Count} hits for '{target}':");
                foreach (var d in defs)
                {
                    Console.WriteLine($"  - **{d.Fqn}** ({d.Kind})");
                    Console.WriteLine($"    {d.FilePath}:{d.StartLine}:{d.StartCol}");
                }
            }
        }
        Console.WriteLine();

        Console.WriteLine($"demo against scope={scopeId}: ok");
        return 0;
    }

    private static void PrintSectionHeader(string name)
    {
        Console.WriteLine($"── {name} ──");
    }

    private static string LeafPrefix(bool suppressed) => suppressed ? "" : "🌿 ";

    /// <summary>Reads the scope id from .sourcegraph.json's default_scope; falls back to "default".</summary>
    private static string ResolveDefaultScopeId(string root)
    {
        try
        {
            var cfg = ScopeConfigLoader.Load(root);
            if (!string.IsNullOrEmpty(cfg.DefaultScope)) return cfg.DefaultScope!;
            if (cfg.Scopes.Count > 0) return cfg.Scopes[0].Id;
        }
        catch (ScopeConfigException) { /* malformed config — fall through to synthesised default */ }
        catch (IOException) { /* unreadable config — fall through */ }
        return "default";
    }

    /// <summary>
    /// Pick a 3-character substring from the symbol name to use as a search fragment. We use a
    /// fragment rather than the full name so search_symbols actually exercises FTS5 fragment
    /// matching rather than degenerating into an exact-equality lookup.
    /// </summary>
    private static string PickQueryFragment(string name)
    {
        if (string.IsNullOrEmpty(name)) return "a";
        var len = Math.Min(3, name.Length);
        return name.Substring(0, len);
    }
}
