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

        var documents = await LoadSourceCandidatesAsync(
            mode == SourceTextSearchMode.Literal ? query : null,
            ct).ConfigureAwait(false);
        var hits = new List<SourceTextSearchHit>(Math.Min(maxResults, 256));
        long totalMatches = 0;
        long totalMatchingLines = 0;
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        foreach (var document in documents)
        {
            ct.ThrowIfCancellationRequested();
            var normalizedPath = document.Path.Replace('\\', '/');
            if (globRegex is not null && !globRegex.IsMatch(normalizedPath)) continue;

            var lines = SplitLines(document.Content);
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
                    document.Path,
                    lineIndex + 1,
                    firstIndex + 1,
                    firstIndex + firstLength + 1,
                    matchCount,
                    line,
                    lines[beforeStart..lineIndex],
                    lines[(lineIndex + 1)..(afterEnd + 1)]));
            }
        }

        return new SourceTextSearchPage(
            hits,
            totalMatches,
            totalMatchingLines,
            documents.Count,
            totalMatchingLines > hits.Count);
    }

    private async Task<IReadOnlyList<SourceDocumentRow>> LoadSourceCandidatesAsync(
        string? literalQuery,
        CancellationToken ct)
    {
        if (literalQuery is not null && literalQuery.EnumerateRunes().Count() >= 3)
        {
            try
            {
                await using var reader = await OpenReaderAsync(ct).ConfigureAwait(false);
                var ftsQuery = "content : \"" + literalQuery.Replace("\"", "\"\"") + "\"";
                var rows = await reader.QueryAsync<SourceDocumentRow>(new CommandDefinition(
                    """
                    SELECT d.file_id AS FileId, d.path AS Path, d.content AS Content
                    FROM source_text_fts f
                    JOIN source_documents d ON d.file_id = f.rowid
                    WHERE source_text_fts MATCH @query
                    ORDER BY d.path;
                    """,
                    new { query = ftsQuery },
                    cancellationToken: ct)).ConfigureAwait(false);
                return rows.AsList();
            }
            catch (SqliteException)
            {
                // Some punctuation-only phrases cannot be represented by the trigram MATCH
                // grammar. A full scan preserves correctness; it is still entirely in-process
                // and backed by SourceGraph's own persisted source document store.
            }
        }

        await using var fallbackReader = await OpenReaderAsync(ct).ConfigureAwait(false);
        var fallback = await fallbackReader.QueryAsync<SourceDocumentRow>(new CommandDefinition(
            """
            SELECT file_id AS FileId, path AS Path, content AS Content
            FROM source_documents
            ORDER BY path;
            """,
            cancellationToken: ct)).ConfigureAwait(false);
        return fallback.AsList();
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

    private sealed record SourceDocumentRow(long FileId, string Path, string Content);
}
