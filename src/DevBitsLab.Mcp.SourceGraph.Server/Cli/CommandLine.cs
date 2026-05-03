namespace DevBitsLab.Mcp.SourceGraph.Server.Cli;

internal sealed class CommandLine
{
    public string Subcommand { get; private init; } = "serve";
    public string? SolutionPath { get; private init; }
    public string? DatabasePath { get; private init; }
    public bool ShowHelp { get; private init; }
    /// <summary>True when <c>--no-history</c> was passed; disables the git-blame pipeline.</summary>
    public bool NoHistory { get; private init; }

    public static CommandLine Parse(string[] args)
    {
        if (args.Length == 0) return new CommandLine();
        if (args[0] is "-h" or "--help") return new CommandLine { ShowHelp = true };

        var subcommand = args[0];
        string? solution = null;
        string? db = null;
        var noHistory = false;

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
                case "--no-history":
                    noHistory = true;
                    break;
                default:
                    if (subcommand == "index" && solution is null && !a.StartsWith('-'))
                    {
                        solution = ExpandTokens(a);
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

        return new CommandLine
        {
            Subcommand = subcommand,
            SolutionPath = solution,
            DatabasePath = db,
            NoHistory = noHistory,
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
          sourcegraph-mcp serve [--solution <path>] [--db <path>] [--no-history]
              Run the MCP stdio server. If --solution is given, opens that solution on startup
              and watches it for changes; otherwise indexes lazily on first query.

          sourcegraph-mcp index <solution-path> [--db <path>] [--no-history]
              Build/refresh the graph database from the given .sln file, then exit.

          sourcegraph-mcp stats [--db <path>]
              Print counts of files / symbols / references / edges in the graph database.

          sourcegraph-mcp clear [--db <path>]
              Delete all rows from the graph database (schema preserved).

        Flags:
          --no-history   Disable the git-blame history pipeline. Use in environments without
                         git on PATH or in CI runs where per-symbol history isn't needed.

        Defaults:
          --db   ./.sourcegraph/graph.db   (created if missing)

        Examples:
          sourcegraph-mcp index ./MySln.sln
          sourcegraph-mcp serve --solution ./MySln.sln
          sourcegraph-mcp index ./MySln.sln --no-history
        """;

    /// <summary>
    /// Resolves the SQLite database path with this priority:
    ///   1. --db &lt;path&gt; if provided.
    ///   2. Beside --solution as &lt;solution-dir&gt;/.sourcegraph/graph.db. This is the common case
    ///      and gives each registered solution its own graph file regardless of CWD.
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
                return EnsureDir(Path.Combine(solutionDir, ".sourcegraph", "graph.db"));
            }
        }

        var userCache =
            Environment.GetEnvironmentVariable("XDG_CACHE_HOME")
            ?? Environment.GetEnvironmentVariable("LOCALAPPDATA")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify), ".cache");
        if (!string.IsNullOrEmpty(userCache))
        {
            return EnsureDir(Path.Combine(userCache, "sourcegraph-mcp", "graph.db"));
        }

        return EnsureDir(Path.Combine(Path.GetTempPath(), "sourcegraph-mcp", "graph.db"));
    }

    private static string EnsureDir(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        return filePath;
    }
}
