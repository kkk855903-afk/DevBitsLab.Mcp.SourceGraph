using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class DiagnosticRootCauseTests
{
    [Fact]
    public void Detects_missing_SDK_compiler_inputs_from_cascading_BCL_failures()
    {
        var missingTypes = new[]
        {
            "Task",
            "TimeSpan",
            "DateTimeOffset",
            "List",
            "EventHandler",
        };
        var diagnostics = Enumerable.Range(0, 20)
            .Select(index => Diagnostic(
                index % 2 == 0 ? "CS0246" : "CS0103",
                $"The type or name '{missingTypes[index % missingTypes.Length]}' could not be found"))
            .ToArray();

        var rootCause = GraphTools.InferWorkspaceRootCause(diagnostics);

        rootCause.Should().Contain("SDK-generated global usings")
            .And.Contain("probably cascading");
    }

    [Fact]
    public void Does_not_claim_workspace_failure_for_unrelated_errors()
    {
        var diagnostics = Enumerable.Range(0, 20)
            .Select(_ => Diagnostic(
                "CS0029",
                "Cannot implicitly convert type 'string' to 'int'"))
            .ToArray();

        GraphTools.InferWorkspaceRootCause(diagnostics).Should().BeNull();
    }

    private static DiagnosticHit Diagnostic(
        string code,
        string message) =>
        new(
            Id: 1,
            SymbolId: null,
            SymbolFqn: null,
            SymbolCanonicalKey: null,
            FileId: 1,
            FilePath: "Program.cs",
            Severity: 3,
            Code: code,
            Message: message,
            Line: 1,
            Col: 1);
}
