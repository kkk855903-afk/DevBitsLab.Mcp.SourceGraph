using System.Reflection;
using DevBitsLab.Mcp.SourceGraph.Server;
using DevBitsLab.Mcp.SourceGraph.Server.Plugins;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using FluentAssertions;
using ModelContextProtocol.Server;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Catalog-level coverage for the per-tool brand-mark introduced by add-leaf-to-tool-identity.
/// Sibling to <see cref="LeafChokepointInvariantTests"/> — that one pins the per-call response
/// chokepoint; this one pins the post-build catalog-identity pass that stamps
/// <c>Tool.Title</c> and <c>Tool.Description</c>.
///
/// Three invariants:
/// 1. Every built-in tool method (declaring type carries <see cref="McpServerToolTypeAttribute"/>),
///    when run through <see cref="ToolIdentityFormatter.ApplyBrandMark"/>, ends up with a
///    <c>Title</c> starting with the leaf mark and a <c>Description</c> starting with the leaf mark.
/// 2. Plugin-registered tools (registered via <see cref="ToolRegistry.AddTool(string, string, System.Delegate)"/>)
///    are skipped — their <c>Title</c> stays unbranded and their <c>Description</c> doesn't gain a
///    leaf prefix.
/// 3. With <see cref="LeafFormatter.Suppressed"/> set, no tool's <c>Title</c> or <c>Description</c>
///    is stamped — the pass is a complete no-op.
///
/// Joins the <c>LeafFormatterState</c> collection so it doesn't race with tests that flip
/// <see cref="LeafFormatter.Suppressed"/>.
/// </summary>
[Collection("LeafFormatterState")]
public sealed class ToolIdentityInvariantTests : IDisposable
{
    public ToolIdentityInvariantTests() => LeafFormatter.Suppressed = false;
    public void Dispose() => LeafFormatter.Suppressed = false;

    [Fact]
    public void ApplyBrandMark_brandsTitleAndDescription_onEveryBuiltInTool()
    {
        // Reflect over the server assembly to find every [McpServerTool] method on a
        // [McpServerToolType] declaring type — the same enumeration the SDK's
        // WithToolsFromAssembly() sweep uses to register built-ins. Construct an McpServerTool
        // for each, run the formatter, and assert the catalog-shape invariant.
        var tools = EnumerateBuiltInTools().ToList();
        tools.Should().NotBeEmpty(
            "the per-tool brand-mark invariant only matters if the server registers built-in tools");

        ToolIdentityFormatter.ApplyBrandMark(tools);

        foreach (var tool in tools)
        {
            tool.ProtocolTool.Title.Should().StartWith(LeafFormatter.Mark,
                $"every built-in tool's Title must carry the leaf prefix; offender: {tool.ProtocolTool.Name}");
            tool.ProtocolTool.Description.Should().StartWith(LeafFormatter.Mark,
                $"every built-in tool's Description must carry the leaf prefix; offender: {tool.ProtocolTool.Name}");
        }
    }

    [Fact]
    public void ApplyBrandMark_skipsPluginTool_byDelegatePath()
    {
        // Construct a plugin-style tool the same way ToolRegistry.AddTool does — through the
        // Delegate overload of McpServerTool.Create. The resulting tool's Metadata MethodInfo
        // (when present) lives on the lambda's compiler-generated closure, which doesn't carry
        // [McpServerToolType] — so ToolIdentityFormatter.IsBuiltInTool returns false.
        var record = new PluginRecord("plugin", "1.0", "/path/to.dll", isNuGet: false);
        var registry = new ToolRegistry("mine", new HashSet<string>(StringComparer.Ordinal), record);
        registry.AddTool("hello", "Greet from a plugin.", new Func<string>(() => "ok"));
        var pluginTool = registry.RegisteredTools.Should().ContainSingle().Subject;

        var originalTitle = pluginTool.ProtocolTool.Title;
        var originalDescription = pluginTool.ProtocolTool.Description;

        ToolIdentityFormatter.ApplyBrandMark(new[] { pluginTool });

        pluginTool.ProtocolTool.Title.Should().Be(originalTitle,
            "plugin tools keep their authored Title unchanged");
        pluginTool.ProtocolTool.Description.Should().Be(originalDescription,
            "plugin tools keep their authored Description unchanged");
        pluginTool.ProtocolTool.Description.Should().NotStartWith(LeafFormatter.Mark);
    }

    [Fact]
    public void ApplyBrandMark_doesNotBrand_whenSuppressed()
    {
        var tools = EnumerateBuiltInTools().ToList();
        var originalTitles = tools.Select(t => t.ProtocolTool.Title).ToList();
        var originalDescriptions = tools.Select(t => t.ProtocolTool.Description).ToList();

        try
        {
            LeafFormatter.Suppressed = true;
            ToolIdentityFormatter.ApplyBrandMark(tools);

            for (var i = 0; i < tools.Count; i++)
            {
                tools[i].ProtocolTool.Title.Should().Be(originalTitles[i],
                    $"suppressed pass leaves Title unchanged; offender: {tools[i].ProtocolTool.Name}");
                tools[i].ProtocolTool.Description.Should().Be(originalDescriptions[i],
                    $"suppressed pass leaves Description unchanged; offender: {tools[i].ProtocolTool.Name}");
                tools[i].ProtocolTool.Description.Should().NotStartWith(LeafFormatter.Mark);
            }
        }
        finally
        {
            LeafFormatter.Suppressed = false;
        }
    }

    /// <summary>
    /// Enumerate every built-in tool by reflecting over the server assembly. Mirrors the
    /// SDK's <c>WithToolsFromAssembly()</c> discovery: types decorated with
    /// <see cref="McpServerToolTypeAttribute"/>, methods decorated with
    /// <see cref="McpServerToolAttribute"/>. Constructs a fresh <see cref="McpServerTool"/> per
    /// method via <c>McpServerTool.Create(MethodInfo, ...)</c> so each test gets an unbranded
    /// starting state.
    /// </summary>
    private static IEnumerable<McpServerTool> EnumerateBuiltInTools()
    {
        var serverAsm = typeof(ServerInstructions).Assembly;
        var toolMethods = serverAsm.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null);

        foreach (var method in toolMethods)
        {
            yield return McpServerTool.Create(method, target: null, new McpServerToolCreateOptions());
        }
    }
}
