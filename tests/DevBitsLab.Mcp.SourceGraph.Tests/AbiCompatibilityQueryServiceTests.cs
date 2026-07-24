using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Interop;
using DevBitsLab.Mcp.SourceGraph.Server.Interop;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class AbiCompatibilityQueryServiceTests : IAsyncLifetime
{
    private static readonly InteropTarget Target =
        InteropTarget.WindowsX64Msvc;
    private string _tempDirectory = string.Empty;
    private SqliteGraphStore? _store;

    public async Task InitializeAsync()
    {
        _tempDirectory = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-abi-query-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _store = new SqliteGraphStore(
            Path.Join(_tempDirectory, "graph.db"));
        await _store.EnsureSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        if (_store is not null)
        {
            await _store.DisposeAsync();
        }
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task Exact_keys_return_compatible_typed_checks_and_exact_evidence()
    {
        var managed = await SeedRecordAsync(
            "managed/Packet.cs",
            "csharp:T:Fixture.Packet",
            "Packet",
            AbiRecordKind.Sequential,
            pack: 8);
        var native = await SeedRecordAsync(
            "native/packet.h",
            "cpp:T:native/packet.h::Packet",
            "Packet",
            AbiRecordKind.Native,
            pack: 8);

        var result = await Service().QueryAsync(
            "scope-a",
            "ok",
            _store!,
            CompleteState(),
            managed.Layout.SymbolCanonicalKey,
            native.Layout.SymbolCanonicalKey,
            managedInputComplete: true);

        result.Status.Should().Be("ok");
        result.Compatibility.Should().Be("compatible");
        result.Partial.Should().BeFalse();
        result.ManagedSelection.Status.Should().Be("selected");
        result.NativeSelection.Status.Should().Be("selected");
        result.Checks.Should().NotBeEmpty();
        result.Checks.Should().OnlyContain(check =>
            check.Compatibility == "compatible");
        result.Finding.Should().BeNull();
        result.TotalFindingCount.Should().Be(0);
        result.ManagedRecord!.CanonicalKey.Should().Be(
            managed.Layout.SymbolCanonicalKey);
        result.NativeRecord!.CanonicalKey.Should().Be(
            native.Layout.SymbolCanonicalKey);
        result.Checks.SelectMany(check => check.Evidence)
            .Should().Contain(evidence =>
                evidence.FilePath == managed.Path
                && evidence.StartLine == 1
                && evidence.StartColumn == 2
                && evidence.EndLine == 1
                && evidence.EndColumn == 20);
    }

    [Fact]
    public async Task Proven_layout_mismatch_returns_error_and_Interop002_finding()
    {
        var managed = await SeedRecordAsync(
            "managed/Packet.cs",
            "csharp:T:Fixture.Packet",
            "Packet",
            AbiRecordKind.Sequential,
            pack: 8);
        var native = await SeedRecordAsync(
            "native/packet.h",
            "c:T:native/packet.h::Packet",
            "Packet",
            AbiRecordKind.Native,
            pack: 1);

        var result = await Service().QueryAsync(
            "scope-a",
            "ok",
            _store!,
            CompleteState(),
            managed.Layout.SymbolCanonicalKey,
            native.Layout.SymbolCanonicalKey,
            managedInputComplete: true);

        result.Compatibility.Should().Be("error");
        result.Checks.Should().Contain(check =>
            check.Aspect == "pack"
            && check.Compatibility == "error");
        result.Reasons.Should().NotBeEmpty();
        result.Finding.Should().NotBeNull();
        result.Finding!.RuleId.Should().Be("Interop002");
        result.Finding.Severity.Should().Be("error");
        result.Finding.ManagedSymbol.Should().Be(
            managed.Layout.SymbolCanonicalKey);
        result.Finding.NativeSymbol.Should().Be(
            native.Layout.SymbolCanonicalKey);
        result.TotalFindingCount.Should().Be(1);
    }

    [Fact]
    public async Task Selected_target_mismatch_is_partial_unknown_without_engine_checks()
    {
        var managed = await SeedRecordAsync(
            "managed/Packet.cs",
            "csharp:T:Fixture.Packet",
            "Packet",
            AbiRecordKind.Sequential,
            pack: 8,
            target: InteropTarget.WindowsX86Msvc);
        var native = await SeedRecordAsync(
            "native/packet.h",
            "cpp:T:native/packet.h::Packet",
            "Packet",
            AbiRecordKind.Native,
            pack: 8,
            target: InteropTarget.WindowsX86Msvc);

        var result = await Service().QueryAsync(
            "scope-a",
            "ok",
            _store!,
            CompleteState(),
            managed.Layout.SymbolCanonicalKey,
            native.Layout.SymbolCanonicalKey,
            managedInputComplete: true);

        result.Status.Should().Be("partial");
        result.Compatibility.Should().Be("unknown");
        result.Partial.Should().BeTrue();
        result.Checks.Should().BeEmpty();
        result.Finding.Should().BeNull();
        result.Failures.Should().Contain(failure =>
            failure.Code == "managed-target-mismatch");
        result.Failures.Should().Contain(failure =>
            failure.Code == "native-target-mismatch");
    }

    [Fact]
    public async Task Partial_runtime_never_compares_retained_last_good_records()
    {
        var managed = await SeedRecordAsync(
            "managed/Packet.cs",
            "csharp:T:Fixture.Packet",
            "Packet",
            AbiRecordKind.Sequential,
            pack: 8);
        var native = await SeedRecordAsync(
            "native/packet.h",
            "cpp:T:native/packet.h::Packet",
            "Packet",
            AbiRecordKind.Native,
            pack: 8);
        var state = CompleteState() with
        {
            Status = NativeInteropRuntimeStatus.Partial,
            RetainedLastGood = true,
            IsExportUniverseComplete = false,
        };

        var result = await Service().QueryAsync(
            "scope-a",
            "partial",
            _store!,
            state,
            managed.Layout.SymbolCanonicalKey,
            native.Layout.SymbolCanonicalKey,
            managedInputComplete: false);

        result.Compatibility.Should().Be("unknown");
        result.Partial.Should().BeTrue();
        result.RetainedLastGood.Should().BeTrue();
        result.Checks.Should().BeEmpty();
        result.ManagedSelection.Candidates.Should().BeEmpty();
        result.NativeSelection.Candidates.Should().BeEmpty();
        result.ManagedRecord.Should().BeNull();
        result.NativeRecord.Should().BeNull();
        result.Failures.Should().Contain(failure =>
            failure.Code == "retained-last-good");
        result.Failures.Should().Contain(failure =>
            failure.Code == "export-universe-incomplete");
    }

    [Fact]
    public async Task Name_query_with_multiple_managed_records_is_ambiguous()
    {
        var first = await SeedRecordAsync(
            "managed/A.cs",
            "csharp:T:Fixture.A.Packet",
            "Packet",
            AbiRecordKind.Sequential,
            pack: 8);
        var second = await SeedRecordAsync(
            "managed/B.cs",
            "csharp:T:Fixture.B.Packet",
            "Packet",
            AbiRecordKind.Sequential,
            pack: 8);
        var native = await SeedRecordAsync(
            "native/packet.h",
            "cpp:T:native/packet.h::Packet",
            "NativePacket",
            AbiRecordKind.Native,
            pack: 8);

        var result = await Service().QueryAsync(
            "scope-a",
            "ok",
            _store!,
            CompleteState(),
            "Packet",
            native.Layout.SymbolCanonicalKey,
            managedInputComplete: true);

        result.Compatibility.Should().Be("unknown");
        result.Partial.Should().BeTrue();
        result.ManagedSelection.Status.Should().Be("ambiguous");
        result.ManagedSelection.Candidates
            .Select(candidate => candidate.CanonicalKey)
            .Should().Equal(
                first.Layout.SymbolCanonicalKey,
                second.Layout.SymbolCanonicalKey);
        result.NativeSelection.Status.Should().Be("selected");
        result.Checks.Should().BeEmpty();
        result.Failures.Should().Contain(failure =>
            failure.Code == "managed-record-ambiguous");
    }

    [Fact]
    public async Task Explicit_nested_mapping_resolves_exact_records_without_name_guessing()
    {
        var managedChild = await SeedRecordAsync(
            "managed/Child.cs",
            "csharp:T:Fixture.Child",
            "Child",
            AbiRecordKind.Sequential,
            pack: 8);
        var nativeChild = await SeedRecordAsync(
            "native/child.h",
            "cpp:T:native/child.h::Child",
            "NativeChild",
            AbiRecordKind.Native,
            pack: 8);
        var managedRoot = await SeedRecordAsync(
            "managed/Parent.cs",
            "csharp:T:Fixture.Parent",
            "Parent",
            AbiRecordKind.Sequential,
            pack: 8,
            fieldType: new AbiTypeRef(
                "Fixture.Child",
                AbiTypeCategory.Record,
                sizeBytes: 4,
                alignmentBytes: 4));
        var nativeRoot = await SeedRecordAsync(
            "native/parent.h",
            "cpp:T:native/parent.h::Parent",
            "NativeParent",
            AbiRecordKind.Native,
            pack: 8,
            fieldType: new AbiTypeRef(
                "native::Child",
                AbiTypeCategory.Record,
                sizeBytes: 4,
                alignmentBytes: 4));
        AbiRecordMappingQuery[] mappings =
        [
            new(
                "Fixture.Child",
                "native::Child",
                managedChild.Layout.SymbolCanonicalKey,
                nativeChild.Layout.SymbolCanonicalKey),
        ];

        var result = await Service().QueryAsync(
            "scope-a",
            "ok",
            _store!,
            CompleteState(),
            managedRoot.Layout.SymbolCanonicalKey,
            nativeRoot.Layout.SymbolCanonicalKey,
            managedInputComplete: true,
            nestedMappings: mappings);

        result.Compatibility.Should().Be("compatible");
        result.Checks.Should().Contain(check =>
            check.Aspect == "nested_record_identity"
            && check.Compatibility == "compatible");
        result.Checks.Should().Contain(check =>
            check.Aspect == "nested_record_layout"
            && check.Compatibility == "compatible");
    }

    [Fact]
    public async Task Missing_nested_mapping_warns_and_missing_mapping_record_fails_closed()
    {
        var managedRoot = await SeedRecordAsync(
            "managed/Parent.cs",
            "csharp:T:Fixture.Parent",
            "Parent",
            AbiRecordKind.Sequential,
            pack: 8,
            fieldType: new AbiTypeRef(
                "Fixture.Child",
                AbiTypeCategory.Record,
                sizeBytes: 4,
                alignmentBytes: 4));
        var nativeRoot = await SeedRecordAsync(
            "native/parent.h",
            "cpp:T:native/parent.h::Parent",
            "NativeParent",
            AbiRecordKind.Native,
            pack: 8,
            fieldType: new AbiTypeRef(
                "native::Child",
                AbiTypeCategory.Record,
                sizeBytes: 4,
                alignmentBytes: 4));

        var warning = await Service().QueryAsync(
            "scope-a",
            "ok",
            _store!,
            CompleteState(),
            managedRoot.Layout.SymbolCanonicalKey,
            nativeRoot.Layout.SymbolCanonicalKey,
            managedInputComplete: true);

        warning.Compatibility.Should().Be("warning");
        warning.Checks.Should().Contain(check =>
            check.Aspect == "nested_record_identity"
            && check.Compatibility == "warning");
        warning.Finding!.RuleId.Should().Be("Interop002");

        AbiRecordMappingQuery[] invalid =
        [
            new(
                "Fixture.Child",
                "native::Child",
                "csharp:T:Fixture.Missing",
                "cpp:T:native/missing.h::Missing"),
        ];
        var failed = await Service().QueryAsync(
            "scope-a",
            "ok",
            _store!,
            CompleteState(),
            managedRoot.Layout.SymbolCanonicalKey,
            nativeRoot.Layout.SymbolCanonicalKey,
            managedInputComplete: true,
            nestedMappings: invalid);

        failed.Compatibility.Should().Be("unknown");
        failed.Partial.Should().BeTrue();
        failed.Checks.Should().BeEmpty();
        failed.Failures.Should().Contain(failure =>
            failure.Code == "mapping-record-not-found");
    }

    [Fact]
    public async Task Malformed_or_conflicting_payload_makes_entire_query_partial_unknown()
    {
        var managed = await SeedRecordAsync(
            "managed/Packet.cs",
            "csharp:T:Fixture.Packet",
            "Packet",
            AbiRecordKind.Sequential,
            pack: 8);
        var native = await SeedRecordAsync(
            "native/packet.h",
            "cpp:T:native/packet.h::Packet",
            "Packet",
            AbiRecordKind.Native,
            pack: 8);
        var malformedOwner = await SeedOwnerAsync(
            "managed/Broken.cs",
            "csharp:T:Fixture.Broken",
            "Broken",
            "struct");
        await _store!.BulkInsertAnnotationsAsync(
        [
            Annotation(malformedOwner.SymbolId, "{}"),
        ]);

        var malformed = await Service().QueryAsync(
            "scope-a",
            "ok",
            _store,
            CompleteState(),
            managed.Layout.SymbolCanonicalKey,
            native.Layout.SymbolCanonicalKey,
            managedInputComplete: true);

        malformed.Compatibility.Should().Be("unknown");
        malformed.Partial.Should().BeTrue();
        malformed.Checks.Should().BeEmpty();
        malformed.Failures.Should().Contain(failure =>
            failure.Stage == "fact-read");

        await _store.BulkInsertAnnotationsAsync(
        [
            Annotation(
                managed.SymbolId,
                InteropFactPayloadCodec.EncodeAbiRecord(
                    managed.Layout with { Pack = 1 })),
        ]);
        var conflicting = await Service().QueryAsync(
            "scope-a",
            "ok",
            _store,
            CompleteState(),
            managed.Layout.SymbolCanonicalKey,
            native.Layout.SymbolCanonicalKey,
            managedInputComplete: true);

        conflicting.Compatibility.Should().Be("unknown");
        conflicting.Checks.Should().BeEmpty();
        conflicting.Failures.Should().Contain(failure =>
            failure.Stage == "fact-read"
            && failure.Message.Contains(
                "Conflicting",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task Mapping_limit_and_wrong_record_scheme_fail_closed()
    {
        var managed = await SeedRecordAsync(
            "managed/Packet.cs",
            "csharp:T:Fixture.Packet",
            "Packet",
            AbiRecordKind.Sequential,
            pack: 8);
        var native = await SeedRecordAsync(
            "native/packet.h",
            "cpp:T:native/packet.h::Packet",
            "Packet",
            AbiRecordKind.Native,
            pack: 8);
        var mappings = Enumerable.Range(
                0,
                AbiCompatibilityQueryService.MaximumNestedMappings + 1)
            .Select(index => new AbiRecordMappingQuery(
                $"Fixture.Child{index}",
                $"native::Child{index}",
                managed.Layout.SymbolCanonicalKey,
                native.Layout.SymbolCanonicalKey))
            .ToArray();

        var overLimit = await Service().QueryAsync(
            "scope-a",
            "ok",
            _store!,
            CompleteState(),
            managed.Layout.SymbolCanonicalKey,
            native.Layout.SymbolCanonicalKey,
            managedInputComplete: true,
            nestedMappings: mappings);
        overLimit.Compatibility.Should().Be("unknown");
        overLimit.Failures.Should().Contain(failure =>
            failure.Code == "mapping-limit-exceeded");

        var wrongRole = await Service().QueryAsync(
            "scope-a",
            "ok",
            _store!,
            CompleteState(),
            native.Layout.SymbolCanonicalKey,
            native.Layout.SymbolCanonicalKey,
            managedInputComplete: true);
        wrongRole.Compatibility.Should().Be("unknown");
        wrongRole.ManagedSelection.Status.Should().Be("invalid");
        wrongRole.Failures.Should().Contain(failure =>
            failure.Code == "managed-selection-role-scheme-invalid");
    }

    [Fact]
    public async Task Ten_thousand_symbol_hits_never_prove_unique_selection()
    {
        var managed = await SeedRecordAsync(
            "managed/Packet.cs",
            "csharp:T:Fixture.Packet",
            "Packet",
            AbiRecordKind.Sequential,
            pack: 8);
        var native = await SeedRecordAsync(
            "native/packet.h",
            "cpp:T:native/packet.h::Packet",
            "NativePacket",
            AbiRecordKind.Native,
            pack: 8);
        await SeedSearchOnlySymbolsAsync(
            managed.FileId,
            AbiCompatibilityQueryService.MaximumSearchHits - 1);

        var result = await Service().QueryAsync(
            "scope-a",
            "ok",
            _store!,
            CompleteState(),
            "Packet",
            native.Layout.SymbolCanonicalKey,
            managedInputComplete: true);

        result.Compatibility.Should().Be("unknown");
        result.Partial.Should().BeTrue();
        result.ManagedSelection.Status.Should().Be("unknown");
        result.Checks.Should().BeEmpty();
        result.Failures.Should().Contain(failure =>
            failure.Code == "managed-search-bound-reached");
    }

    private AbiCompatibilityQueryService Service() => new();

    private async Task<SeededRecord> SeedRecordAsync(
        string relativePath,
        string key,
        string name,
        AbiRecordKind kind,
        int? pack,
        InteropTarget? target = null,
        AbiTypeRef? fieldType = null)
    {
        var owner = await SeedOwnerAsync(
            relativePath,
            key,
            name,
            kind == AbiRecordKind.Native
                ? "native-record"
                : "struct");
        var type = fieldType ?? Int32Type();
        var layout = new AbiRecordLayout(
            key,
            kind,
            SizeBytes: 4,
            AlignmentBytes: 4,
            pack,
            [
                new AbiFieldLayout(
                    0,
                    "Value",
                    type,
                    OffsetBytes: 0,
                    SizeBytes: 4,
                    EvidenceAt(
                        owner.FileId,
                        owner.Path,
                        kind == AbiRecordKind.Native,
                        line: 2)),
            ],
            target ?? Target,
            EvidenceAt(
                owner.FileId,
                owner.Path,
                kind == AbiRecordKind.Native,
                line: 1));
        await _store!.BulkInsertAnnotationsAsync(
        [
            Annotation(
                owner.SymbolId,
                InteropFactPayloadCodec.EncodeAbiRecord(layout)),
        ]);
        return new SeededRecord(
            owner.FileId,
            owner.SymbolId,
            owner.Path,
            layout);
    }

    private async Task<Owner> SeedOwnerAsync(
        string relativePath,
        string key,
        string name,
        string kind)
    {
        var path = Path.GetFullPath(
            Path.Join(_tempDirectory, relativePath));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "// fixture");
        var fileId = await _store!.UpsertFileAsync(
            path,
            [1, 2, 3, 4],
            DateTimeOffset.UtcNow);
        var symbolId = await _store.UpsertSymbolAsync(
            key,
            new Symbol(
                0,
                name,
                name,
                kind,
                fileId,
                1,
                2,
                3,
                4,
                null,
                null));
        return new Owner(fileId, symbolId, path);
    }

    private async Task SeedSearchOnlySymbolsAsync(
        long fileId,
        int count)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={Path.Join(_tempDirectory, "graph.db")};Pooling=False");
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO symbols(
                canonical_key,
                name,
                fqn,
                kind_name,
                file_id,
                start_line,
                start_col,
                end_line,
                end_col)
            VALUES(
                @key,
                'Packet',
                @fqn,
                'struct',
                @fileId,
                1,
                1,
                1,
                10);
            """;
        var key = command.Parameters.Add("@key", SqliteType.Text);
        var fqn = command.Parameters.Add("@fqn", SqliteType.Text);
        command.Parameters.AddWithValue("@fileId", fileId);
        for (var index = 0; index < count; index++)
        {
            key.Value = $"csharp:T:Fixture.SearchOnly{index:D5}";
            fqn.Value = $"Fixture.SearchOnly{index:D5}.Packet";
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    private static AnnotationRecord Annotation(
        long symbolId,
        string payload) =>
        new(
            symbolId,
            "InteropFact",
            "MedInterop.AbiRecord",
            InteropAnnotationFlavors.AbiRecord,
            payload,
            AttributeSymbolId: null);

    private static Evidence EvidenceAt(
        long fileId,
        string path,
        bool native,
        int line) =>
        new(
            fileId,
            new SourceLocation(
                path,
                line,
                2,
                line,
                20),
            native
                ? EvidenceConfidence.Exact
                : EvidenceConfidence.Semantic,
            native
                ? "clang-native-layout"
                : "roslyn-managed-layout",
            new Dictionary<string, string>
            {
                ["source"] = native ? "header" : "managed",
            });

    private static AbiTypeRef Int32Type() =>
        new(
            "int32",
            AbiTypeCategory.SignedInteger,
            sizeBytes: 4,
            alignmentBytes: 4,
            isSigned: true);

    private static NativeInteropRuntimeState CompleteState() =>
        new(
            NativeInteropRuntimeStatus.Complete,
            Target,
            LastAttemptAt: DateTimeOffset.UtcNow,
            LastSuccessfulAt: DateTimeOffset.UtcNow,
            RetainedLastGood: false,
            IsExportUniverseComplete: true,
            TranslationUnits: 1,
            IncludedFiles: 1,
            NativeSymbols: 1,
            ManagedMatches: 0,
            Findings: 0,
            BoundaryEdges: 0,
            PendingStaleSymbols: 0,
            Failures: []);

    private sealed record Owner(
        long FileId,
        long SymbolId,
        string Path);

    private sealed record SeededRecord(
        long FileId,
        long SymbolId,
        string Path,
        AbiRecordLayout Layout);
}
