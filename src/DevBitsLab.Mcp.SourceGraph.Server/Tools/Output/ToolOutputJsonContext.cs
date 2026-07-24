using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> for tool structured-output DTOs. Tool
/// bodies serialise their typed result into <c>CallToolResult.StructuredContent</c>
/// (a <see cref="JsonElement"/>) using this context's <c>Default</c> instance, which keeps the
/// hot path off the reflection-based serializer and gives us snake_case property names on the
/// wire (matching the MCP <c>tools/list</c> schema generator).
///
/// Each new tool DTO defined under <c>Tools/Output/</c> is registered here with a
/// <see cref="JsonSerializableAttribute"/>; the source generator picks them up at compile time.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(FindDefinitionResult))]
[JsonSerializable(typeof(FindReferencesResult))]
[JsonSerializable(typeof(SearchSymbolsResult))]
[JsonSerializable(typeof(FindByAnnotationResult))]
[JsonSerializable(typeof(ListCallersResult))]
[JsonSerializable(typeof(ListCalleesResult))]
[JsonSerializable(typeof(FindDataBindingsResult))]
[JsonSerializable(typeof(FindEventHandlersResult))]
[JsonSerializable(typeof(FindImplementationsResult))]
[JsonSerializable(typeof(ListMembersResult))]
[JsonSerializable(typeof(ListSymbolsInFileResult))]
[JsonSerializable(typeof(NeighborhoodResult))]
[JsonSerializable(typeof(ModuleSummaryResult))]
[JsonSerializable(typeof(ImpactOfChangeResult))]
[JsonSerializable(typeof(TraceCallPathResult))]
[JsonSerializable(typeof(SemanticSearchResult))]
[JsonSerializable(typeof(FindDiagnosticsResult))]
[JsonSerializable(typeof(RecentChangesResult))]
[JsonSerializable(typeof(ListTestsForResult))]
[JsonSerializable(typeof(WhoAuthoredResult))]
[JsonSerializable(typeof(ListGeneratedFilesResult))]
[JsonSerializable(typeof(ListScopesResult))]
[JsonSerializable(typeof(VerifyScopeResult))]
[JsonSerializable(typeof(RepairScopeResult))]
[JsonSerializable(typeof(ReconcileDriftResult))]
[JsonSerializable(typeof(GraphStatsResult))]
[JsonSerializable(typeof(DescribeSchemaResult))]
[JsonSerializable(typeof(QueryGraphResult))]
[JsonSerializable(typeof(EmbeddingsStatusResult))]
[JsonSerializable(typeof(EmbeddingsRemoveResult))]
[JsonSerializable(typeof(TraceBindingResult))]
[JsonSerializable(typeof(TraceCommandResult))]
[JsonSerializable(typeof(CheckResourcesResult))]
[JsonSerializable(typeof(CompareStructResult))]
[JsonSerializable(typeof(MatchPInvokeResult))]
[JsonSerializable(typeof(AnalyzeNativeBoundaryResult))]
[JsonSerializable(typeof(TraceRpcResult))]
[JsonSerializable(typeof(CheckProtoContractResult))]
internal partial class ToolOutputJsonContext : JsonSerializerContext
{
}
