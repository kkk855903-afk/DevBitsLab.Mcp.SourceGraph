using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using FluentAssertions;
using ModelContextProtocol;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Coverage for the per-scope cold-start progress broadcaster owned by
/// <c>LiveIndexService</c>. Shape contract: subscribe / unsubscribe / monotonically-increasing
/// fraction / no emit-after-ready.
/// </summary>
public sealed class IndexingProgressSourceTests
{
    [Fact]
    public void Subscribe_thenEmit_invokesHandler()
    {
        var source = new IndexingProgressSource();
        var captured = new List<ProgressNotificationValue>();
        source.Reported += captured.Add;

        source.Emit(new ProgressNotificationValue { Progress = 0.0f, Total = 1.0f, Message = "opening workspace" });

        captured.Should().HaveCount(1);
        captured[0].Message.Should().Be("opening workspace");
        captured[0].Progress.Should().Be(0.0f);
    }

    [Fact]
    public void Unsubscribe_stopsReceivingEvents()
    {
        var source = new IndexingProgressSource();
        var captured = new List<ProgressNotificationValue>();
        Action<ProgressNotificationValue> handler = captured.Add;
        source.Reported += handler;
        source.Emit(new ProgressNotificationValue { Progress = 0.0f, Message = "first" });
        source.Reported -= handler;
        source.Emit(new ProgressNotificationValue { Progress = 0.5f, Message = "second" });

        captured.Should().HaveCount(1);
        captured[0].Message.Should().Be("first");
    }

    [Fact]
    public void MarkReady_emitsTerminalEvent_andFlipsIsReady()
    {
        var source = new IndexingProgressSource();
        var captured = new List<ProgressNotificationValue>();
        source.Reported += captured.Add;

        source.IsReady.Should().BeFalse();
        source.MarkReady();
        source.IsReady.Should().BeTrue();
        captured.Should().HaveCount(1);
        captured[0].Message.Should().Be("ready");
        captured[0].Progress.Should().Be(1.0f);
    }

    [Fact]
    public void EmitAfterReady_isNoOp()
    {
        var source = new IndexingProgressSource();
        var captured = new List<ProgressNotificationValue>();
        source.Reported += captured.Add;

        source.MarkReady();
        source.Emit(new ProgressNotificationValue { Progress = 0.7f, Message = "late" });

        captured.Should().HaveCount(1); // only the ready event
        captured[0].Message.Should().Be("ready");
    }

    [Fact]
    public void MarkReady_isIdempotent()
    {
        var source = new IndexingProgressSource();
        var captured = new List<ProgressNotificationValue>();
        source.Reported += captured.Add;
        source.MarkReady();
        source.MarkReady();
        captured.Should().HaveCount(1);
    }

    [Fact]
    public void TypicalColdStartSequence_isMonotonic()
    {
        var source = new IndexingProgressSource();
        var captured = new List<ProgressNotificationValue>();
        source.Reported += captured.Add;

        source.Emit(new ProgressNotificationValue { Progress = 0.0f, Total = 1.0f, Message = "opening workspace" });
        source.Emit(new ProgressNotificationValue { Progress = 0.5f, Total = 1.0f, Message = "indexing" });
        source.MarkReady();

        captured.Should().HaveCount(3);
        captured[0].Progress.Should().BeLessThan(captured[1].Progress);
        captured[1].Progress.Should().BeLessThan(captured[2].Progress);
        captured.Select(c => c.Message).Should().Equal("opening workspace", "indexing", "ready");
    }

    [Fact]
    public void MultipleSubscribers_receiveAllEvents()
    {
        var source = new IndexingProgressSource();
        var a = new List<ProgressNotificationValue>();
        var b = new List<ProgressNotificationValue>();
        source.Reported += a.Add;
        source.Reported += b.Add;

        source.Emit(new ProgressNotificationValue { Progress = 0.5f, Message = "indexing" });

        a.Should().HaveCount(1);
        b.Should().HaveCount(1);
    }
}
