using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Storage;

namespace DevBitsLab.Mcp.SourceGraph.Server.Cli;

/// <summary>
/// Implementation of the <c>init-scopes</c> and <c>scopes list/add/remove</c> CLI subcommands.
/// All operations read and write <c>.sourcegraph.json</c> at the resolved repo root; nothing
/// touches the per-scope databases (those are rebuilt on the next <c>serve</c> / <c>index</c>).
/// </summary>
internal static class ScopesCli
{
    public static async Task<int> RunInitAsync(CommandLine cli)
    {
        var root = cli.ResolvedRepoRoot();
        var existing = Path.Join(root, ScopeConfigLoader.FileName);
        if (File.Exists(existing))
        {
            await Console.Error.WriteLineAsync($"{ScopeConfigLoader.FileName} already exists at {existing}; remove it first to re-scaffold.").ConfigureAwait(false);
            return 1;
        }
        var solutions = ScopeConfigLoader.DiscoverSolutionSiblings(root);
        if (solutions.Count == 0)
        {
            await Console.Error.WriteLineAsync($"No .slnx or .sln files at {root}; nothing to scaffold.").ConfigureAwait(false);
            return 1;
        }

        var scopes = new List<Scope>();
        foreach (var path in solutions)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            // Sanitise to a kebab-case slug. Replace non-alphanumeric runs with '-'.
            var sanitised = SanitiseToSlug(name);
            scopes.Add(new Scope(
                Id: sanitised,
                Name: sanitised,
                Root: root,
                ProjectSet: new ScopeProjectSet.Solutions(new[] { Path.GetFileName(path) }, Array.Empty<string>()),
                Isolated: false,
                LastIndexedAt: DateTimeOffset.MinValue));
        }
        // Pick the largest .sln by size as the default scope; on ties, alphabetical first.
        var defaultScope = scopes.Count == 1 ? scopes[0].Id : null;
        var config = new ScopeConfig(scopes, defaultScope);
        ScopeConfigLoader.Save(root, config);
        Console.WriteLine($"Wrote {Path.Join(root, ScopeConfigLoader.FileName)} with {scopes.Count} scope(s):");
        foreach (var scope in scopes) Console.WriteLine($"  - {scope.Id}  ->  {((ScopeProjectSet.Solutions)scope.ProjectSet).Items[0]}");
        Console.WriteLine("A running sourcegraph-mcp server will pick up the change automatically.");
        return 0;
    }

    public static async Task<int> RunSubcommandAsync(CommandLine cli)
    {
        if (cli.Positional.Count == 0)
        {
            await Console.Error.WriteLineAsync("Usage: sourcegraph-mcp scopes <list|info|add|remove> [args]").ConfigureAwait(false);
            return 2;
        }

        var op = cli.Positional[0];
        var root = cli.ResolvedRepoRoot();
        ScopeConfig config;
        try
        {
            config = ScopeConfigLoader.Load(
                root,
                ScopeConfigLoader.DiscoverSolutionSiblings(root));
        }
        catch (ScopeConfigException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 2;
        }

        return op switch
        {
            "list" => RunList(config, root),
            "info" => await RunInfoAsync(cli, config, root).ConfigureAwait(false),
            "add" => await RunAddAsync(cli, config, root).ConfigureAwait(false),
            "remove" => await RunRemoveAsync(cli, config, root).ConfigureAwait(false),
            _ => UnknownOp(op),
        };
    }

    private static int RunList(ScopeConfig config, string root)
    {
        Console.WriteLine($"{config.Scopes.Count} scope(s) at {root}:");
        foreach (var scope in config.Scopes)
        {
            var kind = scope.ProjectSet switch
            {
                ScopeProjectSet.Solutions s => $"solutions[{s.Items.Count}]",
                ScopeProjectSet.Projects p => $"projects[{p.Items.Count}]",
                ScopeProjectSet.Paths g => $"paths[{g.Globs.Count}]",
                _ => "?",
            };
            var isolation = scope.Isolated ? " (isolated)" : "";
            Console.WriteLine($"  - {scope.Id}  ({kind}){isolation}");
        }
        if (!string.IsNullOrEmpty(config.DefaultScope))
        {
            Console.WriteLine($"  default_scope: {config.DefaultScope}");
        }
        return 0;
    }

    private static async Task<int> RunAddAsync(CommandLine cli, ScopeConfig config, string root)
    {
        if (cli.Positional.Count < 2)
        {
            await Console.Error.WriteLineAsync("Usage: sourcegraph-mcp scopes add <name> --solution <path> [--isolated]").ConfigureAwait(false);
            return 2;
        }
        var name = cli.Positional[1];
        if (!ScopeIdValidator.IsValid(name))
        {
            await Console.Error.WriteLineAsync($"Invalid scope id '{name}'. Must match ^[a-z0-9][a-z0-9-]{{0,63}}$").ConfigureAwait(false);
            return 2;
        }
        if (config.Scopes.Any(s => s.Id == name))
        {
            await Console.Error.WriteLineAsync($"Scope '{name}' already exists.").ConfigureAwait(false);
            return 1;
        }
        if (string.IsNullOrEmpty(cli.SolutionPath))
        {
            await Console.Error.WriteLineAsync("`scopes add` requires --solution <path>.").ConfigureAwait(false);
            return 2;
        }
        var solutionPath = cli.SolutionPath;
        if (Path.IsPathRooted(solutionPath))
        {
            // Store the path relative to root when possible so the JSON file is portable.
            var rooted = Path.GetFullPath(solutionPath);
            var rel = Path.GetRelativePath(root, rooted);
            if (!rel.StartsWith("..", StringComparison.Ordinal)) solutionPath = rel;
            else solutionPath = rooted;
        }
        var newScope = new Scope(
            Id: name,
            Name: name,
            Root: root,
            ProjectSet: new ScopeProjectSet.Solutions(new[] { solutionPath }, Array.Empty<string>()),
            Isolated: cli.Positional.Contains("--isolated"),
            LastIndexedAt: DateTimeOffset.MinValue);
        var newScopes = config.Scopes.ToList();
        newScopes.Add(newScope);
        var updated = new ScopeConfig(newScopes, config.DefaultScope);
        ScopeConfigLoader.Save(root, updated);
        Console.WriteLine($"Added scope '{name}' -> {solutionPath}");
        Console.WriteLine("A running sourcegraph-mcp server will pick up the change automatically.");
        return 0;
    }

    private static async Task<int> RunRemoveAsync(CommandLine cli, ScopeConfig config, string root)
    {
        if (cli.Positional.Count < 2)
        {
            await Console.Error.WriteLineAsync("Usage: sourcegraph-mcp scopes remove <name>").ConfigureAwait(false);
            return 2;
        }
        var name = cli.Positional[1];
        var existing = config.Scopes.FirstOrDefault(s => s.Id == name);
        if (existing is null)
        {
            await Console.Error.WriteLineAsync($"Scope '{name}' not found.").ConfigureAwait(false);
            return 1;
        }
        var newScopes = config.Scopes.Where(s => s.Id != name).ToList();
        var newDefault = config.DefaultScope == name ? null : config.DefaultScope;
        var updated = new ScopeConfig(newScopes, newDefault);
        ScopeConfigLoader.Save(root, updated);
        // The per-scope DB on disk is preserved. A live server may still hold an open SQLite
        // connection to it during the live-remove grace window; deleting from underneath would
        // corrupt the running query. The DB is a rebuildable cache, so leaving it costs only
        // disk space (recoverable later via a `scopes prune` command) and re-adding the same
        // scope id reuses the existing DB without a cold reindex.
        Console.WriteLine($"Removed scope '{name}'");
        Console.WriteLine("A running sourcegraph-mcp server will pick up the change automatically.");
        return 0;
    }

    private static async Task<int> RunInfoAsync(CommandLine cli, ScopeConfig config, string root)
    {
        if (cli.Positional.Count < 2)
        {
            await Console.Error.WriteLineAsync("Usage: sourcegraph-mcp scopes info <name> [--json]").ConfigureAwait(false);
            return 2;
        }
        var name = cli.Positional[1];
        var scope = config.Scopes.FirstOrDefault(s => s.Id == name);
        if (scope is null)
        {
            await Console.Error.WriteLineAsync($"Scope '{name}' not found.").ConfigureAwait(false);
            return 1;
        }

        ScopeRow? runtime = null;
        var metaDatabasePath = ScopeLayout.MetaDbPath(root);
        if (File.Exists(metaDatabasePath))
        {
            await using var registry =
                new SqliteScopeRegistry(metaDatabasePath);
            await registry.EnsureSchemaAsync().ConfigureAwait(false);
            runtime = await registry.GetAsync(name).ConfigureAwait(false);
        }

        if (cli.Json)
        {
            EmitInfoJson(scope, runtime);
        }
        else
        {
            EmitInfoMarkdown(scope, root, runtime);
        }
        return 0;
    }

    private static void EmitInfoMarkdown(
        Scope scope,
        string repoRoot,
        ScopeRow? runtime)
    {
        Console.WriteLine($"## Identity");
        Console.WriteLine($"- id:   {scope.Id}");
        Console.WriteLine($"- name: {scope.Name}");
        Console.WriteLine($"- root: {repoRoot}");

        Console.WriteLine();
        Console.WriteLine($"## Project set");
        switch (scope.ProjectSet)
        {
            case ScopeProjectSet.Solutions s:
                Console.WriteLine($"- kind: solutions ({s.Items.Count})");
                foreach (var item in s.Items) Console.WriteLine($"  - {item}");
                break;
            case ScopeProjectSet.Projects p:
                Console.WriteLine($"- kind: projects ({p.Items.Count})");
                foreach (var item in p.Items) Console.WriteLine($"  - {item}");
                break;
            case ScopeProjectSet.Paths g:
                Console.WriteLine($"- kind: paths ({g.Globs.Count})");
                foreach (var glob in g.Globs) Console.WriteLine($"  - {glob}");
                break;
        }
        if (scope.ProjectSet.Exclude.Count > 0)
        {
            Console.WriteLine($"- exclude ({scope.ProjectSet.Exclude.Count}):");
            foreach (var ex in scope.ProjectSet.Exclude) Console.WriteLine($"  - {ex}");
        }
        if (scope.Isolated)
        {
            Console.WriteLine($"- isolated: true (excluded from `scope=\"*\"` fan-out)");
        }

        Console.WriteLine();
        Console.WriteLine("## Runtime index");
        Console.WriteLine($"- status: {runtime?.Status ?? "(not indexed)"}");
        if (!string.IsNullOrWhiteSpace(runtime?.StatusMessage))
        {
            Console.WriteLine($"- status_message: {runtime.StatusMessage}");
        }
        Console.WriteLine(
            $"- loaded_projects: {runtime?.ProjectCount?.ToString() ?? "(unknown)"}");
        Console.WriteLine(
            $"- last_indexed_at: {(runtime is null || runtime.LastIndexedAt == DateTimeOffset.MinValue ? "(never)" : runtime.LastIndexedAt.ToString("o"))}");

        Console.WriteLine();
        Console.WriteLine($"## Language");
        Console.WriteLine($"- {scope.Language ?? "(unset)"}");

        Console.WriteLine();
        Console.WriteLine($"## Enrichment");
        if (scope.Enrichment is null)
        {
            Console.WriteLine("- (unset)");
        }
        else if (scope.Enrichment.Lsp is { } lsp)
        {
            Console.WriteLine($"- lsp.command: {lsp.Command}");
            if (lsp.Args.Count > 0)
            {
                Console.WriteLine($"- lsp.args:    {string.Join(" ", lsp.Args)}");
            }
            Console.WriteLine($"- (no consumer at this version — first runtime use lands with the follow-up `add-typescript-lsp-enrichment` change)");
        }
    }

    private static void EmitInfoJson(Scope scope, ScopeRow? runtime)
    {
        var dto = new
        {
            id = scope.Id,
            name = scope.Name,
            root = scope.Root,
            project_set = scope.ProjectSet switch
            {
                ScopeProjectSet.Solutions s => new { kind = "solutions", items = s.Items, exclude = s.Exclude },
                ScopeProjectSet.Projects p => new { kind = "projects", items = p.Items, exclude = p.Exclude },
                ScopeProjectSet.Paths g => new { kind = "paths", items = g.Globs, exclude = g.Exclude },
                _ => new { kind = "?", items = (IReadOnlyList<string>)Array.Empty<string>(), exclude = (IReadOnlyList<string>)Array.Empty<string>() },
            },
            isolated = scope.Isolated,
            language = scope.Language,
            enrichment = scope.Enrichment is { Lsp: { } lsp }
                ? new
                {
                    lsp = new { command = lsp.Command, args = lsp.Args },
                    consumed = false,
                }
                : null,
            status = runtime?.Status,
            status_message = runtime?.StatusMessage,
            project_count = runtime?.ProjectCount,
            last_indexed_at = runtime?.LastIndexedAt is { } runtimeIndexedAt
                && runtimeIndexedAt != DateTimeOffset.MinValue
                ? (DateTimeOffset?)runtimeIndexedAt
                : scope.LastIndexedAt == DateTimeOffset.MinValue
                ? null
                : (DateTimeOffset?)scope.LastIndexedAt,
        };
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(dto, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private static int UnknownOp(string op)
    {
        Console.Error.WriteLine($"Unknown scopes subcommand: {op}");
        Console.Error.WriteLine("Expected one of: list, info, add, remove");
        return 2;
    }

    private static string SanitiseToSlug(string raw)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in raw.ToLowerInvariant())
        {
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
            {
                sb.Append(c);
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }
        var slug = sb.ToString().Trim('-');
        return string.IsNullOrEmpty(slug) ? "default" : slug;
    }
}
