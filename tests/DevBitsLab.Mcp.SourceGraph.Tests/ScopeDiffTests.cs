using System;
using System.Collections.Generic;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Covers the pure diff helper consumed by <c>LiveIndexService.OnConfigChangedAsync</c>. Every
/// scenario corresponds to a delta kind the live-watch path reacts to (add / remove / modify /
/// default-scope / plugins) plus the no-op same-config case.
/// </summary>
public sealed class ScopeDiffTests
{
    private static Scope SolutionsScope(string id, params string[] solutions) =>
        new(
            Id: id,
            Name: id,
            Root: "/repo",
            ProjectSet: new ScopeProjectSet.Solutions(solutions, Array.Empty<string>()),
            Isolated: false,
            LastIndexedAt: DateTimeOffset.MinValue);

    [Fact]
    public void NoChange_returnsEmptyDiff()
    {
        var a = SolutionsScope("foo", "foo.sln");
        var diff = ScopeDiff.Compute(
            currentScopes: new[] { a },
            newScopes: new[] { a },
            currentDefaultScope: "foo",
            newDefaultScope: "foo",
            currentPlugins: Array.Empty<PluginRef>(),
            newPlugins: Array.Empty<PluginRef>());
        diff.HasAny.Should().BeFalse();
        diff.Added.Should().BeEmpty();
        diff.Removed.Should().BeEmpty();
        diff.Modified.Should().BeEmpty();
        diff.DefaultScopeChanged.Should().BeFalse();
        diff.PluginsChanged.Should().BeFalse();
    }

    [Fact]
    public void AddedScope_appearsInAdded()
    {
        var foo = SolutionsScope("foo", "foo.sln");
        var bar = SolutionsScope("bar", "bar.sln");
        var diff = ScopeDiff.Compute(
            currentScopes: new[] { foo },
            newScopes: new[] { foo, bar },
            currentDefaultScope: null, newDefaultScope: null,
            currentPlugins: Array.Empty<PluginRef>(), newPlugins: Array.Empty<PluginRef>());
        diff.Added.Should().ContainSingle().Which.Id.Should().Be("bar");
        diff.Removed.Should().BeEmpty();
        diff.Modified.Should().BeEmpty();
    }

    [Fact]
    public void RemovedScope_appearsInRemoved()
    {
        var foo = SolutionsScope("foo", "foo.sln");
        var bar = SolutionsScope("bar", "bar.sln");
        var diff = ScopeDiff.Compute(
            currentScopes: new[] { foo, bar },
            newScopes: new[] { foo },
            currentDefaultScope: null, newDefaultScope: null,
            currentPlugins: Array.Empty<PluginRef>(), newPlugins: Array.Empty<PluginRef>());
        diff.Added.Should().BeEmpty();
        diff.Removed.Should().ContainSingle().Which.Id.Should().Be("bar");
        diff.Modified.Should().BeEmpty();
    }

    [Fact]
    public void ChangedSolutions_appearsInModified()
    {
        var oldFoo = SolutionsScope("foo", "old.sln");
        var newFoo = SolutionsScope("foo", "new.sln");
        var diff = ScopeDiff.Compute(
            currentScopes: new[] { oldFoo },
            newScopes: new[] { newFoo },
            currentDefaultScope: null, newDefaultScope: null,
            currentPlugins: Array.Empty<PluginRef>(), newPlugins: Array.Empty<PluginRef>());
        diff.Modified.Should().ContainSingle();
        diff.Modified[0].Old.Should().BeSameAs(oldFoo);
        diff.Modified[0].New.Should().BeSameAs(newFoo);
    }

    [Fact]
    public void IsolatedFlagFlip_appearsInModified()
    {
        var unsealed = SolutionsScope("foo", "foo.sln");
        var sealedScope = unsealed with { Isolated = true };
        var diff = ScopeDiff.Compute(
            currentScopes: new[] { unsealed },
            newScopes: new[] { sealedScope },
            currentDefaultScope: null, newDefaultScope: null,
            currentPlugins: Array.Empty<PluginRef>(), newPlugins: Array.Empty<PluginRef>());
        diff.Modified.Should().ContainSingle().Which.New.Isolated.Should().BeTrue();
    }

    [Fact]
    public void ExcludeOnlyChange_appearsInModified()
    {
        var withoutEx = SolutionsScope("foo", "foo.sln");
        var withEx = withoutEx with
        {
            ProjectSet = new ScopeProjectSet.Solutions(new[] { "foo.sln" }, new[] { "**/Generated/**" }),
        };
        var diff = ScopeDiff.Compute(
            currentScopes: new[] { withoutEx },
            newScopes: new[] { withEx },
            currentDefaultScope: null, newDefaultScope: null,
            currentPlugins: Array.Empty<PluginRef>(), newPlugins: Array.Empty<PluginRef>());
        diff.Modified.Should().ContainSingle();
    }

    [Fact]
    public void DefaultScopeFlip_setsDefaultChanged()
    {
        var foo = SolutionsScope("foo", "foo.sln");
        var diff = ScopeDiff.Compute(
            currentScopes: new[] { foo },
            newScopes: new[] { foo },
            currentDefaultScope: "foo", newDefaultScope: null,
            currentPlugins: Array.Empty<PluginRef>(), newPlugins: Array.Empty<PluginRef>());
        diff.DefaultScopeChanged.Should().BeTrue();
        diff.Added.Should().BeEmpty();
        diff.Modified.Should().BeEmpty();
    }

    [Fact]
    public void PluginsAdded_setsPluginsChanged()
    {
        var foo = SolutionsScope("foo", "foo.sln");
        var diff = ScopeDiff.Compute(
            currentScopes: new[] { foo },
            newScopes: new[] { foo },
            currentDefaultScope: null, newDefaultScope: null,
            currentPlugins: Array.Empty<PluginRef>(),
            newPlugins: new[] { new PluginRef("Some.Plugin", "1.0.0", null) });
        diff.PluginsChanged.Should().BeTrue();
    }

    [Fact]
    public void PluginsReorder_isStillAChange()
    {
        // Order matters in the plugins[] array because it controls load order. Reordering counts
        // as a change so the warning surfaces.
        var foo = SolutionsScope("foo", "foo.sln");
        var a = new PluginRef("A", "1.0.0", null);
        var b = new PluginRef("B", "1.0.0", null);
        var diff = ScopeDiff.Compute(
            currentScopes: new[] { foo }, newScopes: new[] { foo },
            currentDefaultScope: null, newDefaultScope: null,
            currentPlugins: new[] { a, b }, newPlugins: new[] { b, a });
        diff.PluginsChanged.Should().BeTrue();
    }

    [Fact]
    public void Summary_describesDeltas()
    {
        var foo = SolutionsScope("foo", "foo.sln");
        var bar = SolutionsScope("bar", "bar.sln");
        var diff = ScopeDiff.Compute(
            currentScopes: new[] { foo },
            newScopes: new[] { bar },
            currentDefaultScope: "foo", newDefaultScope: "bar",
            currentPlugins: Array.Empty<PluginRef>(), newPlugins: Array.Empty<PluginRef>());
        diff.HasAny.Should().BeTrue();
        var summary = diff.Summary();
        summary.Should().Contain("+bar");
        summary.Should().Contain("-foo");
        summary.Should().Contain("default-scope");
    }
}
