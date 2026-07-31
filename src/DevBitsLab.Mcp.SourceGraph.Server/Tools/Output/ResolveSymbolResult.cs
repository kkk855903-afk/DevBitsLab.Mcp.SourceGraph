using System.Text.Json.Serialization;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

public sealed record ResolveSymbolResult(
    string Status,
    string Match,
    ResolveSymbolIdentity? Symbol,
    IReadOnlyList<ResolveSymbolIdentity> Candidates,
    string? Error);

public sealed record ResolveSymbolIdentity(
    [property: JsonPropertyName("symbol_id")] long SymbolId,
    [property: JsonPropertyName("canonical_key")] string? CanonicalKey,
    string Name,
    string Fqn,
    string Kind,
    [property: JsonPropertyName("file_path")] string FilePath,
    int Line,
    int Column,
    string? Signature);
