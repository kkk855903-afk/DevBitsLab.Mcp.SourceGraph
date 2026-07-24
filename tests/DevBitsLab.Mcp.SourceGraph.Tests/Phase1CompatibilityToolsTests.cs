using System.Reflection;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;
using FluentAssertions;
using ModelContextProtocol.Server;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class Phase1CompatibilityToolsTests
{
    [Theory]
    [InlineData(nameof(Phase1CompatibilityTools.SearchCodeAsync), "search_code", typeof(SearchSymbolsResult))]
    [InlineData(nameof(Phase1CompatibilityTools.FindSymbolAsync), "find_symbol", typeof(SearchSymbolsResult))]
    [InlineData(nameof(Phase1CompatibilityTools.FindReferenceAsync), "find_reference", typeof(FindReferencesResult))]
    [InlineData(nameof(Phase1CompatibilityTools.FindCallersAsync), "find_callers", typeof(ListCallersResult))]
    [InlineData(nameof(Phase1CompatibilityTools.FindCalleesAsync), "find_callees", typeof(ListCalleesResult))]
    [InlineData(nameof(Phase1CompatibilityTools.TraceCallWithProfileAsync), "trace_call", typeof(TraceCallPathResult))]
    [InlineData(nameof(Phase1CompatibilityTools.ImpactAnalysisAsync), "impact_analysis", typeof(ImpactOfChangeResult))]
    public void CompatibilityEntryPoint_registersExactContractName_andPreservesOutputSchema(
        string methodName,
        string expectedToolName,
        Type expectedOutputSchema)
    {
        var method = typeof(Phase1CompatibilityTools).GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.Static);

        method.Should().NotBeNull();
        var attribute = method!.GetCustomAttribute<McpServerToolAttribute>();
        attribute.Should().NotBeNull();
        attribute!.UseStructuredContent.Should().BeTrue();
        attribute.OutputSchemaType.Should().Be(expectedOutputSchema);

        var tool = McpServerTool.Create(method, target: null, new McpServerToolCreateOptions());
        tool.ProtocolTool.Name.Should().Be(expectedToolName);
    }

    [Fact]
    public async Task CompatibilityEntryPoints_preserveEstablishedDiagnosticResponses()
    {
        var router = new ScopeRouter();

        var search = await Phase1CompatibilityTools.SearchCodeAsync(router, "Missing");
        var symbol = await Phase1CompatibilityTools.FindSymbolAsync(router, "Missing");
        var reference = await Phase1CompatibilityTools.FindReferenceAsync(router, "Missing");
        var callers = await Phase1CompatibilityTools.FindCallersAsync(router, "Missing");
        var callees = await Phase1CompatibilityTools.FindCalleesAsync(router, "Missing");
        var trace = await Phase1CompatibilityTools.TraceCallAsync(
            router,
            "Missing",
            "AlsoMissing");
        var impact = await Phase1CompatibilityTools.ImpactAnalysisAsync(router, "Missing");

        CallToolResultHelpers.ProseText(search).Should().Contain("No scopes are registered.");
        CallToolResultHelpers.ProseText(symbol).Should().Contain("No scopes are registered.");
        CallToolResultHelpers.ProseText(reference).Should().Contain("No scopes are registered.");
        CallToolResultHelpers.ProseText(callers).Should().Contain("No scopes are registered.");
        CallToolResultHelpers.ProseText(callees).Should().Contain("No scopes are registered.");
        CallToolResultHelpers.ProseText(trace).Should().Contain("No scopes are registered.");
        CallToolResultHelpers.ProseText(impact).Should().Contain("No scopes are registered.");
    }
}
