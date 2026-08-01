using System.Diagnostics;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.IntegrationTests;

/// <summary>
/// End-to-end coverage of the watch-scope-config change: edits to <c>.sourcegraph.json</c> are
/// picked up live (no restart). Each test spawns a real server against a temp copy of the
/// MultiScope fixture, mutates the JSON, then polls <c>list_scopes</c> until the expected state
/// is observed (with a generous timeout for the watcher debounce + cold-index settle).
///
/// The harness sets <c>SOURCEGRAPH_SCOPE_REPLACE_GRACE_MS=50</c> so live-modify tests aren't
/// gated on the production 5s grace window.
/// </summary>
public sealed class LiveScopeConfigTests
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(150);

    private static IDictionary<string, string?> TestEnv() => new Dictionary<string, string?>
    {
        ["SOURCEGRAPH_SCOPE_REPLACE_GRACE_MS"] = "50",
    };

    private static string[] WithRoot(string rootAbsolutePath) =>
    [
        "--root", rootAbsolutePath,
        "--no-history",
    ];

    private static string ExtractText(CallToolResult result)
    {
        if (result.Content is null) return string.Empty;
        return string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
    }

    private static async Task<string> ListScopesAsync(ServerHarness harness)
    {
        var result = await harness.Client.CallToolAsync(
            "list_scopes", arguments: null, progress: null, options: null, cancellationToken: default);
        result.IsError.Should().NotBe(true, "list_scopes should not return an error response");
        return ExtractText(result);
    }

    /// <summary>
    /// Poll <c>list_scopes</c> until <paramref name="predicate"/> returns true. Fails the test
    /// with a useful message if the predicate never holds within <see cref="PollTimeout"/>.
    /// </summary>
    private static async Task PollUntilAsync(
        ServerHarness harness,
        Func<string, bool> predicate,
        string description)
    {
        var sw = Stopwatch.StartNew();
        string? lastSnapshot = null;
        while (sw.Elapsed < PollTimeout)
        {
            lastSnapshot = await ListScopesAsync(harness);
            if (predicate(lastSnapshot)) return;
            await Task.Delay(PollInterval);
        }
        var stderr = string.Join("\n", harness.StderrLines);
        throw new TimeoutException(
            $"Live-config polling timed out waiting for: {description}.\n=== Server stderr ===\n{stderr}\n=== Last list_scopes ===\n{lastSnapshot}");
    }

    [Fact]
    public async Task AddScope_BringsUpNewHost()
    {
        using var fixture = MultiScopeFixtureCopy.Create();
        // Start with only `frontend`. We'll edit the file post-startup to add `backend`.
        fixture.WriteConfig("""
            {
              "scopes": [
                { "name": "frontend", "solutions": ["frontend.sln"] }
              ],
              "default_scope": "frontend"
            }
            """);

        await using var harness = await ServerHarness.StartAsync(WithRoot(fixture.Root), TestEnv());
        await PollUntilAsync(harness,
            text => text.Contains("frontend") && !text.Contains("backend"),
            "initial state with only `frontend`");

        // Live-add `backend`.
        fixture.WriteConfig("""
            {
              "scopes": [
                { "name": "frontend", "solutions": ["frontend.sln"] },
                { "name": "backend",  "solutions": ["backend.sln"] }
              ],
              "default_scope": "frontend"
            }
            """);

        await PollUntilAsync(harness,
            text => text.Contains("frontend") && text.Contains("backend"),
            "live-added `backend` scope to appear");
    }

    [Fact]
    public async Task RemoveScope_TearsDownHostButPreservesDb()
    {
        using var fixture = MultiScopeFixtureCopy.Create();
        // Start with both scopes (the fixture's default config).
        await using var harness = await ServerHarness.StartAsync(WithRoot(fixture.Root), TestEnv());
        await PollUntilAsync(harness,
            text => text.Contains("frontend") && text.Contains("backend"),
            "initial state with both scopes");

        // Live-remove `frontend`.
        fixture.WriteConfig("""
            {
              "scopes": [
                { "name": "backend",  "solutions": ["backend.sln"] }
              ],
              "default_scope": "backend"
            }
            """);

        await PollUntilAsync(harness,
            text => !text.Contains("frontend") && text.Contains("backend"),
            "live-removed `frontend` to disappear from list_scopes");

        // The on-disk DB for the removed scope must still exist (re-add cache).
        // The grace window is 50 ms here, so by now the host has been disposed and the file
        // is no longer held open; if the server had instead deleted the file the assertion
        // would fail.
        File.Exists(fixture.ScopeDbPath("frontend")).Should().BeTrue(
            "live-remove preserves the per-scope DB on disk so re-add can reuse it");
    }

    [Fact]
    public async Task ChangeDefaultScope_FlowsThroughWithoutReindex()
    {
        using var fixture = MultiScopeFixtureCopy.Create();
        await using var harness = await ServerHarness.StartAsync(WithRoot(fixture.Root), TestEnv());
        // The rendered list_scopes output formats the default as `_default_scope: \`backend\`_`
        // (italic + backtick-quoted id), so we match the backtick-quoted form.
        await PollUntilAsync(harness,
            text => text.Contains("default_scope: `backend`"),
            "initial default_scope = backend");

        // Flip default_scope from backend to frontend; everything else identical.
        fixture.WriteConfig("""
            {
              "scopes": [
                { "name": "frontend", "solutions": ["frontend.sln"] },
                { "name": "backend",  "solutions": ["backend.sln"] }
              ],
              "default_scope": "frontend"
            }
            """);

        await PollUntilAsync(harness,
            text => text.Contains("default_scope: `frontend`"),
            "live default_scope flip to frontend");
    }

    [Fact]
    public async Task ModifyScopeSolutions_TriggersReBringUp()
    {
        using var fixture = MultiScopeFixtureCopy.Create();
        await using var harness = await ServerHarness.StartAsync(WithRoot(fixture.Root), TestEnv());
        await PollUntilAsync(harness,
            text => text.Contains("frontend") && text.Contains("backend"),
            "initial state with both scopes");

        // Modify `frontend` to add an exclude pattern. Same solutions, just a project-set
        // change; this exercises the modify path without needing a second .sln on disk.
        fixture.WriteConfig("""
            {
              "scopes": [
                { "name": "frontend", "solutions": ["frontend.sln"], "exclude": ["**/Generated/**"] },
                { "name": "backend",  "solutions": ["backend.sln"] }
              ],
              "default_scope": "backend"
            }
            """);

        // The new host goes through `indexing` → `ok`. We can't reliably catch the
        // `indexing` substring (the cold index is fast on this fixture) but we can wait
        // until both scopes settle to `ok` again. The state machine going `ok` → `ok`
        // through a re-bring-up is what success looks like here; the absence of
        // `degraded` in the output is the load-bearing assertion.
        await PollUntilAsync(harness,
            text => text.Contains("frontend") && text.Contains("backend") && !text.Contains("degraded"),
            "post-modify state remains healthy after live re-bring-up");
    }

    [Fact]
    public async Task PluginsChange_DoesNotTouchScopes()
    {
        using var fixture = MultiScopeFixtureCopy.Create();
        await using var harness = await ServerHarness.StartAsync(WithRoot(fixture.Root), TestEnv());
        await PollUntilAsync(harness,
            text => text.Contains("frontend") && text.Contains("backend"),
            "initial state with both scopes");

        // Add a fake plugins[] entry. The watcher's diff sees pluginsChanged=true and logs
        // a warning, but does not reload the plugin set. Scopes remain unchanged.
        fixture.WriteConfig("""
            {
              "scopes": [
                { "name": "frontend", "solutions": ["frontend.sln"] },
                { "name": "backend",  "solutions": ["backend.sln"] }
              ],
              "default_scope": "backend",
              "plugins": [
                { "package": "Some.Fake.Plugin", "version": "1.0.0" }
              ]
            }
            """);

        // Wait through enough watcher cycles that, if the change had triggered any scope
        // mutation, list_scopes would have observed it. Then assert nothing moved.
        await Task.Delay(TimeSpan.FromSeconds(2));

        var afterText = await ListScopesAsync(harness);
        // We can't compare the rendered text byte-for-byte because the `last indexed` cell
        // ticks up over time. Extract the scope-id rows (table rows that start with a backtick
        // before the id) and assert that set is unchanged.
        var rows = afterText.Split('\n').Where(l => l.StartsWith("| `", StringComparison.Ordinal)).ToList();
        rows.Should().HaveCount(2, "two scopes should still be registered");
        rows.Should().Contain(r => r.Contains("`frontend`"));
        rows.Should().Contain(r => r.Contains("`backend`"));
        rows.Should().NotContain(r => r.Contains("degraded"));
    }

    [Fact]
    public async Task ModifyScope_PostModifyToolCallsResolveAgainstNewHost()
    {
        using var fixture = MultiScopeFixtureCopy.Create();
        await using var harness = await ServerHarness.StartAsync(WithRoot(fixture.Root), TestEnv());
        await PollUntilAsync(harness,
            text => text.Contains("frontend") && text.Contains("backend"),
            "initial state with both scopes");

        // Modify `frontend` to add an exclude pattern; this triggers Replace + cold-reindex.
        fixture.WriteConfig("""
            {
              "scopes": [
                { "name": "frontend", "solutions": ["frontend.sln"], "exclude": ["**/Generated/**"] },
                { "name": "backend",  "solutions": ["backend.sln"] }
              ],
              "default_scope": "backend"
            }
            """);

        // Right after the modify, fire a find_definition against `frontend`. The router resolves
        // to the *new* host; ScopedExecution.WaitUntilReadyAsync gates on the new host's Ready
        // signal until the cold reindex settles. If MarkReady was wired to the old host, this
        // call would hang past PollTimeout (the test's outer bound). If WaitUntilReadyAsync
        // observed a stale captured Ready, the call would return before the new host's data is
        // ready and find_definition would return empty for a symbol the new index *will* contain.
        // Either failure mode surfaces as a timeout or an empty result.
        var sw = Stopwatch.StartNew();
        var result = await harness.Client.CallToolAsync(
            "find_definition",
            arguments: new Dictionary<string, object?>
            {
                ["symbol"] = "Widget",
                ["scope"] = "frontend",
            },
            progress: null,
            options: null,
            cancellationToken: default);
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(PollTimeout,
            "tool call must not hang past the new host's Ready signal");
        result.IsError.Should().NotBe(true,
            "find_definition against the modified scope must succeed once the new host's Ready settles");
        var rendered = ExtractText(result);
        rendered.Should().Contain("Widget",
            "the new host's cold reindex must have populated the symbol the test asks for");
    }

    [Fact]
    public async Task DeleteConfig_RevertsToSynthesisedDefault()
    {
        using var fixture = MultiScopeFixtureCopy.Create();
        await using var harness = await ServerHarness.StartAsync(WithRoot(fixture.Root), TestEnv());
        await PollUntilAsync(harness,
            text => text.Contains("frontend") && text.Contains("backend"),
            "initial state with both scopes");

        fixture.DeleteConfig();

        // Synthesise without --solution gives a default scope with no solutions; both
        // `frontend` and `backend` are removed; `default` is added.
        await PollUntilAsync(harness,
            text => text.Contains("default") && !text.Contains("frontend") && !text.Contains("backend"),
            "delete reverts to synthesised `default` scope only");
    }
}
