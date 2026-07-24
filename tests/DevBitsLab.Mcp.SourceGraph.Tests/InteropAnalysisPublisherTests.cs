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

    private static InteropTarget Target { get; } =
        InteropTarget.WindowsX86Msvc;

    private sealed record Owner(
        long FileId,
        long SymbolId,
        string Key,
        string Path);
}
