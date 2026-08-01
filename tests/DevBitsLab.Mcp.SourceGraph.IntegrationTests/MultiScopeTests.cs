using System.Text.Json;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.IntegrationTests;

/// <summary>
/// End-to-end smoke against the multi-scope fixture under <c>tests/fixtures/MultiScope/</c>:
/// a server spawned at the fixture root reads <c>.sourcegraph.json</c>, registers the
/// declared scopes, and the <c>list_scopes</c> MCP tool reports each one. Also verifies that
/// the per-scope vocabulary published under
/// <c>capabilities.experimental["sourcegraph.vocabulary"].scopes</c> reflects the actual
/// configured scope identifiers (not the union, not a single synthesised default).
/// </summary>
public sealed class MultiScopeTests
{
    private static readonly string[] CommonServeArgs =
    [
        "--no-history",
    ];

    private static string[] WithRoot(string rootAbsolutePath)
    {
        var args = new List<string>(CommonServeArgs.Length + 2)
        {
            "--root",
            rootAbsolutePath,
        };
        args.AddRange(CommonServeArgs);
        return args.ToArray();
    }

    // §12.2 unblocked: LiveIndexService.OpenScopeAsync now calls _router.Register(host) inline
    // (status="indexing") before the cold index begins, so list_scopes sees every configured
    // scope from the moment the process accepts MCP requests.
    [Fact]
    public async Task ListScopes_tool_reports_every_configured_scope()
    {
        var multiScopeRoot = ServerHarness.LocateFixture("MultiScope");
        await using var harness = await ServerHarness.StartAsync(WithRoot(multiScopeRoot));

        // Real MCP wire round-trip: ask the server to invoke its `list_scopes` tool. The result
        // is a CallToolResult whose Content carries the markdown the tool produced. The
        // production tool (ScopeTools.ListScopesAsync) renders a markdown table with one row per
        // scope — we assert the row text contains both `frontend` and `backend` (the scope ids
        // declared in tests/fixtures/MultiScope/.sourcegraph.json).
        var result = await harness.Client.CallToolAsync(
            "list_scopes",
            arguments: null,
            progress: null,
            options: null,
            cancellationToken: default);

        result.Should().NotBeNull();
        result.IsError.Should().NotBe(true, "list_scopes should not return an error response");

        var rendered = ExtractText(result);
        rendered.Should().Contain("frontend",
            "list_scopes must report the `frontend` scope from .sourcegraph.json");
        rendered.Should().Contain("backend",
            "list_scopes must report the `backend` scope from .sourcegraph.json");
    }

    [Fact]
    public async Task PerScope_vocabulary_carries_each_configured_scope_id()
    {
        var multiScopeRoot = ServerHarness.LocateFixture("MultiScope");
        await using var harness = await ServerHarness.StartAsync(WithRoot(multiScopeRoot));

        var experimental = harness.Client.ServerCapabilities?.Experimental;
        // xUnit's Assert.NotNull propagates non-nullness to the static analyzer
        // (annotated with [NotNull]), unlike FluentAssertions's NotBeNull. Subsequent
        // dereferences are then safe without `!` and without tripping CodeQL's
        // "may be null at this access" warning.
        Assert.NotNull(experimental);
        experimental.Should().ContainKey("sourcegraph.vocabulary");

        var raw = experimental["sourcegraph.vocabulary"];
        raw.Should().BeOfType<JsonElement>(
            "Experimental values come through the SDK as JsonElement on the wire");
        var vocab = (JsonElement)raw;
        vocab.ValueKind.Should().Be(JsonValueKind.Object);

        // The wire shape is { edge_kinds, symbol_kinds, annotation_flavors, scopes: { <id>: { ... } } }.
        // We assert the per-scope map carries an entry for every configured scope id, which means
        // an isolated scope's vocabulary stays scope-local (not folded into a single union view).
        vocab.TryGetProperty("scopes", out var scopesProp).Should().BeTrue(
            "vocabulary must carry a `scopes` per-scope map");
        scopesProp.ValueKind.Should().Be(JsonValueKind.Object);

        var scopeIds = scopesProp.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        scopeIds.Should().Contain("frontend",
            "per-scope vocabulary must include the `frontend` scope from .sourcegraph.json");
        scopeIds.Should().Contain("backend",
            "per-scope vocabulary must include the `backend` scope from .sourcegraph.json");

        // Each per-scope entry must shape-match the top-level vocabulary: the same three arrays.
        // We don't assert content equality because each scope's distinct kinds may differ — only
        // that the structure is right (so an isolated scope returns its own vocabulary, not the
        // server-wide union).
        foreach (var scopeId in new[] { "frontend", "backend" })
        {
            var entry = scopesProp.GetProperty(scopeId);
            entry.ValueKind.Should().Be(JsonValueKind.Object);
            entry.TryGetProperty("edge_kinds", out var ek).Should().BeTrue(
                $"scope `{scopeId}` must have edge_kinds");
            ek.ValueKind.Should().Be(JsonValueKind.Array);
            entry.TryGetProperty("symbol_kinds", out var sk).Should().BeTrue(
                $"scope `{scopeId}` must have symbol_kinds");
            sk.ValueKind.Should().Be(JsonValueKind.Array);
            entry.TryGetProperty("annotation_flavors", out var af).Should().BeTrue(
                $"scope `{scopeId}` must have annotation_flavors");
            af.ValueKind.Should().Be(JsonValueKind.Array);
        }
    }

    /// <summary>
    /// A <see cref="CallToolResult"/> carries content blocks (text, image, etc.). The graph
    /// tools render markdown text, so we concatenate every text block we got back. Other block
    /// types are ignored; the production tools only ever emit text.
    /// </summary>
    private static string ExtractText(CallToolResult result)
    {
        if (result.Content is null) return string.Empty;
        var parts = result.Content.OfType<TextContentBlock>().Select(block => block.Text);
        return string.Join(Environment.NewLine, parts);
    }
}
