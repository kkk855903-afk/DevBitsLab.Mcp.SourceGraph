using System.ComponentModel;
using System.Reflection;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using FluentAssertions;
using ModelContextProtocol.Server;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Unit coverage for <see cref="ToolIdentityFormatter"/>: the post-build pass that stamps the
/// brand mark on every built-in tool's <c>Tool.Title</c> and <c>Tool.Description</c>. Mirrors
/// <see cref="ToolTriggerTests"/> in shape — uses real <see cref="McpServerTool"/> instances
/// constructed via <see cref="McpServerTool.Create(MethodInfo, object?, McpServerToolCreateOptions?)"/>
/// against fixture methods on private nested types, controlled to either carry or omit
/// <see cref="McpServerToolTypeAttribute"/>.
///
/// Joined to the <c>LeafFormatterState</c> collection so the static
/// <see cref="LeafFormatter.Suppressed"/> flag doesn't race with parallel test classes.
/// </summary>
[Collection("LeafFormatterState")]
public sealed class ToolIdentityFormatterTests : IDisposable
{
    public ToolIdentityFormatterTests() => LeafFormatter.Suppressed = false;
    public void Dispose() => LeafFormatter.Suppressed = false;

    [Fact]
    public void ApplyBrandMark_setsTitleAndPrependsDescription_onBuiltInTool()
    {
        var tool = CreateBuiltInTool();
        // Pre-conditions: SDK leaves Title null; Description is populated from [Description].
        tool.ProtocolTool.Title.Should().BeNullOrEmpty();
        tool.ProtocolTool.Description.Should().Be("Find a thing in the graph.");

        ToolIdentityFormatter.ApplyBrandMark(new[] { tool });

        tool.ProtocolTool.Title.Should().Be(LeafFormatter.Mark + tool.ProtocolTool.Name);
        tool.ProtocolTool.Description.Should().Be(LeafFormatter.Mark + "Find a thing in the graph.");
    }

    [Fact]
    public void ApplyBrandMark_isIdempotent_whenDescriptionAlreadyStartsWithMark()
    {
        var tool = CreateBuiltInTool();
        tool.ProtocolTool.Description = LeafFormatter.Mark + "Already branded.";

        ToolIdentityFormatter.ApplyBrandMark(new[] { tool });

        tool.ProtocolTool.Description.Should().Be(LeafFormatter.Mark + "Already branded.");
    }

    [Fact]
    public void ApplyBrandMark_overwritesPreviouslySetTitle_withMarkPlusName()
    {
        // Per the spec ("Tool.Title SHALL be set to '🌿 ' + Tool.Name") and design.md Decision 2
        // ("set unconditionally, no fallback to existing Title"), any previously-set Title is
        // overwritten. Idempotency is by construction: pass twice, same result.
        var tool = CreateBuiltInTool();
        tool.ProtocolTool.Title = LeafFormatter.Mark + "stale_value";

        ToolIdentityFormatter.ApplyBrandMark(new[] { tool });

        tool.ProtocolTool.Title.Should().Be(LeafFormatter.Mark + tool.ProtocolTool.Name);
    }

    [Fact]
    public void ApplyBrandMark_isNoOp_whenSuppressed()
    {
        var tool = CreateBuiltInTool();
        var originalDescription = tool.ProtocolTool.Description;

        try
        {
            LeafFormatter.Suppressed = true;
            ToolIdentityFormatter.ApplyBrandMark(new[] { tool });

            tool.ProtocolTool.Title.Should().BeNullOrEmpty();
            tool.ProtocolTool.Description.Should().Be(originalDescription);
        }
        finally
        {
            LeafFormatter.Suppressed = false;
        }
    }

    [Fact]
    public void ApplyBrandMark_skipsTool_whenDeclaringTypeLacksMcpServerToolTypeAttribute()
    {
        var tool = CreatePluginStyleTool();
        var originalTitle = tool.ProtocolTool.Title;
        var originalDescription = tool.ProtocolTool.Description;

        ToolIdentityFormatter.ApplyBrandMark(new[] { tool });

        tool.ProtocolTool.Title.Should().Be(originalTitle);
        tool.ProtocolTool.Description.Should().Be(originalDescription);
        tool.ProtocolTool.Description.Should().NotStartWith(LeafFormatter.Mark);
    }

    [Fact]
    public void ApplyBrandMark_runTwice_isIdempotent()
    {
        var tool = CreateBuiltInTool();

        ToolIdentityFormatter.ApplyBrandMark(new[] { tool });
        var titleAfterFirst = tool.ProtocolTool.Title;
        var descriptionAfterFirst = tool.ProtocolTool.Description;

        ToolIdentityFormatter.ApplyBrandMark(new[] { tool });

        tool.ProtocolTool.Title.Should().Be(titleAfterFirst);
        tool.ProtocolTool.Description.Should().Be(descriptionAfterFirst);
    }

    [Fact]
    public void ApplyBrandMark_leavesNullDescriptionUnchanged_butStillSetsTitle()
    {
        var tool = CreateBuiltInTool();
        tool.ProtocolTool.Description = null;

        ToolIdentityFormatter.ApplyBrandMark(new[] { tool });

        // Title is set from Name even when Description is null — Name always exists.
        tool.ProtocolTool.Title.Should().Be(LeafFormatter.Mark + tool.ProtocolTool.Name);
        // Null Description is left null — branding nothing produces nothing.
        tool.ProtocolTool.Description.Should().BeNull();
    }

    [Fact]
    public void ApplyBrandMark_overwritesUnbrandedCustomTitle_withMarkPlusName()
    {
        // A custom Title without the leaf mark is replaced by "🌿 + Name", not augmented.
        // The pass treats any pre-existing Title as a stale value to overwrite — matching the
        // spec invariant that built-in Title is exactly the Name-derived form.
        var tool = CreateBuiltInTool();
        tool.ProtocolTool.Title = "Custom Display Name";

        ToolIdentityFormatter.ApplyBrandMark(new[] { tool });

        tool.ProtocolTool.Title.Should().Be(LeafFormatter.Mark + tool.ProtocolTool.Name);
    }

    private static McpServerTool CreateBuiltInTool() =>
        McpServerTool.Create(
            typeof(BuiltInFixture).GetMethod(nameof(BuiltInFixture.Hello))!,
            target: null,
            new McpServerToolCreateOptions());

    private static McpServerTool CreatePluginStyleTool() =>
        McpServerTool.Create(
            typeof(PluginStyleFixture).GetMethod(nameof(PluginStyleFixture.Bye))!,
            target: null,
            new McpServerToolCreateOptions());

    // Built-in fixture: declaring type carries [McpServerToolType] so
    // ToolIdentityFormatter.IsBuiltInTool returns true.
    [McpServerToolType]
    private static class BuiltInFixture
    {
        [Description("Find a thing in the graph.")]
        public static string Hello() => "ok";
    }

    // Plugin-style fixture: declaring type does NOT carry [McpServerToolType] so
    // ToolIdentityFormatter.IsBuiltInTool returns false. Stand-in for plugin tools (which the
    // production server registers via ToolRegistry.AddTool through the Delegate overload of
    // McpServerTool.Create).
    private static class PluginStyleFixture
    {
        [Description("Plugin author's description.")]
        public static string Bye() => "ok";
    }
}
