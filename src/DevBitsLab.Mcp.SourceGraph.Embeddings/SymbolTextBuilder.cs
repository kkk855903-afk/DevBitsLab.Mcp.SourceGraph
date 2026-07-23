using System.Security.Cryptography;
using System.Text;

namespace DevBitsLab.Mcp.SourceGraph.Embeddings;

/// <summary>
/// Builds the per-symbol "synthesised text" that feeds the embedding model and the SHA-256
/// content hash that gates re-embedding. Pure: no IO, no mutable state.
///
/// <para>Format (per design.md decision 4):</para>
/// <code>
///   {kind} {fqn}
///   {xml_summary}
///   {signature}
///   {first 40 lines of body}
/// </code>
/// </summary>
public static class SymbolTextBuilder {
    /// <summary>
    /// Build the synthesised text. <paramref name="bodyExcerpt"/> may be null (callers without
    /// a parsed body — types, namespaces — pass null and only the first three rows are emitted).
    /// </summary>
    public static string Build(string kind, string fqn, string? xmlSummary, string? signature, string? bodyExcerpt)
    {
        var sb = new StringBuilder();
        sb.Append(kind);
        sb.Append(' ');
        sb.AppendLine(fqn ?? "");

        if (!string.IsNullOrWhiteSpace(xmlSummary))
        {
            sb.AppendLine(xmlSummary);
        }
        if (!string.IsNullOrWhiteSpace(signature))
        {
            sb.AppendLine(signature);
        }
        if (!string.IsNullOrWhiteSpace(bodyExcerpt))
        {
            sb.Append(bodyExcerpt);
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>SHA-256 of the synthesised text in UTF-8. Used as <c>embedding_meta.content_hash</c>.</summary>
    public static byte[] HashOf(string text) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(text ?? string.Empty));

    /// <summary>
    /// Returns true if the given symbol or file should NOT be embedded:
    ///   - generated files (<c>*.g.cs</c>, <c>*.Designer.cs</c>)
    ///   - trivial accessors (a property whose synthesised text reduces to its signature)
    /// The body-triviality test runs at the call site since we don't materialise the body
    /// here; we only screen by file path and by an empty body+summary combination.
    /// </summary>
    public static bool ShouldSkip(string filePath, string? xmlSummary, string? signature, string? bodyExcerpt)
    {
        if (IsGeneratedFile(filePath)) return true;

        // Trivial: no doc, no body. The signature alone isn't worth a 768-d vector.
        if (string.IsNullOrWhiteSpace(xmlSummary) && string.IsNullOrWhiteSpace(bodyExcerpt))
        {
            return true;
        }
        return false;
    }

    /// <summary>Generated-file heuristic: filenames ending in <c>.g.cs</c> or <c>.Designer.cs</c>.</summary>
    public static bool IsGeneratedFile(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return false;
        var name = Path.GetFileName(filePath);
        if (name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
