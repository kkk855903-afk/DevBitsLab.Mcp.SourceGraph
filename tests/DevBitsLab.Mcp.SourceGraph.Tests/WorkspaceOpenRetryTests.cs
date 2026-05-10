using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Server.Observability;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Coverage for <see cref="WorkspaceOpenRetry.RunAsync"/>: retries on failure with bounded
/// backoff, emits the documented heal events, surfaces <see cref="OperationCanceledException"/>
/// without retry. Uses tiny test backoffs (10ms entries) so the worst-case total stays under a
/// second instead of the production 31s. Joins the <c>HealLogState</c> collection so the
/// process-static path doesn't race with other heal-aware tests.
/// </summary>
[Collection("HealLogState")]
public sealed class WorkspaceOpenRetryTests : IDisposable
{
    private readonly string? _previousHealPath = HealLog.LogPath;
    private readonly string _healPath;

    public WorkspaceOpenRetryTests()
    {
        var dir = Path.Join(Path.GetTempPath(), "sourcegraph-retry-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _healPath = Path.Join(dir, "heals.jsonl");
        HealLog.Configure(_healPath);
    }

    public void Dispose()
    {
        HealLog.Configure(string.IsNullOrEmpty(_previousHealPath) ? null : _previousHealPath);
        try
        {
            var dir = Path.GetDirectoryName(_healPath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (IOException) { }
    }

    private static readonly IReadOnlyList<TimeSpan> TinyBackoffs =
        new[] { TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10) };

    [Fact]
    public async Task FirstAttemptThrows_secondSucceeds_emitsOkHealLine()
    {
        var scopeId = $"retry-test-{Guid.NewGuid():N}";
        var openCalls = 0;
        var indexCalls = 0;
        var expected = new IndexResult(1, 2, 3, TimeSpan.FromMilliseconds(5));

        Task Open(CancellationToken ct)
        {
            openCalls++;
            if (openCalls == 1) throw new IOException("dotnet restore in progress");
            return Task.CompletedTask;
        }
        Task<IndexResult> IndexAll(CancellationToken ct)
        {
            indexCalls++;
            return Task.FromResult(expected);
        }

        var sw = Stopwatch.StartNew();
        var result = await WorkspaceOpenRetry.RunAsync(scopeId, Open, IndexAll, TinyBackoffs, NullLogger.Instance, CancellationToken.None);
        sw.Stop();

        result.Should().Be(expected);
        openCalls.Should().Be(2);
        indexCalls.Should().Be(1);

        var line = HealLineForScope(scopeId);
        line.GetProperty("kind").GetString().Should().Be("workspace-open-retried");
        line.GetProperty("ok").GetBoolean().Should().BeTrue();
        line.GetProperty("details").GetString().Should().Be("succeeded on attempt 2");
    }

    [Fact]
    public async Task AllAttemptsFail_throwsLastException_andEmitsFailureHealLine()
    {
        var scopeId = $"retry-test-{Guid.NewGuid():N}";
        var openCalls = 0;
        Task Open(CancellationToken ct)
        {
            openCalls++;
            throw new IOException($"attempt {openCalls} failed");
        }

        var act = () => WorkspaceOpenRetry.RunAsync(scopeId, Open,
            _ => Task.FromResult(new IndexResult(0, 0, 0, TimeSpan.Zero)),
            TinyBackoffs, NullLogger.Instance, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<IOException>();
        ex.Which.Message.Should().Be("attempt 4 failed");
        openCalls.Should().Be(4);

        var line = HealLineForScope(scopeId);
        line.GetProperty("kind").GetString().Should().Be("workspace-open-retried");
        line.GetProperty("ok").GetBoolean().Should().BeFalse();
        line.GetProperty("details").GetString().Should().Contain("all 4 attempts failed");
    }

    [Fact]
    public async Task SuccessOnFirstAttempt_emitsNoHealLine()
    {
        var scopeId = $"retry-test-{Guid.NewGuid():N}";
        var expected = new IndexResult(1, 1, 1, TimeSpan.FromMilliseconds(1));
        var result = await WorkspaceOpenRetry.RunAsync(scopeId,
            _ => Task.CompletedTask,
            _ => Task.FromResult(expected),
            TinyBackoffs, NullLogger.Instance, CancellationToken.None);

        result.Should().Be(expected);
        AnyHealLineForScope(scopeId).Should().BeNull();
    }

    [Fact]
    public async Task CancellationDuringBackoff_rethrows_andEmitsNoHealLine()
    {
        var scopeId = $"retry-test-{Guid.NewGuid():N}";
        using var cts = new CancellationTokenSource();

        Task Open(CancellationToken ct)
        {
            // Cancel before throwing so the next iteration's Task.Delay sees a cancelled token.
            cts.Cancel();
            throw new IOException("transient");
        }

        // Use a generous backoff so the test reliably observes the cancellation in Task.Delay.
        var slowBackoffs = new[] { TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30) };

        var act = () => WorkspaceOpenRetry.RunAsync(scopeId, Open,
            _ => Task.FromResult(new IndexResult(0, 0, 0, TimeSpan.Zero)),
            slowBackoffs, NullLogger.Instance, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        AnyHealLineForScope(scopeId).Should().BeNull();
    }

    private JsonElement HealLineForScope(string scopeId)
    {
        var found = AnyHealLineForScope(scopeId);
        found.Should().NotBeNull($"expected one heals.jsonl line with scope = `{scopeId}`");
        return found!.Value;
    }

    private JsonElement? AnyHealLineForScope(string scopeId)
    {
        if (!File.Exists(_healPath)) return null;
        foreach (var raw in File.ReadAllLines(_healPath))
        {
            var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.GetProperty("scope").GetString() == scopeId)
            {
                return doc.RootElement.Clone();
            }
        }
        return null;
    }
}
