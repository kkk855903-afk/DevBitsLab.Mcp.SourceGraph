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
    /// <summary>True when <c>--no-history</c> was passed; disables the git-blame pipeline.</summary>
    public bool NoHistory { get; private init; }
    /// <summary>True when <c>--no-instructions</c> was passed; suppresses the server-published
    /// usage guidance string in the MCP <c>initialize</c> response.</summary>
    public bool NoInstructions { get; private init; }
    /// <summary>True when <c>--no-leaf</c> was passed; suppresses the green-leaf brand mark on
    /// every built-in tool response and on the published <c>ServerInstructions</c> string.</summary>
    public bool NoLeaf { get; private init; }
    /// <summary>True when <c>--strict</c> was passed; consumed by <c>vocabulary list</c> to exit
    /// non-zero when drift candidates are reported.</summary>
    public bool Strict { get; private init; }
    /// <summary>The scope id passed via <c>--scope &lt;id&gt;</c>; consumed by <c>vocabulary list</c>
    /// to filter the output to a single scope. Null means every scope.</summary>
    public string? ScopeId { get; private init; }
    /// <summary>Positional rest args (used by `scopes add`, `scopes remove`, etc.).</summary>
    public IReadOnlyList<string> Positional { get; private init; } = Array.Empty<string>();

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
        var noHistory = false;
        var noInstructions = false;
        var noLeaf = false;
        var strict = false;
        string? scopeId = null;
        var positional = new List<string>();

        for (var i = 1; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "-h" or "--help":
                    return new CommandLine { Subcommand = subcommand, ShowHelp = true };
                case "--solution" or "-s":
                    solution = ExpandTokens(RequireArg(args, ref i, a));
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
                case "--no-history":
                    noHistory = true;
                    break;
                case "--no-instructions":
                    noInstructions = true;
                    break;
                case "--no-leaf":
                    noLeaf = true;
                    break;
                case "--strict":
                    strict = true;
                    break;
                case "--scope":
                    scopeId = RequireArg(args, ref i, a);
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
            NoHistory = noHistory,
            NoInstructions = noInstructions,
            NoLeaf = noLeaf,
            Strict = strict,
            ScopeId = scopeId,
            Positional = positional,
        };
    }

    private static string RequireArg(string[] args, ref int i, string flag)
    {
        if (++i >= args.Length) throw new ArgumentException($"{flag} requires a value");
        return args[i];
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
          sourcegraph-mcp serve [--solution <path>] [--db <path>] [--root <repo>] [--model <id>] [--no-embeddings] [--no-history]
              Run the MCP stdio server. With --solution given, registers an implicit single-scope
              `default` mapped to that solution. Otherwise reads `.sourcegraph.json` from --root
              (or CWD) for multi-scope configuration.

          sourcegraph-mcp index <solution-path> [--db <path>] [--model <id>] [--no-embeddings] [--no-history]
              Build/refresh the graph database from the given .sln file, then exit.

          sourcegraph-mcp stats [--db <path>]
              Print counts of files / symbols / references / edges in the graph database.

          sourcegraph-mcp clear [--db <path>]
              Delete all rows from the graph database (schema preserved).

          sourcegraph-mcp init-scopes [--root <path>]
              Discover .slnx (or .sln) files at <root> (default: cwd) and write a .sourcegraph.json
              listing one scope per discovered solution.

          sourcegraph-mcp scopes list [--root <path>]
              List the scopes declared in the repo's .sourcegraph.json (or the synthesised default).

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

        Common flags:
          --root <path>     Repository root used for `.sourcegraph.json` discovery and scope DBs.
                            Defaults to the directory holding `--solution`, then CWD.
          --model <id>      Override the embedding model identity (default:
                            jinaai/jina-embeddings-v2-base-code). Applies to serve/index.
          --no-embeddings   Skip the embedding pipeline entirely. semantic_search returns the
                            disabled-message; every other tool works as before.
          --no-history      Disable the git-blame history pipeline. Use in environments without
                            git on PATH or in CI runs where per-symbol history isn't needed.
          --no-instructions Don't publish server-side usage guidance in the MCP `initialize`
                            response. Equivalent to setting SOURCEGRAPH_NO_INSTRUCTIONS=1.
          --no-leaf         Don't prefix tool responses (or the published `ServerInstructions`
                            string) with the green-leaf brand mark. Equivalent to setting
                            SOURCEGRAPH_NO_LEAF=1.
          --scope <id>      Restrict the operation to a single scope. Currently consumed by
                            `vocabulary list`; ignored elsewhere.
          --strict          Treat warnings as errors. Currently consumed by `vocabulary list`,
                            which exits 2 on drift candidates when set.

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
