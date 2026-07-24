using System.Reflection;
using Dapper;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class FileDerivedProjectionReplacementTests : IAsyncLifetime
{
    private const string Producer = "interop-resolver";
    private const string FindingFlavor = "interop-finding";
    private const string MatchFlavor = "interop-match";

    private string _tempDir = string.Empty;
    private string _dbPath = string.Empty;
    private SqliteGraphStore? _store;

    public async Task InitializeAsync()
    {
        _tempDir = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-derived-projection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Join(_tempDir, "graph.db");
        _store = new SqliteGraphStore(_dbPath);
        await _store.EnsureSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        if (_store is not null)
        {
            await _store.DisposeAsync();
        }
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Replacement_deduplicates_stably_and_preserves_unselected_owners()
    {
        var managedPath = Path.Join(_tempDir, "Managed.cs");
        var otherPath = Path.Join(_tempDir, "Other.cs");
        var nativePath = Path.Join(_tempDir, "native.h");
        var managedFileId = await SeedFileAsync(managedPath);
        var otherFileId = await SeedFileAsync(otherPath);
        var nativeFileId = await SeedFileAsync(nativePath);
        var sourceKey = "csharp:M:NativeMethods.Compute";
        var otherKey = "csharp:M:Other.Helper";
        var attributeKey = "csharp:T:InteropFindingAttribute";
        var targetKey = "c:E:native.h::compute";
        var sourceId = await SeedSymbolAsync(managedFileId, sourceKey, "Compute");
        var otherId = await SeedSymbolAsync(otherFileId, otherKey, "Helper");
        var attributeId = await SeedSymbolAsync(
            otherFileId,
            attributeKey,
            "InteropFindingAttribute");
        var targetId = await SeedSymbolAsync(nativeFileId, targetKey, "compute");

        await _store!.BulkInsertAnnotationsAsync(
        [
            Stored(sourceId, "OldFinding", FindingFlavor),
            Stored(sourceId, "OldMatch", MatchFlavor),
            Stored(sourceId, "DllImport", "csharp-attribute"),
            Stored(otherId, "OtherFinding", FindingFlavor),
        ]);
        await _store.BulkInsertEdgesAsync(
        [
            StoredEdge(
                sourceId,
                targetId,
                managedFileId,
                managedPath,
                line: 4,
                producer: "managed-indexer",
                value: "survivor"),
            StoredEdge(
                sourceId,
                targetId,
                managedFileId,
                managedPath,
                line: 5,
                producer: Producer,
                value: "old"),
            StoredEdge(
                sourceId,
                targetId,
                otherFileId,
                otherPath,
                line: 6,
                producer: Producer,
                value: "other-file"),
        ]);

        var finding = Fact(
            sourceKey,
            "Interop001",
            FindingFlavor,
            attributeKey) with
        {
            ArgsJson = """{"code":"Interop001"}""",
        };
        var match = Fact(sourceKey, "Matched", MatchFlavor);
        var edge = EdgeFact(
            sourceKey,
            targetKey,
            managedPath,
            line: 10,
            new Dictionary<string, string>
            {
                ["z"] = "2",
                ["a"] = "1",
            });
        var duplicateWithDifferentMapOrder = EdgeFact(
            sourceKey,
            targetKey,
            managedPath,
            line: 10,
            new Dictionary<string, string>
            {
                ["a"] = "1",
                ["z"] = "2",
            });

        await _store.ReplaceFileDerivedProjectionAsync(
            managedPath,
            Producer,
            [MatchFlavor, FindingFlavor],
            [match, finding, finding],
            [edge, duplicateWithDifferentMapOrder]);

        var annotations = await _store.GetAnnotationsForSymbolAsync(sourceId);
        annotations.Select(item => (item.Flavor, item.Name)).Should().BeEquivalentTo(
        [
            ("csharp-attribute", "DllImport"),
            (FindingFlavor, "Interop001"),
            (MatchFlavor, "Matched"),
        ]);
        annotations.Single(item => item.Name == "Interop001")
            .AttributeSymbolId.Should().Be(attributeId);
        (await _store.GetAnnotationsForSymbolAsync(otherId))
            .Should().ContainSingle()
            .Which.Name.Should().Be("OtherFinding");

        var evidence = await _store.ListEdgeEvidenceAsync(
            sourceId,
            targetId,
            "pinvoke-maps-to");
        evidence.Should().HaveCount(3);
        evidence.Should().ContainSingle(item =>
            item.Producer == Producer
            && item.ProducingFileId == managedFileId
            && item.Location.StartLine == 10
            && item.Metadata!["a"] == "1"
            && item.Metadata!["z"] == "2");
        evidence.Should().ContainSingle(item =>
            item.Producer == "managed-indexer"
            && item.ProducingFileId == managedFileId);
        evidence.Should().ContainSingle(item =>
            item.Producer == Producer
            && item.ProducingFileId == otherFileId);
        (await GetEdgePayloadAsync(sourceId, targetId))
            .Should().Be(
                """{"value":"survivor"}""",
                "the earliest surviving evidence remains the logical compatibility payload");
    }

    [Fact]
    public async Task Empty_facts_clear_only_selected_flavors_and_exact_producer_evidence()
    {
        var managedPath = Path.Join(_tempDir, "Managed.cs");
        var nativePath = Path.Join(_tempDir, "native.h");
        var managedFileId = await SeedFileAsync(managedPath);
        var nativeFileId = await SeedFileAsync(nativePath);
        var sourceKey = "csharp:M:NativeMethods.Compute";
        var sharedTargetKey = "c:E:native.h::shared";
        var orphanTargetKey = "c:E:native.h::orphan";
        var sourceId = await SeedSymbolAsync(managedFileId, sourceKey, "Compute");
        var sharedTargetId = await SeedSymbolAsync(
            nativeFileId,
            sharedTargetKey,
            "shared");
        var orphanTargetId = await SeedSymbolAsync(
            nativeFileId,
            orphanTargetKey,
            "orphan");
        await _store!.BulkInsertAnnotationsAsync(
        [
            Stored(sourceId, "OldFinding", FindingFlavor),
            Stored(sourceId, "DllImport", "csharp-attribute"),
        ]);
        await _store.BulkInsertEdgesAsync(
        [
            StoredEdge(
                sourceId,
                sharedTargetId,
                managedFileId,
                managedPath,
                line: 2,
                producer: Producer,
                value: "old"),
            StoredEdge(
                sourceId,
                sharedTargetId,
                managedFileId,
                managedPath,
                line: 3,
                producer: "managed-indexer",
                value: "survivor"),
            StoredEdge(
                sourceId,
                orphanTargetId,
                managedFileId,
                managedPath,
                line: 4,
                producer: Producer,
                value: "orphan"),
        ]);

        await _store.ReplaceFileDerivedProjectionAsync(
            managedPath,
            Producer,
            [FindingFlavor, MatchFlavor],
            [],
            []);

        (await _store.GetAnnotationsForSymbolAsync(sourceId))
            .Should().ContainSingle()
            .Which.Flavor.Should().Be("csharp-attribute");
        (await _store.ListEdgeEvidenceAsync(
                sourceId,
                sharedTargetId,
                "pinvoke-maps-to"))
            .Should().ContainSingle()
            .Which.Producer.Should().Be("managed-indexer");
        (await GetEdgePayloadAsync(sourceId, sharedTargetId))
            .Should().Be("""{"value":"survivor"}""");
        (await EdgeExistsAsync(sourceId, orphanTargetId)).Should().BeFalse();
    }

    [Fact]
    public async Task External_annotation_host_rolls_back_both_projection_parts()
    {
        var managedPath = Path.Join(_tempDir, "Managed.cs");
        var otherPath = Path.Join(_tempDir, "Other.cs");
        var nativePath = Path.Join(_tempDir, "native.h");
        var managedFileId = await SeedFileAsync(managedPath);
        var otherFileId = await SeedFileAsync(otherPath);
        var nativeFileId = await SeedFileAsync(nativePath);
        var sourceKey = "csharp:M:NativeMethods.Compute";
        var externalKey = "csharp:M:Other.Compute";
        var targetKey = "c:E:native.h::compute";
        var sourceId = await SeedSymbolAsync(managedFileId, sourceKey, "Compute");
        await SeedSymbolAsync(otherFileId, externalKey, "Compute");
        var targetId = await SeedSymbolAsync(nativeFileId, targetKey, "compute");
        await SeedOldProjectionAsync(
            managedPath,
            managedFileId,
            sourceId,
            targetId);

        var replace = () => _store!.ReplaceFileDerivedProjectionAsync(
            managedPath,
            Producer,
            [FindingFlavor],
            [
                Fact(sourceKey, "Candidate", FindingFlavor),
                Fact(externalKey, "External", FindingFlavor),
            ],
            [EdgeFact(sourceKey, targetKey, managedPath, line: 10)]);

        await replace.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*external*");
        await AssertOldProjectionAsync(sourceId, targetId);
    }

    [Fact]
    public async Task Invalid_inputs_are_rejected_before_old_projection_is_changed()
    {
        var managedPath = Path.Join(_tempDir, "Managed.cs");
        var nativePath = Path.Join(_tempDir, "native.h");
        var managedFileId = await SeedFileAsync(managedPath);
        var nativeFileId = await SeedFileAsync(nativePath);
        var sourceKey = "csharp:M:NativeMethods.Compute";
        var targetKey = "c:E:native.h::compute";
        var sourceId = await SeedSymbolAsync(managedFileId, sourceKey, "Compute");
        var targetId = await SeedSymbolAsync(nativeFileId, targetKey, "compute");
        await SeedOldProjectionAsync(
            managedPath,
            managedFileId,
            sourceId,
            targetId);
        var malformed = Fact(sourceKey, "Candidate", FindingFlavor) with
        {
            ArgsJson = "{",
        };

        var invalidJson = () => _store!.ReplaceFileDerivedProjectionAsync(
            managedPath,
            Producer,
            [FindingFlavor],
            [malformed],
            []);
        var duplicateFlavor = () => _store!.ReplaceFileDerivedProjectionAsync(
            managedPath,
            Producer,
            [FindingFlavor, FindingFlavor],
            [],
            []);

        await invalidJson.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*valid JSON*");
        await duplicateFlavor.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*duplicated*");
        await AssertOldProjectionAsync(sourceId, targetId);
    }

    [Fact]
    public async Task Unresolved_attribute_rolls_back_both_projection_parts()
    {
        var managedPath = Path.Join(_tempDir, "Managed.cs");
        var nativePath = Path.Join(_tempDir, "native.h");
        var managedFileId = await SeedFileAsync(managedPath);
        var nativeFileId = await SeedFileAsync(nativePath);
        var sourceKey = "csharp:M:NativeMethods.Compute";
        var targetKey = "c:E:native.h::compute";
        var sourceId = await SeedSymbolAsync(managedFileId, sourceKey, "Compute");
        var targetId = await SeedSymbolAsync(nativeFileId, targetKey, "compute");
        await SeedOldProjectionAsync(
            managedPath,
            managedFileId,
            sourceId,
            targetId);

        var replace = () => _store!.ReplaceFileDerivedProjectionAsync(
            managedPath,
            Producer,
            [FindingFlavor],
            [
                Fact(
                    sourceKey,
                    "Candidate",
                    FindingFlavor,
                    "csharp:T:MissingAttribute"),
            ],
            [EdgeFact(sourceKey, targetKey, managedPath, line: 10)]);

        await replace.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*MissingAttribute*");
        await AssertOldProjectionAsync(sourceId, targetId);
    }

    [Fact]
    public async Task Invalid_edge_evidence_is_rejected_before_old_projection_is_changed()
    {
        var managedPath = Path.Join(_tempDir, "Managed.cs");
        var nativePath = Path.Join(_tempDir, "native.h");
        var managedFileId = await SeedFileAsync(managedPath);
        var nativeFileId = await SeedFileAsync(nativePath);
        var sourceKey = "csharp:M:NativeMethods.Compute";
        var targetKey = "c:E:native.h::compute";
        var sourceId = await SeedSymbolAsync(managedFileId, sourceKey, "Compute");
        var targetId = await SeedSymbolAsync(nativeFileId, targetKey, "compute");
        await SeedOldProjectionAsync(
            managedPath,
            managedFileId,
            sourceId,
            targetId);
        var wrongProducer = EdgeFact(
            sourceKey,
            targetKey,
            managedPath,
            line: 10) with
        {
            Evidence = new FileEvidenceFact(
                new SourceLocation(managedPath, 10, 1, 10, 2),
                EvidenceConfidence.Exact,
                "different-producer",
                Metadata: null),
        };
        var wrongPath = EdgeFact(
            sourceKey,
            targetKey,
            nativePath,
            line: 10);
        var invalidMetadata = new Dictionary<string, string>
        {
            ["invalid"] = null!,
        };

        var replaceProducer = () => _store!.ReplaceFileDerivedProjectionAsync(
            managedPath,
            Producer,
            [FindingFlavor],
            [],
            [wrongProducer]);
        var replacePath = () => _store!.ReplaceFileDerivedProjectionAsync(
            managedPath,
            Producer,
            [FindingFlavor],
            [],
            [wrongPath]);
        var replaceMetadata = () => _store!.ReplaceFileDerivedProjectionAsync(
            managedPath,
            Producer,
            [FindingFlavor],
            [],
            [
                EdgeFact(
                    sourceKey,
                    targetKey,
                    managedPath,
                    line: 10,
                    invalidMetadata),
            ]);

        await replaceProducer.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*exactly match*");
        await replacePath.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*path does not match*");
        await replaceMetadata.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*null value*");
        await AssertOldProjectionAsync(sourceId, targetId);
    }

    [Fact]
    public async Task Sql_failure_after_annotation_insert_rolls_back_cleanup_and_new_rows()
    {
        var managedPath = Path.Join(_tempDir, "Managed.cs");
        var nativePath = Path.Join(_tempDir, "native.h");
        var managedFileId = await SeedFileAsync(managedPath);
        var nativeFileId = await SeedFileAsync(nativePath);
        var sourceKey = "csharp:M:NativeMethods.Compute";
        var targetKey = "c:E:native.h::compute";
        var sourceId = await SeedSymbolAsync(managedFileId, sourceKey, "Compute");
        var targetId = await SeedSymbolAsync(nativeFileId, targetKey, "compute");
        await SeedOldProjectionAsync(
            managedPath,
            managedFileId,
            sourceId,
            targetId);
        await ExecuteAsync(
            $"""
            CREATE TRIGGER fail_derived_edge
            BEFORE INSERT ON edge_evidence
            WHEN NEW.producer = '{Producer}' AND NEW.start_line = 10
            BEGIN
                SELECT RAISE(ABORT, 'forced derived edge failure');
            END;
            """);

        var replace = () => _store!.ReplaceFileDerivedProjectionAsync(
            managedPath,
            Producer,
            [FindingFlavor],
            [Fact(sourceKey, "Candidate", FindingFlavor)],
            [EdgeFact(sourceKey, targetKey, managedPath, line: 10)]);

        await replace.Should().ThrowAsync<SqliteException>();
        await AssertOldProjectionAsync(sourceId, targetId);
    }

    [Fact]
    public async Task Cancellation_rolls_back_without_exposing_an_empty_projection()
    {
        var managedPath = Path.Join(_tempDir, "Managed.cs");
        var nativePath = Path.Join(_tempDir, "native.h");
        var managedFileId = await SeedFileAsync(managedPath);
        var nativeFileId = await SeedFileAsync(nativePath);
        var sourceKey = "csharp:M:NativeMethods.Compute";
        var targetKey = "c:E:native.h::compute";
        var sourceId = await SeedSymbolAsync(managedFileId, sourceKey, "Compute");
        var targetId = await SeedSymbolAsync(nativeFileId, targetKey, "compute");
        await SeedOldProjectionAsync(
            managedPath,
            managedFileId,
            sourceId,
            targetId);
        using var cancellation = new CancellationTokenSource();
        var connectionField = typeof(SqliteGraphStore).GetField(
            "_connection",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var storeConnection = connectionField!.GetValue(_store)
            .Should().BeOfType<SqliteConnection>().Subject;
        storeConnection.CreateFunction(
            "cancel_derived_projection",
            () =>
            {
                cancellation.Cancel();
                return 0;
            });
        await ExecuteAsync(
            """
            CREATE TRIGGER cancel_after_derived_annotation_delete
            AFTER DELETE ON annotations
            WHEN OLD.flavor = 'interop-finding'
            BEGIN
                SELECT cancel_derived_projection();
            END;
            """);

        var replace = () => _store!.ReplaceFileDerivedProjectionAsync(
            managedPath,
            Producer,
            [FindingFlavor],
            [],
            [],
            cancellation.Token);

        await replace.Should().ThrowAsync<OperationCanceledException>();
        await AssertOldProjectionAsync(sourceId, targetId);
    }

    [Fact]
    public async Task Batch_unresolved_endpoint_in_second_projection_preserves_both_last_good()
    {
        var firstPath = Path.Join(_tempDir, "First.cs");
        var secondPath = Path.Join(_tempDir, "Second.cs");
        var nativePath = Path.Join(_tempDir, "native.h");
        var firstFileId = await SeedFileAsync(firstPath);
        var secondFileId = await SeedFileAsync(secondPath);
        var nativeFileId = await SeedFileAsync(nativePath);
        var firstKey = "csharp:M:NativeMethods.First";
        var secondKey = "csharp:M:NativeMethods.Second";
        var targetKey = "c:E:native.h::compute";
        var firstId = await SeedSymbolAsync(firstFileId, firstKey, "First");
        var secondId = await SeedSymbolAsync(secondFileId, secondKey, "Second");
        var targetId = await SeedSymbolAsync(nativeFileId, targetKey, "compute");
        await SeedOldProjectionAsync(firstPath, firstFileId, firstId, targetId);
        await SeedOldProjectionAsync(secondPath, secondFileId, secondId, targetId);

        var replace = () => _store!.ReplaceFileDerivedProjectionsAsync(
        [
            new FileDerivedProjectionReplacement(
                firstPath,
                Producer,
                [FindingFlavor],
                [Fact(firstKey, "FirstCandidate", FindingFlavor)],
                [EdgeFact(firstKey, targetKey, firstPath, line: 10)]),
            new FileDerivedProjectionReplacement(
                secondPath,
                Producer,
                [FindingFlavor],
                [Fact(secondKey, "SecondCandidate", FindingFlavor)],
                [
                    EdgeFact(
                        secondKey,
                        "c:E:native.h::missing",
                        secondPath,
                        line: 20),
                ]),
        ]);

        await replace.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*native.h::missing*");
        await AssertOldProjectionAsync(firstId, targetId);
        await AssertOldProjectionAsync(secondId, targetId);
    }

    [Fact]
    public async Task Batch_sql_failure_in_second_projection_rolls_back_both_last_good()
    {
        var firstPath = Path.Join(_tempDir, "First.cs");
        var secondPath = Path.Join(_tempDir, "Second.cs");
        var nativePath = Path.Join(_tempDir, "native.h");
        var firstFileId = await SeedFileAsync(firstPath);
        var secondFileId = await SeedFileAsync(secondPath);
        var nativeFileId = await SeedFileAsync(nativePath);
        var firstKey = "csharp:M:NativeMethods.First";
        var secondKey = "csharp:M:NativeMethods.Second";
        var targetKey = "c:E:native.h::compute";
        var firstId = await SeedSymbolAsync(firstFileId, firstKey, "First");
        var secondId = await SeedSymbolAsync(secondFileId, secondKey, "Second");
        var targetId = await SeedSymbolAsync(nativeFileId, targetKey, "compute");
        await SeedOldProjectionAsync(firstPath, firstFileId, firstId, targetId);
        await SeedOldProjectionAsync(secondPath, secondFileId, secondId, targetId);
        await ExecuteAsync(
            $"""
            CREATE TRIGGER fail_second_derived_projection
            BEFORE INSERT ON edge_evidence
            WHEN NEW.producer = '{Producer}' AND NEW.start_line = 20
            BEGIN
                SELECT RAISE(ABORT, 'forced second projection failure');
            END;
            """);

        var replace = () => _store!.ReplaceFileDerivedProjectionsAsync(
        [
            new FileDerivedProjectionReplacement(
                firstPath,
                Producer,
                [FindingFlavor],
                [Fact(firstKey, "FirstCandidate", FindingFlavor)],
                [EdgeFact(firstKey, targetKey, firstPath, line: 10)]),
            new FileDerivedProjectionReplacement(
                secondPath,
                Producer,
                [FindingFlavor],
                [Fact(secondKey, "SecondCandidate", FindingFlavor)],
                [EdgeFact(secondKey, targetKey, secondPath, line: 20)]),
        ]);

        await replace.Should().ThrowAsync<SqliteException>();
        await AssertOldProjectionAsync(firstId, targetId);
        await AssertOldProjectionAsync(secondId, targetId);
    }

    [Fact]
    public async Task Batch_duplicate_producing_path_is_rejected_before_cleanup()
    {
        var managedPath = Path.Join(_tempDir, "Managed.cs");
        var nativePath = Path.Join(_tempDir, "native.h");
        var managedFileId = await SeedFileAsync(managedPath);
        var nativeFileId = await SeedFileAsync(nativePath);
        var sourceKey = "csharp:M:NativeMethods.Compute";
        var targetKey = "c:E:native.h::compute";
        var sourceId = await SeedSymbolAsync(managedFileId, sourceKey, "Compute");
        var targetId = await SeedSymbolAsync(nativeFileId, targetKey, "compute");
        await SeedOldProjectionAsync(
            managedPath,
            managedFileId,
            sourceId,
            targetId);

        var replace = () => _store!.ReplaceFileDerivedProjectionsAsync(
        [
            new FileDerivedProjectionReplacement(
                managedPath,
                Producer,
                [FindingFlavor],
                [Fact(sourceKey, "FirstCandidate", FindingFlavor)],
                [EdgeFact(sourceKey, targetKey, managedPath, line: 10)]),
            new FileDerivedProjectionReplacement(
                Path.Join(_tempDir, ".", "Managed.cs"),
                Producer,
                [FindingFlavor],
                [],
                []),
        ]);

        await replace.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*duplicate producing file path*");
        await AssertOldProjectionAsync(sourceId, targetId);
    }

    private async Task SeedOldProjectionAsync(
        string producingPath,
        long producingFileId,
        long sourceId,
        long targetId)
    {
        await _store!.BulkInsertAnnotationsAsync(
            [Stored(sourceId, "OldFinding", FindingFlavor)]);
        await _store.BulkInsertEdgesAsync(
        [
            StoredEdge(
                sourceId,
                targetId,
                producingFileId,
                producingPath,
                line: 2,
                producer: Producer,
                value: "old"),
        ]);
    }

    private async Task AssertOldProjectionAsync(long sourceId, long targetId)
    {
        (await _store!.GetAnnotationsForSymbolAsync(sourceId))
            .Should().ContainSingle()
            .Which.Name.Should().Be("OldFinding");
        var evidence = await _store.ListEdgeEvidenceAsync(
            sourceId,
            targetId,
            "pinvoke-maps-to");
        evidence.Should().ContainSingle();
        evidence[0].Producer.Should().Be(Producer);
        evidence[0].Metadata.Should().Contain("value", "old");
        (await GetEdgePayloadAsync(sourceId, targetId))
            .Should().Be("""{"value":"old"}""");
    }

    private static FileAnnotationFact Fact(
        string canonicalKey,
        string name,
        string flavor,
        string? attributeCanonicalKey = null) =>
        new(
            canonicalKey,
            name,
            $"MedInterop.{name}",
            flavor,
            ArgsJson: null,
            attributeCanonicalKey);

    private static AnnotationRecord Stored(long symbolId, string name, string flavor) =>
        new(
            symbolId,
            name,
            $"MedInterop.{name}",
            flavor,
            ArgsJson: null,
            AttributeSymbolId: null);

    private static ProducerEdgeEvidenceFact EdgeFact(
        string sourceKey,
        string targetKey,
        string producingPath,
        int line,
        IReadOnlyDictionary<string, string>? metadata = null) =>
        new(
            sourceKey,
            targetKey,
            "pinvoke-maps-to",
            metadata ?? new Dictionary<string, string> { ["value"] = "new" },
            new FileEvidenceFact(
                new SourceLocation(producingPath, line, 1, line, 2),
                EvidenceConfidence.Exact,
                Producer,
                Metadata: null));

    private static Edge StoredEdge(
        long sourceId,
        long targetId,
        long producingFileId,
        string producingPath,
        int line,
        string producer,
        string value) =>
        new(
            sourceId,
            targetId,
            "pinvoke-maps-to",
            new Dictionary<string, string> { ["value"] = value })
        {
            Evidence = new Evidence(
                producingFileId,
                new SourceLocation(producingPath, line, 1, line, 2),
                EvidenceConfidence.Exact,
                producer,
                new Dictionary<string, string> { ["value"] = value }),
        };

    private async Task<long> SeedFileAsync(string path) =>
        await _store!.UpsertFileAsync(
            path,
            [1, 2, 3, 4],
            DateTimeOffset.UtcNow);

    private async Task<long> SeedSymbolAsync(long fileId, string canonicalKey, string name) =>
        await _store!.UpsertSymbolAsync(
            canonicalKey,
            new Symbol(
                0,
                name,
                name,
                "method",
                fileId,
                1,
                1,
                2,
                1,
                $"void {name}()",
                null));

    private async Task<bool> EdgeExistsAsync(long sourceId, long targetId) =>
        await ScalarAsync<long>(
            """
            SELECT COUNT(*)
            FROM edges
            WHERE src = @sourceId
              AND dst = @targetId
              AND kind_name = 'pinvoke-maps-to';
            """,
            new { sourceId, targetId }) > 0;

    private async Task<string?> GetEdgePayloadAsync(long sourceId, long targetId) =>
        await ScalarAsync<string?>(
            """
            SELECT payload
            FROM edges
            WHERE src = @sourceId
              AND dst = @targetId
              AND kind_name = 'pinvoke-maps-to';
            """,
            new { sourceId, targetId });

    private async Task<T> ScalarAsync<T>(string sql, object parameters)
    {
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        return (await connection.ExecuteScalarAsync<T>(sql, parameters))!;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        await connection.ExecuteAsync(sql);
    }
}
