using System.ComponentModel;
using DevBitsLab.Mcp.SourceGraph.Sdk;

namespace SamplePlugin;

/// <summary>
/// Reference <see cref="IMcpToolPlugin"/> implementation. Registers a single tool
/// <c>sample.list_decorated</c>; the actual handler is intentionally minimal — it returns a
/// hard-coded marker string so the round-trip test only needs to verify that the JSON-RPC
/// dispatch mechanism delivers a result, not that the answer is correct (which would require
/// per-scope DB access we don't model in this v1 plugin).
/// </summary>
public sealed class SampleToolPlugin : IMcpToolPlugin
{
    /// <inheritdoc />
    public string Prefix => "sample";

    /// <inheritdoc />
    public Task RegisterAsync(IToolRegistry registry)
    {
        registry.AddTool("list_decorated", "List classes carrying the [Decorated] attribute (sample plugin).", (Delegate)ListDecorated);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tool body. The MCP SDK auto-detects parameters from the delegate signature; here we
    /// declare an optional <paramref name="filter"/> so the schema includes one input. For a
    /// real plugin this would query the per-scope graph store via DI; for the fixture it just
    /// echoes the filter back into a deterministic string the test asserts against.
    /// </summary>
    public static string ListDecorated(
        [Description("Optional substring filter on class name.")] string? filter = null)
    {
        return string.IsNullOrEmpty(filter)
            ? "decorated-classes: (none, sample plugin no-op)"
            : $"decorated-classes (filter={filter}): (none, sample plugin no-op)";
    }
}
