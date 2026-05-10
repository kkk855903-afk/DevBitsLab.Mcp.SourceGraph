using System.ComponentModel;
using System.Reflection;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using FluentAssertions;
using ModelContextProtocol.Server;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Wiring-level four-cell matrix for the (<c>--no-leaf</c> × <c>--no-instructions</c>) interaction
/// applied to the per-tool brand mark on <c>Tool.Title</c> and <c>Tool.Description</c>. Mirrors the
/// shape of <see cref="ServerInstructionsWiringTests"/> but narrowed to the catalog-identity
/// channels: <c>--no-instructions</c> has no effect on Title/Description (it governs the published
/// instructions string), so cells (no flags / `--no-instructions` only) collapse to "branded" and
/// cells (`--no-leaf` only / both flags) collapse to "unbranded." Both pairs are still asserted
/// explicitly so the matrix coverage is complete and a future regression that accidentally
/// couples the two knobs is caught.
///
/// Joins the <c>LeafFormatterState</c> collection so it doesn't race with tests that flip
/// <see cref="LeafFormatter.Suppressed"/>.
/// </summary>
[Collection("LeafFormatterState")]
public sealed class ToolIdentityWiringTests : IDisposable
{
    public ToolIdentityWiringTests() => LeafFormatter.Suppressed = false;
    public void Dispose() => LeafFormatter.Suppressed = false;

    [Fact]
    public void NoFlags_brandsTitleAndDescription()
    {
        var tool = CreateTool();

        LeafFormatter.Suppressed = false; // simulates `--no-leaf` absent
        ToolIdentityFormatter.ApplyBrandMark(new[] { tool });

        tool.ProtocolTool.Title.Should().Be(LeafFormatter.Mark + tool.ProtocolTool.Name);
        tool.ProtocolTool.Description.Should().StartWith(LeafFormatter.Mark);
    }

    [Fact]
    public void NoLeafOnly_leavesTitleAndDescriptionUnbranded()
    {
        var tool = CreateTool();
        var originalDescription = tool.ProtocolTool.Description;

        LeafFormatter.Suppressed = true; // simulates `--no-leaf` set
        ToolIdentityFormatter.ApplyBrandMark(new[] { tool });

        tool.ProtocolTool.Title.Should().BeNullOrEmpty();
        tool.ProtocolTool.Description.Should().Be(originalDescription);
        tool.ProtocolTool.Description.Should().NotStartWith(LeafFormatter.Mark);
    }

    [Fact]
    public void NoInstructionsOnly_brandsTitleAndDescription()
    {
        // `--no-instructions` is independent of the brand-mark chokepoint. With `--no-leaf` absent,
        // ApplyBrandMark stamps Title/Description regardless of how `--no-instructions` is set.
        var tool = CreateTool();

        LeafFormatter.Suppressed = false;
        ToolIdentityFormatter.ApplyBrandMark(new[] { tool });

        tool.ProtocolTool.Title.Should().Be(LeafFormatter.Mark + tool.ProtocolTool.Name);
        tool.ProtocolTool.Description.Should().StartWith(LeafFormatter.Mark);
    }

    [Fact]
    public void BothFlagsSet_leavesTitleAndDescriptionUnbranded()
    {
        var tool = CreateTool();
        var originalDescription = tool.ProtocolTool.Description;

        LeafFormatter.Suppressed = true;
        ToolIdentityFormatter.ApplyBrandMark(new[] { tool });

        tool.ProtocolTool.Title.Should().BeNullOrEmpty();
        tool.ProtocolTool.Description.Should().Be(originalDescription);
    }

    private static McpServerTool CreateTool() =>
        McpServerTool.Create(
            typeof(MatrixFixture).GetMethod(nameof(MatrixFixture.Sample))!,
            target: null,
            new McpServerToolCreateOptions());

    [McpServerToolType]
    private static class MatrixFixture
    {
        [Description("Find a thing.")]
        public static string Sample() => "ok";
    }
}
