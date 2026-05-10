namespace DevBitsLab.Mcp.SourceGraph.Server.Resources;

/// <summary>
/// Single source of truth for the <c>graph://</c> URI shape that
/// <see cref="GraphResources"/> serves and that built-in tools emit as
/// <c>resource_link</c> content items. Tools and resource handlers must agree on the URI
/// structure; centralising construction here means a future scheme change lands in one place
/// instead of being scattered through every tool body.
/// </summary>
public static class GraphResourceUris
{
    /// <summary>
    /// Build a URI pointing at a graph symbol resource. The integer id is the symbol's row id in
    /// the per-scope SQLite store, the same id <see cref="GraphResources.GetSymbolAsync"/>
    /// resolves against.
    /// </summary>
    public static string Symbol(long id) => $"graph://symbol/{id}";

    /// <summary>
    /// Build a URI pointing at a graph file resource. The path is URL-escaped so paths containing
    /// spaces or special characters are valid URIs. Resolved by
    /// <see cref="GraphResources.GetFileAsync"/>.
    /// </summary>
    public static string File(string path) => $"graph://file/{Uri.EscapeDataString(path)}";

    /// <summary>
    /// Build a URI pointing at a graph namespace resource. Names are passed through unchanged
    /// because namespace identifiers don't carry URI-illegal characters.
    /// </summary>
    public static string Namespace(string name) => $"graph://namespace/{name}";

    /// <summary>
    /// Static URI for the on-demand help body referenced from <c>ServerInstructions</c>. Served
    /// by <see cref="GraphResources.GetHelp"/>.
    /// </summary>
    public const string Help = "graph://help";
}
