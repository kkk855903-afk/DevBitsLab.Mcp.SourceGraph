namespace DevBitsLab.Mcp.SourceGraph.Storage;

/// <summary>
/// Thrown when <c>query_graph</c> receives input containing more than one SQL statement.
/// The leading SELECT/WITH is identified by the single-statement enforcement step; everything
/// after the first <c>;</c> token (excluding trailing whitespace) is captured in
/// <see cref="LeftoverSql"/> so the structured-error response can echo it back to the agent.
///
/// Multi-statement input is rejected because <c>SELECT 1; ATTACH 'evil.db' AS evil;</c> would
/// otherwise smuggle in a second statement that bypasses the read-only invariants enforced
/// by the first connection-level guard. See
/// <c>openspec/changes/add-graph-query/design.md</c> Decision 4 (safety rails).
/// </summary>
public sealed class MultiStatementRejectedException : Exception
{
    public string LeftoverSql { get; }

    public MultiStatementRejectedException(string leftoverSql)
        : base($"query_graph accepts a single SELECT or WITH statement; leftover after the first statement: {Truncate(leftoverSql, 80)}")
    {
        LeftoverSql = leftoverSql;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
