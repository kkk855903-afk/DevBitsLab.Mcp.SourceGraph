using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Storage;
using DevBitsLab.Mcp.SourceGraph.Watcher;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Covers the file-watch + parse-tolerance contract of <see cref="ScopeConfigWatcher"/>:
///   - parse-tolerance on malformed JSON (no event emitted)
///   - file deletion → revert to synthesised default
///   - present and parseable → Updated event with the parsed config
/// </summary>
public sealed class ScopeConfigWatcherTests
{
    private static readonly TimeSpan ShortDebounce = TimeSpan.FromMilliseconds(60);

    private static string MakeTempRoot()
    {
        var tmp = Path.Join(Path.GetTempPath(), "scope-watcher-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        return tmp;
    }

    private static async Task<ScopeConfigChange?> ReadOneWithTimeoutAsync(
        ScopeConfigWatcher watcher, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await foreach (var change in watcher.ReadAllAsync(cts.Token))
            {
                return change;
            }
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Read events until one matching <paramref name="predicate"/> appears, or the timeout
    /// fires. Used by tests that need to look past the watcher's synthetic-init emit (which
    /// fires unconditionally on the first poll iteration so the diff-and-apply path catches a
    /// race with cold-index startup).
    /// </summary>
    private static async Task<ScopeConfigChange?> ReadUntilAsync(
        ScopeConfigWatcher watcher, Func<ScopeConfigChange, bool> predicate, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await foreach (var change in watcher.ReadAllAsync(cts.Token))
            {
                if (predicate(change)) return change;
            }
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    [Fact]
    public async Task ValidSave_emitsUpdated()
    {
        var root = MakeTempRoot();
        try
        {
            await using var watcher = new ScopeConfigWatcher(root, debounce: ShortDebounce);
            File.WriteAllText(
                Path.Join(root, ScopeConfigLoader.FileName),
                """{ "scopes": [ { "name": "foo", "solutions": ["foo.sln"] } ], "default_scope": "foo" }""");

            // The watcher emits a synthetic Reverted on its first poll (file didn't exist when
            // the watcher started); we look past that for the post-write Updated event.
            var change = await ReadUntilAsync(
                watcher,
                c => c is ScopeConfigChange.Updated,
                TimeSpan.FromSeconds(2));
            change.Should().NotBeNull();
            change!.Config.Scopes.Should().ContainSingle().Which.Id.Should().Be("foo");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task MalformedSave_doesNotEmit()
    {
        var root = MakeTempRoot();
        try
        {
            await using var watcher = new ScopeConfigWatcher(root, debounce: ShortDebounce);
            // Drain the synthetic-init Reverted (the file didn't exist when the watcher started).
            var initial = await ReadOneWithTimeoutAsync(watcher, TimeSpan.FromSeconds(2));
            initial.Should().BeOfType<ScopeConfigChange.Reverted>();

            File.WriteAllText(Path.Join(root, ScopeConfigLoader.FileName), "{ this is not json");
            var change = await ReadOneWithTimeoutAsync(watcher, TimeSpan.FromSeconds(1));
            // Parse fails → log info, no event. The timeout path is the test pass.
            change.Should().BeNull();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task FileDeletedAfterValidSave_emitsReverted()
    {
        var root = MakeTempRoot();
        try
        {
            File.WriteAllText(
                Path.Join(root, ScopeConfigLoader.FileName),
                """{ "scopes": [ { "name": "foo", "solutions": ["foo.sln"] } ] }""");

            await using var watcher = new ScopeConfigWatcher(
                root,
                discoveredSolutions: Array.Empty<string>(),
                debounce: ShortDebounce);

            // The watcher polls on each tick and emits a synthetic event on the first iteration
            // (an Updated, since the file is present at startup). We drain past that and look
            // specifically for the post-deletion Reverted event.
            File.Delete(Path.Join(root, ScopeConfigLoader.FileName));
            var change = await ReadUntilAsync(
                watcher,
                c => c is ScopeConfigChange.Reverted,
                TimeSpan.FromSeconds(3));
            change.Should().NotBeNull();
            change!.Config.Scopes.Should().ContainSingle().Which.Id.Should().Be("default");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task DisposeAsync_completesQuickly()
    {
        var root = MakeTempRoot();
        try
        {
            var watcher = new ScopeConfigWatcher(root, debounce: ShortDebounce);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await watcher.DisposeAsync();
            sw.Stop();
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2),
                "dispose should not hang waiting for events that won't come");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort cleanup */ }
        }
    }
}
