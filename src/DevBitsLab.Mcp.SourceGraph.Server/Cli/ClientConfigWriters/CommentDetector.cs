namespace DevBitsLab.Mcp.SourceGraph.Server.Cli.ClientConfigWriters;

/// <summary>
/// Scans JSON text for JS-style line or block comments outside string literals.
/// VS Code reads <c>.vscode/mcp.json</c> in JSONC mode; users sometimes hand-edit our other
/// targets with comments too. Round-tripping a commented file through <see cref="System.Text.Json"/>
/// would silently strip the comments — so when we detect any, the writer degrades to print-only
/// mode and warns the user to paste the snippet manually (design.md Decision: comment-aware
/// degraded mode).
/// </summary>
internal static class CommentDetector
{
    /// <summary>
    /// Returns true when <paramref name="text"/> contains <c>//</c> or <c>/*</c> at a position
    /// outside any double-quoted string literal. Backslash-escaped quotes inside strings are
    /// honoured.
    /// </summary>
    public static bool HasJsonComments(ReadOnlySpan<char> text)
    {
        var inString = false;
        var escaped = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (escaped) { escaped = false; continue; }
            if (inString && c == '\\') { escaped = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;
            if (c == '/' && i + 1 < text.Length)
            {
                var next = text[i + 1];
                if (next == '/' || next == '*') return true;
            }
        }
        return false;
    }
}
