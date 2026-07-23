using System.Text.Json;
using DevBitsLab.Mcp.SourceGraph.Server.Observability;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class ToolMetricsPrivacyTests : IDisposable
{
    private readonly string _tempDir =
        Path.Join(Path.GetTempPath(), "sourcegraph-tool-metrics-privacy-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousLogPath = ToolMetrics.LogPath;

    public ToolMetricsPrivacyTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        ToolMetrics.Configure(_previousLogPath);
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task UsageLog_persistsRequestSizeButNeverRequestValues()
    {
        const string toolName = "privacy_log_canary";
        const string patientCanary = "PATIENT-CANARY-7F8E61";
        var logPath = Path.Join(_tempDir, "usage.jsonl");
        ToolMetrics.Configure(logPath);

        await ToolMetrics.TrackAsync(
            toolName,
            new
            {
                scope = "frontend",
                query = patientCanary,
                fileHint = @"PatientData\patient-123.cs",
                sql = $"SELECT * FROM records WHERE patient = '{patientCanary}'",
            },
            () => Task.FromResult("ok"));

        var raw = await File.ReadAllTextAsync(logPath);
        raw.Should().NotContain(patientCanary);
        raw.Should().NotContain("PatientData");
        raw.Should().NotContain("SELECT *");

        using var entry = JsonDocument.Parse(
            File.ReadLines(logPath).Single(line => line.Contains(toolName, StringComparison.Ordinal)));
        entry.RootElement.TryGetProperty("args", out _).Should().BeFalse();
        entry.RootElement.GetProperty("request_len").GetInt32().Should().BeGreaterThan(0);
        entry.RootElement.GetProperty("scope").GetString().Should().Be("frontend");
        entry.RootElement.GetProperty("tool").GetString().Should().Be(toolName);
    }
}
