using System.Reflection;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Server.Plugins;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Xunit;
using CoreEvidenceConfidence = DevBitsLab.Mcp.SourceGraph.Core.EvidenceConfidence;
using SdkEvidenceConfidence = DevBitsLab.Mcp.SourceGraph.Sdk.EvidenceConfidence;
using SdkSourceLocation = DevBitsLab.Mcp.SourceGraph.Sdk.SourceLocation;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class SdkEdgeEvidenceTests : IAsyncLifetime
{
    private const string SourceKey = "csharp:M:Evidence.Source";
    private const string TargetKey = "csharp:M:Evidence.Target";

    private string _tempDir = string.Empty;
    private string _dbPath = string.Empty;
    private string _producerPath = string.Empty;
    private SqliteGraphStore? _store;
    private long _producerFileId;
    private long _sourceId;
    private long _targetId;
    private Dictionary<string, long> _symbolIds = null!;

    public async Task InitializeAsync()
    {
        _tempDir = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-sdk-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Join(_tempDir, "graph.db");
        _producerPath = Path.Join(_tempDir, "Producer.xaml");

        _store = new SqliteGraphStore(_dbPath);
        await _store.EnsureSchemaAsync();

        _producerFileId = await SeedFileAsync(_producerPath);
        var sourceFileId = await SeedFileAsync(Path.Join(_tempDir, "Source.cs"));
        var targetFileId = await SeedFileAsync(Path.Join(_tempDir, "Target.cs"));
        _sourceId = await SeedSymbolAsync(sourceFileId, "Source", "Evidence.Source");
        _targetId = await SeedSymbolAsync(targetFileId, "Target", "Evidence.Target");
        _symbolIds = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            [SourceKey] = _sourceId,
            [TargetKey] = _targetId,
        };
    }

    public async Task DisposeAsync()
    {
        if (_store is not null) await _store.DisposeAsync();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public void EdgeEmitted_preservesOriginalFourParameterConstructor()
    {
        var signature = new[]
        {
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(IReadOnlyDictionary<string, string>),
        };

        typeof(IndexEvent.EdgeEmitted).GetConstructor(
                BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                types: signature,
                modifiers: null)
            .Should().NotBeNull("plugins compiled against SDK 2.2 bind to this exact constructor");

        new IndexEvent.EdgeEmitted(SourceKey, TargetKey, EdgeKinds.Calls)
            .Evidence.Should().BeNull();
    }

    [Theory]
    [InlineData(SdkEvidenceConfidence.Inferred, CoreEvidenceConfidence.Inferred)]
    [InlineData(SdkEvidenceConfidence.Semantic, CoreEvidenceConfidence.Semantic)]
    [InlineData(SdkEvidenceConfidence.Exact, CoreEvidenceConfidence.Exact)]
    public async Task GraphStoreEmitter_mapsSdkEvidence_andOwnsItWithCurrentFileId(
        SdkEvidenceConfidence sdkConfidence,
        CoreEvidenceConfidence coreConfidence)
    {
        var occurrenceMetadata = new Dictionary<string, string>
        {
            ["path"] = "Patient.Name",
        };
        var emitted = new IndexEvent.EdgeEmitted(
            SourceKey,
            TargetKey,
            EdgeKinds.Calls)
        {
            Evidence = new EdgeEvidence(
                new SdkSourceLocation(_producerPath, 12, 9, 12, 17),
                sdkConfidence,
                "sample-plugin",
                occurrenceMetadata),
        };

        var emitter = new GraphStoreEmitter(_store!, _producerFileId, _symbolIds);
        emitter.EmitEdge(emitted);
        await emitter.FlushAsync();

        var stored = await _store!.ListEdgeEvidenceAsync(
            _sourceId,
            _targetId,
            EdgeKinds.Calls);
        stored.Should().ContainSingle();
        stored[0].ProducingFileId.Should().Be(
            _producerFileId,
            "the host, not the plugin or source-symbol declaration, owns producer identity");
        stored[0].Location.Should().Be(
            new DevBitsLab.Mcp.SourceGraph.Core.SourceLocation(
                _producerPath,
                12,
                9,
                12,
                17));
        stored[0].Confidence.Should().Be(coreConfidence);
        stored[0].Producer.Should().Be("sample-plugin");
        stored[0].Metadata.Should().Contain("path", "Patient.Name");

        await _store.ClearFileOutgoingAsync(_producerFileId);
        (await _store.ListEdgeEvidenceAsync(_sourceId, _targetId, EdgeKinds.Calls))
            .Should().BeEmpty("producer-owned evidence is removed with the producer file");
        (await _store.ListCalleesAsync(_sourceId, edgeKind: EdgeKinds.Calls))
            .Should().BeEmpty("the logical edge is removed after its final evidence disappears");
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative.cs")]
    public async Task GraphStoreEmitter_rejectsInvalidEvidencePath_withoutPersistingEdge(
        string filePath)
    {
        await AssertInvalidEvidenceAsync(filePath, 1, 1, 1, 1);
    }

    [Theory]
    [InlineData(0, 1, 1, 1)]
    [InlineData(2, 1, 1, 1)]
    [InlineData(1, 5, 1, 4)]
    public async Task GraphStoreEmitter_rejectsInvalidEvidenceRange_withoutPersistingEdge(
        int startLine,
        int startColumn,
        int endLine,
        int endColumn)
    {
        await AssertInvalidEvidenceAsync(
            _producerPath,
            startLine,
            startColumn,
            endLine,
            endColumn);
    }

    private async Task AssertInvalidEvidenceAsync(
        string filePath,
        int startLine,
        int startColumn,
        int endLine,
        int endColumn)
    {
        var emitted = new IndexEvent.EdgeEmitted(
            SourceKey,
            TargetKey,
            EdgeKinds.Calls)
        {
            Evidence = new EdgeEvidence(
                new SdkSourceLocation(
                    filePath,
                    startLine,
                    startColumn,
                    endLine,
                    endColumn),
                SdkEvidenceConfidence.Exact,
                "sample-plugin"),
        };
        var emitter = new GraphStoreEmitter(_store!, _producerFileId, _symbolIds);
        emitter.EmitEdge(emitted);

        var act = () => emitter.FlushAsync();

        await act.Should().ThrowAsync<ArgumentException>();
        (await _store!.ListEdgeEvidenceAsync(_sourceId, _targetId, EdgeKinds.Calls))
            .Should().BeEmpty();
        (await _store.ListCalleesAsync(_sourceId, edgeKind: EdgeKinds.Calls))
            .Should().BeEmpty();
    }

    private async Task<long> SeedFileAsync(string path) =>
        await _store!.UpsertFileAsync(
            path,
            new byte[] { 1, 2, 3, 4 },
            DateTimeOffset.UtcNow);

    private async Task<long> SeedSymbolAsync(long fileId, string name, string fqn) =>
        await _store!.UpsertSymbolAsync(
            $"csharp:M:{fqn}",
            new Symbol(
                0,
                name,
                fqn,
                "method",
                fileId,
                1,
                1,
                5,
                1,
                $"void {name}()",
                null));
}
