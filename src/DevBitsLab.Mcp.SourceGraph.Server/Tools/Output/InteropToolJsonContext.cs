using System.Text.Json.Serialization;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(MatchPInvokeResult))]
[JsonSerializable(typeof(AnalyzeNativeBoundaryResult))]
internal partial class InteropToolJsonContext : JsonSerializerContext
{
}
