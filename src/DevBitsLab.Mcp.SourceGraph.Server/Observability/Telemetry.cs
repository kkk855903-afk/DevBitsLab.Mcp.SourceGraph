using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace DevBitsLab.Mcp.SourceGraph.Server.Observability;

/// <summary>
/// OpenTelemetry-compatible signal sources. The <see cref="ActivitySource"/> emits one span per
/// MCP tool call (when an OTel listener is attached); the <see cref="Meter"/> exposes counters and
/// a duration histogram. Both are zero-cost when no listener subscribes — `Activity.Current` stays
/// null, the histogram skips its sample buffer, and the counter increments a single field.
///
/// Consumers attach via:
///   <list type="bullet">
///     <item>OpenTelemetry SDK — <c>builder.AddSource("DevBitsLab.Mcp.SourceGraph")</c> /
///           <c>builder.AddMeter("DevBitsLab.Mcp.SourceGraph")</c>.</item>
///     <item><c>dotnet-counters monitor --name sourcegraph-mcp DevBitsLab.Mcp.SourceGraph</c>.</item>
///   </list>
/// </summary>
public static class Telemetry
{
    /// <summary>The single source/meter name advertised to OpenTelemetry consumers.</summary>
    public const string Name = "DevBitsLab.Mcp.SourceGraph";

    private static readonly string Version =
        typeof(Telemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(Telemetry).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    public static readonly ActivitySource ActivitySource = new(Name, Version);
    public static readonly Meter Meter = new(Name, Version);

    public static readonly Counter<long> ToolCalls =
        Meter.CreateCounter<long>(
            name: "sourcegraph.tool.calls",
            unit: "{call}",
            description: "Number of MCP tool invocations handled by the server.");

    public static readonly Counter<long> ToolErrors =
        Meter.CreateCounter<long>(
            name: "sourcegraph.tool.errors",
            unit: "{call}",
            description: "Number of MCP tool invocations that threw an exception.");

    public static readonly Histogram<double> ToolDurationMs =
        Meter.CreateHistogram<double>(
            name: "sourcegraph.tool.duration",
            unit: "ms",
            description: "End-to-end latency of one MCP tool invocation, in milliseconds.");

    public static readonly Histogram<long> ToolResponseBytes =
        Meter.CreateHistogram<long>(
            name: "sourcegraph.tool.response_size",
            unit: "By",
            description: "Serialised response size of one MCP tool invocation, in UTF-8 bytes — the wire-format size of the JSON payload returned to the MCP client.");
}
