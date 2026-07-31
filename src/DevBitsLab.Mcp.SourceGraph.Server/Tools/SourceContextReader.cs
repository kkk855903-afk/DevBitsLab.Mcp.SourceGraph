using DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools;

internal sealed record EvidenceDetailOptions(
    string Detail,
    int ContextLines,
    bool IncludeSnippet)
{
    internal const int MaximumContextLines = 20;

    internal static bool TryCreate(
        string? detail,
        int contextLines,
        bool includeSnippet,
        out EvidenceDetailOptions options,
        out string? error)
    {
        var normalized = string.IsNullOrWhiteSpace(detail)
            ? "locations"
            : detail.Trim().ToLowerInvariant();
        if (normalized is not ("summary" or "locations" or "evidence" or "audit"))
        {
            options = null!;
            error = "`detail` must be summary, locations, evidence, or audit.";
            return false;
        }
        if (contextLines is < 0 or > MaximumContextLines)
        {
            options = null!;
            error = $"`contextLines` must be between 0 and {MaximumContextLines}.";
            return false;
        }

        options = new EvidenceDetailOptions(normalized, contextLines, includeSnippet);
        error = null;
        return true;
    }
}

/// <summary>
/// Reads a small source window without allowing persisted paths to escape the configured scope.
/// Missing, stale, or unreadable source is represented by a null snippet rather than failing the
/// graph query that supplied the durable location evidence.
/// </summary>
internal static class SourceContextReader
{
    internal static async Task<SourceSnippet?> ReadAsync(
        string scopeRoot,
        string storedPath,
        int startLine,
        int endLine,
        int contextLines,
        CancellationToken ct)
    {
        if (startLine < 1 || endLine < startLine) return null;

        string root;
        string candidate;
        try
        {
            root = Path.GetFullPath(scopeRoot);
            candidate = Path.GetFullPath(
                Path.IsPathRooted(storedPath)
                    ? storedPath
                    : Path.Combine(root, storedPath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        var relative = Path.GetRelativePath(root, candidate);
        if (Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            return null;
        }
        if (!File.Exists(candidate)) return null;

        var firstLine = Math.Max(1, startLine - contextLines);
        var lastLine = checked(endLine + contextLines);
        var lines = new List<string>(Math.Max(1, lastLine - firstLine + 1));
        try
        {
            using var stream = new FileStream(
                candidate,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                useAsync: true);
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            var lineNumber = 0;
            while (lineNumber < lastLine)
            {
                ct.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;
                lineNumber++;
                if (lineNumber >= firstLine) lines.Add(line);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (lines.Count == 0) return null;
        return new SourceSnippet(
            StartLine: firstLine,
            EndLine: firstLine + lines.Count - 1,
            Text: string.Join('\n', lines));
    }

    internal static async Task<IReadOnlyList<AuditableRelation>> AttachAsync(
        string scopeRoot,
        IReadOnlyList<AuditableRelation> relations,
        int contextLines,
        CancellationToken ct)
    {
        var enriched = new List<AuditableRelation>(relations.Count);
        foreach (var relation in relations)
        {
            var evidence = new List<TraceCallPathEvidence>(relation.Hop.Evidence.Count);
            foreach (var item in relation.Hop.Evidence)
            {
                var snippet = await ReadAsync(
                    scopeRoot,
                    item.FilePath,
                    item.StartLine,
                    item.EndLine,
                    contextLines,
                    ct).ConfigureAwait(false);
                evidence.Add(item with { Snippet = snippet });
            }
            enriched.Add(relation with
            {
                Hop = relation.Hop with { Evidence = evidence },
            });
        }
        return enriched;
    }
}
