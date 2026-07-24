using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Interop;
using DevBitsLab.Mcp.SourceGraph.Server.Interop;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;
using EdgeKinds = DevBitsLab.Mcp.SourceGraph.Sdk.EdgeKinds;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class InteropAnalysisPublisherTests : IAsyncLifetime
{
    private string _tempDirectory = string.Empty;
    private SqliteGraphStore? _store;

    public async Task InitializeAsync()
    {
        _tempDirectory = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-interop-publisher-" + Guid.NewGuid().ToString("N"));
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
    public async Task Complete_verified_match_publishes_edge_and_proven_phase2_finding()
    {
        var managed = await SeedManagedAsync(
            callingConvention: InteropCallingConvention.Cdecl);
        var native = await SeedNativeAsync(
            "native/export.h",
            "c:E:native/export.h::run",
            library: "native.dll",
            callingConvention: InteropCallingConvention.StdCall,
            binaryVerified: true);

        var result = await Publisher().PublishAsync(Target, true);

        result.IsComplete.Should().BeTrue();
        result.FilesPublished.Should().Be(1);
        result.MatchesPublished.Should().Be(1);
        result.FindingsPublished.Should().Be(1);
        result.EdgesPublished.Should().Be(1);

        var matches = await InteropFactStoreReader.ReadMatchesAsync(_store!);
        matches.IsComplete.Should().BeTrue();
        var match = matches.Facts.Should().ContainSingle().Subject.Fact;
        match.Status.Should().Be(InteropMatchStatus.Matched);
        match.CandidateCount.Should().Be(1);
        match.NativeSymbolCanonicalKey.Should().Be(native.Key);

        var findings = await InteropFactStoreReader.ReadFindingsAsync(_store!);
        findings.IsComplete.Should().BeTrue();
        findings.Facts.Should().ContainSingle()
            .Which.Fact.RuleId.Should().Be(InteropRuleIds.CallingConvention);
        findings.Facts.Should().NotContain(item =>
            item.Fact.RuleId == InteropRuleIds.StructLayout
            || item.Fact.RuleId == InteropRuleIds.CallbackGcRisk
            || item.Fact.RuleId == InteropRuleIds.NativeException
            || item.Fact.RuleId == InteropRuleIds.AllocatorMismatch);

        var targets = await _store!.ListCalleesAsync(
            managed.SymbolId,
            edgeKind: EdgeKinds.PInvokeMapsTo);
        targets.Should().ContainSingle()
            .Which.CanonicalKey.Should().Be(native.Key);
        var evidence = await _store.ListEdgeEvidenceAsync(
            managed.SymbolId,
            native.SymbolId,
            EdgeKinds.PInvokeMapsTo);
        evidence.Should().ContainSingle();
        evidence[0].Location.FilePath.Should().Be(managed.Path);
        evidence[0].Producer.Should().Be(InteropAnalysisPublisher.Producer);
    }

    [Fact]
    public async Task Verified_record_boundary_publishes_unique_struct_mapping()
    {
        const string managedImportKey =
            "csharp:M:Fixture.NativeMethods.Run";
        const string nativeExportKey =
            "c:E:native/export.h::run";
        var managedImportOwner = await SeedOwnerAsync(
            "managed/NativeMethods.cs",
            managedImportKey,
            "Run",
            "method");
        var nativeExportOwner = await SeedOwnerAsync(
            "native/export.h",
            nativeExportKey,
            "run",
            "native-export");
        var managedRecord = await SeedRecordAsync(
            "managed/Packet.cs",
            "csharp:T:Fixture.Packet",
            "Packet",
            AbiRecordKind.Sequential);
        var nativeRecord = await SeedRecordAsync(
            "native/packet.h",
            "c:T:native/packet.h::Packet",
            "Packet",
            AbiRecordKind.Native);

        var managedParameter = new AbiParameter(
            0,
            "packet",
            new AbiTypeRef(
                "Fixture.Packet",
                AbiTypeCategory.Record,
                pointerDepth: 1,
                sizeBytes: Target.PointerSizeBytes,
                alignmentBytes: Target.PointerSizeBytes),
            AbiParameterDirection.In,
            new SourceLocation(
                managedImportOwner.Path,
                2,
                5,
                2,
                20));
        var nativeParameter = new AbiParameter(
            0,
            "packet",
            new AbiTypeRef(
                "const Packet *",
                AbiTypeCategory.Pointer,
                pointerDepth: 1,
                sizeBytes: Target.PointerSizeBytes,
                alignmentBytes: Target.PointerSizeBytes,
                pointeeType: new AbiTypeRef(
                    "Packet",
                    AbiTypeCategory.Record),
                isPointeeConst: true),
            AbiParameterDirection.In,
            new SourceLocation(
                nativeExportOwner.Path,
                2,
                5,
                2,
                20));
        var managedImport = ManagedFact(
            managedImportOwner.FileId,
            managedImportKey,
            managedImportOwner.Path) with
        {
            Parameters = [managedParameter],
        };
        var nativeExport = NativeFact(
            nativeExportOwner.FileId,
            nativeExportKey,
            nativeExportOwner.Path,
            "native.dll",
            InteropCallingConvention.Cdecl,
            binaryVerified: true) with
        {
            Parameters = [nativeParameter],
        };
        await _store!.BulkInsertAnnotationsAsync(
        [
            Annotation(
                managedImportOwner.SymbolId,
                InteropAnnotationFlavors.ManagedImport,
                InteropFactPayloadCodec.EncodeManagedImport(managedImport)),
            Annotation(
                nativeExportOwner.SymbolId,
                InteropAnnotationFlavors.NativeExport,
                InteropFactPayloadCodec.EncodeNativeExport(nativeExport)),
        ]);

        var result = await Publisher().PublishAsync(Target, true);

        result.IsComplete.Should().BeTrue();
        result.EdgesPublished.Should().Be(2);
        (await _store.ListCalleesAsync(
                managedRecord.SymbolId,
                edgeKind: EdgeKinds.StructMapsTo))
            .Should().ContainSingle()
            .Which.CanonicalKey.Should().Be(nativeRecord.Key);
        var evidence = await _store.ListEdgeEvidenceAsync(
            managedRecord.SymbolId,
            nativeRecord.SymbolId,
            EdgeKinds.StructMapsTo);
        evidence.Should().ContainSingle();
        evidence[0].Producer.Should().Be(
            InteropAnalysisPublisher.Producer);
        evidence[0].Confidence.Should().Be(
            EvidenceConfidence.Semantic);
        evidence[0].Location.FilePath.Should().Be(
            managedImportOwner.Path);
        evidence[0].Metadata.Should().Contain(
            "managedType",
            "Fixture.Packet");
        evidence[0].Metadata.Should().Contain(
            "nativeType",
            "Packet");
        evidence[0].Metadata.Should().Contain(
            "position",
            "parameter:0");
    }

    [Fact]
    public async Task Ambiguous_native_record_identity_never_publishes_struct_mapping()
    {
        var managed = await SeedManagedRecordBoundaryAsync();
        await SeedRecordAsync(
            "native/first.h",
            "c:T:native/first.h::Packet",
            "Packet",
            AbiRecordKind.Native);
        await SeedRecordAsync(
            "native/second.h",
            "c:T:native/second.h::Packet",
            "Packet",
            AbiRecordKind.Native);

        var result = await Publisher().PublishAsync(Target, true);

        result.IsComplete.Should().BeTrue();
        result.EdgesPublished.Should().Be(1,
            "the verified P/Invoke edge remains, but an ambiguous record name is not mapped");
        (await _store!.ListCalleesAsync(
                managed.SymbolId,
                edgeKind: EdgeKinds.StructMapsTo))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task Source_only_match_remains_queryable_without_edge_or_findings()
    {
        var managed = await SeedManagedAsync();
        await SeedNativeAsync(
            "native/source.h",
            "c:E:native/source.h::run",
            library: "native.dll",
            callingConvention: InteropCallingConvention.Cdecl,
            binaryVerified: false);

        var result = await Publisher().PublishAsync(Target, true);

        result.IsComplete.Should().BeTrue();
        result.MatchesPublished.Should().Be(1);
        result.FindingsPublished.Should().Be(0);
        result.EdgesPublished.Should().Be(0);
        var match = (await InteropFactStoreReader.ReadMatchesAsync(_store!))
            .Facts.Should().ContainSingle().Subject.Fact;
        match.Status.Should().Be(InteropMatchStatus.SourceMatched);
        match.NativeSymbolCanonicalKey.Should().NotBeNull();
        (await InteropFactStoreReader.ReadFindingsAsync(_store!))
            .Facts.Should().BeEmpty();
        (await _store!.ListCalleesAsync(
            managed.SymbolId,
            edgeKind: EdgeKinds.PInvokeMapsTo))
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData("unmatched", InteropMatchStatus.Unmatched, 0)]
    [InlineData("unknown", InteropMatchStatus.Unknown, 1)]
    [InlineData("ambiguous", InteropMatchStatus.Ambiguous, 2)]
    public async Task Non_matches_publish_status_but_never_boundary_facts(
        string shape,
        InteropMatchStatus expectedStatus,
        int expectedCandidateCount)
    {
        var managed = await SeedManagedAsync();
        if (shape == "unknown")
        {
            await SeedNativeAsync(
                "native/unknown.h",
                "c:E:native/unknown.h::run",
                library: null,
                callingConvention: InteropCallingConvention.Cdecl,
                binaryVerified: false);
        }
        else if (shape == "ambiguous")
        {
            await SeedNativeAsync(
                "native/first.h",
                "c:E:native/first.h::run",
                library: "native.dll",
                callingConvention: InteropCallingConvention.Cdecl,
                binaryVerified: true);
            await SeedNativeAsync(
                "native/second.h",
                "c:E:native/second.h::run",
                library: "native.dll",
                callingConvention: InteropCallingConvention.Cdecl,
                binaryVerified: true);
        }

        var result = await Publisher().PublishAsync(Target, true);

        result.IsComplete.Should().BeTrue();
        result.FindingsPublished.Should().Be(0);
        result.EdgesPublished.Should().Be(0);
        var match = (await InteropFactStoreReader.ReadMatchesAsync(_store!))
            .Facts.Should().ContainSingle().Subject.Fact;
        match.Status.Should().Be(expectedStatus);
        match.CandidateCount.Should().Be(expectedCandidateCount);
        match.NativeSymbolCanonicalKey.Should().BeNull();
        (await _store!.ListCalleesAsync(
            managed.SymbolId,
            edgeKind: EdgeKinds.PInvokeMapsTo))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task Incomplete_snapshot_retains_last_successful_projection()
    {
        var managed = await SeedManagedAsync();
        var native = await SeedNativeAsync(
            "native/verified.h",
            "c:E:native/verified.h::run",
            library: "native.dll",
            callingConvention: InteropCallingConvention.Cdecl,
            binaryVerified: true);
        (await Publisher().PublishAsync(Target, true))
            .IsComplete.Should().BeTrue();

        var partial = await Publisher().PublishAsync(Target, false);

        partial.IsComplete.Should().BeFalse();
        partial.FilesPublished.Should().Be(0);
        partial.Failures.Should().ContainSingle(failure =>
            failure.Stage == "native-snapshot");
        var retained = (await InteropFactStoreReader.ReadMatchesAsync(_store!))
            .Facts.Should().ContainSingle().Subject.Fact;
        retained.Status.Should().Be(InteropMatchStatus.Matched);
        (await _store!.ListCalleesAsync(
            managed.SymbolId,
            edgeKind: EdgeKinds.PInvokeMapsTo))
            .Should().ContainSingle()
            .Which.CanonicalKey.Should().Be(native.Key);
    }

    [Fact]
    public async Task Invalid_managed_evidence_retains_last_successful_projection()
    {
        var managed = await SeedManagedAsync();
        await SeedNativeAsync(
            "native/verified.h",
            "c:E:native/verified.h::run",
            library: "native.dll",
            callingConvention: InteropCallingConvention.Cdecl,
            binaryVerified: true);
        (await Publisher().PublishAsync(Target, true))
            .IsComplete.Should().BeTrue();

        var escapedFact = ManagedFact(
            managed.FileId,
            managed.Key,
            Path.Join(_tempDirectory, "other.cs"));
        await _store!.ReplaceAnnotationsForFileByFlavorAsync(
            managed.Path,
            InteropAnnotationFlavors.ManagedImport,
            [
                new FileAnnotationFact(
                    managed.Key,
                    "InteropFact",
                    "MedInterop.InteropFact",
                    InteropAnnotationFlavors.ManagedImport,
                    InteropFactPayloadCodec.EncodeManagedImport(escapedFact),
                    AttributeCanonicalKey: null),
            ]);

        var invalid = await Publisher().PublishAsync(Target, true);

        invalid.IsComplete.Should().BeFalse();
        invalid.FilesPublished.Should().Be(0);
        invalid.Failures.Should().ContainSingle(failure =>
            failure.Stage == "projection");
        (await InteropFactStoreReader.ReadMatchesAsync(_store!))
            .Facts.Should().ContainSingle()
            .Which.Fact.Status.Should().Be(InteropMatchStatus.Matched);
    }

    [Fact]
    public async Task Successful_zero_import_refresh_removes_stale_projection()
    {
        var managed = await SeedManagedAsync();
        var native = await SeedNativeAsync(
            "native/verified.h",
            "c:E:native/verified.h::run",
            library: "native.dll",
            callingConvention: InteropCallingConvention.Cdecl,
            binaryVerified: true);
        (await Publisher().PublishAsync(Target, true))
            .IsComplete.Should().BeTrue();
        await _store!.ReplaceAnnotationsForFileByFlavorAsync(
            managed.Path,
            InteropAnnotationFlavors.ManagedImport,
            []);

        var cleared = await Publisher().PublishAsync(Target, true);

        cleared.IsComplete.Should().BeTrue();
        cleared.FilesPublished.Should().Be(1);
        cleared.MatchesPublished.Should().Be(0);
        (await InteropFactStoreReader.ReadMatchesAsync(_store))
            .Facts.Should().BeEmpty();
        (await InteropFactStoreReader.ReadFindingsAsync(_store))
            .Facts.Should().BeEmpty();
        (await _store.ListEdgeEvidenceAsync(
            managed.SymbolId,
            native.SymbolId,
            EdgeKinds.PInvokeMapsTo))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task Explicit_clear_removes_only_analysis_projection_and_keeps_import_fact()
    {
        var managed = await SeedManagedAsync(
            callingConvention: InteropCallingConvention.Cdecl);
        var native = await SeedNativeAsync(
            "native/verified.h",
            "c:E:native/verified.h::run",
            library: "native.dll",
            callingConvention: InteropCallingConvention.StdCall,
            binaryVerified: true);
        (await Publisher().PublishAsync(Target, true))
            .IsComplete.Should().BeTrue();

        var cleared = await Publisher().ClearAsync();

        cleared.IsComplete.Should().BeTrue();
        cleared.FilesPublished.Should().Be(1);
        (await InteropFactStoreReader.ReadManagedImportsAsync(_store!))
            .Facts.Should().ContainSingle()
            .Which.Fact.SymbolCanonicalKey.Should().Be(managed.Key);
        (await InteropFactStoreReader.ReadNativeExportsAsync(_store!))
            .Facts.Should().ContainSingle()
            .Which.Fact.SymbolCanonicalKey.Should().Be(native.Key);
        (await InteropFactStoreReader.ReadMatchesAsync(_store!))
            .Facts.Should().BeEmpty();
        (await InteropFactStoreReader.ReadFindingsAsync(_store!))
            .Facts.Should().BeEmpty();
        (await _store!.ListEdgeEvidenceAsync(
                managed.SymbolId,
                native.SymbolId,
                EdgeKinds.PInvokeMapsTo))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task Removing_native_configuration_clears_analysis_before_native_facts()
    {
        var managed = await SeedManagedAsync(
            callingConvention: InteropCallingConvention.Cdecl);
        var native = await SeedNativeAsync(
            "native/verified.h",
            "c:E:native/verified.h::run",
            library: "native.dll",
            callingConvention: InteropCallingConvention.StdCall,
            binaryVerified: true);
        (await Publisher().PublishAsync(Target, true))
            .IsComplete.Should().BeTrue();

        var cleared = await new NativeInteropSnapshotPublisher(_store!)
            .ClearAsync();

        cleared.IsComplete.Should().BeTrue();
        (await InteropFactStoreReader.ReadManagedImportsAsync(_store!))
            .Facts.Should().ContainSingle()
            .Which.Fact.SymbolCanonicalKey.Should().Be(managed.Key);
        (await InteropFactStoreReader.ReadNativeExportsAsync(_store!))
            .Facts.Should().BeEmpty();
        (await InteropFactStoreReader.ReadMatchesAsync(_store!))
            .Facts.Should().BeEmpty();
        (await InteropFactStoreReader.ReadFindingsAsync(_store!))
            .Facts.Should().BeEmpty();
        (await _store!.ListEdgeEvidenceAsync(
                managed.SymbolId,
                native.SymbolId,
                EdgeKinds.PInvokeMapsTo))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task Proven_native_exception_fact_publishes_Interop005_only()
    {
        await SeedManagedAsync();
        var native = await SeedNativeAsync(
            "native/throws.h",
            "c:E:native/throws.h::run",
            library: "native.dll",
            callingConvention: InteropCallingConvention.Cdecl,
            binaryVerified: true);
        var fact = NativeFact(
            native.FileId,
            native.Key,
            native.Path,
            "native.dll",
            InteropCallingConvention.Cdecl,
            binaryVerified: true) with
        {
            ExceptionEscape = new NativeExceptionEscape(
                Target,
                new Evidence(
                    native.FileId,
                    new SourceLocation(native.Path, 4, 1, 4, 8),
                    EvidenceConfidence.Exact,
                    "clang-dataflow")),
        };
        await _store!.ReplaceAnnotationsForFileByFlavorAsync(
            native.Path,
            InteropAnnotationFlavors.NativeExport,
            [
                new FileAnnotationFact(
                    native.Key,
                    "InteropFact",
                    "MedInterop.InteropFact",
                    InteropAnnotationFlavors.NativeExport,
                    InteropFactPayloadCodec.EncodeNativeExport(fact),
                    AttributeCanonicalKey: null),
            ]);

        var result = await Publisher().PublishAsync(Target, true);

        result.IsComplete.Should().BeTrue();
        var findings = (await InteropFactStoreReader.ReadFindingsAsync(_store))
            .Facts.Select(item => item.Fact).ToArray();
        findings.Should().ContainSingle();
        findings[0].RuleId.Should().Be(InteropRuleIds.NativeException);
    }

    [Fact]
    public async Task Caller_owned_usage_facts_publish_query_and_clean_up_Interop004_and_Interop006()
    {
        const string callbackImportKey =
            "csharp:M:Fixture.NativeMethods.Register";
        const string allocationImportKey =
            "csharp:M:Fixture.NativeMethods.Allocate";
        const string callerKey =
            "csharp:M:Fixture.NativeService.RunRisks";
        const string callbackNativeKey =
            "c:E:native/interop.h::register";
        const string allocationNativeKey =
            "c:E:native/interop.h::allocate";

        var callbackType = new AbiTypeRef(
            "callback_t",
            AbiTypeCategory.FunctionPointer,
            pointerDepth: 1,
            sizeBytes: 4,
            alignmentBytes: 4);
        var pointerType = new AbiTypeRef(
            "void*",
            AbiTypeCategory.Pointer,
            pointerDepth: 1,
            sizeBytes: 4,
            alignmentBytes: 4,
            pointeeType: VoidType);
        var importOwner = await SeedOwnerAsync(
            "managed/NativeMethods.cs",
            callbackImportKey,
            "Register",
            "method");
        var allocationOwner = await SeedOwnerAsync(
            "managed/NativeMethods.cs",
            allocationImportKey,
            "Allocate",
            "method");
        var callerOwner = await SeedOwnerAsync(
            "managed/NativeService.cs",
            callerKey,
            "RunRisks",
            "method");
        var callbackNativeOwner = await SeedOwnerAsync(
            "native/interop.h",
            callbackNativeKey,
            "register",
            "native-export");
        var allocationNativeOwner = await SeedOwnerAsync(
            "native/interop.h",
            allocationNativeKey,
            "allocate",
            "native-export");

        var callbackParameter = new AbiParameter(
            0,
            "callback",
            callbackType,
            AbiParameterDirection.In,
            new SourceLocation(importOwner.Path, 4, 1, 4, 20));
        var callbackImport = new ManagedImport(
            callbackImportKey,
            ManagedImportKind.DllImport,
            "native.dll",
            "register",
            InteropCallingConvention.Cdecl,
            VoidType,
            [callbackParameter],
            CharacterSet: null,
            SetLastError: false,
            Target,
            EvidenceAt(
                importOwner.FileId,
                importOwner.Path,
                "roslyn-managed-interop"))
        {
            ExactSpelling = true,
        };
        var allocationImport = new ManagedImport(
            allocationImportKey,
            ManagedImportKind.DllImport,
            "native.dll",
            "allocate",
            InteropCallingConvention.Cdecl,
            pointerType,
            [],
            CharacterSet: null,
            SetLastError: false,
            Target,
            EvidenceAt(
                allocationOwner.FileId,
                allocationOwner.Path,
                "roslyn-managed-interop"))
        {
            ExactSpelling = true,
        };
        await _store!.BulkInsertAnnotationsAsync(
        [
            Annotation(
                importOwner.SymbolId,
                InteropAnnotationFlavors.ManagedImport,
                InteropFactPayloadCodec.EncodeManagedImport(callbackImport)),
            Annotation(
                allocationOwner.SymbolId,
                InteropAnnotationFlavors.ManagedImport,
                InteropFactPayloadCodec.EncodeManagedImport(allocationImport)),
        ]);

        var callbackNative = new NativeExport(
            callbackNativeKey,
            "register",
            InteropCallingConvention.Cdecl,
            VoidType,
            [
                callbackParameter with
                {
                    Location = new SourceLocation(
                        callbackNativeOwner.Path,
                        2,
                        1,
                        2,
                        20),
                },
            ],
            HasCLinkage: true,
            IsBinaryVerified: true,
            Target,
            EvidenceAt(
                callbackNativeOwner.FileId,
                callbackNativeOwner.Path,
                "clang-native-interop"))
        {
            LibraryName = "native.dll",
            ModuleIdentitySource = NativeModuleIdentitySource.Binary,
            RetainedCallbacks =
            [
                new NativeCallbackRetention(
                    0,
                    Target,
                    EvidenceAt(
                        callbackNativeOwner.FileId,
                        callbackNativeOwner.Path,
                        "clang-native-callback-retention")),
            ],
        };
        var allocationNative = new NativeExport(
            allocationNativeKey,
            "allocate",
            InteropCallingConvention.Cdecl,
            pointerType,
            [],
            HasCLinkage: true,
            IsBinaryVerified: true,
            Target,
            EvidenceAt(
                allocationNativeOwner.FileId,
                allocationNativeOwner.Path,
                "clang-native-interop"))
        {
            LibraryName = "native.dll",
            ModuleIdentitySource = NativeModuleIdentitySource.Binary,
            ReturnAllocation = new NativeReturnAllocation(
                InteropAllocatorFamily.CrtHeap,
                Target,
                EvidenceAt(
                    allocationNativeOwner.FileId,
                    allocationNativeOwner.Path,
                    "clang-native-return-allocation")),
        };
        await _store.BulkInsertAnnotationsAsync(
        [
            Annotation(
                callbackNativeOwner.SymbolId,
                InteropAnnotationFlavors.NativeExport,
                InteropFactPayloadCodec.EncodeNativeExport(callbackNative)),
            Annotation(
                allocationNativeOwner.SymbolId,
                InteropAnnotationFlavors.NativeExport,
                InteropFactPayloadCodec.EncodeNativeExport(allocationNative)),
        ]);

        var callbackUsage = new ManagedCallbackUsageProjection(
            callbackImportKey,
            new ManagedCallbackUsage(
                0,
                callerKey,
                CallbackGcRooting.Unrooted,
                Target,
                EvidenceAt(
                    callerOwner.FileId,
                    callerOwner.Path,
                    "roslyn-managed-interop-usage")));
        var releaseUsage = new ManagedReturnReleaseProjection(
            allocationImportKey,
            new ManagedReturnRelease(
                callerKey,
                InteropAllocatorFamily.CoTaskMem,
                Target,
                EvidenceAt(
                    callerOwner.FileId,
                    callerOwner.Path,
                    "roslyn-managed-interop-usage")));
        var callbackPayload =
            InteropFactPayloadCodec.EncodeManagedCallbackUsage(callbackUsage);
        var releasePayload =
            InteropFactPayloadCodec.EncodeManagedReturnRelease(releaseUsage);
        await _store.BulkInsertAnnotationsAsync(
        [
            Annotation(
                callerOwner.SymbolId,
                InteropAnnotationFlavors.ManagedCallbackUsage,
                callbackPayload),
            Annotation(
                callerOwner.SymbolId,
                InteropAnnotationFlavors.ManagedReturnRelease,
                releasePayload),
        ]);

        var published = await Publisher().PublishAsync(Target, true);

        published.IsComplete.Should().BeTrue();
        published.MatchesPublished.Should().Be(2);
        published.FindingsPublished.Should().Be(2);
        var findings = (await InteropFactStoreReader.ReadFindingsAsync(_store))
            .Facts;
        findings.Should().HaveCount(2);
        findings.Should().OnlyContain(item =>
            item.Row.FilePath == callerOwner.Path
            && item.Fact.ManagedSymbolCanonicalKey == callerKey);
        findings.Should().ContainSingle(item =>
            item.Fact.RuleId == InteropRuleIds.CallbackGcRisk
            && item.Fact.BoundaryManagedSymbolCanonicalKey
                == callbackImportKey);
        findings.Should().ContainSingle(item =>
            item.Fact.RuleId == InteropRuleIds.AllocatorMismatch
            && item.Fact.BoundaryManagedSymbolCanonicalKey
                == allocationImportKey);

        var queryService = new InteropQueryService();
        var callbackQuery = await queryService.QueryAsync(
            "scope-a",
            _store,
            CompleteState(),
            callbackImportKey,
            InteropQuerySelectionMode.ManagedImportOnly,
            includeFindings: true);
        callbackQuery.Result.Findings.Should().ContainSingle()
            .Which.ManagedSymbol.Should().Be(callerKey);
        callbackQuery.Result.Findings[0].RuleId.Should().Be(
            InteropRuleIds.CallbackGcRisk);
        var allocationQuery = await queryService.QueryAsync(
            "scope-a",
            _store,
            CompleteState(),
            allocationImportKey,
            InteropQuerySelectionMode.ManagedImportOnly,
            includeFindings: true);
        allocationQuery.Result.Findings.Should().ContainSingle()
            .Which.RuleId.Should().Be(InteropRuleIds.AllocatorMismatch);

        await _store.ReplaceAnnotationsForFileByFlavorAsync(
            callerOwner.Path,
            InteropAnnotationFlavors.ManagedCallbackUsage,
            [
                new FileAnnotationFact(
                    callerKey,
                    "ManagedCallbackUsageV1",
                    "MedInterop.ManagedCallbackUsage.v1",
                    InteropAnnotationFlavors.ManagedCallbackUsage,
                    "{}",
                    AttributeCanonicalKey: null),
            ]);
        var malformed = await Publisher().PublishAsync(Target, true);

        malformed.IsComplete.Should().BeFalse();
        malformed.FilesPublished.Should().Be(0);
        malformed.Failures.Should().Contain(failure =>
            failure.Stage == "managed-callback-usages");
        (await InteropFactStoreReader.ReadFindingsAsync(_store))
            .Facts.Should().HaveCount(2);

        await _store.ReplaceAnnotationsForFileByFlavorAsync(
            callerOwner.Path,
            InteropAnnotationFlavors.ManagedCallbackUsage,
            []);
        await _store.ReplaceAnnotationsForFileByFlavorAsync(
            callerOwner.Path,
            InteropAnnotationFlavors.ManagedReturnRelease,
            []);
        var cleaned = await Publisher().PublishAsync(Target, true);

        cleaned.IsComplete.Should().BeTrue();
        cleaned.FindingsPublished.Should().Be(0);
        (await InteropFactStoreReader.ReadFindingsAsync(_store))
            .Facts.Should().BeEmpty();
    }

    private async Task<Owner> SeedManagedRecordBoundaryAsync()
    {
        const string managedImportKey =
            "csharp:M:Fixture.NativeMethods.Run";
        const string nativeExportKey =
            "c:E:native/export.h::run";
        var managedImportOwner = await SeedOwnerAsync(
            "managed/NativeMethods.cs",
            managedImportKey,
            "Run",
            "method");
        var nativeExportOwner = await SeedOwnerAsync(
            "native/export.h",
            nativeExportKey,
            "run",
            "native-export");
        var managedRecord = await SeedRecordAsync(
            "managed/Packet.cs",
            "csharp:T:Fixture.Packet",
            "Packet",
            AbiRecordKind.Sequential);
        var managedParameter = new AbiParameter(
            0,
            "packet",
            new AbiTypeRef(
                "Fixture.Packet",
                AbiTypeCategory.Record,
                pointerDepth: 1,
                sizeBytes: Target.PointerSizeBytes,
                alignmentBytes: Target.PointerSizeBytes),
            AbiParameterDirection.In,
            new SourceLocation(
                managedImportOwner.Path,
                2,
                5,
                2,
                20));
        var nativeParameter = new AbiParameter(
            0,
            "packet",
            new AbiTypeRef(
                "const Packet *",
                AbiTypeCategory.Pointer,
                pointerDepth: 1,
                sizeBytes: Target.PointerSizeBytes,
                alignmentBytes: Target.PointerSizeBytes,
                pointeeType: new AbiTypeRef(
                    "Packet",
                    AbiTypeCategory.Record),
                isPointeeConst: true),
            AbiParameterDirection.In,
            new SourceLocation(
                nativeExportOwner.Path,
                2,
                5,
                2,
                20));
        var managedImport = ManagedFact(
            managedImportOwner.FileId,
            managedImportKey,
            managedImportOwner.Path) with
        {
            Parameters = [managedParameter],
        };
        var nativeExport = NativeFact(
            nativeExportOwner.FileId,
            nativeExportKey,
            nativeExportOwner.Path,
            "native.dll",
            InteropCallingConvention.Cdecl,
            binaryVerified: true) with
        {
            Parameters = [nativeParameter],
        };
        await _store!.BulkInsertAnnotationsAsync(
        [
            Annotation(
                managedImportOwner.SymbolId,
                InteropAnnotationFlavors.ManagedImport,
                InteropFactPayloadCodec.EncodeManagedImport(managedImport)),
            Annotation(
                nativeExportOwner.SymbolId,
                InteropAnnotationFlavors.NativeExport,
                InteropFactPayloadCodec.EncodeNativeExport(nativeExport)),
        ]);
        return managedRecord;
    }

    private async Task<Owner> SeedRecordAsync(
        string relativePath,
        string canonicalKey,
        string name,
        AbiRecordKind kind)
    {
        var owner = await SeedOwnerAsync(
            relativePath,
            canonicalKey,
            name,
            "struct");
        var layout = new AbiRecordLayout(
            canonicalKey,
            kind,
            SizeBytes: 4,
            AlignmentBytes: 4,
            Pack: kind == AbiRecordKind.Native ? null : Target.DefaultPack,
            [
                new AbiFieldLayout(
                    0,
                    "value",
                    Int32Type,
                    OffsetBytes: 0,
                    SizeBytes: 4,
                    RecordEvidence()),
            ],
            Target,
            RecordEvidence());
        await _store!.BulkInsertAnnotationsAsync(
        [
            Annotation(
                owner.SymbolId,
                InteropAnnotationFlavors.AbiRecord,
                InteropFactPayloadCodec.EncodeAbiRecord(layout)),
        ]);
        return owner;

        Evidence RecordEvidence() =>
            EvidenceAt(
                owner.FileId,
                owner.Path,
                kind == AbiRecordKind.Native
                    ? "clang-native"
                    : "roslyn-managed-layout") with
            {
                Confidence = kind == AbiRecordKind.Native
                    ? EvidenceConfidence.Exact
                    : EvidenceConfidence.Semantic,
            };
    }

    private InteropAnalysisPublisher Publisher() => new(_store!);

    private async Task<Owner> SeedManagedAsync(
        InteropCallingConvention callingConvention =
            InteropCallingConvention.Cdecl)
    {
        const string key = "csharp:M:Fixture.NativeMethods.Run";
        var owner = await SeedOwnerAsync(
            "managed/NativeMethods.cs",
            key,
            "Run",
            "method");
        var fact = ManagedFact(
            owner.FileId,
            owner.Key,
            owner.Path,
            callingConvention);
        await _store!.BulkInsertAnnotationsAsync(
        [
            Annotation(
                owner.SymbolId,
                InteropAnnotationFlavors.ManagedImport,
                InteropFactPayloadCodec.EncodeManagedImport(fact)),
        ]);
        return owner;
    }

    private async Task<Owner> SeedNativeAsync(
        string relativePath,
        string canonicalKey,
        string? library,
        InteropCallingConvention callingConvention,
        bool binaryVerified)
    {
        var owner = await SeedOwnerAsync(
            relativePath,
            canonicalKey,
            "run",
            "native-export");
        var fact = NativeFact(
            owner.FileId,
            owner.Key,
            owner.Path,
            library,
            callingConvention,
            binaryVerified);
        await _store!.BulkInsertAnnotationsAsync(
        [
            Annotation(
                owner.SymbolId,
                InteropAnnotationFlavors.NativeExport,
                InteropFactPayloadCodec.EncodeNativeExport(fact)),
        ]);
        return owner;
    }

    private async Task<Owner> SeedOwnerAsync(
        string relativePath,
        string canonicalKey,
        string name,
        string kind)
    {
        var path = Path.GetFullPath(Path.Join(_tempDirectory, relativePath));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "// fixture");
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
                kind,
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

    private static ManagedImport ManagedFact(
        long ownerFileId,
        string key,
        string path,
        InteropCallingConvention callingConvention =
            InteropCallingConvention.Cdecl) =>
        new(
            key,
            ManagedImportKind.DllImport,
            "native.dll",
            "run",
            callingConvention,
            VoidType,
            [],
            CharacterSet: null,
            SetLastError: false,
            Target,
            EvidenceAt(ownerFileId, path, "roslyn-managed-interop"))
        {
            ExactSpelling = true,
        };

    private static NativeExport NativeFact(
        long ownerFileId,
        string key,
        string path,
        string? library,
        InteropCallingConvention callingConvention,
        bool binaryVerified) =>
        new(
            key,
            "run",
            callingConvention,
            VoidType,
            [],
            HasCLinkage: true,
            IsBinaryVerified: binaryVerified,
            Target,
            EvidenceAt(ownerFileId, path, "clang-native-interop"))
        {
            LibraryName = library,
            ModuleIdentitySource = binaryVerified
                ? NativeModuleIdentitySource.Binary
                : library is null
                    ? NativeModuleIdentitySource.Unknown
                    : NativeModuleIdentitySource.Configuration,
        };

    private static Evidence EvidenceAt(
        long ownerFileId,
        string path,
        string producer) =>
        new(
            ownerFileId,
            new SourceLocation(path, 1, 1, 1, 5),
            EvidenceConfidence.Exact,
            producer);

    private static AbiTypeRef VoidType { get; } =
        new("void", AbiTypeCategory.Void);

    private static AbiTypeRef Int32Type { get; } =
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
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            RetainedLastGood: false,
            IsExportUniverseComplete: true,
            TranslationUnits: 1,
            IncludedFiles: 2,
            NativeSymbols: 2,
            ManagedMatches: 2,
            Findings: 2,
            BoundaryEdges: 2,
            PendingStaleSymbols: 0,
            Failures: []);

    private static InteropTarget Target { get; } =
        InteropTarget.WindowsX86Msvc;

    private sealed record Owner(
        long FileId,
        long SymbolId,
        string Key,
        string Path);
}
