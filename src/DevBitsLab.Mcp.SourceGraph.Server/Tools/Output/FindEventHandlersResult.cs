using System.Text.Json.Serialization;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

/// <summary>
/// Typed structured output for the <c>find_event_handlers</c> MCP tool. Mirrors
/// <see cref="FindDataBindingsResult"/> for the <c>handles-event</c> edge kind:
/// <see cref="Note"/> carries the soft-empty diagnostic when present (null otherwise);
/// <see cref="Handlers"/> is one row per matching edge.
/// </summary>
public sealed record FindEventHandlersResult(
    [property: JsonPropertyName("edge_kind")] string EdgeKind,
    string? Note,
    IReadOnlyList<FindEventHandlersRow> Handlers);

/// <summary>
/// One <c>handles-event</c> edge row: source UI element on the left, resolved handler method
/// on the right (or a synthesised command target when the edge wires to a command).
/// <see cref="EventName"/> / <see cref="CommandName"/> surface the well-known payload keys
/// pre-extracted so consumers can branch on them without parsing <see cref="PayloadJson"/>.
/// </summary>
public sealed record FindEventHandlersRow(
    [property: JsonPropertyName("element_symbol_id")] long ElementSymbolId,
    [property: JsonPropertyName("element_fqn")] string ElementFqn,
    [property: JsonPropertyName("element_canonical_key")] string? ElementCanonicalKey,
    [property: JsonPropertyName("handler_symbol_id")] long HandlerSymbolId,
    [property: JsonPropertyName("handler_fqn")] string HandlerFqn,
    [property: JsonPropertyName("handler_canonical_key")] string? HandlerCanonicalKey,
    [property: JsonPropertyName("event_name")] string? EventName,
    [property: JsonPropertyName("command_name")] string? CommandName,
    [property: JsonPropertyName("payload_json")] string? PayloadJson);
