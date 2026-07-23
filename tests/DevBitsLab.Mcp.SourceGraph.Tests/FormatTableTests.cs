using System.Text;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Pins the GFM table-emission contract for <see cref="Format.AppendTable"/>: header row,
/// alignment-cued separator row, escaped cell content, and shape validation. Every list-shaped
/// MCP tool funnels its rendering through this helper, so its behaviour is the canonical
/// definition of "table" across the source-graph surface.
/// </summary>
public sealed class FormatTableTests
{
    [Fact]
    public void EmitsHeaderSeparatorAndDataRows()
    {
        var sb = new StringBuilder();
        Format.AppendTable(
            sb,
            new[] { "Kind", "Location" },
            new[]
            {
                new[] { "ref", "Sample/Other.cs:10:3" },
                new[] { "call", "Sample/Caller.cs:22:5" },
            });

        var s = sb.ToString();
        s.Should().Contain("| Kind | Location |");
        s.Should().Contain("|---|---|");
        s.Should().Contain("| ref | Sample/Other.cs:10:3 |");
        s.Should().Contain("| call | Sample/Caller.cs:22:5 |");
    }

    [Fact]
    public void EmptyRows_emitsHeaderAndSeparatorOnly()
    {
        var sb = new StringBuilder();
        Format.AppendTable(
            sb,
            new[] { "Kind", "Location" },
            System.Array.Empty<IReadOnlyList<string>>());

        var lines = sb.ToString().TrimEnd('\n', '\r').Split('\n');
        lines.Length.Should().Be(2);
        lines[0].Trim().Should().Be("| Kind | Location |");
        lines[1].Trim().Should().Be("|---|---|");
    }

    [Fact]
    public void Alignments_emitCorrectSeparatorCues()
    {
        var sb = new StringBuilder();
        Format.AppendTable(
            sb,
            new[] { "Left", "Right", "Center", "Default" },
            new[]
            {
                new[] { "a", "1", "x", "p" },
            },
            new[] { TableAlignment.Left, TableAlignment.Right, TableAlignment.Center, TableAlignment.Left });

        sb.ToString().Should().Contain("|---|---:|:---:|---|");
    }

    [Fact]
    public void NullAlignments_defaultToLeft()
    {
        var sb = new StringBuilder();
        Format.AppendTable(
            sb,
            new[] { "A", "B" },
            new[] { new[] { "x", "y" } });

        sb.ToString().Should().Contain("|---|---|");
    }

    [Fact]
    public void EscapesPipeInCellContent()
    {
        var sb = new StringBuilder();
        Format.AppendTable(
            sb,
            new[] { "Symbol", "Note" },
            new[]
            {
                // A literal pipe in a path or symbol shouldn't split the cell.
                new[] { "Foo|Bar", "x | y" },
            });

        var s = sb.ToString();
        s.Should().Contain(@"| Foo\|Bar | x \| y |");
    }

    [Fact]
    public void EscapesPipeInHeader()
    {
        var sb = new StringBuilder();
        Format.AppendTable(
            sb,
            new[] { "A|B", "C" },
            new[] { new[] { "x", "y" } });

        sb.ToString().Should().Contain(@"| A\|B | C |");
    }

    [Fact]
    public void RowWithMismatchedColumnCount_throws()
    {
        var sb = new StringBuilder();
        var act = () => Format.AppendTable(
            sb,
            new[] { "A", "B" },
            new[] { new[] { "only-one-cell" } });

        act.Should().Throw<System.ArgumentException>()
            .Where(e => e.Message.Contains("Row column count"));
    }

    [Fact]
    public void HeaderlessTable_throws()
    {
        var sb = new StringBuilder();
        var act = () => Format.AppendTable(sb, System.Array.Empty<string>(), System.Array.Empty<IReadOnlyList<string>>());
        act.Should().Throw<System.ArgumentException>();
    }

    [Fact]
    public void AlignmentLengthMismatch_throws()
    {
        var sb = new StringBuilder();
        var act = () => Format.AppendTable(
            sb,
            new[] { "A", "B" },
            System.Array.Empty<IReadOnlyList<string>>(),
            new[] { TableAlignment.Right }); // only one alignment for two columns

        act.Should().Throw<System.ArgumentException>()
            .Where(e => e.Message.Contains("Alignments length"));
    }
}
