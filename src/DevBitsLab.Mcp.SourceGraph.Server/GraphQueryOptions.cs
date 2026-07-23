namespace DevBitsLab.Mcp.SourceGraph.Server;

/// <summary>
/// Repository root passed into the host at startup, surfaced to MCP tools that need it without
/// going through <see cref="DevBitsLab.Mcp.SourceGraph.Server.Cli.CommandLine"/>. The
/// <c>query_graph</c> / <c>describe_schema</c> tools use it to locate the per-scope DB files
/// via <see cref="DevBitsLab.Mcp.SourceGraph.Storage.ScopeLayout"/>.
/// </summary>
public sealed record RepoRootInfo(string Path);

/// <summary>
/// Tunables for the <c>query_graph</c> MCP tool. Resolved once at server start from CLI flags
/// (<c>--query-timeout-seconds</c>, <c>--query-row-limit</c>) with env-var fallback
/// (<c>SOURCEGRAPH_QUERY_TIMEOUT_SECONDS</c>, <c>SOURCEGRAPH_QUERY_ROW_LIMIT</c>) and built-in
/// defaults — registered as a singleton in DI.
/// </summary>
/// <param name="TimeoutSeconds">Per-call statement timeout. <c>SqliteCommand.CommandTimeout</c>
/// reads this; queries that exceed it are interrupted via <c>sqlite3_interrupt</c>.</param>
/// <param name="RowLimit">Maximum row count returned per call. The tool reads up to
/// <c>RowLimit + 1</c> rows; if the extra row exists, the response carries
/// <c>truncated: true</c> and the agent gets a hint to add a tighter <c>WHERE</c> or
/// <c>LIMIT</c>.</param>
public sealed record GraphQueryOptions(int TimeoutSeconds, int RowLimit)
{
    public const int DefaultTimeoutSeconds = 5;
    public const int DefaultRowLimit = 5000;
    public const string TimeoutEnvVarName = "SOURCEGRAPH_QUERY_TIMEOUT_SECONDS";
    public const string RowLimitEnvVarName = "SOURCEGRAPH_QUERY_ROW_LIMIT";

    /// <summary>
    /// Resolve the options with priority: CLI flag → env var → built-in default. Both env vars are
    /// parsed as positive integers; non-positive or unparseable values are silently ignored so a
    /// stale env doesn't crash startup.
    /// </summary>
    public static GraphQueryOptions Resolve(int? timeoutFlag, int? rowLimitFlag)
    {
        var timeout = timeoutFlag
            ?? ParseEnvPositiveInt(TimeoutEnvVarName)
            ?? DefaultTimeoutSeconds;
        var rowLimit = rowLimitFlag
            ?? ParseEnvPositiveInt(RowLimitEnvVarName)
            ?? DefaultRowLimit;
        return new GraphQueryOptions(timeout, rowLimit);
    }

    private static int? ParseEnvPositiveInt(string name)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(raw)) return null;
        return int.TryParse(raw, out var n) && n > 0 ? n : null;
    }
}
