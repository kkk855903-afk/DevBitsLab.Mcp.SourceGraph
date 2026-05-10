using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

/// <summary>
/// Typed structured output for the <c>query_graph</c> MCP tool. The wire shape ships in
/// <see cref="ModelContextProtocol.Protocol.CallToolResult.StructuredContent"/> so agents
/// can consume the result programmatically without re-parsing the markdown table.
///
/// <para><b>On <see cref="Rows"/>'s element type.</b> Each row is a heterogeneous mix of
/// strings, integers, doubles, NULLs, and (rarely) BLOBs. Modelling rows as
/// <c>IReadOnlyList&lt;object?&gt;</c> would force the source-generated
/// <see cref="ToolOutputJsonContext"/> to register <see cref="object"/> as a serializable type,
/// which the source-gen serializer handles via reflection fallback (and which does not preserve
/// the SQLite-typed values cleanly — every <c>long</c> would risk being boxed and serialized
/// as a JSON number that loses precision). Instead, each row is pre-serialized to a
/// <see cref="JsonElement"/> (a JSON array) by the tool body using
/// <see cref="JsonSerializer.SerializeToElement{T}(T, JsonSerializerOptions?)"/> with the
/// reflection-based serializer on the row-by-row cold path. The outer
/// <see cref="QueryGraphResult"/> then rides the source-gen context, and the pre-built
/// <see cref="JsonElement"/> rows pass through as opaque JSON. This keeps the hot path
/// (envelope serialization) source-gen and confines reflection to the per-row inner serialization
/// where the heterogeneous shape is unavoidable.</para>
/// </summary>
public sealed record QueryGraphResult(
    [property: JsonPropertyName("row_count")] int RowCount,
    bool Truncated,
    [property: JsonPropertyName("row_cap")] int RowCap,
    [property: JsonPropertyName("elapsed_ms")] long ElapsedMs,
    IReadOnlyList<QueryGraphColumn> Columns,
    IReadOnlyList<JsonElement> Rows);

/// <summary>
/// One column in the result set. <see cref="Type"/> is the SQLite affinity name (<c>TEXT</c>,
/// <c>INTEGER</c>, <c>REAL</c>, <c>BLOB</c>) — empty when the column has no recognisable
/// affinity (e.g. the result of an expression).
/// </summary>
public sealed record QueryGraphColumn(string Name, string Type);
