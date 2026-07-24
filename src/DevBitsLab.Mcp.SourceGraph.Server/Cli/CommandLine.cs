namespace DevBitsLab.Mcp.SourceGraph.Server.Cli;

internal enum CliHelpLanguage
{
    English,
    Chinese,
}

internal sealed class CommandLine
{
    public string Subcommand { get; private init; } = "serve";
    public string? SolutionPath { get; private init; }
    public string? DatabasePath { get; private init; }
    public string? RepoRoot { get; private init; }
    public bool ShowHelp { get; private init; }
    public CliHelpLanguage HelpLanguage { get; private init; } = CliHelpLanguage.English;
    /// <summary>Override the default embedding model identity (Hugging Face-style id).</summary>
    public string? Model { get; private init; }
    /// <summary>Disable the embedding pipeline (no model download, no vec0 writes, semantic_search returns disabled-message).</summary>
    public bool NoEmbeddings { get; private init; }
    /// <summary>Disables automatic model downloads. Defaults to <see langword="true"/>; callers
    /// must opt in with <c>--allow-model-download</c> or
    /// <c>SOURCEGRAPH_ALLOW_MODEL_DOWNLOAD=1</c>. A populated local cache remains usable.</summary>
    public bool NoModelDownload { get; private init; } = true;
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
    /// <summary>The scope id passed via <c>--scope &lt;id&gt;</c>; consumed by commands that need
    /// one configured scope, including <c>index</c>, <c>demo</c>, and <c>vocabulary list</c>.</summary>
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
        var forceNoModelDownload = string.Equals(
            Environment.GetEnvironmentVariable("SOURCEGRAPH_NO_MODEL_DOWNLOAD"), "1", StringComparison.Ordinal);
        var allowModelDownload = string.Equals(
            Environment.GetEnvironmentVariable("SOURCEGRAPH_ALLOW_MODEL_DOWNLOAD"), "1", StringComparison.Ordinal);

        if (args.Length == 0)
        {
            return new CommandLine
            {
                NoModelDownload = forceNoModelDownload || !allowModelDownload,
            };
        }
        if (args[0] == "--lang")
        {
            var i = 0;
            var language = RequireHelpLanguage(args, ref i);
            if (i + 1 < args.Length && args[i + 1] == "--lang")
            {
                throw new ArgumentException("--lang may only be specified once.");
            }
            if (++i >= args.Length || args[i] is not ("-h" or "--help"))
            {
                throw new ArgumentException("--lang can only be used together with --help.");
            }

            return new CommandLine
            {
                ShowHelp = true,
                HelpLanguage = ParseHelpLanguageAfterHelp(
                    args,
                    helpIndex: i,
                    language: language,
                    alreadySpecified: true),
                NoModelDownload = forceNoModelDownload || !allowModelDownload,
            };
        }
        if (args[0] is "-h" or "--help")
        {
            return new CommandLine
            {
                ShowHelp = true,
                HelpLanguage = ParseHelpLanguageAfterHelp(
                    args,
                    helpIndex: 0,
                    language: CliHelpLanguage.English,
                    alreadySpecified: false),
                NoModelDownload = forceNoModelDownload || !allowModelDownload,
            };
        }

        var subcommand = args[0];
        string? solution = null;
        string? db = null;
        string? model = null;
        string? root = null;
        var noEmbeddings = false;
        var noModelDownload = forceNoModelDownload || !allowModelDownload;
        var sawAllowModelDownload = false;
        var sawNoModelDownload = false;
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
        var helpLanguage = CliHelpLanguage.English;
        var sawHelpLanguage = false;

        for (var i = 1; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "-h" or "--help":
                    return new CommandLine
                    {
                        Subcommand = subcommand,
                        ShowHelp = true,
                        HelpLanguage = ParseHelpLanguageAfterHelp(
                            args,
                            i,
                            helpLanguage,
                            sawHelpLanguage),
                        NoModelDownload = noModelDownload,
                    };
                case "--lang":
                    if (sawHelpLanguage)
                    {
                        throw new ArgumentException("--lang may only be specified once.");
                    }
                    helpLanguage = RequireHelpLanguage(args, ref i);
                    sawHelpLanguage = true;
                    break;
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
                    sawNoModelDownload = true;
                    break;
                case "--allow-model-download":
                    noModelDownload = false;
                    sawAllowModelDownload = true;
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
                case "--no-claude-code" or "--no-codex" or "--no-copilot" or "--no-cursor"
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
        if (sawHelpLanguage)
        {
            throw new ArgumentException("--lang can only be used together with --help.");
        }
        if (sawAllowModelDownload && sawNoModelDownload)
        {
            throw new ArgumentException(
                "--allow-model-download and --no-model-download cannot be used together.");
        }
        if (forceNoModelDownload)
        {
            // Preserve the legacy environment variable as an operator-controlled fail-closed
            // boundary even when a lower-precedence command line attempts to opt in.
            noModelDownload = true;
        }

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

    private static CliHelpLanguage RequireHelpLanguage(string[] args, ref int i)
    {
        if (++i >= args.Length || args[i].StartsWith("-", StringComparison.Ordinal))
        {
            throw new ArgumentException("--lang requires a value: en or zh.");
        }

        var value = args[i];
        return value.ToLowerInvariant() switch
        {
            "en" => CliHelpLanguage.English,
            "zh" => CliHelpLanguage.Chinese,
            _ => throw new ArgumentException(
                $"Unsupported help language '{value}'. Expected en or zh."),
        };
    }

    private static CliHelpLanguage ParseHelpLanguageAfterHelp(
        string[] args,
        int helpIndex,
        CliHelpLanguage language,
        bool alreadySpecified)
    {
        var languageFlagIndex = helpIndex + 1;
        if (languageFlagIndex >= args.Length || args[languageFlagIndex] != "--lang")
        {
            return language;
        }
        if (alreadySpecified)
        {
            throw new ArgumentException("--lang may only be specified once.");
        }

        language = RequireHelpLanguage(args, ref languageFlagIndex);
        if (languageFlagIndex + 1 < args.Length && args[languageFlagIndex + 1] == "--lang")
        {
            throw new ArgumentException("--lang may only be specified once.");
        }
        return language;
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

    public string SelectedHelpText => GetHelpText(HelpLanguage);

    public static string GetHelpText(CliHelpLanguage language) =>
        language == CliHelpLanguage.Chinese ? ChineseHelpText : HelpText;

    public static string HelpText => """
        sourcegraph-mcp — live code source graph MCP server for .NET

        Usage:
          sourcegraph-mcp --help [--lang <en|zh>]
              Print this help in English (`en`) or Simplified Chinese (`zh`).

          sourcegraph-mcp serve [--solution <path>] [--db <path>] [--root <repo>] [--model <id>] [--no-embeddings] [--allow-model-download|--no-model-download] [--no-history]
              Run the MCP stdio server. With --solution given, registers an implicit single-scope
              `default` mapped to that solution. Otherwise reads `.sourcegraph.json` from --root
              (or CWD) for multi-scope configuration.

          sourcegraph-mcp index <solution-path> [--scope <id>] [--db <path>] [--model <id>] [--no-embeddings] [--allow-model-download|--no-model-download] [--no-history]
              Build/refresh the graph database from the given .sln file, then exit. When multiple
              configured scopes contain the solution, --scope is required.

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
              optionally pre-warms the index. First-class clients: claude-code, codex, copilot,
              cursor, continue, claude-desktop. Use --print-only for a CI-friendly preview that
              writes nothing. Codex uses .codex/config.toml and is project-scope only.

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
              SHA-256, and the free disk on the cache volume. Useful as a first stop when the
              default offline mode reports that the cache is empty.

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
          --lang <en|zh>    Select the language used by --help. Must be used together with --help.
          --root <path>     Repository root used for `.sourcegraph.json` discovery and scope DBs.
                            Defaults to the directory holding `--solution`, then CWD.
          --model <id>      Override the embedding model identity (default:
                            jinaai/jina-embeddings-v2-base-code). Applies to serve/index.
          --no-embeddings   Skip the embedding pipeline entirely. semantic_search returns the
                            disabled-message; every other tool works as before.
          --allow-model-download
                            Explicitly allow serve/index to auto-fetch the embedding model from
                            Hugging Face when the local cache is empty. Automatic network access is
                            disabled by default. Equivalent to SOURCEGRAPH_ALLOW_MODEL_DOWNLOAD=1.
          --no-model-download
                            Explicitly retain the default offline mode. The pipeline uses an
                            already-populated cache or degrades to the same shape as
                            --no-embeddings. SOURCEGRAPH_NO_MODEL_DOWNLOAD=1 is retained for
                            backwards-compatible fail-closed deployments.
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
          --scope <id>      Restrict the operation to a single scope. Used by `index`, `demo`,
                            and `vocabulary list`.
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

    public static string ChineseHelpText => """
        sourcegraph-mcp — .NET 实时代码源图 MCP 服务器

        用法:
          sourcegraph-mcp --help [--lang <en|zh>]
              使用英文 (`en`) 或简体中文 (`zh`) 显示本帮助。

          sourcegraph-mcp serve [--solution <path>] [--db <path>] [--root <repo>] [--model <id>] [--no-embeddings] [--allow-model-download|--no-model-download] [--no-history]
              运行 MCP stdio 服务器。指定 --solution 时，将创建映射到该解决方案的隐式
              `default` 单作用域；否则从 --root（或当前工作目录）读取 `.sourcegraph.json`
              多作用域配置。

          sourcegraph-mcp index <solution-path> [--scope <id>] [--db <path>] [--model <id>] [--no-embeddings] [--allow-model-download|--no-model-download] [--no-history]
              构建或刷新指定 .sln/.slnx 的图数据库，然后退出。当多个已配置作用域包含
              该解决方案时，必须指定 --scope。

          sourcegraph-mcp stats [--db <path>]
              输出数据库中的文件、符号、引用和边数量。

          sourcegraph-mcp clear [--db <path>]
              删除数据库中的所有数据行，但保留架构。

          sourcegraph-mcp init [--yes] [--client <id>] [--no-<client>] [--user-<client>]
                                [--claude-desktop] [--solution <path>] [--install-mode <mode>]
                                [--print-only] [--force] [--prewarm | --no-prewarm]
                                [--no-embeddings] [--no-history] [--root <path>]
              运行交互式（默认）或参数驱动的初始化流程。检测环境、选择 MCP 客户端、
              写入客户端配置文件（默认写入项目级配置），并可预热索引。首选客户端包括
              claude-code、codex、copilot、cursor、continue 和 claude-desktop。使用
              --print-only 可仅预览配置而不写入文件，适用于 CI。Codex 使用
              .codex/config.toml，目前仅支持项目级配置。

          sourcegraph-mcp doctor [--root <path>] [--json]
              执行只读环境诊断，检查 SDK、git、解决方案、配置和各客户端状态。
              退出码 0 表示全部通过，2 表示至少一项警告，1 表示硬错误。
              --json 输出机器可读的 {checks, exit_code} 文档。

          sourcegraph-mcp demo [--scope <id>] [--root <path>] [--no-color]
              对当前作用域数据库执行四项示例操作：ping、graph_stats、search_symbols
              和 find_definition，并输出 MCP 客户端可看到的叶标记 Markdown，
              用于快速确认安装和索引是否可用。

          sourcegraph-mcp init-scopes [--root <path>]
              在 <root>（默认为当前工作目录）中发现 .slnx 或 .sln，并生成
              `.sourcegraph.json`，每个解决方案对应一个作用域。

          sourcegraph-mcp scopes list [--root <path>]
              列出仓库 `.sourcegraph.json` 中声明的作用域；未配置时列出合成的
              default 作用域。

          sourcegraph-mcp scopes info <name> [--root <path>] [--json]
              显示一个作用域的详细信息，包括标识、项目集合、可选 `language` 字段
              和可选 `enrichment` 块。--json 输出与 Markdown 各节对应的稳定结构。

          sourcegraph-mcp scopes add <name> --solution <path> [--root <path>] [--isolated]
              向 `.sourcegraph.json` 添加作用域。<name> 是 kebab-case 标识，
              --solution 指定 .slnx/.sln；首次使用时会创建配置文件。

          sourcegraph-mcp scopes remove <name> [--root <path>]
              从 `.sourcegraph.json` 中删除一个作用域。

          sourcegraph-mcp vocabulary list [--scope <id>] [--strict] [--root <path>]
              按作用域诊断当前类型词汇表：列出每个 edge_kind、symbol_kind 和
              annotation_flavor，标注来源（`[sdk]`、`[plugin: ...]` 或 `[unknown]`）
              及实际数量。每类后还会列出“漂移候选”，即同一作用域中编辑距离不超过
              2 的近似标识（例如 `bind-path` 与 `binds-path`）。默认退出码为 0；
              使用 --strict 后，发现漂移候选时退出码为 2。

          sourcegraph-mcp embeddings status [--model <id>]
              显示模型缓存目录、当前模型标识和维度、各文件是否存在、大小、SHA-256
              以及缓存卷剩余空间。默认离线模式报告缓存为空时，可先运行此命令。

          sourcegraph-mcp embeddings pull [--model <id>]
              同步下载当前模型（或 --model 指定模型）的清单。该操作幂等：
              缓存完整时不会重复下载。

          sourcegraph-mcp embeddings remove [--model <id>] [--all]
              清除当前模型缓存（默认）、一个指定模型缓存，或使用 --all 清除全部缓存。
              --model 不能与 --all 同时使用。

          sourcegraph-mcp embeddings verify [--model <id>]
              重新计算每个缓存文件的 SHA。默认模型带有固定 SHA，校验不匹配时退出码为 2。
              自定义 --model 路径使用不含固定 SHA 的尽力清单，只显示计算结果和
              “informational only”说明，并以 0 退出。

        通用参数:
          --lang <en|zh>    选择 --help 的显示语言；必须与 --help 一起使用。
          --root <path>     `.sourcegraph.json` 发现和作用域数据库使用的仓库根目录。
                            默认为 --solution 所在目录，其次为当前工作目录。
          --model <id>      覆盖嵌入模型标识（默认：
                            jinaai/jina-embeddings-v2-base-code），用于 serve/index。
          --no-embeddings   完全跳过嵌入流程，不下载模型、不写入 vec0。
                            semantic_search 返回禁用说明，其他工具不受影响。
          --allow-model-download
                            显式允许 serve/index 在本地缓存为空时从 Hugging Face
                            自动下载模型。默认禁止自动联网。等价于
                            SOURCEGRAPH_ALLOW_MODEL_DOWNLOAD=1。
          --no-model-download
                            显式保持默认离线模式。使用已有缓存；缓存为空时退化为
                            --no-embeddings。保留 SOURCEGRAPH_NO_MODEL_DOWNLOAD=1
                            作为向后兼容的故障关闭设置。
          --no-history      禁用 git-blame 历史流程，适用于 PATH 中没有 git
                            或不需要逐符号历史信息的 CI 环境。
          --no-instructions 不在 MCP `initialize` 响应中发布服务器端使用指引。
                            等价于 SOURCEGRAPH_NO_INSTRUCTIONS=1。
          --no-leaf         不在工具响应和 `ServerInstructions` 中添加绿色叶子标记。
                            等价于 SOURCEGRAPH_NO_LEAF=1。
          --no-tool-triggers
                            不在 tools/list 的工具说明末尾附加 `Use when: …`。
                            可减少首次载荷，但代理选择工具时获得的指引也会减少。
                            等价于 SOURCEGRAPH_NO_TOOL_TRIGGERS=1。
          --scope <id>      将操作限制到一个作用域，用于 index、demo 和
                            vocabulary list。
          --strict          将警告视为错误。目前用于 vocabulary list；发现漂移候选时
                            以退出码 2 结束。
          --query-timeout-seconds <int>
                            query_graph MCP 工具的语句超时秒数，默认为 5。
                            等价于 SOURCEGRAPH_QUERY_TIMEOUT_SECONDS=<int>。
          --query-row-limit <int>
                            query_graph 每次调用最多返回的行数，默认为 5000。
                            超出时返回最多 <int> 行并报告 truncated: true。
                            等价于 SOURCEGRAPH_QUERY_ROW_LIMIT=<int>。

        默认值:
          --db   ./.sourcegraph/scopes/default.db   （不存在时创建；旧 graph.db 会迁移）

        示例:
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
