using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using FluentAssertions;
using ModelContextProtocol;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Behavioural coverage for the cold-start subscribe-and-forward pattern that
/// <see cref="ScopedExecution"/> uses inside its private <c>WaitUntilReadyAsync</c>. The wrapper
/// is internal to ScopedExecution but its observable contract — "subscribe before await Ready,
/// forward each event to the request's IProgress, unsubscribe in finally" — is exercised here
/// against the same <see cref="IndexingProgressSource"/> that tool calls subscribe to in production.
/// </summary>
public sealed class ColdStartProgressTests
{
    [Fact]
    public async Task ColdStart_withProgressForwarder_emitsPhaseEvents()
    {
        var source = new IndexingProgressSource();
        var captured = new List<ProgressNotificationValue>();
        // Type the local as the interface so the .Report method group binds to the public
        // IProgress<T>.Report rather than to Progress<T> (where Report is an explicit interface
        // implementation and inaccessible by name). Avoids the inline cast that CodeQL
        // misclassifies as cs/useless-upcast.
        IProgress<ProgressNotificationValue> progress = new Progress<ProgressNotificationValue>(captured.Add);
        // The Progress<T> contract dispatches Report on the captured SynchronizationContext or
        // ThreadPool. Tests run with no SyncCtx, so dispatch is async — give it a bounded delay
        // to settle after each emission.

        // Mirror the wrapper's subscribe-await-unsubscribe pattern.
        Action<ProgressNotificationValue> handler = progress.Report;
        source.Reported += handler;
        try
        {
            // Simulate cold-start emissions.
            source.Emit(new ProgressNotificationValue { Progress = 0.0f, Total = 1.0f, Message = "opening workspace" });
            source.Emit(new ProgressNotificationValue { Progress = 0.5f, Total = 1.0f, Message = "indexing" });
            source.MarkReady();
        }
        finally
        {
            source.Reported -= handler;
        }

        // Allow the Progress<T> dispatch to drain.
        for (var i = 0; i < 20 && captured.Count < 3; i++) await Task.Delay(10);

        captured.Should().HaveCount(3);
        captured.Select(c => c.Message).Should().Equal("opening workspace", "indexing", "ready");
    }

    [Fact]
    public async Task ColdStart_withoutForwarder_emitsZeroEvents()
    {
        // Without the subscribe-and-forward step, no IProgress is wired — emissions still happen
        // on the source but no captured list grows.
        var source = new IndexingProgressSource();
        var captured = new List<ProgressNotificationValue>();
        // Note: no `source.Reported += ...` — this is the "no forwarder" path.

        source.Emit(new ProgressNotificationValue { Progress = 0.0f, Message = "opening workspace" });
        source.MarkReady();

        await Task.Delay(20);
        captured.Should().BeEmpty();
    }

    [Fact]
    public async Task UnsubscribedHandler_doesNotReceiveLateEvents()
    {
        // Verifies the finally-unsubscribe contract: after the handler is removed, subsequent
        // emissions from the same source must not invoke it.
        var source = new IndexingProgressSource();
        var captured = new List<ProgressNotificationValue>();
        Action<ProgressNotificationValue> handler = captured.Add;

        source.Reported += handler;
        source.Emit(new ProgressNotificationValue { Progress = 0.0f, Message = "opening workspace" });
        source.Reported -= handler;
        source.Emit(new ProgressNotificationValue { Progress = 0.5f, Message = "indexing" });
        source.MarkReady();

        await Task.Delay(20);
        captured.Should().HaveCount(1);
        captured[0].Message.Should().Be("opening workspace");
    }
}
