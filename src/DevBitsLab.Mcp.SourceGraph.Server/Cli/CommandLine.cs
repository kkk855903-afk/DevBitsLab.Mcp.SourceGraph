namespace DevBitsLab.Mcp.SourceGraph.Server.Cli;

internal sealed class CommandLine
{
    public string Subcommand { get; private init; } = "serve";
    public string? SolutionPath { get; private init; }
    public string? DatabasePath { get; private init; }
    public string? RepoRoot { get; private init; }
    public bool ShowHelp { get; private init; }
    /// <summary>Override the default embedding model identity (Hugging Face-style id).</summary>
    public string? Model { get; private init; }
    /// <summary>Disable the embedding pipeline (no model download, no vec0 writes, semantic_search returns disabled-message).</summary>
    public bool NoEmbeddings { get; private init; }
    /// <summary>True when <c>--no-model-download</c> was passed (or <c>SOURCEGRAPH_NO_MODEL_DOWNLOAD=1</c>);
    /// disables auto-fetching the embedding model. With this flag the pipeline runs only if the cache is
    /// already populated; otherwise it degrades to the same shape as <c>--no-embeddings</c>.</summary>
    public bool NoModelDownload { get; private init; }
    /// <summary>True when <c>--no-history</c> was passed; disables the git-blame pipeline.</summary>
    public bool NoHistory { get; private init; }
    /// <summary>True when <c>--no-instructions</c> was passed; suppresses the server-published
    /// usage guidance string in the MCP <c>initialize</c> response.</summary>
    public bool NoInstructions { get; private init; }
    /// <summary>True when <c>--no-leaf</c> was passed; suppresses the green-leaf brand mark on
    /// every built-in tool response and on the published <c>ServerInstructions</c> string.</summary>
    public bool NoLeaf { get; private init; }
    /// <summary>True when <c>--no-tool-triggers</c> was passed; suppresses the
    /// <c>Use when: …</c> append on every tool description (built-in and plugin).</summary>
    public bool NoToolTriggers { get; private init; }
    /// <summary>True when <c>--strict</c> was passed; consumed by <c>vocabulary list</c> to exit
    /// non-zero when drift candidates are reported.</summary>
    public bool Strict { get; private init; }
    /// <summary>True when <c>--all</c> was passed; consumed by <c>embeddings remove</c> to wipe
    /// every cached model directory rather than just the active one.</summary>
    public bool All { get; private init; }
    /// <summary>The scope id passed via <c>--scope &lt;id&gt;</c>; consumed by <c>vocabulary list</c>
    /// to filter the output to a single scope. Null means every scope.</summary>
    public string? ScopeId { get; private init; }
    /// <summary>Statement timeout (seconds) for the <c>query_graph</c> tool. Null when not set;
    /// resolution falls back to <c>SOURCEGRAPH_QUERY_TIMEOUT_SECONDS</c> then the built-in default.</summary>
    public int? QueryTimeoutSeconds { get; private init; }
    /// <summary>Maximum row count returned per <c>query_graph</c> call. Null when not set; resolution
    /// falls back to <c>SOURCEGRAPH_QUERY_ROW_LIMIT</c> then the built-in default.</summary>
    public int? QueryRowLimit { get; private init; }
    /// <summary>Positional rest args (used by `scopes add`, `scopes remove`, etc.).</summary>
    public IReadOnlyList<string> Positional { get; private init; } = Array.Empty<string>();
    /// <summary>True when <c>--yes</c>/<c>-y</c> was passed; consumed by <c>init</c> to skip every interactive prompt.</summary>
    public bool Yes { get; private init; }
    /// <summary>True when <c>--force</c> was passed; consumed by <c>init</c> to overwrite an existing differing <c>sourcegraph</c> server entry without prompting.</summary>
    public bool Force { get; private init; }
    /// <summary>True when <c>--print-only</c> was passed; consumed by <c>init</c> to emit per-client config snippets to stdout without writing files.</summary>
    public bool PrintOnly { get; private init; }
    /// <summary>Tristate: <c>true</c> = <c>--prewarm</c>, <c>false</c> = <c>--no-prewarm</c>, <c>null</c> = unspecified (use the default for the active mode: on under interactive, off under <c>--yes</c>).</summary>
    public bool? Prewarm { get; private init; }
    /// <summary>Selected install mode for <c>init</c>'s emitted <c>command</c> + <c>args</c>: <c>global</c> (default), <c>local-tool</c>, or <c>in-repo</c>.</summary>
    public string? InstallMode { get; private init; }
    /// <summary>Every <c>--client &lt;id&gt;</c> value (init-only). Empty means "use auto-detected defaults".</summary>
    public IReadOnlyList<string> Clients { get; private init; } = Array.Empty<string>();
    /// <summary>Every <c>--no-&lt;client&gt;</c> flag (init-only). Used to drop clients from the auto-detected set.</summary>
    public IReadOnlyCollection<string> NoClients { get; private init; } = Array.Empty<string>();
    /// <summary>Every <c>--user-&lt;client&gt;</c> flag (init-only). Switches that client's target from its project-scoped path to its user-scoped path.</summary>
    public IReadOnlyCollection<string> UserClients { get; private init; } = Array.Empty<string>();
    /// <summary>True when <c>--claude-desktop</c> was passed; required to opt Claude Desktop into init since it has no project-scoped path.</summary>
    public bool ClaudeDesktop { get; private init; }
    /// <summary>Every <c>--solution</c> value, in order. <see cref="SolutionPath"/> mirrors the last entry for back-compat with single-valued consumers.</summary>
    public IReadOnlyList<string> Solutions { get; private init; } = Array.Empty<string>();
    /// <summary>True when <c>--json</c> was passed; consumed by <c>doctor</c> to emit a machine-readable structured document instead of glyph output, and by <c>scopes info</c> to emit a stable JSON shape mirroring the markdown sections.</summary>
    public bool Json { get; private init; }
    /// <summary>True when <c>--no-color</c> was passed; consumed by <c>demo</c> to suppress the green-leaf glyph on per-line output. Independent of <see cref="NoLeaf"/>, which is the server-wide opt-out.</summary>
    public bool NoColor { get; private init; }

    public static CommandLine Parse(string[] args)
    {
        if (args.Length == 0) return new CommandLine();
        if (args[0] is "-h" or "--help") return new CommandLine { ShowHelp = true };

        var subcommand = args[0];
        string? solution = null;
        string? db = null;
        string? model = null;
        string? root = null;
        var noEmbeddings = false;
        var noModelDownload = string.Equals(
            Environment.GetEnvironmentVariable("SOURCEGRAPH_NO_MODEL_DOWNLOAD"), "1", StringComparison.Ordinal);
        var noHistory = false;
        var noInstructions = false;
        var noLeaf = false;
        var noToolTriggers = false;
        var strict = false;
        var all = false;
        string? scopeId = null;
        int? queryTimeoutSeconds = null;
        int? queryRowLimit = null;
        var positional = new List<string>();
        var yes = false;
        var force = false;
        var printOnly = false;
        bool? prewarm = null;
        string? installMode = null;
        var clients = new List<string>();
        var noClients = new HashSet<string>(StringComparer.Ordinal);
        var userClients = new HashSet<string>(StringComparer.Ordinal);
        var claudeDesktop = false;
        var solutions = new List<string>();
        var json = false;
        var noColor = false;

        for (var i = 1; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "-h" or "--help":
                    return new CommandLine { Subcommand = subcommand, ShowHelp = true };
                case "--solution" or "-s":
                    solution = ExpandTokens(RequireArg(args, ref i, a));
                    solutions.Add(solution);
                    break;
                case "--db":
                    db = ExpandTokens(RequireArg(args, ref i, a));
                    break;
                case "--model":
                    model = RequireArg(args, ref i, a);
                    break;
                case "--root":
                    root = ExpandTokens(RequireArg(args, ref i, a));
                    break;
                case "--no-embeddings":
                    noEmbeddings = true;
                    break;
                case "--no-model-download":
                    noModelDownload = true;
                    break;
                case "--no-history":
                    noHistory = true;
                    break;
                case "--no-instructions":
                    noInstructions = true;
                    break;
                case "--no-leaf":
                    noLeaf = true;
                    break;
                case "--no-tool-triggers":
                    noToolTriggers = true;
                    break;
                case "--strict":
                    strict = true;
                    break;
                case "--all":
                    all = true;
                    break;
                case "--scope":
                    scopeId = RequireArg(args, ref i, a);
                    break;
                case "--query-timeout-seconds":
                    queryTimeoutSeconds = RequirePositiveInt(args, ref i, a);
                    break;
                case "--query-row-limit":
                    queryRowLimit = RequirePositiveInt(args, ref i, a);
                    break;
                case "--yes" or "-y":
                    yes = true;
                    break;
                case "--force":
                    force = true;
                    break;
                case "--print-only":
                    printOnly = true;
                    break;
                case "--prewarm":
                    prewarm = true;
                    break;
                case "--no-prewarm":
                    prewarm = false;
                    break;
                case "--install-mode":
                    installMode = RequireArg(args, ref i, a);
                    break;
                case "--client":
                    clients.Add(RequireArg(args, ref i, a));
                    break;
                case "--claude-desktop":
                    claudeDesktop = true;
                    break;
                case "--no-claude-code" or "--no-copilot" or "--no-cursor"
                    or "--no-continue" or "--no-claude-desktop":
                    noClients.Add(a.Substring(5));
                    break;
                case "--user-claude-code" or "--user-copilot" or "--user-cursor"
                    or "--user-continue":
                    userClients.Add(a.Substring(7));
                    break;
                case "--json":
                    json = true;
                    break;
                case "--no-color":
                    noColor = true;
                    break;
                default:
                    if (subcommand == "index" && solution is null && !a.StartsWith('-'))
                    {
                        solution = ExpandTokens(a);
                    }
                    else if (!a.StartsWith('-'))
                    {
                        positional.Add(a);
                    }
                    else
                    {
                        throw new ArgumentException($"Unrecognised argument: {a}");
                    }
                    break;
            }
        }

        if (solution is not null) AssertExpanded(solution, "--solution");
        if (db is not null) AssertExpanded(db, "--db");
        if (root is not null) AssertExpanded(root, "--root");

        return new CommandLine
        {
            Subcommand = subcommand,
            SolutionPath = solution,
            DatabasePath = db,
            RepoRoot = root,
            Model = model,
            NoEmbeddings = noEmbeddings,
            NoModelDownload = noModelDownload,
            NoHistory = noHistory,
            NoInstructions = noInstructions,
            NoLeaf = noLeaf,
            NoToolTriggers = noToolTriggers,
            Strict = strict,
            All = all,
            ScopeId = scopeId,
            QueryTimeoutSeconds = queryTimeoutSeconds,
            QueryRowLimit = queryRowLimit,
            Positional = positional,
            Yes = yes,
            Force = force,
            PrintOnly = printOnly,
            Prewarm = prewarm,
            InstallMode = installMode,
            Clients = clients,
            NoClients = noClients,
            UserClients = userClients,
            ClaudeDesktop = claudeDesktop,
            Solutions = solutions,
            Json = json,
            NoColor = noColor,
        };
    }

    private static string RequireArg(string[] args, ref int i, string flag)
    {
        if (++i >= args.Length) throw new ArgumentException($"{flag} requires a value");
        return args[i];
    }

    private static int RequirePositiveInt(string[] args, ref int i, string flag)
    {
        var raw = RequireArg(args, ref i, flag);
        if (!int.TryParse(raw, out var n) || n <= 0)
        {
            throw new ArgumentException($"{flag} requires a positive integer; got '{raw}'");
        }
        return n;
    }

    /// <summary>
    /// Expand <c>${VAR}</c> placeholders in a CLI value. Handles the special <c>${workspaceFolder}</c>
    /// token that some MCP clients (Claude Code, Cursor) substitute themselves; if the client
    /// didn't, we fall back to <c>WORKSPACE_FOLDER</c> / <c>CLAUDE_PROJECT_DIR</c> /
    /// <c>MCP_WORKSPACE_FOLDER</c> env vars, in that order. Generic <c>${X}</c> expands to
    /// <c>$X</c> from the process env.
    /// </summary>
    internal static string ExpandTokens(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains("${", StringComparison.Ordinal)) return value;
        return System.Text.RegularExpressions.Regex.Replace(value, @"\$\{([^}]+)\}", match =>
        {
            var name = match.Groups[1].Value;
            if (string.Equals(name, "workspaceFolder", StringComparison.Ordinal))
            {
                return Environment.GetEnvironmentVariable("WORKSPACE_FOLDER")
                    ?? Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR")
                    ?? Environment.GetEnvironmentVariable("MCP_WORKSPACE_FOLDER")
                    ?? match.Value;
            }
            return Environment.GetEnvironmentVariable(name) ?? match.Value;
        });
    }

    private static void AssertExpanded(string value, string flag)
    {
        if (value.Contains("${", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{flag} value '{value}' contains an unresolved placeholder. Either set the env var, " +
                "or pass an absolute path. For .mcp.json with project-scoped servers, use ${workspaceFolder} " +
                "(supported by Claude Code/Cursor) or set MCP_WORKSPACE_FOLDER yourself.");
        }
    }

    public static string HelpText => """
        sourcegraph-mcp — live code source graph MCP server for .NET

        Usage:
          sourcegraph-mcp serve [--solution <path>] [--db <path>] [--root <repo>] [--model <id>] [--no-embeddings] [--no-model-download] [--no-history]
              Run the MCP stdio server. With --solution given, registers an implicit single-scope
              `default` mapped to that solution. Otherwise reads `.sourcegraph.json` from --root
              (or CWD) for multi-scope configuration.

          sourcegraph-mcp index <solution-path> [--db <path>] [--model <id>] [--no-embeddings] [--no-model-download] [--no-history]
              Build/refresh the graph database from the given .sln file, then exit.

          sourcegraph-mcp stats [--db <path>]
              Print counts of files / symbols / references / edges in the graph database.

          sourcegraph-mcp clear [--db <path>]
              Delete all rows from the graph database (schema preserved).

          sourcegraph-mcp init [--yes] [--client <id>] [--no-<client>] [--user-<client>]
                                [--claude-desktop] [--solution <path>] [--install-mode <mode>]
                                [--print-only] [--force] [--prewarm | --no-prewarm]
                                [--no-embeddings] [--no-history] [--root <path>]
              Interactive (default) or flag-driven onboarding flow. Detects environment, picks
              MCP clients, writes per-client config files (project-scoped by default), and
              optionally pre-warms the index. First-class clients: claude-code, copilot, cursor,
              continue, claude-desktop. Use --print-only for a CI-friendly preview that writes
              nothing.

          sourcegraph-mcp doctor [--root <path>] [--json]
              Read-only environment diagnostic. Reports SDK/git/solution/config/per-client status.
              Exit 0 = all-pass; 2 = at least one warning; 1 = hard failure. --json emits a
              machine-readable {checks, exit_code} document instead of glyph output.

          sourcegraph-mcp demo [--scope <id>] [--root <path>] [--no-color]
              Run four canned operations (ping, graph_stats, search_symbols, find_definition)
              against the active scope's DB and print leaf-stamped markdown — the same shape
              an MCP client would see. Provides the "ah, it works" confidence moment.

          sourcegraph-mcp init-scopes [--root <path>]
              Discover .slnx (or .sln) files at <root> (default: cwd) and write a .sourcegraph.json
              listing one scope per discovered solution.

          sourcegraph-mcp scopes list [--root <path>]
              List the scopes declared in the repo's .sourcegraph.json (or the synthesised default).

          sourcegraph-mcp scopes info <name> [--root <path>] [--json]
              Detailed view of one scope: identity, project set, optional `language` field,
              optional `enrichment` block. With --json, emits a stable shape mirroring the markdown
              sections.

          sourcegraph-mcp scopes add <name> --solution <path> [--root <path>] [--isolated]
              Add a scope to .sourcegraph.json. <name> is the kebab-case id; --solution gives the
              .slnx/.sln. The file is created on first use.

          sourcegraph-mcp scopes remove <name> [--root <path>]
              Remove a scope from .sourcegraph.json.

          sourcegraph-mcp vocabulary list [--scope <id>] [--strict] [--root <path>]
              Diagnostic dump of the active kind vocabulary per scope: every distinct edge_kind,
              symbol_kind, and annotation_flavor in storage, attributed to `[sdk]`, `[plugin: ...]`,
              or `[unknown]`, with live emission counts. Each kind list is followed by a "Drift
              candidates" section: pairs within Levenshtein distance ≤ 2 that may indicate two
              indexers emitting near-duplicate identifiers (e.g. `bind-path` vs `binds-path`).
              Exits 0 by default; with --strict, exits 2 when any drift candidate is reported.

          sourcegraph-mcp embeddings status [--model <id>]
              Print the cache directory, active model id and dimension, per-file presence/size/
              SHA-256, and the free disk on the cache volume. Useful as a first stop when
              `--no-model-download` warned the cache was empty.

          sourcegraph-mcp embeddings pull [--model <id>]
              Synchronously download the active (or --model) manifest into the cache. Idempotent:
              a populated cache is a no-op.

          sourcegraph-mcp embeddings remove [--model <id>] [--all]
              Clear the cache for the active model (default), one specific --model, or every
              cached model with --all. Combining --model with --all is rejected.

          sourcegraph-mcp embeddings verify [--model <id>]
              Recompute SHAs of every cached file. The default model ships with pinned SHAs —
              exits 2 on mismatch. Override `--model <id>` paths use a best-effort manifest with
              no pinned SHAs; in that case prints the computed SHA with an "informational only"
              note and exits 0.

        Common flags:
          --root <path>     Repository root used for `.sourcegraph.json` discovery and scope DBs.
                            Defaults to the directory holding `--solution`, then CWD.
          --model <id>      Override the embedding model identity (default:
                            jinaai/jina-embeddings-v2-base-code). Applies to serve/index.
          --no-embeddings   Skip the embedding pipeline entirely. semantic_search returns the
                            disabled-message; every other tool works as before.
          --no-model-download
                            Disable auto-fetching the embedding model from Hugging Face. With this
                            flag the pipeline runs only when the cache is already populated;
                            otherwise it degrades to the same shape as --no-embeddings. Use in
                            air-gapped environments where outbound network is denied. Equivalent
                            to setting SOURCEGRAPH_NO_MODEL_DOWNLOAD=1.
          --no-history      Disable the git-blame history pipeline. Use in environments without
                            git on PATH or in CI runs where per-symbol history isn't needed.
          --no-instructions Don't publish server-side usage guidance in the MCP `initialize`
                            response. Equivalent to setting SOURCEGRAPH_NO_INSTRUCTIONS=1.
          --no-leaf         Don't prefix tool responses (or the published `ServerInstructions`
                            string) with the green-leaf brand mark. Equivalent to setting
                            SOURCEGRAPH_NO_LEAF=1.
          --no-tool-triggers
                            Don't append the `Use when: …` line to tool descriptions in
                            tools/list. Saves upfront tokens at the cost of less guidance
                            for agents picking between tools. Equivalent to setting
                            SOURCEGRAPH_NO_TOOL_TRIGGERS=1.
          --scope <id>      Restrict the operation to a single scope. Currently consumed by
                            `vocabulary list`; ignored elsewhere.
          --strict          Treat warnings as errors. Currently consumed by `vocabulary list`,
                            which exits 2 on drift candidates when set.
          --query-timeout-seconds <int>
                            Statement timeout (seconds) for the `query_graph` MCP tool. Default 5.
                            Equivalent to setting SOURCEGRAPH_QUERY_TIMEOUT_SECONDS=<int>.
          --query-row-limit <int>
                            Maximum rows returned by a `query_graph` call. Default 5000. The tool
                            returns up to <int> rows and reports `truncated: true` if more matched.
                            Equivalent to setting SOURCEGRAPH_QUERY_ROW_LIMIT=<int>.

        Defaults:
          --db   ./.sourcegraph/scopes/default.db   (created if missing; legacy graph.db is migrated)

        Examples:
          sourcegraph-mcp index ./MySln.sln
          sourcegraph-mcp serve --solution ./MySln.sln
          sourcegraph-mcp serve --root ./repo
          sourcegraph-mcp init-scopes
          sourcegraph-mcp scopes add backend --solution ./backend.slnx
        """;

    /// <summary>
    /// Resolve the repository root for scope-based layout. Priority: explicit <c>--root</c>, then
    /// the directory holding <c>--solution</c>, then the current working directory.
    /// </summary>
    public string ResolvedRepoRoot()
    {
        if (!string.IsNullOrEmpty(RepoRoot)) return Path.GetFullPath(RepoRoot);
        if (!string.IsNullOrEmpty(SolutionPath))
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(SolutionPath));
            if (!string.IsNullOrEmpty(dir)) return dir;
        }
        return Path.GetFullPath(Directory.GetCurrentDirectory());
    }

    /// <summary>
    /// Resolves the SQLite database path with this priority:
    ///   1. --db &lt;path&gt; if provided.
    ///   2. Beside --solution as &lt;solution-dir&gt;/.sourcegraph/scopes/default.db (post-scoping
    ///      layout). The legacy graph.db is migrated to this location automatically.
    ///   3. A per-user cache directory: ~/.cache/sourcegraph-mcp/graph.db (Linux/macOS) or
    ///      %LOCALAPPDATA%/sourcegraph-mcp/graph.db (Windows). Used when no solution is given.
    ///   4. Last resort: $TMPDIR/sourcegraph-mcp/graph.db.
    /// CWD is never used: when Claude Code or another MCP host spawns this process, CWD may be
    /// the filesystem root (read-only on macOS), which made the previous default `/.sourcegraph`
    /// fail with IOException.
    /// </summary>
    public string ResolvedDbPath()
    {
        if (!string.IsNullOrEmpty(DatabasePath)) return EnsureDir(Path.GetFullPath(DatabasePath));

        if (!string.IsNullOrEmpty(SolutionPath))
        {
            var solutionDir = Path.GetDirectoryName(Path.GetFullPath(SolutionPath));
            if (!string.IsNullOrEmpty(solutionDir))
            {
                // Post-scoping layout: scopes/default.db. The migrator handles the legacy graph.db.
                return EnsureDir(Path.Join(solutionDir, ".sourcegraph", "scopes", "default.db"));
            }
        }

        var userCache =
            Environment.GetEnvironmentVariable("XDG_CACHE_HOME")
            ?? Environment.GetEnvironmentVariable("LOCALAPPDATA")
            ?? Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify), ".cache");
        if (!string.IsNullOrEmpty(userCache))
        {
            return EnsureDir(Path.Join(userCache, "sourcegraph-mcp", "graph.db"));
        }

        return EnsureDir(Path.Join(Path.GetTempPath(), "sourcegraph-mcp", "graph.db"));
    }

    private static string EnsureDir(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        return filePath;
    }
}
