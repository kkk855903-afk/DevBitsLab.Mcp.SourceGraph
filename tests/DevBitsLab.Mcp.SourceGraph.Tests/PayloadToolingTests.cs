using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Storage-level coverage for the payload-aware edge walks introduced by the
/// <c>payload-tooling</c> change. Each test seeds a fresh on-disk SQLite store with
/// <c>binds-path</c> and <c>handles-event</c> edges (carrying realistic payload dictionaries),
/// then drives <see cref="IGraphStore.FindDataBindingsAsync"/> /
/// <see cref="IGraphStore.FindEventHandlersAsync"/> through the documented filter knobs and
/// asserts the (Source, Target, PayloadJson) projection round-trips correctly.
///
/// <para>Tool-layer coverage (markdown rendering, soft-empty notes, scope fan-out) lives in
/// later phases of this change; this file owns the SQL projection contract.</para>
/// </summary>
public sealed class PayloadToolingTests : IAsyncLifetime
{
    private string _dbPath = string.Empty;
    private SqliteGraphStore? _store;

    public async Task InitializeAsync()
    {
        var tmp = Path.Join(Path.GetTempPath(), "sourcegraph-payload-tooling-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        _dbPath = Path.Join(tmp, "graph.db");
        _store = new SqliteGraphStore(_dbPath);
        await _store.EnsureSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        if (_store is not null) await _store.DisposeAsync();
        // Recursive delete clears `graph.db`, the SQLite WAL/SHM sidecars, and the per-test temp
        // directory itself so `sourcegraph-payload-tooling-tests-*` folders don't accumulate
        // under TEMP across many runs (Copilot review #33: PayloadToolingTests.cs:47).
        try
        {
            var dir = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (IOException) { /* best-effort cleanup; WAL/SHM sidecars sometimes linger */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup; ACL drift or readonly bit */ }
    }

    private async Task<long> SeedSymbolAsync(string canonicalKey, string name, string kind = SymbolKinds.Method)
    {
        var fileId = await _store!.UpsertFileAsync(
            path: $"/virtual/{canonicalKey.Replace(':', '_').Replace('/', '_')}.cs",
            contentSha256: new byte[32],
            indexedAt: DateTimeOffset.UtcNow,
            isGenerated: false);

        return await _store.UpsertSymbolAsync(canonicalKey, new Symbol(
            Id: 0,
            Name: name,
            Fqn: $"Sample.{name}",
            Kind: kind,
            FileId: fileId,
            StartLine: 1, StartCol: 1, EndLine: 5, EndCol: 1,
            Signature: $"{kind} {name}",
            ContainerId: null,
            Modifiers: null,
            Accessibility: 6,
            XmlSummary: null,
            TestFramework: null));
    }

    [Fact]
    public async Task FindDataBindings_filtersByMode_returnsOnlyMatchingEdges()
    {
        // Two binds-path edges to the same target — one TwoWay, one OneWay. The mode filter
        // narrows to the matching row; the row's payload column round-trips so the renderer can
        // surface mode/path on its sub-line.
        var srcA = await SeedSymbolAsync("xaml:element:View.xaml#TextBoxA", "TextBoxA", SymbolKinds.Class);
        var srcB = await SeedSymbolAsync("xaml:element:View.xaml#TextBoxB", "TextBoxB", SymbolKinds.Class);
        var target = await SeedSymbolAsync("csharp:P:Sample.UserVm.UserName", "UserName", SymbolKinds.Property);

        await _store!.BulkInsertEdgesAsync(new[]
        {
            new Edge(srcA, target, "binds-path", new Dictionary<string, string>
            {
                [PayloadKeys.Path] = "UserName",
                [PayloadKeys.Mode] = "two-way",
            }),
            new Edge(srcB, target, "binds-path", new Dictionary<string, string>
            {
                [PayloadKeys.Path] = "UserName",
                [PayloadKeys.Mode] = "one-way",
            }),
        });

        var twoWay = await _store.FindDataBindingsAsync(
            targetCanonicalKey: null, sourceCanonicalKey: null,
            pathContains: null, modeExact: "two-way", converterExact: null,
            limit: 50);

        twoWay.Should().HaveCount(1);
        twoWay[0].Source.Id.Should().Be(srcA);
        twoWay[0].Target.Id.Should().Be(target);
        twoWay[0].PayloadJson.Should().Contain("\"mode\":\"two-way\"");
    }

    [Fact]
    public async Task FindDataBindings_filtersByTargetCanonicalKey()
    {
        // Two binds-path edges to two different targets — the targetCanonicalKey filter narrows
        // to one row.
        var src = await SeedSymbolAsync("xaml:element:View.xaml#A", "A", SymbolKinds.Class);
        var tgt1 = await SeedSymbolAsync("csharp:P:Sample.Vm.PropOne", "PropOne", SymbolKinds.Property);
        var tgt2 = await SeedSymbolAsync("csharp:P:Sample.Vm.PropTwo", "PropTwo", SymbolKinds.Property);

        await _store!.BulkInsertEdgesAsync(new[]
        {
            new Edge(src, tgt1, "binds-path", new Dictionary<string, string> { [PayloadKeys.Path] = "PropOne" }),
            new Edge(src, tgt2, "binds-path", new Dictionary<string, string> { [PayloadKeys.Path] = "PropTwo" }),
        });

        var hits = await _store!.FindDataBindingsAsync(
            targetCanonicalKey: "csharp:P:Sample.Vm.PropTwo",
            sourceCanonicalKey: null, pathContains: null, modeExact: null, converterExact: null,
            limit: 50);

        hits.Should().HaveCount(1);
        hits[0].Target.CanonicalKey.Should().Be("csharp:P:Sample.Vm.PropTwo");
    }

    [Fact]
    public async Task FindDataBindings_filtersByPathSubstring()
    {
        // path uses INSTR — a "User." substring should match "User.Name" and "User.Email" but
        // not "OrderTotal".
        var src = await SeedSymbolAsync("xaml:element:View.xaml#Element", "Element", SymbolKinds.Class);
        var tgtName = await SeedSymbolAsync("csharp:P:Sample.Vm.User.Name", "Name", SymbolKinds.Property);
        var tgtEmail = await SeedSymbolAsync("csharp:P:Sample.Vm.User.Email", "Email", SymbolKinds.Property);
        var tgtOrder = await SeedSymbolAsync("csharp:P:Sample.Vm.OrderTotal", "OrderTotal", SymbolKinds.Property);

        await _store!.BulkInsertEdgesAsync(new[]
        {
            new Edge(src, tgtName, "binds-path", new Dictionary<string, string> { [PayloadKeys.Path] = "User.Name" }),
            new Edge(src, tgtEmail, "binds-path", new Dictionary<string, string> { [PayloadKeys.Path] = "User.Email" }),
            new Edge(src, tgtOrder, "binds-path", new Dictionary<string, string> { [PayloadKeys.Path] = "OrderTotal" }),
        });

        var hits = await _store!.FindDataBindingsAsync(
            targetCanonicalKey: null, sourceCanonicalKey: null,
            pathContains: "User.", modeExact: null, converterExact: null,
            limit: 50);

        hits.Should().HaveCount(2);
        hits.Should().OnlyContain(h => h.PayloadJson != null && h.PayloadJson.Contains("User."));
    }

    [Fact]
    public async Task FindDataBindings_filtersByConverterExact()
    {
        var src = await SeedSymbolAsync("xaml:element:View.xaml#Element", "Element", SymbolKinds.Class);
        var t1 = await SeedSymbolAsync("csharp:P:Sample.Vm.IsVisible", "IsVisible", SymbolKinds.Property);
        var t2 = await SeedSymbolAsync("csharp:P:Sample.Vm.Other", "Other", SymbolKinds.Property);

        await _store!.BulkInsertEdgesAsync(new[]
        {
            new Edge(src, t1, "binds-path", new Dictionary<string, string>
            {
                [PayloadKeys.Path] = "IsVisible",
                [PayloadKeys.Converter] = "BoolToVisibility",
            }),
            new Edge(src, t2, "binds-path", new Dictionary<string, string>
            {
                [PayloadKeys.Path] = "Other",
            }),
        });

        var hits = await _store!.FindDataBindingsAsync(
            targetCanonicalKey: null, sourceCanonicalKey: null, pathContains: null,
            modeExact: null, converterExact: "BoolToVisibility",
            limit: 50);

        hits.Should().HaveCount(1);
        hits[0].Target.Id.Should().Be(t1);
    }

    [Fact]
    public async Task FindDataBindings_unfiltered_returnsAllBindsPathRows_withinLimit()
    {
        // The "all filters null" case is documented as "first `limit` rows, with a note".
        // The note belongs to the tool layer; storage just returns the rows.
        var src = await SeedSymbolAsync("xaml:element:View.xaml#X", "X", SymbolKinds.Class);
        var tgt = await SeedSymbolAsync("csharp:P:Sample.Vm.X", "X", SymbolKinds.Property);
        await _store!.BulkInsertEdgesAsync(new[]
        {
            new Edge(src, tgt, "binds-path", new Dictionary<string, string> { [PayloadKeys.Path] = "X" }),
        });

        var hits = await _store!.FindDataBindingsAsync(
            targetCanonicalKey: null, sourceCanonicalKey: null,
            pathContains: null, modeExact: null, converterExact: null,
            limit: 10);

        hits.Should().ContainSingle();
    }

    [Fact]
    public async Task FindDataBindings_ignoresNonBindsPathEdges()
    {
        // A handles-event edge should never appear in find_data_bindings — the kind clause is
        // pinned to binds-path.
        var src = await SeedSymbolAsync("xaml:element:View.xaml#Btn", "Btn", SymbolKinds.Class);
        var handler = await SeedSymbolAsync("csharp:M:Sample.Vw.OnSave", "OnSave", SymbolKinds.Method);
        await _store!.BulkInsertEdgesAsync(new[]
        {
            new Edge(src, handler, "handles-event", new Dictionary<string, string> { [PayloadKeys.Event] = "Click" }),
        });

        var hits = await _store!.FindDataBindingsAsync(
            targetCanonicalKey: null, sourceCanonicalKey: null,
            pathContains: null, modeExact: null, converterExact: null,
            limit: 50);

        hits.Should().BeEmpty();
    }

    [Fact]
    public async Task FindEventHandlers_filtersByEventName()
    {
        // Two handles-event edges from the same element to two handlers — Click + Hover. The
        // event filter narrows.
        var btn = await SeedSymbolAsync("xaml:element:View.xaml#SaveBtn", "SaveBtn", SymbolKinds.Class);
        var onSave = await SeedSymbolAsync("csharp:M:Sample.Vw.OnSave", "OnSave", SymbolKinds.Method);
        var onHover = await SeedSymbolAsync("csharp:M:Sample.Vw.OnHover", "OnHover", SymbolKinds.Method);

        await _store!.BulkInsertEdgesAsync(new[]
        {
            new Edge(btn, onSave, "handles-event", new Dictionary<string, string> { [PayloadKeys.Event] = "Click" }),
            new Edge(btn, onHover, "handles-event", new Dictionary<string, string> { [PayloadKeys.Event] = "MouseEnter" }),
        });

        var hits = await _store!.FindEventHandlersAsync(
            handlerCanonicalKey: null, eventExact: "Click",
            elementCanonicalKey: null, commandExact: null,
            limit: 50);

        hits.Should().HaveCount(1);
        hits[0].Source.Id.Should().Be(btn);
        hits[0].Target.Id.Should().Be(onSave);
        hits[0].PayloadJson.Should().Contain("\"event\":\"Click\"");
    }

    [Fact]
    public async Task FindEventHandlers_filtersByHandlerCanonicalKey()
    {
        // Multiple buttons all wired to the same OnSave handler — the handler filter returns
        // every wiring.
        var save1 = await SeedSymbolAsync("xaml:element:View.xaml#SaveBtn1", "SaveBtn1", SymbolKinds.Class);
        var save2 = await SeedSymbolAsync("xaml:element:View.xaml#SaveBtn2", "SaveBtn2", SymbolKinds.Class);
        var onSave = await SeedSymbolAsync("csharp:M:Sample.Vw.OnSave", "OnSave", SymbolKinds.Method);

        await _store!.BulkInsertEdgesAsync(new[]
        {
            new Edge(save1, onSave, "handles-event", new Dictionary<string, string> { [PayloadKeys.Event] = "Click" }),
            new Edge(save2, onSave, "handles-event", new Dictionary<string, string> { [PayloadKeys.Event] = "Click" }),
        });

        var hits = await _store!.FindEventHandlersAsync(
            handlerCanonicalKey: "csharp:M:Sample.Vw.OnSave",
            eventExact: null, elementCanonicalKey: null, commandExact: null,
            limit: 50);

        hits.Should().HaveCount(2);
        hits.Should().OnlyContain(h => h.Target.CanonicalKey == "csharp:M:Sample.Vw.OnSave");
    }

    [Fact]
    public async Task FindEventHandlers_filtersByCommandPayload()
    {
        // command is a forward-looking payload knob (not in PayloadKeys yet); pass the literal
        // kebab key to mirror what a downstream indexer would emit.
        var btn = await SeedSymbolAsync("xaml:element:View.xaml#SaveBtn", "SaveBtn", SymbolKinds.Class);
        var cmdHandler = await SeedSymbolAsync("csharp:M:Sample.Vw.SaveCommand", "SaveCommand", SymbolKinds.Method);
        var nonCmd = await SeedSymbolAsync("csharp:M:Sample.Vw.OnHover", "OnHover", SymbolKinds.Method);

        await _store!.BulkInsertEdgesAsync(new[]
        {
            new Edge(btn, cmdHandler, "handles-event", new Dictionary<string, string>
            {
                [PayloadKeys.Event] = "Click",
                ["command"] = "SaveCommand",
            }),
            new Edge(btn, nonCmd, "handles-event", new Dictionary<string, string>
            {
                [PayloadKeys.Event] = "MouseEnter",
            }),
        });

        var hits = await _store!.FindEventHandlersAsync(
            handlerCanonicalKey: null, eventExact: null,
            elementCanonicalKey: null, commandExact: "SaveCommand",
            limit: 50);

        hits.Should().HaveCount(1);
        hits[0].Target.Id.Should().Be(cmdHandler);
    }

    [Fact]
    public async Task FindEventHandlers_ignoresNonHandlesEventEdges()
    {
        // A binds-path edge should never appear in find_event_handlers.
        var src = await SeedSymbolAsync("xaml:element:View.xaml#El", "El", SymbolKinds.Class);
        var prop = await SeedSymbolAsync("csharp:P:Sample.Vm.User", "User", SymbolKinds.Property);
        await _store!.BulkInsertEdgesAsync(new[]
        {
            new Edge(src, prop, "binds-path", new Dictionary<string, string> { [PayloadKeys.Path] = "User" }),
        });

        var hits = await _store!.FindEventHandlersAsync(
            handlerCanonicalKey: null, eventExact: null,
            elementCanonicalKey: null, commandExact: null,
            limit: 50);

        hits.Should().BeEmpty();
    }

    [Fact]
    public async Task FindDataBindings_returnsBothEndpointsHydratedFromSymbols()
    {
        // The (Source, Target) projection must hydrate both SymbolHits with file/path/canonical
        // key info — the renderer uses these fields to format the row.
        var src = await SeedSymbolAsync("xaml:element:Views/MainWindow.xaml#NameBox", "NameBox", SymbolKinds.Class);
        var tgt = await SeedSymbolAsync("csharp:P:Sample.MainViewModel.UserName", "UserName", SymbolKinds.Property);

        await _store!.BulkInsertEdgesAsync(new[]
        {
            new Edge(src, tgt, "binds-path", new Dictionary<string, string>
            {
                [PayloadKeys.Path] = "UserName",
                [PayloadKeys.Mode] = "two-way",
            }),
        });

        var hits = await _store!.FindDataBindingsAsync(
            targetCanonicalKey: "csharp:P:Sample.MainViewModel.UserName",
            sourceCanonicalKey: null, pathContains: null, modeExact: null, converterExact: null,
            limit: 50);

        hits.Should().ContainSingle();
        var hit = hits[0];
        hit.Source.Name.Should().Be("NameBox");
        hit.Source.CanonicalKey.Should().Be("xaml:element:Views/MainWindow.xaml#NameBox");
        hit.Target.Name.Should().Be("UserName");
        hit.Target.CanonicalKey.Should().Be("csharp:P:Sample.MainViewModel.UserName");
    }
}
