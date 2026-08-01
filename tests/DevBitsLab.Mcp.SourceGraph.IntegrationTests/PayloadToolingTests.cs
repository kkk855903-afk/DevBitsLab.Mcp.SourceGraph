using System.Text.Json;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.IntegrationTests;

/// <summary>
/// End-to-end coverage for <c>find_data_bindings</c> driven through the real MCP <c>tools/call</c>
/// boundary against the <c>SampleWpf</c> fixture. Spawns the server as a child process via
/// <see cref="ServerHarness"/> so the request, the JSON-RPC framing, the structured-output
/// serialisation, and the leaf chokepoint all match what a real client (Claude Code, Cursor)
/// observes.
/// </summary>
public sealed class PayloadToolingTests
{
    /// <summary>
    /// Cold-index time on the SampleWpf fixture is comparable to <c>Sample.sln</c>: tens of
    /// seconds in CI with full Roslyn workspace bring-up plus the XAML dispatcher pass. Mirror
    /// the cap used by <c>ColdIndexTests</c> so a slow CI scheduling slice doesn't false-fail.
    /// </summary>
    private static readonly TimeSpan IndexAndQueryTimeout = TimeSpan.FromMinutes(2);

    private static readonly string[] CommonServeArgs =
    [
        "--no-history",
    ];

    [Fact]
    public async Task FindDataBindings_ToolsCall_returns_binds_path_payload_for_SampleWpf_UserName()
    {
        var sln = ServerHarness.LocateFixture(Path.Join("SampleWpf", "SampleWpf.sln"));
        var args = new List<string>(CommonServeArgs.Length + 2)
        {
            "--solution",
            sln,
        };
        args.AddRange(CommonServeArgs);

        await using var harness = await ServerHarness.StartAsync(args.ToArray());

        using var cts = new CancellationTokenSource(IndexAndQueryTimeout);

        // Match the spec scenario shape: filter on `path` (substring) + `mode` (exact) so the
        // tool answers "find every TwoWay binding to User.Name" without relying on canonical-key
        // resolution of an unindexed-by-XAML binding target.
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["path"] = "User.Name",
            ["mode"] = "two-way",
        };
        var result = await harness.Client.CallToolAsync(
            "find_data_bindings",
            arguments: arguments,
            progress: null,
            options: null,
            cancellationToken: cts.Token);

        result.Should().NotBeNull();
        result.IsError.Should().NotBe(true,
            "find_data_bindings should not surface as an error when the fixture binds User.Name TwoWay");

        // Structured output exists and decodes to the typed shape with `binds-path` rows present.
        result.StructuredContent.Should().NotBeNull(
            "find_data_bindings declares OutputSchemaType so every successful response carries structured_content");
        var structured = (JsonElement)result.StructuredContent!;
        structured.ValueKind.Should().Be(JsonValueKind.Object);

        structured.TryGetProperty("edge_kind", out var edgeKind).Should().BeTrue();
        edgeKind.GetString().Should().Be("binds-path");

        structured.TryGetProperty("bindings", out var bindings).Should().BeTrue();
        bindings.ValueKind.Should().Be(JsonValueKind.Array);
        bindings.GetArrayLength().Should().BeGreaterThan(0,
            "SampleWpf MainWindow.xaml binds Text=\"{Binding User.Name, Mode=TwoWay}\" so at least one row should match");

        // At least one row's payload_json must round-trip the documented kebab-case keys.
        var payloadFound = bindings.EnumerateArray().Any(row =>
            row.TryGetProperty("payload_json", out var payloadProp)
            && payloadProp.ValueKind == JsonValueKind.String
            && payloadProp.GetString() is { } pj
            && pj.Contains("\"path\":\"User.Name\"", StringComparison.Ordinal)
            && pj.Contains("\"mode\":\"two-way\"", StringComparison.Ordinal));
        payloadFound.Should().BeTrue(
            "at least one bindings[].payload_json must carry the documented kebab path + mode keys");
    }
}
