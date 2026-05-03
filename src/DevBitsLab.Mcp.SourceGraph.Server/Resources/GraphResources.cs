using System.ComponentModel;
using System.Text;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Storage;
using ModelContextProtocol.Server;

namespace DevBitsLab.Mcp.SourceGraph.Server.Resources;

[McpServerResourceType]
public static class GraphResources
{
    private static string AccessibilityLabel(int accessibility) => accessibility switch
    {
        6 => "public",
        5 => "protected internal",
        4 => "internal",
        3 => "protected",
        2 => "private protected",
        1 => "private",
        _ => "",
    };

    [McpServerResource(UriTemplate = "graph://symbol/{symbolId}", Name = "graph-symbol", MimeType = "text/markdown")]
    [Description("Markdown card for a graph symbol: signature, definition location, top callers and callees.")]
    public static async Task<string> GetSymbolAsync(
        IGraphStore store,
        [Description("Numeric symbol id (as returned by find_definition or search_symbols)")] string symbolId,
        CancellationToken ct = default)
    {
        if (!long.TryParse(symbolId, out var id)) return $"# Invalid symbol id: {symbolId}";
        var hit = await store.GetSymbolByIdAsync(id, ct).ConfigureAwait(false);
        if (hit is null) return $"# Symbol id {id} not found";

        var callers = await store.ListCallersAsync(id, 10, ct: ct).ConfigureAwait(false);
        var callees = await store.ListCalleesAsync(id, 10, ct: ct).ConfigureAwait(false);

        var sb = new StringBuilder();
        sb.AppendLine($"# {hit.Fqn}");
        sb.AppendLine();
        sb.AppendLine($"- **Kind:** {hit.Kind}");
        var accLabel = AccessibilityLabel(hit.Accessibility);
        if (!string.IsNullOrEmpty(accLabel)) sb.AppendLine($"- **Accessibility:** {accLabel}");
        if (!string.IsNullOrEmpty(hit.Modifiers)) sb.AppendLine($"- **Modifiers:** {hit.Modifiers.Replace(',', ' ')}");
        sb.AppendLine($"- **Defined in:** `{hit.FilePath}:{hit.StartLine}:{hit.StartCol}`");
        if (!string.IsNullOrEmpty(hit.Signature)) sb.AppendLine($"- **Signature:** `{hit.Signature}`");
        if (!string.IsNullOrEmpty(hit.XmlSummary))
        {
            sb.AppendLine();
            sb.AppendLine("## Summary");
            sb.AppendLine();
            foreach (var line in hit.XmlSummary.Split('\n'))
            {
                sb.Append("> ");
                sb.AppendLine(line.TrimEnd());
            }
        }
        sb.AppendLine();
        sb.AppendLine($"## Callers ({callers.Count})");
        if (callers.Count == 0) sb.AppendLine("_(none)_");
        else foreach (var c in callers) sb.AppendLine($"- `{c.Fqn}` — {c.FilePath}:{c.StartLine}");
        sb.AppendLine();
        sb.AppendLine($"## Callees ({callees.Count})");
        if (callees.Count == 0) sb.AppendLine("_(none)_");
        else foreach (var c in callees) sb.AppendLine($"- `{c.Fqn}` — {c.FilePath}:{c.StartLine}");
        return sb.ToString();
    }

    [McpServerResource(UriTemplate = "graph://file/{path}", Name = "graph-file", MimeType = "text/markdown")]
    [Description("Symbol outline of a single file: every class/method/property declared, with line numbers.")]
    public static async Task<string> GetFileOutlineAsync(
        IGraphStore store,
        [Description("URL-encoded absolute or partial file path")] string path,
        CancellationToken ct = default)
    {
        var decoded = Uri.UnescapeDataString(path);
        var hits = await store.ListSymbolsInFileAsync(decoded, ct).ConfigureAwait(false);
        var sb = new StringBuilder();
        if (hits.Count == 0) { sb.AppendLine($"# {decoded}"); sb.AppendLine(); sb.AppendLine("_No indexed symbols._"); return sb.ToString(); }
        sb.AppendLine($"# {hits[0].FilePath}");
        sb.AppendLine();
        foreach (var h in hits)
        {
            var prefix = h.Kind switch
            {
                SymbolKind.Namespace => "##",
                SymbolKind.Class or SymbolKind.Struct or SymbolKind.Interface or SymbolKind.Enum => "###",
                _ => "-",
            };
            if (prefix.StartsWith('#'))
            {
                sb.AppendLine();
                sb.AppendLine($"{prefix} {h.Name} _({h.Kind})_ — line {h.StartLine}");
            }
            else
            {
                var sig = string.IsNullOrEmpty(h.Signature) ? h.Name : h.Signature;
                sb.AppendLine($"- `{sig}` _(line {h.StartLine})_");
            }
        }
        return sb.ToString();
    }

    [McpServerResource(UriTemplate = "graph://namespace/{name}", Name = "graph-namespace", MimeType = "text/markdown")]
    [Description("Namespace summary: most-referenced symbols in the namespace, ranked by inbound call count.")]
    public static async Task<string> GetNamespaceSummaryAsync(
        IGraphStore store,
        [Description("Fully qualified namespace, e.g. 'Sample.Domain'")] string name,
        CancellationToken ct = default)
    {
        var rows = await store.ModuleSummaryAsync(name, 50, ct).ConfigureAwait(false);
        var sb = new StringBuilder();
        sb.AppendLine($"# Namespace: {name}");
        sb.AppendLine();
        if (rows.Count == 0) { sb.AppendLine("_No symbols found._"); return sb.ToString(); }
        sb.AppendLine($"{rows.Count} symbol(s) — listed by inbound call count, then FQN:");
        sb.AppendLine();
        foreach (var r in rows)
        {
            sb.AppendLine($"- in-deg **{r.InDegree}** — `{r.Symbol.Fqn}` _({r.Symbol.Kind})_ — {r.Symbol.FilePath}:{r.Symbol.StartLine}");
        }
        return sb.ToString();
    }
}
