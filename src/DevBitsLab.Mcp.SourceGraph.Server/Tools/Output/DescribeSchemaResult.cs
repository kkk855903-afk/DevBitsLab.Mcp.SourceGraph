using System.Text.Json.Serialization;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

/// <summary>
/// Typed structured output for the <c>describe_schema</c> MCP tool. Returns the agent-facing
/// view layer (<see cref="Storage.Views.All"/>) plus the live vocabularies present in
/// <c>v_symbols</c> / <c>v_edges</c> / <c>v_annotations</c> for the resolved scope set, so an
/// agent can compose a <c>query_graph</c> SQL statement against this server in one round-trip.
///
/// The <see cref="ViewSchemaVersion"/> mirrors <see cref="Storage.Views.SchemaVersion"/> and
/// bumps on any view-set change (per the Views.SchemaVersion XML doc) so cache-aware clients
/// always re-introspect after a server upgrade.
/// </summary>
public sealed record DescribeSchemaResult(
    [property: JsonPropertyName("view_schema_version")] int ViewSchemaVersion,
    IReadOnlyList<DescribeSchemaView> Views,
    [property: JsonPropertyName("symbol_kinds")] IReadOnlyList<string> SymbolKinds,
    [property: JsonPropertyName("edge_kinds")] IReadOnlyList<string> EdgeKinds,
    [property: JsonPropertyName("annotation_flavors")] IReadOnlyList<string> AnnotationFlavors);

/// <summary>
/// One view in the response. <see cref="Name"/> is the SQL identifier the agent passes to
/// <c>FROM …</c>; <see cref="Description"/> documents the contract; <see cref="Columns"/>
/// is the ordered column list.
/// </summary>
public sealed record DescribeSchemaView(
    string Name,
    string Description,
    IReadOnlyList<DescribeSchemaColumn> Columns);

/// <summary>
/// One column descriptor. <see cref="Type"/> is the SQLite affinity name (<c>TEXT</c>,
/// <c>INTEGER</c>, <c>BLOB</c>); <see cref="Nullable"/> reflects whether the column can be
/// NULL when read via the view.
/// </summary>
public sealed record DescribeSchemaColumn(
    string Name,
    string Type,
    bool Nullable,
    string Description);
