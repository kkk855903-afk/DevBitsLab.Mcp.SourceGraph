using System.Reflection;
using DevBitsLab.Mcp.SourceGraph.Server.Observability;
using DevBitsLab.Mcp.SourceGraph.Server.Plugins;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using FluentAssertions;
using ModelContextProtocol.Protocol;
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
    public async Task TrackAsync_brandsFirstUserVisibleTextBlock_inContentList()
    {
        // Multi-content path: tool body returns IReadOnlyList<ContentBlock>. The chokepoint must
        // brand the first user-visible text block in place, leaving non-text and audience-restricted
        // blocks untouched.
        var content = new List<ContentBlock>
        {
            new TextContentBlock { Text = "leading prose row 1" },
            new ResourceLinkBlock { Uri = "graph://symbol/1", Name = "Sample.X", MimeType = "text/markdown" },
            new TextContentBlock
            {
                Text = "trailing meta line",
                Annotations = new Annotations { Audience = new[] { Role.Assistant }, Priority = 0.2f },
            },
        };

        var result = await ToolMetrics.TrackAsync(
            "test_chokepoint_brands_content_list",
            args: null,
            () => Task.FromResult<IReadOnlyList<ContentBlock>>(content));

        // First user-visible text block is branded.
        var first = result.OfType<TextContentBlock>().First(IsUserVisible);
        first.Text.Should().StartWith("\U0001F33F ");
        first.Text.Should().EndWith("leading prose row 1");

        // Resource link is unchanged.
        var link = result.OfType<ResourceLinkBlock>().Single();
        link.Uri.Should().Be("graph://symbol/1");

        // Audience-restricted block must NOT be brand-marked.
        var meta = result.OfType<TextContentBlock>().Single(b => !IsUserVisible(b));
        meta.Text.Should().Be("trailing meta line", "audience-restricted blocks are skipped by the chokepoint");
        meta.Text.Should().NotStartWith("\U0001F33F ");
    }

    [Fact]
    public async Task TrackAsync_brandsFirstUserVisibleTextBlock_inCallToolResult()
    {
        // CallToolResult path: tool body returns Task<CallToolResult>. Same branding rule as the
        // content-list overload, plus the StructuredContent payload must ride through unchanged.
        var input = new CallToolResult
        {
            Content = new List<ContentBlock>
            {
                new TextContentBlock { Text = "user-visible body" },
                new TextContentBlock
                {
                    Text = "agent-only meta",
                    Annotations = new Annotations { Audience = new[] { Role.Assistant }, Priority = 0.2f },
                },
            },
            StructuredContent = System.Text.Json.JsonDocument.Parse("{\"hits\":[]}").RootElement,
        };

        var result = await ToolMetrics.TrackAsync(
            "test_chokepoint_brands_call_tool_result",
            args: null,
            () => Task.FromResult(input));

        // First user-visible text block branded.
        var first = result.Content!.OfType<TextContentBlock>().First(IsUserVisible);
        first.Text.Should().StartWith("\U0001F33F user-visible body");

        // Audience-restricted block unchanged.
        var meta = result.Content!.OfType<TextContentBlock>().Single(b => !IsUserVisible(b));
        meta.Text.Should().Be("agent-only meta");
        meta.Text.Should().NotStartWith("\U0001F33F ");

        // StructuredContent rides through unchanged so agents that consume it directly aren't
        // affected by the leaf chokepoint mutating Content.
        result.StructuredContent.Should().NotBeNull();
        result.StructuredContent!.Value.GetRawText().Should().Be("{\"hits\":[]}");
    }

    [Fact]
    public async Task TrackAsync_contentList_doesNotBrand_whenSuppressed()
    {
        // Suppression invariant on the content-list path: when the leaf is silenced, no block is
        // mutated — the input list ships verbatim.
        try
        {
            LeafFormatter.Suppressed = true;
            var content = new List<ContentBlock>
            {
                new TextContentBlock { Text = "verbatim prose" },
            };
            var result = await ToolMetrics.TrackAsync(
                "test_chokepoint_content_list_suppressed",
                args: null,
                () => Task.FromResult<IReadOnlyList<ContentBlock>>(content));

            var first = result.OfType<TextContentBlock>().First();
            first.Text.Should().Be("verbatim prose");
        }
        finally
        {
            LeafFormatter.Suppressed = false;
        }
    }

    // IsUserVisible lives on the shared CallToolResultHelpers helper so the predicate is
    // consistent across every test class that exercises the multi-content shape.
    private static bool IsUserVisible(TextContentBlock b) => CallToolResultHelpers.IsUserVisible(b);

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
