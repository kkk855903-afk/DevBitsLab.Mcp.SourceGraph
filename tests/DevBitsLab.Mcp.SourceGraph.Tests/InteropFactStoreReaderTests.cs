using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Interop;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class InteropFactStoreReaderTests : IAsyncLifetime
{
    private string _tempDir = string.Empty;
    private SqliteGraphStore? _store;

    public async Task InitializeAsync()
    {
        _tempDir = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-interop-reader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _store = new SqliteGraphStore(Path.Join(_tempDir, "graph.db"));
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
    public async Task Reads_each_strict_payload_flavor_with_store_owner_identity()
    {
        var managed = await SeedOwnerAsync("managed.cs", "csharp:M:Native.Run", "Run");
        var native = await SeedOwnerAsync("native.h", "cpp:function:run", "run");
        var record = await SeedOwnerAsync("types.h", "cpp:record:sample", "sample");
        await _store!.BulkInsertAnnotationsAsync(
        [
            Annotation(
                managed.SymbolId,
                InteropAnnotationFlavors.ManagedImport,
                InteropFactPayloadCodec.EncodeManagedImport(
                    ManagedFact(managed.FileId, managed.Key))),
            Annotation(
                native.SymbolId,
                InteropAnnotationFlavors.NativeExport,
                InteropFactPayloadCodec.EncodeNativeExport(
                    NativeFact(native.FileId, native.Key))),
            Annotation(
                record.SymbolId,
                InteropAnnotationFlavors.AbiRecord,
                InteropFactPayloadCodec.EncodeAbiRecord(
                    RecordFact(record.FileId, record.Key))),
        ]);

        var managedSnapshot =
            await InteropFactStoreReader.ReadManagedImportsAsync(_store);
        var nativeSnapshot =
            await InteropFactStoreReader.ReadNativeExportsAsync(_store);
        var recordSnapshot =
            await InteropFactStoreReader.ReadAbiRecordsAsync(_store);

        managedSnapshot.IsComplete.Should().BeTrue();
        managedSnapshot.Facts.Should().ContainSingle();
        managedSnapshot.Facts[0].Row.FilePath.Should().Be(managed.Path);
        managedSnapshot.Facts[0].Fact.Evidence.ProducingFileId
            .Should().Be(managed.FileId);
        nativeSnapshot.IsComplete.Should().BeTrue();
        nativeSnapshot.Facts.Should().ContainSingle();
        nativeSnapshot.Facts[0].Fact.Evidence.ProducingFileId
            .Should().Be(native.FileId);
        recordSnapshot.IsComplete.Should().BeTrue();
        recordSnapshot.Facts.Should().ContainSingle();
        recordSnapshot.Facts[0].Fact.Evidence.ProducingFileId
            .Should().Be(record.FileId);
    }

    [Fact]
    public async Task Reads_match_and_multiple_findings_for_one_managed_boundary()
    {
        var managed = await SeedOwnerAsync(
            "analysis.cs",
            "csharp:M:Native.Analyze",
            "Analyze");
        var match = MatchFact(managed.Key);
        var firstFinding = FindingFact(
            managed.Key,
            "Interop003",
            "Parameter 0 has an ABI mismatch.");
        var secondFinding = FindingFact(
            managed.Key,
            "Interop003",
            "Parameter 1 has an ABI mismatch.");
        await _store!.BulkInsertAnnotationsAsync(
        [
            Annotation(
                managed.SymbolId,
                InteropAnnotationFlavors.Match,
                InteropFactPayloadCodec.EncodeMatch(match)),
            Annotation(
                managed.SymbolId,
                InteropAnnotationFlavors.Finding,
                InteropFactPayloadCodec.EncodeFinding(firstFinding)),
            Annotation(
                managed.SymbolId,
                InteropAnnotationFlavors.Finding,
                InteropFactPayloadCodec.EncodeFinding(secondFinding)),
        ]);

        var matches = await InteropFactStoreReader.ReadMatchesAsync(_store);
        var findings = await InteropFactStoreReader.ReadFindingsAsync(_store);

        matches.IsComplete.Should().BeTrue();
        matches.Facts.Should().ContainSingle()
            .Which.Fact.Should().BeEquivalentTo(match);
        findings.IsComplete.Should().BeTrue();
        findings.Facts.Select(item => item.Fact)
            .Should()
            .BeEquivalentTo([firstFinding, secondFinding]);
    }

    [Fact]
    public async Task Malformed_and_host_mismatched_rows_fail_closed()
    {
        var malformed = await SeedOwnerAsync(
            "malformed.cs",
            "csharp:M:Native.Malformed",
            "Malformed");
        var mismatched = await SeedOwnerAsync(
            "mismatch.cs",
            "csharp:M:Native.Host",
            "Host");
        await _store!.BulkInsertAnnotationsAsync(
        [
            Annotation(
                malformed.SymbolId,
                InteropAnnotationFlavors.ManagedImport,
                """{"v":1}"""),
            Annotation(
                mismatched.SymbolId,
                InteropAnnotationFlavors.ManagedImport,
                InteropFactPayloadCodec.EncodeManagedImport(
                    ManagedFact(
                        mismatched.FileId,
                        "csharp:M:Native.Payload"))),
        ]);

        var snapshot =
            await InteropFactStoreReader.ReadManagedImportsAsync(_store);

        snapshot.IsComplete.Should().BeFalse();
        snapshot.Facts.Should().BeEmpty();
        snapshot.Failures.Should().HaveCount(2);
        snapshot.Failures.Should().Contain(failure =>
            failure.FilePath == mismatched.Path
            && failure.Reason.Contains(
                "canonical key",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Identical_duplicate_payloads_are_deduplicated()
    {
        var owner = await SeedOwnerAsync(
            "duplicate.cs",
            "csharp:M:Native.Duplicate",
            "Duplicate");
        var payload = InteropFactPayloadCodec.EncodeManagedImport(
            ManagedFact(owner.FileId, owner.Key));
        await _store!.BulkInsertAnnotationsAsync(
        [
            Annotation(
                owner.SymbolId,
                InteropAnnotationFlavors.ManagedImport,
                payload),
            Annotation(
                owner.SymbolId,
                InteropAnnotationFlavors.ManagedImport,
                payload),
        ]);

        var snapshot =
            await InteropFactStoreReader.ReadManagedImportsAsync(_store);

        snapshot.IsComplete.Should().BeTrue();
        snapshot.Facts.Should().ContainSingle();
    }

    [Fact]
    public async Task Conflicting_payloads_for_one_key_remove_the_fact()
    {
        var owner = await SeedOwnerAsync(
            "conflict.cs",
            "csharp:M:Native.Conflict",
            "Conflict");
        var first = ManagedFact(owner.FileId, owner.Key);
        var second = first with { EntryPoint = "different" };
        await _store!.BulkInsertAnnotationsAsync(
        [
            Annotation(
                owner.SymbolId,
                InteropAnnotationFlavors.ManagedImport,
                InteropFactPayloadCodec.EncodeManagedImport(first)),
            Annotation(
                owner.SymbolId,
                InteropAnnotationFlavors.ManagedImport,
                InteropFactPayloadCodec.EncodeManagedImport(second)),
        ]);

        var snapshot =
            await InteropFactStoreReader.ReadManagedImportsAsync(_store);

        snapshot.IsComplete.Should().BeFalse();
        snapshot.Facts.Should().BeEmpty();
        snapshot.Failures.Should().ContainSingle(failure =>
            failure.Reason.Contains("Conflicting", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Conflicting_managed_usage_claims_at_one_call_site_fail_closed()
    {
        var owner = await SeedOwnerAsync(
            "caller.cs",
            "csharp:M:Caller.Run",
            "Run");
        var callback = new ManagedCallbackUsageProjection(
            "csharp:M:Native.Register",
            new ManagedCallbackUsage(
                0,
                owner.Key,
                CallbackGcRooting.Unrooted,
                InteropTarget.WindowsX64Msvc,
                EvidenceFor(owner.FileId, owner.Path)));
        var release = new ManagedReturnReleaseProjection(
            "csharp:M:Native.Allocate",
            new ManagedReturnRelease(
                owner.Key,
                InteropAllocatorFamily.CoTaskMem,
                InteropTarget.WindowsX64Msvc,
                EvidenceFor(owner.FileId, owner.Path)));
        await _store!.BulkInsertAnnotationsAsync(
        [
            Annotation(
                owner.SymbolId,
                InteropAnnotationFlavors.ManagedCallbackUsage,
                InteropFactPayloadCodec.EncodeManagedCallbackUsage(
                    callback)),
            Annotation(
                owner.SymbolId,
                InteropAnnotationFlavors.ManagedCallbackUsage,
                InteropFactPayloadCodec.EncodeManagedCallbackUsage(
                    callback with
                    {
                        Usage = callback.Usage with
                        {
                            Rooting = CallbackGcRooting.Rooted,
                        },
                    })),
            Annotation(
                owner.SymbolId,
                InteropAnnotationFlavors.ManagedReturnRelease,
                InteropFactPayloadCodec.EncodeManagedReturnRelease(
                    release)),
            Annotation(
                owner.SymbolId,
                InteropAnnotationFlavors.ManagedReturnRelease,
                InteropFactPayloadCodec.EncodeManagedReturnRelease(
                    release with
                    {
                        Release = release.Release with
                        {
                            ReleaseFamily =
                                InteropAllocatorFamily.HGlobal,
                        },
                    })),
        ]);

        var callbacks =
            await InteropFactStoreReader.ReadManagedCallbackUsagesAsync(
                _store);
        var releases =
            await InteropFactStoreReader.ReadManagedReturnReleasesAsync(
                _store);

        callbacks.IsComplete.Should().BeFalse();
        callbacks.Facts.Should().BeEmpty();
        callbacks.Failures.Should().ContainSingle(failure =>
            failure.Reason.Contains(
                "Conflicting",
                StringComparison.Ordinal));
        releases.IsComplete.Should().BeFalse();
        releases.Facts.Should().BeEmpty();
        releases.Failures.Should().ContainSingle(failure =>
            failure.Reason.Contains(
                "Conflicting",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task Managed_usage_identity_normalizes_rid_and_uses_owner_range()
    {
        var owner = await SeedOwnerAsync(
            "aliased-caller.cs",
            "csharp:M:Caller.Aliased",
            "Aliased");
        var equivalentPath = Path.Join(
            Path.GetDirectoryName(owner.Path)!,
            ".",
            Path.GetFileName(owner.Path));
        var aliasedTarget = new InteropTarget(
            "WIN-X64",
            InteropArchitecture.X64,
            InteropCompilerAbi.Msvc,
            pointerSizeBytes: 8,
            defaultPack: 8);
        var callback = new ManagedCallbackUsageProjection(
            "csharp:M:Native.Register",
            new ManagedCallbackUsage(
                0,
                owner.Key,
                CallbackGcRooting.Unrooted,
                InteropTarget.WindowsX64Msvc,
                EvidenceFor(owner.FileId, owner.Path)));
        var release = new ManagedReturnReleaseProjection(
            "csharp:M:Native.Allocate",
            new ManagedReturnRelease(
                owner.Key,
                InteropAllocatorFamily.CoTaskMem,
                InteropTarget.WindowsX64Msvc,
                EvidenceFor(owner.FileId, owner.Path)));
        await _store!.BulkInsertAnnotationsAsync(
        [
            Annotation(
                owner.SymbolId,
                InteropAnnotationFlavors.ManagedCallbackUsage,
                InteropFactPayloadCodec.EncodeManagedCallbackUsage(
                    callback)),
            Annotation(
                owner.SymbolId,
                InteropAnnotationFlavors.ManagedCallbackUsage,
                InteropFactPayloadCodec.EncodeManagedCallbackUsage(
                    callback with
                    {
                        Usage = callback.Usage with
                        {
                            Rooting = CallbackGcRooting.Rooted,
                            Target = aliasedTarget,
                            Evidence = EvidenceFor(
                                owner.FileId,
                                equivalentPath),
                        },
                    })),
            Annotation(
                owner.SymbolId,
                InteropAnnotationFlavors.ManagedReturnRelease,
                InteropFactPayloadCodec.EncodeManagedReturnRelease(
                    release)),
            Annotation(
                owner.SymbolId,
                InteropAnnotationFlavors.ManagedReturnRelease,
                InteropFactPayloadCodec.EncodeManagedReturnRelease(
                    release with
                    {
                        Release = release.Release with
                        {
                            ReleaseFamily =
                                InteropAllocatorFamily.HGlobal,
                            Target = aliasedTarget,
                            Evidence = EvidenceFor(
                                owner.FileId,
                                equivalentPath),
                        },
                    })),
        ]);

        var callbacks =
            await InteropFactStoreReader.ReadManagedCallbackUsagesAsync(
                _store);
        var releases =
            await InteropFactStoreReader.ReadManagedReturnReleasesAsync(
                _store);

        callbacks.IsComplete.Should().BeFalse();
        callbacks.Facts.Should().BeEmpty();
        callbacks.Failures.Should().ContainSingle(failure =>
            failure.Reason.Contains(
                "Conflicting",
                StringComparison.Ordinal));
        releases.IsComplete.Should().BeFalse();
        releases.Facts.Should().BeEmpty();
        releases.Failures.Should().ContainSingle(failure =>
            failure.Reason.Contains(
                "Conflicting",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task Row_bound_is_reported_as_truncation_instead_of_completeness()
    {
        for (var index = 0; index < 3; index++)
        {
            var owner = await SeedOwnerAsync(
                $"bounded-{index}.cs",
                $"csharp:M:Native.Bounded{index}",
                $"Bounded{index}");
            await _store!.BulkInsertAnnotationsAsync(
            [
                Annotation(
                    owner.SymbolId,
                    InteropAnnotationFlavors.ManagedImport,
                    InteropFactPayloadCodec.EncodeManagedImport(
                        ManagedFact(owner.FileId, owner.Key))),
            ]);
        }

        var snapshot =
            await InteropFactStoreReader.ReadManagedImportsAsync(
                _store!,
                maximumRows: 2);

        snapshot.IsComplete.Should().BeFalse();
        snapshot.WasTruncated.Should().BeTrue();
        snapshot.Facts.Should().HaveCount(2);
        snapshot.Failures.Should().ContainSingle(failure =>
            failure.AnnotationId == null);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100001)]
    public async Task Rejects_invalid_row_bounds(int maximumRows)
    {
        var act = () => InteropFactStoreReader.ReadManagedImportsAsync(
            _store!,
            maximumRows);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Honors_cancellation_before_reading()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var act = () => InteropFactStoreReader.ReadManagedImportsAsync(
            _store!,
            cancellationToken: cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private async Task<Owner> SeedOwnerAsync(
        string fileName,
        string canonicalKey,
        string name)
    {
        var path = Path.Join(_tempDir, fileName);
        var fileId = await _store!.UpsertFileAsync(
            path,
            [1, 2, 3, 4],
            DateTimeOffset.UtcNow);
        var symbolId = await _store.UpsertSymbolAsync(
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
        return new Owner(fileId, symbolId, canonicalKey, path);
    }

    private static AnnotationRecord Annotation(
        long symbolId,
        string flavor,
        string payload) =>
        new(
            symbolId,
            "InteropFact",
            "MedInterop.InteropFact",
            flavor,
            payload,
            AttributeSymbolId: null);

    private static ManagedImport ManagedFact(long ownerFileId, string key) =>
        new(
            key,
            ManagedImportKind.DllImport,
            "native.dll",
            "run",
            InteropCallingConvention.Cdecl,
            VoidType,
            [],
            CharacterSet: null,
            SetLastError: false,
            InteropTarget.WindowsX64Msvc,
            EvidenceFor(ownerFileId, "managed.cs"))
        {
            ExactSpelling = true,
        };

    private static NativeExport NativeFact(long ownerFileId, string key) =>
        new(
            key,
            "run",
            InteropCallingConvention.Cdecl,
            VoidType,
            [],
            HasCLinkage: true,
            IsBinaryVerified: true,
            InteropTarget.WindowsX64Msvc,
            EvidenceFor(ownerFileId, "native.h"))
        {
            LibraryName = "native.dll",
            ModuleIdentitySource = NativeModuleIdentitySource.Binary,
        };

    private static AbiRecordLayout RecordFact(long ownerFileId, string key) =>
        new(
            key,
            AbiRecordKind.Native,
            SizeBytes: 4,
            AlignmentBytes: 4,
            Pack: 4,
            Fields: [],
            InteropTarget.WindowsX64Msvc,
            EvidenceFor(ownerFileId, "types.h"));

    private static InteropMatchProjection MatchFact(string managedKey) =>
        new(
            managedKey,
            "cpp:function:run",
            InteropMatchStatus.Matched,
            EvidenceConfidence.Exact,
            ["The source and verified export match."],
            InteropTarget.WindowsX64Msvc,
            CandidateCount: 1,
            SnapshotComplete: true,
            [ProjectedEvidence()]);

    private static InteropFindingProjection FindingFact(
        string managedKey,
        string ruleId,
        string message) =>
        new(
            ruleId,
            InteropFindingSeverity.Error,
            message,
            managedKey,
            "cpp:function:run",
            InteropTarget.WindowsX64Msvc,
            EvidenceConfidence.Exact,
            [ProjectedEvidence()]);

    private static InteropEvidenceProjection ProjectedEvidence() =>
        new(
            new SourceLocation("analysis.cs", 1, 1, 1, 5),
            EvidenceConfidence.Exact,
            "interop-analysis");

    private static Evidence EvidenceFor(long ownerFileId, string path) =>
        new(
            ownerFileId,
            new SourceLocation(path, 1, 1, 1, 5),
            EvidenceConfidence.Exact,
            "interop-reader-test");

    private static AbiTypeRef VoidType { get; } =
        new("void", AbiTypeCategory.Void);

    private sealed record Owner(
        long FileId,
        long SymbolId,
        string Key,
        string Path);
}
