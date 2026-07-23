using System.Diagnostics.Metrics;
using System.Text.Json;
using DevBitsLab.Mcp.SourceGraph.Server.Observability;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Unit coverage for <see cref="HealLog"/>: JSONL line shape, monotone append, swallowed write
/// failures, Counter increments, and the no-op when no path is configured. Each test scopes its
/// MeterListener filter to a unique <c>scope</c> tag value so concurrent tests sharing the static
/// meter don't see each other's heals. Joins the <c>HealLogState</c> collection so the
/// process-static <c>HealLog._logPath</c> doesn't race with other heal-aware tests.
/// </summary>
[Collection("HealLogState")]
public sealed class HealLogTests : IDisposable
{
    private readonly string? _previousLogPath = HealLog.LogPath;

    public void Dispose()
    {
        // Restore process-wide state so other tests don't pick up a stale path.
        HealLog.Configure(_previousLogPath);
    }

    [Fact]
    public void Append_writesOneJsonlLine_withDocumentedShape()
    {
        var dir = CreateTempDir();
        var path = Path.Join(dir, "heals.jsonl");
        HealLog.Configure(path);

        var scope = $"heal-test-{Guid.NewGuid():N}";
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        HealLog.Append(kind: "orphan-db-archived", scope: scope, ok: true, ms: 12.5,
            details: "moved to orphans/foo.db");
        var after = DateTimeOffset.UtcNow.AddSeconds(1);

        var lines = File.ReadAllLines(path);
        lines.Should().ContainSingle();
        var doc = JsonDocument.Parse(lines[0]).RootElement;
        doc.GetProperty("kind").GetString().Should().Be("orphan-db-archived");
        doc.GetProperty("scope").GetString().Should().Be(scope);
        doc.GetProperty("ok").GetBoolean().Should().BeTrue();
        doc.GetProperty("ms").GetDouble().Should().Be(12.5);
        doc.GetProperty("details").GetString().Should().Be("moved to orphans/foo.db");
        var ts = doc.GetProperty("ts").GetDateTimeOffset();
        ts.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public void Append_isMonotone_appendingOnePerCall()
    {
        var dir = CreateTempDir();
        var path = Path.Join(dir, "heals.jsonl");
        HealLog.Configure(path);

        var scope = $"heal-test-{Guid.NewGuid():N}";
        for (var i = 0; i < 5; i++)
        {
            HealLog.Append(kind: "orphan-db-archived", scope: scope, ok: true, ms: i, details: $"#{i}");
        }

        var lines = File.ReadAllLines(path)
            .Where(l =>
            {
                using var doc = JsonDocument.Parse(l);
                return doc.RootElement.GetProperty("scope").GetString() == scope;
            })
            .ToArray();
        lines.Should().HaveCount(5);
        for (var i = 0; i < 5; i++)
        {
            using var doc = JsonDocument.Parse(lines[i]);
            doc.RootElement.GetProperty("details").GetString().Should().Be($"#{i}");
        }
    }

    [Fact]
    public void Append_swallows_ioFailures_silently()
    {
        // Configure a path whose parent is a regular file (not a directory) so File.AppendAllText
        // surfaces an IOException. HealLog must swallow it without surfacing.
        var dir = CreateTempDir();
        var blocker = Path.Join(dir, "block");
        File.WriteAllText(blocker, "not a directory");
        var path = Path.Join(blocker, "heals.jsonl");
        HealLog.Configure(path);

        var scope = $"heal-test-{Guid.NewGuid():N}";
        var act = () => HealLog.Append(kind: "orphan-db-archived", scope: scope, ok: false, ms: 0, details: null);
        act.Should().NotThrow();
    }

    [Fact]
    public void Append_incrementsCounter_withDocumentedTags()
    {
        var scope = $"heal-test-{Guid.NewGuid():N}";
        var samples = new List<(string instrument, long value, Dictionary<string, object?> tags)>();
        var gate = new Lock();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == Telemetry.Name && instrument.Name == "sourcegraph.heal.fired")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var dict = new Dictionary<string, object?>(tags.Length);
            foreach (var pair in tags) dict[pair.Key] = pair.Value;
            if (dict.TryGetValue("scope", out var s) && (string?)s == scope)
            {
                lock (gate) samples.Add((instrument.Name, value, dict));
            }
        });
        listener.Start();

        HealLog.Configure(null); // counter must fire even without a log path
        HealLog.Append(kind: "stuck-indexing-detected", scope: scope, ok: true, ms: 0);

        samples.Should().ContainSingle();
        samples[0].instrument.Should().Be("sourcegraph.heal.fired");
        samples[0].value.Should().Be(1);
        samples[0].tags["kind"].Should().Be("stuck-indexing-detected");
        samples[0].tags["scope"].Should().Be(scope);
        samples[0].tags["ok"].Should().Be(true);
    }

    [Fact]
    public void Append_isNoOp_whenLogPathIsNull()
    {
        // When Configure(null) was called or never called, Append must not throw and must not
        // create a file. The Counter still fires (covered by the dedicated test above) — this one
        // verifies the file side stays quiet.
        HealLog.Configure(null);
        var scope = $"heal-test-{Guid.NewGuid():N}";
        var act = () => HealLog.Append(kind: "orphan-db-archived", scope: scope, ok: true, ms: 0);
        act.Should().NotThrow();
        // No reliable way to assert "no file was created" globally — the test is mostly about
        // ensuring Append doesn't throw on the null-path branch. The counter-tag assertion in the
        // sibling test confirms the Counter still fires when the path is null.
    }

    private static string CreateTempDir()
    {
        var dir = Path.Join(Path.GetTempPath(), "sourcegraph-heallog-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
