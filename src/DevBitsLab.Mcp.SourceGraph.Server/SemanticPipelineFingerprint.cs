using DevBitsLab.Mcp.SourceGraph.Server.Cli;
using DevBitsLab.Mcp.SourceGraph.Storage;

namespace DevBitsLab.Mcp.SourceGraph.Server;

/// <summary>
/// Version identity for every producer whose output can change graph semantics without changing
/// source bytes. Bump the relevant component when its projection rules change.
/// </summary>
internal static class SemanticPipelineFingerprint
{
    private const int CSharpIndexerVersion = 2;
    private const int XamlIndexerVersion = 2;
    private const int CppSyntaxIndexerVersion = 3;
    private const int ExecutionProjectionVersion = 5;
    private const int DiagnosticPipelineVersion = 2;

    public static string Current =>
        string.Join(
            ';',
            $"tool={VersionInfo.EffectiveVersion}",
            $"schema={Schema.Version}",
            $"csharp={CSharpIndexerVersion}",
            $"xaml={XamlIndexerVersion}",
            $"cpp-syntax={CppSyntaxIndexerVersion}",
            $"execution={ExecutionProjectionVersion}",
            $"diagnostics={DiagnosticPipelineVersion}");
}
