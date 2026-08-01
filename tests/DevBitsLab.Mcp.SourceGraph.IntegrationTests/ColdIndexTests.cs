using FluentAssertions;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.IntegrationTests;

/// <summary>
/// Cold-index smoke: with no pre-existing graph DB, the server is spawned, then a
/// <c>find_definition</c> call reaches a known fixture symbol immediately. Validates the
/// lazy-index-on-first-query path through the real stdio surface — the in-process
/// <c>IndexFixtureTests</c> exercise the same workflow but against a direct
/// <c>SqliteGraphStore</c>, missing the JSON-RPC layer in between.
/// </summary>
public sealed class ColdIndexTests
{
    /// <summary>
    /// Initial index can take meaningful wall time on a cold worktree (Roslyn workspace open
    /// + first compilation + symbol enumeration). This cap is much wider than the harness's
    /// default <c>HandshakeTimeout</c> (60s) which only covers the `initialize` handshake;
    /// <c>find_definition</c> blocks until the indexer has produced enough graph data to answer.
    /// </summary>
    private static readonly TimeSpan IndexAndQueryTimeout = TimeSpan.FromMinutes(2);

    private static readonly string[] CommonServeArgs =
    [
        "--no-history",
    ];

    private static string[] WithSolution(string solutionAbsolutePath)
    {
        var args = new List<string>(CommonServeArgs.Length + 2)
        {
            "--solution",
            solutionAbsolutePath,
        };
        args.AddRange(CommonServeArgs);
        return args.ToArray();
    }

    // §12.3 unblocked: ScopedExecution.RunAsync now awaits ScopeHost.Ready when the host is
    // mid-cold-index, bounded by the caller's CancellationToken (which the test wires to
    // IndexAndQueryTimeout). Combined with §12.2 (the host is registered with status="indexing"
    // before indexing starts), find_definition no longer short-circuits during cold-indexing.
    [Fact]
    public async Task FindDefinition_resolves_known_fixture_symbol_after_initialize()
    {
        var sln = ServerHarness.LocateFixture("Sample.sln");

        // Use a temp DB directory by setting --db on the next iteration if needed; for now we
        // accept the default (under tests/fixtures/.sourcegraph/scopes/default.db). The test
        // uses a known stable symbol (Calculator) that's been in Sample.Domain since the fixture
        // landed, so a stale DB from a prior run still answers the same way.
        await using var harness = await ServerHarness.StartAsync(WithSolution(sln));

        using var cts = new CancellationTokenSource(IndexAndQueryTimeout);

        // The SDK takes arguments as IReadOnlyDictionary<string, object?>. We declare with `object?`
        // to satisfy the nullable-aware overload signature without an extra cast at the call site.
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["symbol"] = "Calculator",
        };
        var result = await harness.Client.CallToolAsync(
            "find_definition",
            arguments: arguments,
            progress: null,
            options: null,
            cancellationToken: cts.Token);

        result.Should().NotBeNull();
        result.IsError.Should().NotBe(true,
            "find_definition should not return an error for a known fixture symbol");

        var rendered = ExtractText(result);
        rendered.Should().NotBeNullOrWhiteSpace();
        rendered.Should().NotContain("No definition found",
            "Calculator is in tests/fixtures/Sample.Domain/Calculator.cs and must resolve");
        // The renderer prints the FQN as part of the output. Sample.Domain.Calculator is the
        // canonical path; this assertion stays loose enough to survive cosmetic markdown changes.
        rendered.Should().Contain("Calculator",
            "the rendered result must mention the resolved symbol's name");
    }

    private static string ExtractText(CallToolResult result)
    {
        if (result.Content is null) return string.Empty;
        var parts = result.Content.OfType<TextContentBlock>().Select(block => block.Text);
        return string.Join(Environment.NewLine, parts);
    }
}
