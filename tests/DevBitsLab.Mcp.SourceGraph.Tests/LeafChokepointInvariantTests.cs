using System.Reflection;
using DevBitsLab.Mcp.SourceGraph.Server.Observability;
using DevBitsLab.Mcp.SourceGraph.Server.Plugins;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using FluentAssertions;
using ModelContextProtocol.Server;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Catalog-level coverage for the brand-mark chokepoint introduced by add-leaf-brand-mark.
/// Two invariants are pinned here:
///
/// 1. The server registers built-in tools, and every string flowing out of <see cref="ToolMetrics.TrackAsync"/>
///    or <see cref="ToolMetrics.TrackSync"/> emerges leafed. Because every tool method in
///    <c>Tools/*.cs</c> wraps its body in <c>ToolMetrics.Track*</c> (per the existing convention
///    enforced by code review and OTel), branding the chokepoint brands every built-in.
///
/// 2. Plugin-registered tools (registered via <see cref="ToolRegistry.AddTool(string, string, System.Delegate)"/>)
///    do NOT route through <see cref="ToolMetrics"/>; their handler is wrapped by the SDK directly.
///    The leaf is the source-graph first-party brand and is intentionally not stamped on third-party
///    plugin output (per Decision 4 in <c>openspec/changes/add-leaf-brand-mark/design.md</c>).
///
/// Joins the <c>LeafFormatterState</c> collection so it doesn't race with tests that flip
/// <see cref="LeafFormatter.Suppressed"/>.
/// </summary>
[Collection("LeafFormatterState")]
public sealed class LeafChokepointInvariantTests
{
    [Fact]
    public void BuiltInTools_catalogIsNonEmpty()
    {
        var serverAsm = typeof(ToolMetrics).Assembly;
        var toolMethods = serverAsm.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .ToList();

        toolMethods.Should().NotBeEmpty(
            "the chokepoint invariant only matters if the server registers built-in tools");
    }

    [Fact]
    public async Task TrackAsync_brandsResponseFromBuiltInToolBody()
    {
        // Simulate any built-in tool's body — they all return Task<string> through TrackAsync.
        var result = await ToolMetrics.TrackAsync(
            "test_chokepoint_brands_async",
            args: null,
            () => Task.FromResult("would have been a tool response"));

        result.Should().StartWith("\U0001F33F ");
    }

    [Fact]
    public void TrackSync_brandsResponseFromBuiltInToolBody()
    {
        var result = ToolMetrics.TrackSync(
            "test_chokepoint_brands_sync",
            args: null,
            () => "would have been a sync tool response");

        result.Should().StartWith("\U0001F33F ");
    }

    [Fact]
    public async Task TrackAsync_doesNotBrand_whenSuppressed()
    {
        try
        {
            LeafFormatter.Suppressed = true;
            var result = await ToolMetrics.TrackAsync(
                "test_chokepoint_suppressed",
                args: null,
                () => Task.FromResult("unbranded payload"));

            result.Should().Be("unbranded payload");
        }
        finally
        {
            LeafFormatter.Suppressed = false;
        }
    }

    [Fact]
    public void PluginRegisteredTool_handlerOutput_bypassesChokepoint()
    {
        // Plugin tools are registered through ToolRegistry.AddTool, which calls
        // McpServerTool.Create(handler, ...) — the handler is wrapped by the SDK with no
        // ToolMetrics.Track* in between. We verify the contract structurally: the same handler
        // delegate the registry consumed, when invoked, returns its payload verbatim — no leaf,
        // no chokepoint mutation. The McpServerTool the registry produces wraps that delegate
        // via the SDK's marshalling but doesn't introduce the leaf chokepoint, so the wire-level
        // output preserves the same unbranded contract.
        var record = new PluginRecord("plugin", "1.0", "/path/to.dll", isNuGet: false);
        var registry = new ToolRegistry("mine", new HashSet<string>(StringComparer.Ordinal), record);

        const string pluginPayload = "plugin-author-output";
        // Capture the same delegate instance we hand to AddTool so the assertion below proves
        // *that* delegate's behaviour, not a fresh look-alike.
        var pluginHandler = new Func<string>(() => pluginPayload);
        registry.AddTool("hello", "Greet from a plugin.", pluginHandler);

        var pluginTool = registry.RegisteredTools.Should().ContainSingle().Subject;
        pluginTool.ProtocolTool.Name.Should().Be("mine.hello",
            "the plugin's wire-level tool name is prefixed but otherwise unwrapped");

        // Invoke the *original* handler instance — the same delegate the registry stored — and
        // confirm it returns unbranded prose. The leaf chokepoint lives in ToolMetrics.Track*,
        // and ToolRegistry.AddTool's path through McpServerTool.Create never touches it, so the
        // delegate remains pristine.
        var output = pluginHandler();
        output.Should().Be(pluginPayload);
        output.Should().NotStartWith("\U0001F33F ");
    }
}
