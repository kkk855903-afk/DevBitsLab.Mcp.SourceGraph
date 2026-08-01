using System.Diagnostics;
using DevBitsLab.Mcp.SourceGraph.Server;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class ProcessLifetimeManagerTests
{
    [Fact]
    public void ActivityTracker_waitsForActiveRequestAndRestartsWindowOnCompletion()
    {
        var time = new ManualTimeProvider();
        var tracker = new ProcessActivityTracker(time);
        var activity = tracker.BeginActivity();

        time.Advance(TimeSpan.FromHours(1));
        tracker.IsIdleFor(TimeSpan.FromMinutes(30)).Should().BeFalse();

        activity.Dispose();
        tracker.IsIdleFor(TimeSpan.FromMinutes(30)).Should().BeFalse();
        time.Advance(TimeSpan.FromMinutes(30));
        tracker.IsIdleFor(TimeSpan.FromMinutes(30)).Should().BeTrue();
    }

    [Fact]
    public async Task IdleTimeout_stopsApplicationOnlyOnce()
    {
        var time = new ManualTimeProvider();
        var tracker = new ProcessActivityTracker(time);
        var lifetime = new TestApplicationLifetime();
        var terminator = new RecordingTerminator();
        using var manager = CreateManager(lifetime, tracker, terminator, parent: null);

        await manager.StartAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(31));
        await WaitForCancellationAsync(lifetime.ApplicationStopping, TimeSpan.FromSeconds(1));
        await Task.Delay(30);

        lifetime.StopCount.Should().Be(1);
        lifetime.MarkStopped();
        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ActiveRequest_blocksIdleShutdownUntilItCompletes()
    {
        var time = new ManualTimeProvider();
        var tracker = new ProcessActivityTracker(time);
        var lifetime = new TestApplicationLifetime();
        var terminator = new RecordingTerminator();
        using var manager = CreateManager(lifetime, tracker, terminator, parent: null);
        using var activity = tracker.BeginActivity();

        await manager.StartAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(40);
        lifetime.ApplicationStopping.IsCancellationRequested.Should().BeFalse();

        activity.Dispose();
        time.Advance(TimeSpan.FromMinutes(31));
        await WaitForCancellationAsync(lifetime.ApplicationStopping, TimeSpan.FromSeconds(1));

        lifetime.MarkStopped();
        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ParentExit_stopsApplication()
    {
        using var parent = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "--version",
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;
        var lifetime = new TestApplicationLifetime();
        var tracker = new ProcessActivityTracker();
        var terminator = new RecordingTerminator();
        using var manager = CreateManager(lifetime, tracker, terminator, parent);

        await manager.StartAsync(CancellationToken.None);
        await WaitForCancellationAsync(lifetime.ApplicationStopping, TimeSpan.FromSeconds(5));

        lifetime.StopCount.Should().Be(1);
        lifetime.MarkStopped();
        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExternalShutdown_suppressesLaterIdleStopRequest()
    {
        var time = new ManualTimeProvider();
        var tracker = new ProcessActivityTracker(time);
        var lifetime = new TestApplicationLifetime();
        var terminator = new RecordingTerminator();
        using var manager = CreateManager(lifetime, tracker, terminator, parent: null);

        await manager.StartAsync(CancellationToken.None);
        lifetime.StopApplication();
        time.Advance(TimeSpan.FromMinutes(31));
        await Task.Delay(40);

        lifetime.StopCount.Should().Be(1);
        lifetime.MarkStopped();
        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ShutdownDeadline_forcesExitWhenHostDoesNotStop()
    {
        var lifetime = new TestApplicationLifetime();
        var tracker = new ProcessActivityTracker();
        var terminator = new RecordingTerminator();
        using var manager = CreateManager(
            lifetime,
            tracker,
            terminator,
            parent: null,
            shutdownGracePeriod: TimeSpan.FromMilliseconds(20));

        await manager.StartAsync(CancellationToken.None);
        lifetime.StopApplication();

        (await terminator.ExitCode.Task.WaitAsync(TimeSpan.FromSeconds(1))).Should().Be(0);
        lifetime.MarkStopped();
        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void ParentResolver_findsCurrentLauncher()
    {
        ParentProcessResolver.TryGetParentProcessId().Should().BeGreaterThan(0);
    }

    private static ProcessLifetimeManager CreateManager(
        TestApplicationLifetime lifetime,
        ProcessActivityTracker tracker,
        RecordingTerminator terminator,
        Process? parent,
        TimeSpan? shutdownGracePeriod = null) =>
        new(
            lifetime,
            tracker,
            new ProcessLifetimeOptions(
                IdleTimeout: TimeSpan.FromMinutes(30),
                ShutdownGracePeriod: shutdownGracePeriod ?? TimeSpan.FromSeconds(5),
                IdleCheckInterval: TimeSpan.FromMilliseconds(10)),
            terminator,
            NullLogger<ProcessLifetimeManager>.Instance,
            parent);

    private static async Task WaitForCancellationAsync(
        CancellationToken token,
        TimeSpan timeout)
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() => cancelled.TrySetResult());
        await cancelled.Task.WaitAsync(timeout);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _timestamp, duration.Ticks);
    }

    private sealed class TestApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();
        private int _stopCount;

        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;
        public int StopCount => Volatile.Read(ref _stopCount);

        public void StopApplication()
        {
            Interlocked.Increment(ref _stopCount);
            _stopping.Cancel();
        }

        public void MarkStopped() => _stopped.Cancel();
    }

    private sealed class RecordingTerminator : IProcessTerminator
    {
        public TaskCompletionSource<int> ExitCode { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Exit(int exitCode) => ExitCode.TrySetResult(exitCode);
    }
}
