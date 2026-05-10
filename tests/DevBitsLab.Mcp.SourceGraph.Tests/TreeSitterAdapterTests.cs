using DevBitsLab.Mcp.SourceGraph.Indexing.TreeSitter;
using FluentAssertions;
using TreeSitter;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Smoke coverage for the tree-sitter native runtime + bundled grammars. These tests
/// validate that <c>TreeSitter.DotNet</c>'s native binaries actually resolve at runtime on
/// the current RID — i.e. the package's <c>runtimes/&lt;rid&gt;/native/</c> shipping is
/// flowing into the test bin/ correctly. If the runtime can't load <c>libtree-sitter</c>
/// or <c>libtree-sitter-javascript</c>, every downstream tree-sitter test fails too;
/// these are the load-bearing canary.
/// </summary>
public sealed class TreeSitterAdapterTests
{
    [Fact]
    public void GetLanguage_returns_javascript_grammar()
    {
        var language = TreeSitterAdapter.GetLanguage("JavaScript");

        language.Should().NotBeNull();
        language.Name.Should().Be("javascript");
    }

    [Fact]
    public void GetLanguage_caches_instances_per_name()
    {
        var first = TreeSitterAdapter.GetLanguage("JavaScript");
        var second = TreeSitterAdapter.GetLanguage("JavaScript");

        ReferenceEquals(first, second).Should().BeTrue("the adapter caches by name to avoid re-loading the native grammar binary on every parse");
    }

    [Fact]
    public void Parse_simple_javascript_produces_program_root_node()
    {
        var language = TreeSitterAdapter.GetLanguage("JavaScript");
        using var parser = new Parser(language);
        using var tree = parser.Parse("console.log('hello');");

        tree.Should().NotBeNull();
        tree!.RootNode.Type.Should().Be("program");
        tree.RootNode.NamedChildren.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData(0, 0, 1, 1)]
    [InlineData(5, 12, 6, 13)]
    public void ToOneBased_offsets_row_and_column_by_one(int row, int col, int expectedLine, int expectedColumn)
    {
        var (line, column) = TreeSitterAdapter.ToOneBased(new Point(row, col));

        line.Should().Be(expectedLine);
        column.Should().Be(expectedColumn);
    }
}
