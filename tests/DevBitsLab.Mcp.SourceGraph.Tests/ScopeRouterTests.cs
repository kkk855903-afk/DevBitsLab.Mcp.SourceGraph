using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Covers the router primitives added by the watch-scope-config change:
/// <see cref="ScopeRouter.Unregister"/> for the live-remove path and <see cref="ScopeRouter.Replace"/>
/// for the live-modify path. The atomic-swap test is the most load-bearing assertion: under
/// concurrent <see cref="ScopeRouter.TryGet"/> callers, every observation must see either the old
/// host or the new one — never null and never a transient empty slot.
/// </summary>
public sealed class ScopeRouterTests
{
    private static ScopeHost FakeHost(string id) =>
        // ScopeHost requires real graph stores etc. in production; the router only inspects
        // host.Scope.Id, so we construct a minimal record-shape via reflection-free passthrough.
        // Use the actual Scope record + null-forgiving for the rest of the constructor — none of
        // the router code paths under test touch those fields.
        new(
            new Scope(id, id, "/repo", new ScopeProjectSet.Solutions(Array.Empty<string>(), Array.Empty<string>()), false, DateTimeOffset.MinValue),
            store: null!,
            embeddingsStore: null!,
            indexer: null!,
            solutionPath: "");

    [Fact]
    public void Unregister_returnsTrueWhenRegistered()
    {
        var router = new ScopeRouter();
        var host = FakeHost("foo");
        router.Register(host);
        router.Unregister("foo").Should().BeTrue();
        router.TryGet("foo", out _).Should().BeFalse();
    }

    [Fact]
    public void Unregister_returnsFalseWhenMissing()
    {
        var router = new ScopeRouter();
        router.Unregister("nope").Should().BeFalse();
    }

    [Fact]
    public void Replace_returnsDisplacedWhenPresent()
    {
        var router = new ScopeRouter();
        var oldHost = FakeHost("foo");
        var newHost = FakeHost("foo");
        router.Register(oldHost);
        var displaced = router.Replace("foo", newHost);
        displaced.Should().BeSameAs(oldHost);
        router.TryGet("foo", out var current).Should().BeTrue();
        current.Should().BeSameAs(newHost);
    }

    [Fact]
    public void Replace_returnsNullWhenAbsentAndStillRegistersNewHost()
    {
        var router = new ScopeRouter();
        var newHost = FakeHost("foo");
        var displaced = router.Replace("foo", newHost);
        displaced.Should().BeNull();
        router.TryGet("foo", out var current).Should().BeTrue();
        current.Should().BeSameAs(newHost);
    }

    [Fact]
    public async Task Replace_isAtomicUnderConcurrentTryGet()
    {
        var router = new ScopeRouter();
        var oldHost = FakeHost("foo");
        var newHost = FakeHost("foo");
        router.Register(oldHost);

        // Track only the invariants we care about — boolean flags + counts — instead of every
        // observation. The previous version appended to a List in a tight loop with no bound,
        // which on a slow CI runner could allocate gigabytes before `stop` fires.
        var sawNull = false;
        var sawUnexpected = false;
        var observationCount = 0L;
        using var stop = new CancellationTokenSource();

        var observer = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (!router.TryGet("foo", out var host))
                {
                    sawNull = true;
                }
                else if (!ReferenceEquals(host, oldHost) && !ReferenceEquals(host, newHost))
                {
                    sawUnexpected = true;
                }
                Interlocked.Increment(ref observationCount);
            }
        });

        // Let the observer warm up, then issue many replaces back-to-back.
        await Task.Delay(20);
        for (var i = 0; i < 1000; i++)
        {
            router.Replace("foo", newHost);
            router.Replace("foo", oldHost);
        }
        await Task.Delay(20);
        stop.Cancel();
        await observer;

        observationCount.Should().BeGreaterThan(0, "the observer task must have run at least once");
        sawNull.Should().BeFalse("concurrent TryGet during Replace must never observe an empty slot");
        sawUnexpected.Should().BeFalse("concurrent TryGet must never observe a host other than the two we registered");
    }
}
