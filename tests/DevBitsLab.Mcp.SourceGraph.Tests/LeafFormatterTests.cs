using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Unit coverage for the brand-mark prefix helper. <see cref="LeafFormatter.Suppressed"/> is shared
/// process-wide static state, so tests that flip it always restore it via try/finally — and the
/// class is opted into the <c>LeafFormatterState</c> collection so other tests that depend on
/// <c>Suppressed = false</c> never race with these.
/// </summary>
[Collection("LeafFormatterState")]
public sealed class LeafFormatterTests : IDisposable
{
    public LeafFormatterTests() => LeafFormatter.Suppressed = false;
    public void Dispose() => LeafFormatter.Suppressed = false;

    [Fact]
    public void Brand_prependsMark_onUnbrandedString()
    {
        LeafFormatter.Brand("3 hits for 'Calculator':")
            .Should().Be("\U0001F33F 3 hits for 'Calculator':");
    }

    [Fact]
    public void Brand_isIdempotent_whenInputAlreadyStartsWithMark()
    {
        var alreadyBranded = "\U0001F33F already done.";
        LeafFormatter.Brand(alreadyBranded).Should().Be(alreadyBranded);
    }

    [Fact]
    public void Brand_doesNotDoubleStamp_acrossSuccessiveCalls()
    {
        var once = LeafFormatter.Brand("once");
        var twice = LeafFormatter.Brand(once);
        twice.Should().Be("\U0001F33F once");
    }

    [Fact]
    public void Brand_passesThrough_emptyString()
    {
        LeafFormatter.Brand(string.Empty).Should().Be(string.Empty);
    }

    [Fact]
    public void Brand_isPassThrough_whenSuppressed()
    {
        try
        {
            LeafFormatter.Suppressed = true;
            LeafFormatter.Brand("untouched").Should().Be("untouched");
        }
        finally
        {
            LeafFormatter.Suppressed = false;
        }
    }

    [Fact]
    public void Brand_doesNotStampLeaf_whenSuppressedAndInputAlreadyHasOne()
    {
        try
        {
            LeafFormatter.Suppressed = true;
            // We don't strip pre-existing leaves on the way out — Suppressed only governs whether
            // *we* add one. A tool that hand-rolls a leaf in its body is the tool's choice.
            LeafFormatter.Brand("\U0001F33F preserved").Should().Be("\U0001F33F preserved");
        }
        finally
        {
            LeafFormatter.Suppressed = false;
        }
    }

    [Fact]
    public void Mark_isHerbGlyphFollowedBySpace()
    {
        LeafFormatter.Mark.Should().Be("\U0001F33F ");
    }

    [Fact]
    public void EnvVarName_matchesExpectedKnob()
    {
        LeafFormatter.EnvVarName.Should().Be("SOURCEGRAPH_NO_LEAF");
    }

    // ── BrandFirstText overloads (tool-output-content-blocks) ────────────────────────────

    [Fact]
    public void BrandFirstText_listInput_brandsFirstUserVisibleTextBlock()
    {
        var input = new ContentBlock[]
        {
            new TextContentBlock { Text = "3 hits for 'Calculator':" },
            new TextContentBlock { Text = "second block stays unchanged" },
        };
        var result = LeafFormatter.BrandFirstText(input);
        ((TextContentBlock)result[0]).Text.Should().Be("\U0001F33F 3 hits for 'Calculator':");
        ((TextContentBlock)result[1]).Text.Should().Be("second block stays unchanged");
    }

    [Fact]
    public void BrandFirstText_skipsAudienceRestrictedBlocks_andBrandsTheNextUserBlock()
    {
        var input = new ContentBlock[]
        {
            new TextContentBlock
            {
                Text = "scope=default; latency=12ms",
                Annotations = new Annotations { Audience = new[] { Role.Assistant } },
            },
            new TextContentBlock { Text = "user-facing prose" },
        };
        var result = LeafFormatter.BrandFirstText(input);
        // The audience-restricted block is untouched.
        ((TextContentBlock)result[0]).Text.Should().Be("scope=default; latency=12ms");
        // The first user-visible block is branded.
        ((TextContentBlock)result[1]).Text.Should().Be("\U0001F33F user-facing prose");
    }

    [Fact]
    public void BrandFirstText_listInput_isIdempotent()
    {
        var input = new ContentBlock[] { new TextContentBlock { Text = "\U0001F33F already done." } };
        var result = LeafFormatter.BrandFirstText(input);
        // Same reference back when nothing to do.
        result.Should().BeSameAs(input);
    }

    [Fact]
    public void BrandFirstText_listInput_returnsUnchanged_whenNoTextBlock()
    {
        var input = new ContentBlock[] { new ResourceLinkBlock { Uri = "graph://symbol/1", Name = "X" } };
        var result = LeafFormatter.BrandFirstText(input);
        result.Should().BeSameAs(input);
    }

    [Fact]
    public void BrandFirstText_listInput_passesThrough_whenSuppressed()
    {
        var input = new ContentBlock[] { new TextContentBlock { Text = "untouched" } };
        try
        {
            LeafFormatter.Suppressed = true;
            var result = LeafFormatter.BrandFirstText(input);
            result.Should().BeSameAs(input);
        }
        finally
        {
            LeafFormatter.Suppressed = false;
        }
    }

    [Fact]
    public void BrandFirstText_listInput_emptyList_returnsUnchanged()
    {
        var input = Array.Empty<ContentBlock>();
        LeafFormatter.BrandFirstText(input).Should().BeSameAs(input);
    }

    [Fact]
    public void BrandFirstText_callToolResult_brandsContentInPlace()
    {
        var result = new CallToolResult
        {
            Content = new List<ContentBlock>
            {
                new TextContentBlock { Text = "3 hits" },
            },
        };
        var ret = LeafFormatter.BrandFirstText(result);
        ret.Should().BeSameAs(result);
        ((TextContentBlock)result.Content![0]).Text.Should().Be("\U0001F33F 3 hits");
    }

    [Fact]
    public void BrandFirstText_callToolResult_passesThrough_whenSuppressed()
    {
        var result = new CallToolResult
        {
            Content = new List<ContentBlock> { new TextContentBlock { Text = "untouched" } },
        };
        try
        {
            LeafFormatter.Suppressed = true;
            var ret = LeafFormatter.BrandFirstText(result);
            ret.Should().BeSameAs(result);
            ((TextContentBlock)result.Content![0]).Text.Should().Be("untouched");
        }
        finally
        {
            LeafFormatter.Suppressed = false;
        }
    }
}
