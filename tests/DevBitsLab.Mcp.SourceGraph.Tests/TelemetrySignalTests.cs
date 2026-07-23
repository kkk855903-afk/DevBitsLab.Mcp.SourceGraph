using System.Diagnostics;
using System.Diagnostics.Metrics;
using DevBitsLab.Mcp.SourceGraph.Server.Observability;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Coverage for the add-otel-signals change: <see cref="Telemetry"/> exposes the documented
/// <c>ActivitySource</c> + <c>Meter</c>, and <see cref="ToolMetrics"/> emits well-formed signals
/// for every tool call. Each test uses a unique tool name so it can filter out signals produced
/// by other tests sharing the static <c>Telemetry</c> instances.
///
/// One test below reads <see cref="ToolMetrics.TrackAsync"/>'s return value, which the
/// add-leaf-brand-mark chokepoint now decorates with a leaf prefix. Joins the
/// <c>LeafFormatterState</c> collection so it doesn't race with tests that flip
/// <c>LeafFormatter.Suppressed</c>.
/// </summary>
[Collection("LeafFormatterState")]
public sealed class TelemetrySignalTests
{
    [Fact]
    public async Task TrackAsync_withActivityListener_capturesOneServerActivity()
    {
        const string toolName = "test_otel_activity_success";
        var captured = new List<Activity>();

        using var listener = SubscribeActivities(captured);

        await ToolMetrics.TrackAsync(toolName, args: null, () => Task.FromResult("ok"));

        var activity = captured.Should().ContainSingle(a => a.OperationName == $"mcp.tool {toolName}").Subject;
        activity.Kind.Should().Be(ActivityKind.Server);
        activity.Status.Should().Be(ActivityStatusCode.Unset);
        activity.GetTagItem("mcp.tool.name").Should().Be(toolName);
    }

    [Fact]
    public async Task TrackAsync_failingBody_setsErrorStatusAndExceptionTypeTag()
    {
        const string toolName = "test_otel_activity_failure";
        var captured = new List<Activity>();

        using var listener = SubscribeActivities(captured);

        var act = async () => await ToolMetrics.TrackAsync(
            toolName,
            args: null,
            () => Task.FromException<string>(new InvalidOperationException("boom")));

        await act.Should().ThrowAsync<InvalidOperationException>();

        var activity = captured.Should().ContainSingle(a => a.OperationName == $"mcp.tool {toolName}").Subject;
        activity.Status.Should().Be(ActivityStatusCode.Error);
        activity.GetTagItem("exception.type").Should().Be(typeof(InvalidOperationException).FullName);
    }

    [Fact]
    public async Task TrackAsync_withMeterListener_emitsCallsAndDurationOnSuccess_butNoErrors()
    {
        const string toolName = "test_otel_meter_success";
        var samples = new List<MeasurementSample>();

        using var listener = SubscribeMeter(samples, toolName);

        await ToolMetrics.TrackAsync(toolName, args: null, () => Task.FromResult("hello"));

        samples.Should().Contain(s => s.Instrument == "sourcegraph.tool.calls" && s.Value == 1d);
        samples.Should().NotContain(s => s.Instrument == "sourcegraph.tool.errors");
        samples.Should().Contain(s => s.Instrument == "sourcegraph.tool.duration");
        samples.Should().Contain(s => s.Instrument == "sourcegraph.tool.response_size" && s.Value == "hello".Length);

        // mcp.tool.ok=true on every sample for the successful path.
        samples.Should().OnlyContain(s => Equals(GetTag(s, "mcp.tool.ok"), true));
    }

    [Fact]
    public async Task TrackAsync_withMeterListener_emitsBothCallsAndErrorsOnFailure()
    {
        const string toolName = "test_otel_meter_failure";
        var samples = new List<MeasurementSample>();

        using var listener = SubscribeMeter(samples, toolName);

        var act = async () => await ToolMetrics.TrackAsync(
            toolName,
            args: null,
            () => Task.FromException<string>(new InvalidOperationException("boom")));

        await act.Should().ThrowAsync<InvalidOperationException>();

        samples.Should().Contain(s => s.Instrument == "sourcegraph.tool.calls" && s.Value == 1d);
        samples.Should().Contain(s => s.Instrument == "sourcegraph.tool.errors" && s.Value == 1d);
        samples.Should().OnlyContain(s => Equals(GetTag(s, "mcp.tool.ok"), false));
    }

    [Fact]
    public async Task TrackAsync_withScopeArg_attachesScopeTagToEverySample()
    {
        const string toolName = "test_otel_meter_scope";
        var samples = new List<MeasurementSample>();

        using var listener = SubscribeMeter(samples, toolName);

        await ToolMetrics.TrackAsync(toolName, args: new { scope = "frontend" }, () => Task.FromResult(""));

        samples.Should().NotBeEmpty();
        samples.Should().OnlyContain(s => (string?)GetTag(s, "mcp.tool.scope") == "frontend");
    }

    [Fact]
    public async Task TrackAsync_withoutScopeArg_omitsScopeTag()
    {
        const string toolName = "test_otel_meter_noscope";
        var samples = new List<MeasurementSample>();

        using var listener = SubscribeMeter(samples, toolName);

        await ToolMetrics.TrackAsync(toolName, args: new { other = "value" }, () => Task.FromResult(""));

        samples.Should().NotBeEmpty();
        samples.Should().OnlyContain(s => !s.Tags.ContainsKey("mcp.tool.scope"));
    }

    [Fact]
    public async Task TrackAsync_withoutAnyListeners_runsAndReturnsBodyResult()
    {
        // Sanity: no ActivityListener, no MeterListener — Track* must not throw and must surface
        // the body's return value (with the brand-mark prefix from the add-leaf-brand-mark
        // chokepoint applied per design.md Decision 3). The cost path collapses to a null-Activity
        // using-block plus instrument calls into unwatched Counter/Histogram instances.
        const string toolName = "test_otel_no_listener";

        var result = await ToolMetrics.TrackAsync(
            toolName,
            args: null,
            () => Task.FromResult("result-from-body"));

        result.Should().Be("\U0001F33F result-from-body");
    }

    // ── Multi-content overloads (tool-output-content-blocks) ─────────────────────────────

    [Fact]
    public async Task TrackAsync_contentList_brandsFirstTextBlock_andReturnsList()
    {
        // The IReadOnlyList<ContentBlock> overload routes the body's content list through the leaf
        // chokepoint, which prefixes the first user-visible text block. Audience-restricted blocks
        // are skipped. Other items (resource links etc.) flow through unchanged.
        var content = new ModelContextProtocol.Protocol.ContentBlock[]
        {
            new ModelContextProtocol.Protocol.TextContentBlock { Text = "found 3 things" },
            new ModelContextProtocol.Protocol.ResourceLinkBlock { Uri = "graph://symbol/1", Name = "X" },
        };
        var result = await ToolMetrics.TrackAsync(
            "test_content_overload",
            args: null,
            () => Task.FromResult<IReadOnlyList<ModelContextProtocol.Protocol.ContentBlock>>(content));

        result.Count.Should().Be(2);
        ((ModelContextProtocol.Protocol.TextContentBlock)result[0]).Text.Should().StartWith("\U0001F33F ");
        result[1].Should().BeOfType<ModelContextProtocol.Protocol.ResourceLinkBlock>();
    }

    [Fact]
    public async Task TrackAsync_callToolResult_brandsAndReturns_withStructuredContentIntact()
    {
        var dto = new TestStructuredDto("ok", 42);
        var result = await ToolMetrics.TrackAsync(
            "test_calltoolresult_overload",
            args: null,
            () => Task.FromResult(new ModelContextProtocol.Protocol.CallToolResult
            {
                Content = new List<ModelContextProtocol.Protocol.ContentBlock>
                {
                    new ModelContextProtocol.Protocol.TextContentBlock { Text = "1 hit" },
                },
                StructuredContent = System.Text.Json.JsonSerializer.SerializeToElement(dto),
            }));

        ((ModelContextProtocol.Protocol.TextContentBlock)result.Content![0]).Text.Should().StartWith("\U0001F33F ");
        result.StructuredContent.Should().NotBeNull();
    }

    // Note: the change's design called for a runtime anonymous-type guard on
    // CallToolResult.StructuredContent and Meta. On implementation the SDK was found to type
    // those properties as JsonElement? and JsonObject? respectively — anonymous types fail at
    // *compile* time, not runtime, so the guard would be unreachable. Test removed; the typed
    // DTO discipline still applies (the JsonElement boxes only accept pre-serialized payloads).

    [Fact]
    public async Task TrackAsync_callToolResult_isErrorTrue_recordsAsErrorInTelemetry()
    {
        const string toolName = "test_calltoolresult_iserror";
        var samples = new List<MeasurementSample>();
        using var listener = SubscribeMeter(samples, toolName);

        await ToolMetrics.TrackAsync(toolName, args: null, () => Task.FromResult(new ModelContextProtocol.Protocol.CallToolResult
        {
            Content = new List<ModelContextProtocol.Protocol.ContentBlock>
            {
                new ModelContextProtocol.Protocol.TextContentBlock { Text = "tool reported failure" },
            },
            IsError = true,
        }));

        // Cast Value to long to keep CodeQL's float-equality lint quiet — these are integer
        // counters (Counter<long>.Add(1) on the metric instrument), so the read-back is exactly
        // representable and the cast loses no information.
        samples.Should().Contain(s => s.Instrument == "sourcegraph.tool.calls" && (long)s.Value == 1L);
        samples.Should().Contain(s => s.Instrument == "sourcegraph.tool.errors" && (long)s.Value == 1L);
    }

    private sealed record TestStructuredDto(string Status, int Value);

    [Fact]
    public void Telemetry_exposesPublicNameMatchingTheSpec()
    {
        // The instrument name is part of the public surface — consumers configure
        // `AddSource(Telemetry.Name)` / `AddMeter(Telemetry.Name)` against it.
        Telemetry.Name.Should().Be("DevBitsLab.Mcp.SourceGraph");
        Telemetry.ActivitySource.Name.Should().Be("DevBitsLab.Mcp.SourceGraph");
        Telemetry.Meter.Name.Should().Be("DevBitsLab.Mcp.SourceGraph");
    }

    private static ActivityListener SubscribeActivities(List<Activity> sink)
    {
        // ShouldListenTo + Sample(AllData) is the minimum set required to actually create
        // an Activity object — the SDK no-ops StartActivity if no listener returns AllData.
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == Telemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData,
            ActivityStopped = sink.Add,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static MeterListener SubscribeMeter(List<MeasurementSample> sink, string toolNameFilter)
    {
        // Multiple tests share the static Meter instance, so we filter by mcp.tool tag to keep our
        // sink free of cross-test noise. Lock guards the list because the BCL invokes the
        // measurement callback on the publisher's thread.
        var gate = new Lock();
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == Telemetry.Name)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            Capture(sink, gate, instrument.Name, (double)value, tags, toolNameFilter));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            Capture(sink, gate, instrument.Name, value, tags, toolNameFilter));
        listener.Start();
        return listener;
    }

    private static void Capture(
        List<MeasurementSample> sink,
        Lock gate,
        string instrumentName,
        double value,
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        string toolNameFilter)
    {
        var dict = new Dictionary<string, object?>(tags.Length);
        foreach (var pair in tags) dict[pair.Key] = pair.Value;

        if (!dict.TryGetValue("mcp.tool", out var tool) || (string?)tool != toolNameFilter)
        {
            return;
        }

        lock (gate)
        {
            sink.Add(new MeasurementSample(instrumentName, value, dict));
        }
    }

    private sealed record MeasurementSample(string Instrument, double Value, Dictionary<string, object?> Tags);

    private static object? GetTag(MeasurementSample sample, string key)
        => sample.Tags.TryGetValue(key, out var value) ? value : null;
}
