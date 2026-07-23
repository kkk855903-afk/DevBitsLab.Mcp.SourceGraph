using DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Pins the soft-cap arithmetic used by the four currently opted-in tools (find_references,
/// list_members, list_symbols_in_file, semantic_search) to stay under Claude Code's
/// ~64K-character per-tool-result ceiling. The contract is a pure function of
/// (totalItems, perItemChars, baseChars, budget) — so direct unit coverage is enough; the
/// downstream wiring is exercised by the structured-output tests in GraphTools.
/// </summary>
public sealed class OutputBudgetTests
{
    [Fact]
    public void ReturnsAllItems_whenWellUnderBudget()
    {
        var (kept, omitted) = OutputBudget.ChooseKeep(totalItems: 10, perItemChars: 500);
        kept.Should().Be(10);
        omitted.Should().Be(0);
    }

    [Fact]
    public void TrimsTail_whenItemsExceedBudget()
    {
        // 200 items × 500 chars = 100K, budget 50K → only ~96 items fit (after 2K base overhead).
        var (kept, omitted) = OutputBudget.ChooseKeep(totalItems: 200, perItemChars: 500);
        kept.Should().BeLessThan(200);
        kept.Should().BeGreaterThan(0);
        (kept + omitted).Should().Be(200, "trim must preserve total accounting");
    }

    [Fact]
    public void ReturnsZero_forEmptyInput()
    {
        var (kept, omitted) = OutputBudget.ChooseKeep(totalItems: 0, perItemChars: 500);
        kept.Should().Be(0);
        omitted.Should().Be(0);
    }

    [Fact]
    public void TighterPerItemBudget_keepsFewerItems()
    {
        var (compactKept, _) = OutputBudget.ChooseKeep(totalItems: 200, perItemChars: OutputBudget.CompactRowChars);
        var (richKept, _) = OutputBudget.ChooseKeep(totalItems: 200, perItemChars: OutputBudget.RichRowChars);
        var (snippetKept, _) = OutputBudget.ChooseKeep(totalItems: 200, perItemChars: OutputBudget.SnippetRowChars);

        compactKept.Should().BeGreaterThan(richKept, "rich rows are heavier so fewer fit");
        richKept.Should().BeGreaterThan(snippetKept, "snippet rows are heavier still");
    }

    [Fact]
    public void HonoursExplicitBudgetOverride()
    {
        // Tiny budget — every item should be omitted.
        var (kept, omitted) = OutputBudget.ChooseKeep(totalItems: 50, perItemChars: 500, baseChars: 0, budget: 100);
        kept.Should().BeLessThan(50);
        omitted.Should().BeGreaterThan(0);
    }

    [Fact]
    public void ResultStaysUnderBudget_perWorstCaseEstimate()
    {
        // Verify the kept × perItemChars + baseChars never exceeds budget — the invariant the
        // downstream tools rely on to avoid Claude Code's host-side truncation.
        const int budget = 50_000;
        const int baseChars = 2_000;
        const int perItem = 500;
        var (kept, _) = OutputBudget.ChooseKeep(totalItems: 1_000, perItemChars: perItem, baseChars: baseChars, budget: budget);
        (kept * perItem + baseChars).Should().BeLessThanOrEqualTo(budget);
    }
}
