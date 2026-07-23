namespace DevBitsLab.Mcp.SourceGraph.Server.Tools;

/// <summary>
/// Shared helpers for tools that emit GFM tables. The one well-known sharp edge is that a
/// literal <c>|</c> in cell content terminates the cell, and embedded newlines split the
/// row — both can occur naturally in user-controlled values like exception messages,
/// status strings, or filesystem paths.
/// </summary>
internal static class MarkdownTable
{
    /// <summary>
    /// Sanitise a value for inclusion in a GFM table cell:
    /// <list type="bullet">
    ///   <item>Escapes <c>|</c> as <c>\|</c> so it doesn't terminate the cell.</item>
    ///   <item>Collapses any <c>\r\n</c> / <c>\n</c> / <c>\r</c> into a single space so
    ///     embedded line breaks don't split the row.</item>
    ///   <item><c>null</c> / empty input returns <see cref="string.Empty"/>.</item>
    /// </list>
    /// Used by every tool whose markdown rendering interpolates user-supplied text into a
    /// table row (currently <c>list_scopes</c> and the diagnostic / lookup tables in
    /// <c>graph_stats</c> / <c>module_summary</c>).
    /// </summary>
    public static string EscapeCell(string? value)
        => string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace("|", "\\|").Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');

    /// <summary>
    /// Sanitise a value for embedding inside an inline-code span (the <c>`value`</c> form).
    /// Backticks would terminate the span and bleed the surrounding text into raw markdown;
    /// embedded line breaks would split the bullet across rows. Replaces backticks with
    /// the visually-identical <c>ʼ</c> (modifier letter apostrophe, U+02BC) and collapses
    /// newlines into spaces. Used by <c>list_scopes</c>'s failure-detail sub-list to wrap
    /// project names and file paths sourced from arbitrary on-disk text.
    /// </summary>
    public static string EscapeInlineCode(string? value)
        => string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace('`', 'ʼ').Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');

    /// <summary>
    /// Sanitise a value for embedding inside a bullet-list line as plain prose. Strips
    /// embedded line breaks (collapsing into spaces) so a multi-line exception message
    /// doesn't split the bullet across multiple list items.
    /// </summary>
    public static string CollapseLines(string? value)
        => string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
}
