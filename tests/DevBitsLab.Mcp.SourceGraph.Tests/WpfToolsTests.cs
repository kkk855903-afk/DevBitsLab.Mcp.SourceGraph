using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Indexing.Xaml;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Server.Plugins;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Xunit;
using CoreEvidenceConfidence = DevBitsLab.Mcp.SourceGraph.Core.EvidenceConfidence;
using CoreSourceLocation = DevBitsLab.Mcp.SourceGraph.Core.SourceLocation;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class WpfToolCatalogTests
{
    [Fact]
    public void Catalog_discovers_namedWpfTools_withStructuredSchemas()
    {
        var methods = typeof(WpfTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null);
        var tools = methods
            .Select(method => McpServerTool.Create(
                method,
                target: null,
                new McpServerToolCreateOptions()))
            .ToDictionary(tool => tool.ProtocolTool.Name, StringComparer.Ordinal);

        tools.Keys.Should().Contain(new[]
        {
            "trace_binding",
            "trace_command",
            "check_resources",
        });
        tools["trace_binding"].ProtocolTool.OutputSchema.Should().NotBeNull();
        tools["trace_command"].ProtocolTool.OutputSchema.Should().NotBeNull();
        tools["check_resources"].ProtocolTool.OutputSchema.Should().NotBeNull();

        typeof(WpfTools).GetMethod(nameof(WpfTools.TraceBindingAsync))!
            .GetCustomAttribute<McpServerToolAttribute>()!.OutputSchemaType
            .Should().Be(typeof(TraceBindingResult));
        typeof(WpfTools).GetMethod(nameof(WpfTools.TraceCommandAsync))!
            .GetCustomAttribute<McpServerToolAttribute>()!.OutputSchemaType
            .Should().Be(typeof(TraceCommandResult));
        typeof(WpfTools).GetMethod(nameof(WpfTools.CheckResourcesAsync))!
            .GetCustomAttribute<McpServerToolAttribute>()!.OutputSchemaType
            .Should().Be(typeof(CheckResourcesResult));
    }
}

[Collection("LeafFormatterState")]
public sealed class WpfToolBehaviorTests : IAsyncLifetime, IDisposable
{
    private string _tempDir = string.Empty;
    private string _dbPath = string.Empty;
    private SqliteGraphStore? _store;
    private ScopeHost? _host;
    private ScopeRouter? _router;
    private readonly List<ScopeHost> _additionalHosts = new();

    private const string ElementKey = "xaml:element:View.xaml#UserNameBox";
    private const string ButtonKey = "xaml:element:View.xaml#SaveButton";
    private const string PropertyKey = "csharp:P:Sample.MainViewModel.Name";
    private const string CommandKey = "csharp:P:Sample.MainViewModel.SaveCommand";
    private const string ResourceKey = "xaml:resource:App.xaml#AccentBrush";

    public WpfToolBehaviorTests() => LeafFormatter.Suppressed = false;
    public void Dispose() => LeafFormatter.Suppressed = false;

    public async Task InitializeAsync()
    {
        _tempDir = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-wpf-tools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Join(_tempDir, "graph.db");
        _store = new SqliteGraphStore(_dbPath);
        await _store.EnsureSchemaAsync();

        var xamlFileId = await _store.UpsertFileAsync(
            "/repo/View.xaml",
            new byte[32],
            DateTimeOffset.UtcNow);
        var appFileId = await _store.UpsertFileAsync(
            "/repo/App.xaml",
            new byte[32],
            DateTimeOffset.UtcNow);
        var codeFileId = await _store.UpsertFileAsync(
            "/repo/MainViewModel.cs",
            new byte[32],
            DateTimeOffset.UtcNow);
        var previewFileId = await _store.UpsertFileAsync(
            "/repo/Preview.xaml",
            new byte[32],
            DateTimeOffset.UtcNow);
        var themeFileId = await _store.UpsertFileAsync(
            "/repo/Theme.xaml",
            new byte[32],
            DateTimeOffset.UtcNow);

        var elementId = await SeedSymbolAsync(
            ElementKey,
            "UserNameBox",
            "View.xaml#UserNameBox",
            "xaml-element",
            xamlFileId,
            10);
        var buttonId = await SeedSymbolAsync(
            ButtonKey,
            "SaveButton",
            "View.xaml#SaveButton",
            "xaml-element",
            xamlFileId,
            20);
        await SeedSymbolAsync(
            "xaml:element:View.xaml#DuplicateA",
            "Duplicate",
            "View.xaml#DuplicateA",
            "xaml-element",
            xamlFileId,
            30);
        await SeedSymbolAsync(
            "xaml:element:View.xaml#DuplicateB",
            "Duplicate",
            "View.xaml#DuplicateB",
            "xaml-element",
            xamlFileId,
            31);
        var propertyId = await SeedSymbolAsync(
            PropertyKey,
            "Name",
            "Sample.MainViewModel.Name",
            SymbolKinds.Property,
            codeFileId,
            8);
        var commandId = await SeedSymbolAsync(
            CommandKey,
            "SaveCommand",
            "Sample.MainViewModel.SaveCommand",
            SymbolKinds.Property,
            codeFileId,
            12);
        var resourceId = await SeedSymbolAsync(
            ResourceKey,
            "AccentBrush",
            "App.xaml#AccentBrush",
            "xaml-resource",
            appFileId,
            5);
        var previewSourceId = await SeedSymbolAsync(
            "xaml:element:Preview.xaml#PreviewConsumer",
            "PreviewConsumer",
            "Preview.xaml#PreviewConsumer",
            "xaml-element",
            previewFileId,
            3);
        var previewTargetId = await SeedSymbolAsync(
            "xaml:resource:App.xaml#PreviewBrush",
            "PreviewBrush",
            "App.xaml#PreviewBrush",
            "xaml-resource",
            appFileId,
            6);
        var resourceSourceId = await SeedSymbolAsync(
            "xaml:resource:Theme.xaml#NestedBrush",
            "NestedBrush",
            "Theme.xaml#NestedBrush",
            "xaml-resource",
            themeFileId,
            4);
        var styleSourceId = await SeedSymbolAsync(
            "xaml:style:Theme.xaml#DerivedStyle",
            "DerivedStyle",
            "Theme.xaml#DerivedStyle",
            "xaml-style",
            themeFileId,
            8);
        var baseStyleId = await SeedSymbolAsync(
            "xaml:style:App.xaml#BaseStyle",
            "BaseStyle",
            "App.xaml#BaseStyle",
            "xaml-style",
            appFileId,
            10);
        var templateSourceId = await SeedSymbolAsync(
            "xaml:template:Theme.xaml#CardTemplate",
            "CardTemplate",
            "Theme.xaml#CardTemplate",
            "xaml-template",
            themeFileId,
            12);

        var bindingMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PayloadKeys.Path] = "User.Name",
            ["resolution-status"] = "resolved",
            ["resolution-reason"] = "unique-property",
        };
        var commandMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PayloadKeys.Path] = "SaveCommand",
            ["command"] = "SaveCommand",
            ["resolution-status"] = "resolved",
            ["resolution-reason"] = "unique-command-property",
        };
        var resourceMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PayloadKeys.Key] = "AccentBrush",
            ["resource-lookup"] = "static",
        };
        var previewMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PayloadKeys.Key] = "PreviewBrush",
            ["resource-lookup"] = "static",
        };
        var styleMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PayloadKeys.Key] = "BaseStyle",
            ["resource-lookup"] = "static",
        };
        await _store.BulkInsertEdgesAsync(new[]
        {
            new Edge(
                elementId,
                propertyId,
                "binds-path",
                bindingMetadata,
                new Evidence(
                    xamlFileId,
                    new CoreSourceLocation("/repo/View.xaml", 12, 18, 12, 48),
                    CoreEvidenceConfidence.Semantic,
                    "xaml-semantic",
                    bindingMetadata)),
            new Edge(
                buttonId,
                commandId,
                "binds-path",
                commandMetadata,
                new Evidence(
                    xamlFileId,
                    new CoreSourceLocation("/repo/View.xaml", 22, 18, 22, 47),
                    CoreEvidenceConfidence.Semantic,
                    "xaml-semantic",
                    commandMetadata)),
            new Edge(
                buttonId,
                resourceId,
                "uses-resource",
                resourceMetadata,
                new Evidence(
                    xamlFileId,
                    new CoreSourceLocation("/repo/View.xaml", 23, 20, 23, 55),
                    CoreEvidenceConfidence.Exact,
                    "xaml-resource",
                    resourceMetadata)),
            new Edge(
                previewSourceId,
                previewTargetId,
                "uses-resource",
                previewMetadata,
                new Evidence(
                    previewFileId,
                    new CoreSourceLocation("/repo/Preview.xaml", 3, 20, 3, 51),
                    CoreEvidenceConfidence.Exact,
                    "xaml-resource",
                    previewMetadata)),
            new Edge(
                resourceSourceId,
                resourceId,
                "uses-resource",
                resourceMetadata,
                new Evidence(
                    themeFileId,
                    new CoreSourceLocation("/repo/Theme.xaml", 4, 20, 4, 52),
                    CoreEvidenceConfidence.Exact,
                    "xaml-resource",
                    resourceMetadata)),
            new Edge(
                styleSourceId,
                baseStyleId,
                "applies-style",
                styleMetadata,
                new Evidence(
                    themeFileId,
                    new CoreSourceLocation("/repo/Theme.xaml", 8, 18, 8, 49),
                    CoreEvidenceConfidence.Exact,
                    "xaml-resource",
                    styleMetadata)),
            new Edge(
                templateSourceId,
                resourceId,
                "uses-resource",
                resourceMetadata,
                new Evidence(
                    themeFileId,
                    new CoreSourceLocation("/repo/Theme.xaml", 12, 22, 12, 54),
                    CoreEvidenceConfidence.Exact,
                    "xaml-resource",
                    resourceMetadata)),
        });

        await _store.BulkInsertAnnotationsAsync(new[]
        {
            new AnnotationRecord(
                buttonId,
                "Resource不存在",
                "XAMLRESOURCE001",
                "xaml-resource-finding",
                JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["status"] = "missing",
                    ["reason"] = "resource-key-not-found",
                    ["key"] = "MissingBrush",
                    ["resourceLookup"] = "static",
                    ["candidateCount"] = 0,
                    ["file"] = "/repo/View.xaml",
                    ["startLine"] = 24,
                    ["startColumn"] = 20,
                    ["endLine"] = 24,
                    ["endColumn"] = 61,
                    ["confidence"] = "exact",
                    ["producer"] = "xaml-resource",
                    ["code"] = "XAMLRESOURCE001",
                }),
                AttributeSymbolId: null),
            new AnnotationRecord(
                buttonId,
                "Resource解析结果",
                "unsupported",
                "xaml-resource-outcome",
                JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["status"] = "unsupported",
                    ["reason"] = "dynamic-resource-runtime-lookup",
                    ["key"] = "DynamicBrush",
                    ["resourceLookup"] = "dynamic",
                    ["candidateCount"] = 0,
                    ["file"] = "/repo/View.xaml",
                    ["startLine"] = 25,
                    ["startColumn"] = 20,
                    ["endLine"] = 25,
                    ["endColumn"] = 54,
                    ["confidence"] = "exact",
                    ["producer"] = "xaml-resource",
                }),
                AttributeSymbolId: null),
        });

        var scope = new Scope(
            Id: "default",
            Name: "default",
            Root: _tempDir,
            ProjectSet: new ScopeProjectSet.Solutions(
                new[] { Path.Join(_tempDir, "stub.sln") },
                Array.Empty<string>()),
            Isolated: false,
            LastIndexedAt: DateTimeOffset.UtcNow);
        _host = new ScopeHost(
            scope,
            _store,
            _store.CreateEmbeddingsStore(384),
            new RoslynIndexer(_store),
            Path.Join(_tempDir, "stub.sln"));
        _router = new ScopeRouter();
        _router.Register(_host);
        _router.SetDefaultScope("default");
        _host.MarkReady();
    }

    public async Task DisposeAsync()
    {
        foreach (var host in _additionalHosts) await host.DisposeAsync();
        if (_host is not null) await _host.DisposeAsync();
        if (_store is not null) await _store.DisposeAsync();
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task TraceBinding_returnsCanonicalTarget_andStoredOccurrenceEvidence_onPartialScope()
    {
        _host!.Status = "partial";
        _host.StatusMessage = "one unrelated project failed";

        var call = await WpfTools.TraceBindingAsync(
            _router!,
            element: ElementKey,
            binding: "Name",
            scope: null,
            limit: 50);

        call.IsError.Should().NotBe(true);
        var dto = Deserialize<TraceBindingResult>(call);
        dto.Status.Should().Be("resolved");
        dto.ScopeStatus.Should().Be("partial");
        dto.Note.Should().Contain("may be incomplete");
        var match = dto.Matches.Should().ContainSingle().Subject;
        match.Source.CanonicalKey.Should().Be(ElementKey);
        match.Target!.CanonicalKey.Should().Be(PropertyKey);
        match.Path.Should().Be("User.Name");
        match.Confidence.Should().Be("semantic");
        match.Evidence.Should().ContainSingle(item =>
            item.FilePath == "/repo/View.xaml"
            && item.StartLine == 12
            && item.StartColumn == 18
            && item.Producer == "xaml-semantic");
    }

    [Fact]
    public async Task TraceCommand_returnsResolvedCommand_andDoesNotMixOrdinaryBindings()
    {
        var call = await WpfTools.TraceCommandAsync(
            _router!,
            element: ButtonKey,
            command: "SaveCommand",
            scope: null,
            limit: 50);

        var dto = Deserialize<TraceCommandResult>(call);
        dto.Status.Should().Be("resolved");
        var match = dto.Matches.Should().ContainSingle().Subject;
        match.Source.CanonicalKey.Should().Be(ButtonKey);
        match.Target!.CanonicalKey.Should().Be(CommandKey);
        match.Path.Should().Be("SaveCommand");
        match.Relation.Should().Be("binds-path");
        match.Evidence.Should().ContainSingle(item =>
            item.StartLine == 22 && item.Producer == "xaml-semantic");
    }

    [Fact]
    public async Task TraceCommand_ambiguousElement_returnsCandidates_withoutGuessing()
    {
        var call = await WpfTools.TraceCommandAsync(
            _router!,
            element: "Duplicate",
            command: "SaveCommand",
            scope: null,
            limit: 50);

        var dto = Deserialize<TraceCommandResult>(call);
        dto.Status.Should().Be("ambiguous");
        dto.ElementStatus.Should().Be("ambiguous");
        dto.Candidates.Should().HaveCount(2);
        dto.Matches.Should().BeEmpty();
        dto.Note.Should().Contain("provide a canonical key");
    }

    [Fact]
    public async Task TraceBinding_unknownCanonicalElement_isExplicitNotFound()
    {
        var call = await WpfTools.TraceBindingAsync(
            _router!,
            element: "xaml:element:View.xaml#DoesNotExist",
            binding: "Name",
            scope: null,
            limit: 50);

        var dto = Deserialize<TraceBindingResult>(call);
        dto.Status.Should().Be("not-found");
        dto.ElementStatus.Should().Be("not-found");
        dto.Matches.Should().BeEmpty();
        dto.Note.Should().Contain("No XAML element");
    }

    [Fact]
    public async Task CheckResources_combinesResolvedAndMissing_withExactEvidence()
    {
        var call = await WpfTools.CheckResourcesAsync(
            _router!,
            file: "View.xaml",
            key: null,
            scope: null,
            limit: 50);

        var dto = Deserialize<CheckResourcesResult>(call);
        dto.Status.Should().Be("matched");
        dto.Resources.Should().Contain(item =>
            item.Key == "AccentBrush"
            && item.Relation == "uses-resource"
            && item.Status == "resolved"
            && item.Reason == "resolved-by-indexed-resource-edge"
            && item.Target != null
            && item.Target.CanonicalKey == ResourceKey
            && item.Confidence == "exact"
            && item.Evidence.Any(evidence =>
                evidence.StartLine == 23
                && evidence.Producer == "xaml-resource"));
        dto.Resources.Should().Contain(item =>
            item.Key == "MissingBrush"
            && item.Relation == "xaml-resource-finding"
            && item.Status == "missing"
            && item.Target == null
            && item.Confidence == "exact"
            && item.Evidence.Any(evidence =>
                evidence.StartLine == 24
                && evidence.Producer == "xaml-resource"));
        dto.Resources.Should().NotContain(item =>
            item.Source.FilePath.EndsWith("Preview.xaml", StringComparison.OrdinalIgnoreCase),
            "the file suffix `View.xaml` must not match the final segment `Preview.xaml`");
    }

    [Fact]
    public async Task CheckResources_preservesUnsupportedOutcome()
    {
        var call = await WpfTools.CheckResourcesAsync(
            _router!,
            file: "View.xaml",
            key: "DynamicBrush",
            scope: null,
            limit: 50);

        var dto = Deserialize<CheckResourcesResult>(call);
        dto.Status.Should().Be("unsupported");
        dto.Resources.Should().ContainSingle(item =>
            item.Key == "DynamicBrush"
            && item.Relation == "xaml-resource-outcome"
            && item.Status == "unsupported"
            && item.Reason == "dynamic-resource-runtime-lookup");
    }

    [Fact]
    public async Task CheckResources_scansResourceStyleTemplateSources_andBothRelations()
    {
        var call = await WpfTools.CheckResourcesAsync(
            _router!,
            file: "Theme.xaml",
            key: null,
            scope: null,
            limit: 50);

        var dto = Deserialize<CheckResourcesResult>(call);
        dto.Resources.Should().HaveCount(3);
        dto.Resources.Should().Contain(item =>
            item.Source.Kind == "xaml-resource"
            && item.Relation == "uses-resource"
            && item.Key == "AccentBrush");
        dto.Resources.Should().Contain(item =>
            item.Source.Kind == "xaml-style"
            && item.Relation == "applies-style"
            && item.Key == "BaseStyle");
        dto.Resources.Should().Contain(item =>
            item.Source.Kind == "xaml-template"
            && item.Relation == "uses-resource"
            && item.Key == "AccentBrush");
        dto.Resources.Should().OnlyContain(item =>
            item.Status == "resolved"
            && item.Reason == "resolved-by-indexed-resource-edge");
    }

    [Fact]
    public async Task NamedWpfTools_multiScope_preserveStructuredRowsProvenanceAndSharedLimit()
    {
        await RegisterEmptyScopeAsync("secondary");

        var bindingCall = await WpfTools.TraceBindingAsync(
            _router!,
            element: ElementKey,
            binding: "Name",
            scope: "*",
            limit: 50);

        var binding = Deserialize<TraceBindingResult>(bindingCall);
        binding.Scopes.Should().HaveCount(2);
        binding.Scopes.Select(item => item.ScopeId)
            .Should().BeEquivalentTo("default", "secondary");
        binding.Matches.Should().ContainSingle()
            .Which.ScopeId.Should().Be("default");
        binding.Matches[0].Source.ScopeId.Should().Be("default");
        binding.ScopeId.Should().Be("*");

        var resourceCall = await WpfTools.CheckResourcesAsync(
            _router!,
            file: null,
            key: null,
            scope: "default,secondary",
            limit: 1);

        var resources = Deserialize<CheckResourcesResult>(resourceCall);
        resources.Scopes.Should().HaveCount(2);
        resources.Resources.Should().HaveCountLessThanOrEqualTo(1,
            "the public limit is shared across all resolved scopes");
        resources.Resources.Should().OnlyContain(item => item.ScopeId == "default");
        resources.Partial.Should().BeTrue();
        resources.Truncated.Should().BeTrue();
        resources.OmittedCount.Should().BeGreaterThan(0);
        resources.Scopes.Single(item => item.ScopeId == "default")
            .Should().Match<WpfScopeSummary>(item =>
                item.Partial && item.Truncated && item.OmittedCount > 0);
    }

    [Fact]
    public async Task TraceBinding_filtersToXamlKinds_beforeApplyingCandidateLimit()
    {
        var files = await _store!.GetAllFilesAsync();
        var xamlFileId = files.Single(file => file.Path == "/repo/View.xaml").Id;
        var codeFileId = files.Single(file => file.Path == "/repo/MainViewModel.cs").Id;
        await SeedSymbolAsync(
            "xaml:element:View.xaml#Crowded",
            "Crowded",
            "View.xaml#Crowded",
            "xaml-element",
            xamlFileId,
            40);
        for (var i = 0; i < 25; i++)
        {
            await SeedSymbolAsync(
                $"csharp:T:Sample.Crowded{i}",
                "Crowded",
                $"Sample.Crowded{i}",
                SymbolKinds.Class,
                codeFileId,
                50 + i);
        }

        var call = await WpfTools.TraceBindingAsync(
            _router!,
            element: "Crowded",
            binding: "Name",
            scope: null,
            limit: 50);

        var dto = Deserialize<TraceBindingResult>(call);
        dto.ElementStatus.Should().Be("resolved");
        dto.Candidates.Should().BeEmpty();
        dto.Scopes.Should().ContainSingle(item => item.ScopeId == "default");
    }

    [Fact]
    public async Task TraceBinding_elementOnly_preservesPathlessUnsupportedOutcome_withStableReason()
    {
        var elementId = (await _store!.GetAllSymbolKeysAsync())
            .Single(item => item.CanonicalKey == ElementKey)
            .Id;
        await _store.BulkInsertAnnotationsAsync(new[]
        {
            new AnnotationRecord(
                elementId,
                "Binding解析结果",
                "unsupported",
                "xaml-binding-outcome",
                JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["status"] = "unsupported",
                    ["file"] = "/repo/View.xaml",
                    ["startLine"] = 13,
                    ["startColumn"] = 18,
                    ["endLine"] = 13,
                    ["endColumn"] = 27,
                    ["confidence"] = "exact",
                    ["producer"] = "xaml-semantic",
                }),
                AttributeSymbolId: null),
        });

        var call = await WpfTools.TraceBindingAsync(
            _router!,
            element: ElementKey,
            binding: null,
            scope: null,
            limit: 50);

        var dto = Deserialize<TraceBindingResult>(call);
        dto.Matches.Should().ContainSingle(item =>
            item.Status == "unsupported"
            && item.Path == "(unsupported-binding-form)"
            && item.Reason == "xaml-binding-outcome-unsupported"
            && item.Target == null);
        dto.Matches.Should().OnlyContain(item => !string.IsNullOrWhiteSpace(item.Reason));

        var keys = await _store.GetAllSymbolKeysAsync();
        var buttonId = keys.Single(item => item.CanonicalKey == ButtonKey).Id;
        var propertyId = keys.Single(item => item.CanonicalKey == PropertyKey).Id;
        var fileId = (await _store.GetAllFilesAsync())
            .Single(file => file.Path == "/repo/View.xaml")
            .Id;
        var legacyMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PayloadKeys.Path] = "LegacyName",
        };
        await _store.BulkInsertEdgesAsync(new[]
        {
            new Edge(
                buttonId,
                propertyId,
                "binds-to",
                legacyMetadata,
                new Evidence(
                    fileId,
                    new CoreSourceLocation("/repo/View.xaml", 26, 18, 26, 42),
                    CoreEvidenceConfidence.Semantic,
                    "xaml-semantic",
                    legacyMetadata)),
        });

        var legacyCall = await WpfTools.TraceBindingAsync(
            _router!,
            element: ButtonKey,
            binding: "LegacyName",
            scope: null,
            limit: 50);
        Deserialize<TraceBindingResult>(legacyCall).Matches.Should().ContainSingle(item =>
            item.Relation == "binds-to"
            && item.Reason == "resolved-by-legacy-binding-edge");
    }

    [Fact]
    public async Task TraceBinding_actualSerializedBudget_trimsLargeEvidenceInLockstep()
    {
        var keys = await _store!.GetAllSymbolKeysAsync();
        var sourceId = keys.Single(item => item.CanonicalKey == ElementKey).Id;
        var targetId = keys.Single(item => item.CanonicalKey == PropertyKey).Id;
        var fileId = (await _store.GetAllFilesAsync())
            .Single(file => file.Path == "/repo/View.xaml")
            .Id;
        var edges = Enumerable.Range(0, 20)
            .Select(index =>
            {
                var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [PayloadKeys.Path] = "User.Name",
                    ["resolution-status"] = "resolved",
                    ["large-proof"] = new string((char)('a' + index % 20), 4_000),
                };
                return new Edge(
                    sourceId,
                    targetId,
                    "binds-path",
                    metadata,
                    new Evidence(
                        fileId,
                        new CoreSourceLocation(
                            "/repo/View.xaml",
                            100 + index,
                            1,
                            100 + index,
                            20),
                        CoreEvidenceConfidence.Semantic,
                        "xaml-semantic",
                        metadata));
            })
            .ToList();
        await _store.BulkInsertEdgesAsync(edges);

        var call = await WpfTools.TraceBindingAsync(
            _router!,
            element: ElementKey,
            binding: "Name",
            scope: null,
            limit: 50);

        JsonSerializer.Serialize(call, McpJsonUtilities.DefaultOptions).Length
            .Should().BeLessThanOrEqualTo(OutputBudget.DefaultBudgetChars);
        var dto = Deserialize<TraceBindingResult>(call);
        dto.Truncated.Should().BeTrue();
        dto.Partial.Should().BeTrue();
        dto.Matches.Should().ContainSingle();
        dto.Matches[0].EvidenceTruncated.Should().BeTrue();
        dto.Matches[0].Evidence.Should().NotBeEmpty(
            "budget trimming must retain at least one exact occurrence proof for a retained row");
        dto.Scopes.Should().ContainSingle(item =>
            item.Partial && item.Truncated);
    }

    [Fact]
    public async Task NamedWpfTools_manyDegradedScopes_stayTypedAndWithinActualBudget()
    {
        _host!.Status = "degraded";
        _host.StatusMessage = new string('d', 512);
        for (var index = 0; index < 164; index++)
        {
            RegisterDegradedScope($"degraded-{index:D3}");
        }

        var bindingCall = await WpfTools.TraceBindingAsync(
            _router!,
            element: "MissingElement",
            binding: null,
            scope: "*",
            limit: 1);
        var resourceCall = await WpfTools.CheckResourcesAsync(
            _router!,
            file: null,
            key: null,
            scope: "*",
            limit: 1);

        foreach (var call in new[] { bindingCall, resourceCall })
        {
            JsonSerializer.Serialize(call, McpJsonUtilities.DefaultOptions).Length
                .Should().BeLessThanOrEqualTo(OutputBudget.DefaultBudgetChars);
            call.IsError.Should().BeTrue();
        }

        var binding = Deserialize<TraceBindingResult>(bindingCall);
        binding.Scopes.Should().HaveCount(165);
        binding.Scopes.Select(item => item.ScopeId).Should().OnlyHaveUniqueItems();
        binding.Scopes.Should().OnlyContain(item => item.Partial);
        binding.Partial.Should().BeTrue();
        binding.Truncated.Should().BeTrue();

        var resources = Deserialize<CheckResourcesResult>(resourceCall);
        resources.Scopes.Should().HaveCount(165);
        resources.Scopes.Select(item => item.ScopeId).Should().OnlyHaveUniqueItems();
        resources.Scopes.Should().OnlyContain(item => item.Partial);
        resources.Partial.Should().BeTrue();
        resources.Truncated.Should().BeTrue();

        for (var index = 164; index < 200; index++)
        {
            RegisterDegradedScope($"degraded-{index:D3}");
        }

        var overFanout = await WpfTools.TraceBindingAsync(
            _router!,
            element: "MissingElement",
            binding: null,
            scope: "*",
            limit: 1);
        overFanout.IsError.Should().BeTrue();
        overFanout.StructuredContent.Should().BeNull();
        JsonSerializer.Serialize(overFanout, McpJsonUtilities.DefaultOptions).Length
            .Should().BeLessThanOrEqualTo(OutputBudget.DefaultBudgetChars);
        overFanout.Content!.OfType<ModelContextProtocol.Protocol.TextContentBlock>()
            .Should().ContainSingle(block =>
                block.Text.Contains("maximum fan-out of 200", StringComparison.Ordinal));
    }

    private async Task RegisterEmptyScopeAsync(string id)
    {
        var root = Path.Join(_tempDir, id);
        Directory.CreateDirectory(root);
        var store = new SqliteGraphStore(Path.Join(root, "graph.db"));
        await store.EnsureSchemaAsync();
        var scope = new Scope(
            id,
            id,
            root,
            new ScopeProjectSet.Solutions(
                new[] { Path.Join(root, "stub.sln") },
                Array.Empty<string>()),
            Isolated: false,
            LastIndexedAt: DateTimeOffset.UtcNow);
        var host = new ScopeHost(
            scope,
            store,
            store.CreateEmbeddingsStore(384),
            new RoslynIndexer(store),
            Path.Join(root, "stub.sln"));
        host.MarkReady();
        _router!.Register(host);
        _additionalHosts.Add(host);
    }

    private void RegisterDegradedScope(string id)
    {
        var root = _tempDir;
        var store = new SqliteGraphStore(Path.Join(root, id + ".db"));
        var scope = new Scope(
            id,
            id,
            root,
            new ScopeProjectSet.Solutions(
                new[] { Path.Join(root, "stub.sln") },
                Array.Empty<string>()),
            Isolated: false,
            LastIndexedAt: DateTimeOffset.UtcNow);
        var host = new ScopeHost(
            scope,
            store,
            store.CreateEmbeddingsStore(384),
            new RoslynIndexer(store),
            Path.Join(root, "stub.sln"))
        {
            Status = "degraded",
            StatusMessage = new string('x', 512),
        };
        host.MarkReady();
        _router!.Register(host);
        _additionalHosts.Add(host);
    }

    private async Task<long> SeedSymbolAsync(
        string canonicalKey,
        string name,
        string fqn,
        string kind,
        long fileId,
        int line) =>
        await _store!.UpsertSymbolAsync(
            canonicalKey,
            new Symbol(
                Id: 0,
                Name: name,
                Fqn: fqn,
                Kind: kind,
                FileId: fileId,
                StartLine: line,
                StartCol: 1,
                EndLine: line,
                EndCol: 20,
                Signature: null,
                ContainerId: null,
                Modifiers: null,
                Accessibility: 6,
                XmlSummary: null,
                TestFramework: null));

    private static T Deserialize<T>(ModelContextProtocol.Protocol.CallToolResult call)
    {
        call.StructuredContent.Should().NotBeNull();
        return JsonSerializer.Deserialize<T>(
            call.StructuredContent!.Value.GetRawText(),
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            })!;
    }
}

[Collection("LeafFormatterState")]
public sealed class WpfToolIndexedFixtureTests : IAsyncLifetime, IDisposable
{
    private string _tempDir = string.Empty;
    private SqliteGraphStore? _store;
    private ScopeHost? _host;
    private ScopeRouter? _router;

    public WpfToolIndexedFixtureTests() => LeafFormatter.Suppressed = false;
    public void Dispose() => LeafFormatter.Suppressed = false;

    public async Task InitializeAsync()
    {
        var root = LocateFixture("SampleWpf");
        var solutionPath = Path.Join(root, "SampleWpf.sln");
        _tempDir = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-wpf-tool-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _store = new SqliteGraphStore(Path.Join(_tempDir, "graph.db"));

        await using var roslyn = new RoslynIndexer(_store);
        await roslyn.OpenAsync(solutionPath);
        await roslyn.IndexAllAsync();

        var indexers = new LanguageIndexerRegistry();
        indexers.Register(new XamlLanguageIndexer());
        var factories = new LanguageProjectFactoryRegistry();
        factories.Register(new XamlLanguageProjectFactory(
            () => roslyn.SanitizedSolution,
            roslyn.IsProjectSemanticInputComplete));
        var dispatcher = new LanguageIndexerDispatcher(indexers, factories);
        var projectMap = new Dictionary<string, ILanguageProject>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var factory in factories.All())
        {
            var projects = await factory.DiscoverAsync(root, default);
            foreach (var project in projects)
            {
                foreach (var path in project.FilePaths) projectMap.TryAdd(path, project);
            }
        }
        await dispatcher.DispatchAllForTestAsync(_store, "test", root, projectMap);

        var scope = new Scope(
            Id: "default",
            Name: "default",
            Root: root,
            ProjectSet: new ScopeProjectSet.Solutions(
                new[] { solutionPath },
                Array.Empty<string>()),
            Isolated: false,
            LastIndexedAt: DateTimeOffset.UtcNow);
        _host = new ScopeHost(
            scope,
            _store,
            _store.CreateEmbeddingsStore(384),
            new RoslynIndexer(_store),
            solutionPath);
        _router = new ScopeRouter();
        _router.Register(_host);
        _router.SetDefaultScope("default");
        _host.MarkReady();
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
        if (_store is not null) await _store.DisposeAsync();
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task NamedWpfTools_readRealIndexerEdgesOutcomes_andOccurrenceLocations()
    {
        var bindingCall = await WpfTools.TraceBindingAsync(
            _router!,
            element: "UserNameBox",
            binding: "Name",
            scope: null,
            limit: 50);
        var binding = Deserialize<TraceBindingResult>(bindingCall);
        binding.Status.Should().Be("resolved");
        binding.Matches.Should().ContainSingle(match =>
            match.Path == "User.Name"
            && match.Target != null
            && match.Target.CanonicalKey != null
            && match.Target.CanonicalKey.Contains("SampleWpf.ViewModels.User.Name", StringComparison.Ordinal)
            && match.Evidence.Any(evidence =>
                evidence.FilePath.EndsWith("MainWindow.xaml", StringComparison.OrdinalIgnoreCase)
                && evidence.StartLine == 12
                && evidence.Producer == "xaml-semantic"));

        var commandCall = await WpfTools.TraceCommandAsync(
            _router!,
            element: "SaveButton",
            command: "SaveCommand",
            scope: null,
            limit: 50);
        var command = Deserialize<TraceCommandResult>(commandCall);
        command.Status.Should().Be("resolved");
        command.Matches.Should().ContainSingle(match =>
            match.Path == "SaveCommand"
            && match.Target != null
            && match.Target.CanonicalKey != null
            && match.Evidence.Any(evidence =>
                evidence.FilePath.EndsWith("MainWindow.xaml", StringComparison.OrdinalIgnoreCase)
                && evidence.StartLine == 17));

        var missingBindingCall = await WpfTools.TraceBindingAsync(
            _router!,
            element: "BrokenBinding",
            binding: "Missing.Name",
            scope: null,
            limit: 50);
        var missingBinding = Deserialize<TraceBindingResult>(missingBindingCall);
        missingBinding.Status.Should().Be("missing");
        missingBinding.Matches.Should().ContainSingle(match =>
            match.Status == "missing"
            && match.Target == null
            && match.Evidence.Any(evidence =>
                evidence.FilePath.EndsWith("MainWindow.xaml", StringComparison.OrdinalIgnoreCase)
                && evidence.StartLine == 28));

        var resourceCall = await WpfTools.CheckResourcesAsync(
            _router!,
            file: "Views/MainWindow.xaml",
            key: "ResourceThatDoesNotExist",
            scope: null,
            limit: 50);
        var resources = Deserialize<CheckResourcesResult>(resourceCall);
        resources.Status.Should().Be("missing");
        resources.Resources.Should().ContainSingle(resource =>
            resource.Key == "ResourceThatDoesNotExist"
            && resource.Status == "missing"
            && resource.Target == null
            && resource.Evidence.Any(evidence =>
                evidence.FilePath.EndsWith("MainWindow.xaml", StringComparison.OrdinalIgnoreCase)
                && evidence.StartLine == 24
                && evidence.Producer == "xaml-resource"));
    }

    private static T Deserialize<T>(ModelContextProtocol.Protocol.CallToolResult call)
    {
        call.IsError.Should().NotBe(true);
        call.StructuredContent.Should().NotBeNull();
        return JsonSerializer.Deserialize<T>(
            call.StructuredContent!.Value.GetRawText(),
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            })!;
    }

    private static string LocateFixture(string name)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Join(directory.FullName, "tests", "fixtures", name);
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException(
            $"Could not locate tests/fixtures/{name} from {AppContext.BaseDirectory}.");
    }
}
