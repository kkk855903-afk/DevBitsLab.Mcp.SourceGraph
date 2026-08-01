using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;

namespace DevBitsLab.Mcp.SourceGraph.Storage;

public sealed partial class SqliteGraphStore
{
    private static readonly TimeSpan SourceRegexTimeout = TimeSpan.FromSeconds(2);

    public async Task<SourceTextSearchPage> SearchSourceTextAsync(
        string query,
        SourceTextSearchMode mode,
        bool caseSensitive,
        string? fileGlob,
        int contextLines,
        int maxResults,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (query.IndexOfAny(['\r', '\n']) >= 0)
            throw new ArgumentException("source text queries must be single-line", nameof(query));
        if (contextLines is < 0 or > 20)
            throw new ArgumentOutOfRangeException(nameof(contextLines), "contextLines must be between 0 and 20");
        if (maxResults is < 1 or > 5000)
            throw new ArgumentOutOfRangeException(nameof(maxResults), "maxResults must be between 1 and 5000");

        Regex? contentRegex = null;
        if (mode == SourceTextSearchMode.Regex)
        {
            var options = RegexOptions.CultureInvariant;
            if (!caseSensitive) options |= RegexOptions.IgnoreCase;
            contentRegex = new Regex(query, options, SourceRegexTimeout);
        }
        var globRegex = string.IsNullOrWhiteSpace(fileGlob)
            ? null
            : CompileGlob(fileGlob.Trim());

        var hits = new List<SourceTextSearchHit>(Math.Min(maxResults, 256));
        long totalMatches = 0;
        long totalMatchingLines = 0;
        var candidateDocuments = 0;
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        var (connection, command, reader) = await OpenSourceCandidateReaderAsync(
            mode == SourceTextSearchMode.Literal ? query : null,
            ct).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        await using (command.ConfigureAwait(false))
        await using (reader.ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                candidateDocuments++;
                var path = reader.GetString(1);
                var normalizedPath = path.Replace('\\', '/');
                if (globRegex is not null && !globRegex.IsMatch(normalizedPath)) continue;

                // Read and split one source document at a time. Previously Dapper buffered every
                // candidate's full content before scanning, making peak memory proportional to
                // the entire repository rather than its largest source file.
                var lines = SplitLines(reader.GetString(2));
                for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
                {
                    ct.ThrowIfCancellationRequested();
                    var line = lines[lineIndex];
                    int firstIndex;
                    int firstLength;
                    int matchCount;
                    if (contentRegex is null)
                    {
                        firstIndex = line.IndexOf(query, comparison);
                        if (firstIndex < 0) continue;
                        firstLength = query.Length;
                        matchCount = CountLiteral(line, query, comparison, firstIndex);
                    }
                    else
                    {
                        var matches = contentRegex.Matches(line);
                        if (matches.Count == 0) continue;
                        firstIndex = matches[0].Index;
                        firstLength = matches[0].Length;
                        matchCount = matches.Count;
                    }

                    totalMatches += matchCount;
                    totalMatchingLines++;
                    if (hits.Count >= maxResults) continue;

                    var beforeStart = Math.Max(0, lineIndex - contextLines);
                    var afterEnd = Math.Min(lines.Length - 1, lineIndex + contextLines);
                    hits.Add(new SourceTextSearchHit(
                        path,
                        lineIndex + 1,
                        firstIndex + 1,
                        firstIndex + firstLength + 1,
                        matchCount,
                        line,
                        lines[beforeStart..lineIndex],
                        lines[(lineIndex + 1)..(afterEnd + 1)]));
                }
            }
        }

        return new SourceTextSearchPage(
            hits,
            totalMatches,
            totalMatchingLines,
            candidateDocuments,
            totalMatchingLines > hits.Count);
    }

    public async Task<SourceDocumentCoverage> GetSourceDocumentCoverageAsync(
        CancellationToken ct = default)
    {
        await using var reader = await OpenReaderAsync(ct).ConfigureAwait(false);
        var rows = (await reader.QueryAsync<SourceCoverageRow>(new CommandDefinition(
            """
            SELECT
                f.path AS Path,
                CASE WHEN d.file_id IS NULL THEN 0 ELSE 1 END AS HasSourceDocument
            FROM files f
            LEFT JOIN source_documents d ON d.file_id = f.id
            WHERE f.is_generated = 0
            ORDER BY f.path;
            """,
            cancellationToken: ct)).ConfigureAwait(false)).AsList();
        var eligible = rows.Select(row => row.Path).ToList();
        var indexed = rows.Where(row => row.HasSourceDocument != 0)
            .Select(row => row.Path).ToList();
        var missing = rows.Where(row => row.HasSourceDocument == 0)
            .Select(row => row.Path).ToList();
        return new SourceDocumentCoverage(eligible, indexed, missing);
    }

    private async Task<(SqliteConnection Connection, SqliteCommand Command, SqliteDataReader Reader)>
        OpenSourceCandidateReaderAsync(
        string? literalQuery,
        CancellationToken ct)
    {
        if (literalQuery is not null && literalQuery.EnumerateRunes().Count() >= 3)
        {
            var connection = await OpenReaderAsync(ct).ConfigureAwait(false);
            var command = connection.CreateCommand();
            try
            {
                var ftsQuery = "content : \"" + literalQuery.Replace("\"", "\"\"") + "\"";
                command.CommandText = """
                    SELECT d.file_id AS FileId, d.path AS Path, d.content AS Content
                    FROM source_text_fts f
                    JOIN source_documents d ON d.file_id = f.rowid
                    WHERE source_text_fts MATCH @query
                    ORDER BY d.path;
                    """;
                command.Parameters.AddWithValue("@query", ftsQuery);
                var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                return (connection, command, reader);
            }
            catch (SqliteException)
            {
                await command.DisposeAsync().ConfigureAwait(false);
                await connection.DisposeAsync().ConfigureAwait(false);
                // Some punctuation-only phrases cannot be represented by the trigram MATCH
                // grammar. A full scan preserves correctness; it is still entirely in-process
                // and backed by SourceGraph's own persisted source document store.
            }
        }

        var fallbackConnection = await OpenReaderAsync(ct).ConfigureAwait(false);
        var fallbackCommand = fallbackConnection.CreateCommand();
        fallbackCommand.CommandText = """
            SELECT file_id AS FileId, path AS Path, content AS Content
            FROM source_documents
            ORDER BY path;
            """;
        try
        {
            var fallbackReader = await fallbackCommand.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return (fallbackConnection, fallbackCommand, fallbackReader);
        }
        catch
        {
            await fallbackCommand.DisposeAsync().ConfigureAwait(false);
            await fallbackConnection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static string[] SplitLines(string content) =>
        content.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

    private static int CountLiteral(
        string line,
        string query,
        StringComparison comparison,
        int firstIndex)
    {
        var count = 0;
        var offset = firstIndex;
        while (offset >= 0)
        {
            count++;
            offset = line.IndexOf(query, offset + Math.Max(1, query.Length), comparison);
        }
        return count;
    }

    private static Regex CompileGlob(string glob)
    {
        var normalized = glob.Replace('\\', '/');
        var pattern = new StringBuilder("^");
        if (!normalized.Contains('/')) pattern.Append("(?:.*/)?");
        for (var index = 0; index < normalized.Length; index++)
        {
            var ch = normalized[index];
            if (ch == '*')
            {
                var doubleStar = index + 1 < normalized.Length && normalized[index + 1] == '*';
                if (doubleStar)
                {
                    index++;
                    if (index + 1 < normalized.Length && normalized[index + 1] == '/')
                    {
                        index++;
                        pattern.Append("(?:.*/)?");
                    }
                    else
                    {
                        pattern.Append(".*");
                    }
                }
                else
                {
                    pattern.Append("[^/]*");
                }
            }
            else if (ch == '?')
            {
                pattern.Append("[^/]");
            }
            else
            {
                pattern.Append(Regex.Escape(ch.ToString()));
            }
        }
        pattern.Append('$');
        return new Regex(
            pattern.ToString(),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            SourceRegexTimeout);
    }

    private sealed record SourceCoverageRow(string Path, long HasSourceDocument);
}
