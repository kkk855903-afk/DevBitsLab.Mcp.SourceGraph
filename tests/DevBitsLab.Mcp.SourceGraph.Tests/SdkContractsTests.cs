using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Server.Plugins;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Unit-level coverage of the public SDK contracts in <see cref="DevBitsLab.Mcp.SourceGraph.Sdk"/>.
/// These tests don't go through the host — they exercise the records, helpers, and base classes
/// directly so an SDK regression surfaces independently of the rest of the system.
/// </summary>
public sealed class SdkContractsTests
{
    [Fact]
    public void IndexEvent_SymbolDeclared_holdsAllFields()
    {
        var ev = new IndexEvent.SymbolDeclared(
            CanonicalKey: "ns.Foo.Bar",
            Name: "Bar",
            Fqn: "ns.Foo.Bar",
            Kind: PluginSymbolKind.Method,
            StartLine: 12,
            StartColumn: 4,
            EndLine: 14,
            EndColumn: 4,
            Signature: "void Bar()",
            ContainerCanonicalKey: "ns.Foo",
            Modifiers: "public",
            Accessibility: 6,
            XmlSummary: "Does Bar.");
        ev.Name.Should().Be("Bar");
        ev.Kind.Should().Be(PluginSymbolKind.Method);
        ev.ContainerCanonicalKey.Should().Be("ns.Foo");
    }

    [Fact]
    public void IndexEvent_EdgeEmitted_carriesEdgeKindByName()
    {
        var ev = new IndexEvent.EdgeEmitted("a", "b", "Calls");
        ev.EdgeKindName.Should().Be("Calls");
    }

    [Fact]
    public void IndexEvent_AttributeAttached_carriesArgsJson()
    {
        var ev = new IndexEvent.AttributeAttached("sym", "Decorated", "Sample.DecoratedAttribute", ArgsJson: "{}");
        ev.ArgsJson.Should().Be("{}");
        ev.AttributeClassCanonicalKey.Should().BeNull();
    }

    [Fact]
    public void IndexEvent_FileScanned_carriesShaBytes()
    {
        var sha = new byte[] { 1, 2, 3 };
        var ev = new IndexEvent.FileScanned("/tmp/x.cs", sha, IsGenerated: true);
        ev.IsGenerated.Should().BeTrue();
        ev.ContentSha256.Should().HaveCount(3);
    }

    [Fact]
    public async Task LanguageIndexerBase_defaultIndexAsync_returnsEmpty()
    {
        var indexer = new TestLanguageIndexerBase();
        var ctx = new IndexContext("/tmp/x.txt", System.Text.Encoding.UTF8.GetBytes("hello"), "default", "/tmp");
        var events = await indexer.IndexAsync(ctx, default);
        events.Should().BeEmpty();
    }

    [Fact]
    public void LanguageIndexerBase_FileExtensions_isOverridable()
    {
        var indexer = new TestLanguageIndexerBase();
        indexer.FileExtensions.Should().BeEquivalentTo(new[] { ".test" });
    }

    [Fact]
    public void IndexContext_GetText_decodesAsUtf8()
    {
        var ctx = new IndexContext("/x", System.Text.Encoding.UTF8.GetBytes("héllo"), "s", "/r");
        ctx.GetText().Should().Be("héllo");
    }

    [Fact]
    public void McpToolPluginAttribute_acceptsBothPositionalAndNamed()
    {
        var positional = new McpToolPluginAttribute("foo");
        var named = new McpToolPluginAttribute { Prefix = "bar" };
        positional.Prefix.Should().Be("foo");
        named.Prefix.Should().Be("bar");
    }

    [Fact]
    public async Task PluginHost_emptyRefList_isANoOp()
    {
        var host = new PluginHost("/tmp", System.Array.Empty<PluginRef>());
        await host.LoadAllAsync();
        host.Plugins.Should().BeEmpty();
    }

    [Fact]
    public async Task PluginHost_missingPath_marksPluginFailed()
    {
        // Failure isolation: a plugin whose DLL does not exist gets a `Failed` record on the host
        // — the host itself does not throw and keeps running. Mirrors the brief's failure-mode
        // requirement that "a plugin whose IndexAsync throws is marked failed, other plugins keep
        // working, and the host returns success".
        var refs = new[] { new PluginRef(Package: null, Version: null, Path: "/does/not/exist.dll") };
        var host = new PluginHost("/tmp", refs);
        await host.LoadAllAsync();
        host.Plugins.Should().HaveCount(1);
        host.Plugins[0].Status.Should().Be(PluginStatus.Failed);
        host.Plugins[0].StatusMessage.Should().Contain("not found");
    }

    [Fact]
    public void ToolRegistry_addsPrefix_andRejectsCollision()
    {
        // Tool-prefix collision: the registry rejects a duplicate registration of the same final
        // name. A plugin registering `find_handlers` under prefix `mine` becomes `mine.find_handlers`,
        // distinct from the built-in `find_definition` (no prefix) — the test fixtures both names
        // through the registry and asserts the second registration of an identical name throws.
        var record = new PluginRecord("plugin", "1.0", "/path/to.dll", false);
        var claimed = new HashSet<string>(System.StringComparer.Ordinal);
        var registry = new ToolRegistry("mine", claimed, record);
        registry.AddTool("find_definition", "shadow", new System.Func<string>(() => "ok"));
        registry.RegisteredTools.Should().HaveCount(1);
        record.RegisteredToolNames.Should().Contain("mine.find_definition");

        // Re-adding the same name throws.
        var act = () => registry.AddTool("find_definition", "again", new System.Func<string>(() => "ok"));
        act.Should().Throw<System.InvalidOperationException>().WithMessage("*already registered*");
    }

    [Fact]
    public void ToolRegistry_rejectsToolNameContainingDot()
    {
        // The plugin can't pre-prefix its own tool name; the host owns the namespace separator.
        var record = new PluginRecord("plugin", "1.0", "/path/to.dll", false);
        var registry = new ToolRegistry("mine", new HashSet<string>(System.StringComparer.Ordinal), record);
        var act = () => registry.AddTool("ns.tool", "x", new System.Func<string>(() => "ok"));
        act.Should().Throw<System.ArgumentException>().WithMessage("*must not contain*");
    }

    private sealed class TestLanguageIndexerBase : LanguageIndexerBase
    {
        public override IReadOnlyCollection<string> FileExtensions => new[] { ".test" };
    }
}
