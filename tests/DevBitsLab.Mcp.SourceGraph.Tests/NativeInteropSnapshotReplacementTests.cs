using Dapper;
using System.Security.Cryptography;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Interop;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class NativeInteropSnapshotReplacementTests : IAsyncLifetime
{
    private string _temporaryDirectory = string.Empty;
    private string _databasePath = string.Empty;
    private SqliteGraphStore? _store;

    public async Task InitializeAsync()
    {
        _temporaryDirectory = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-native-snapshot-store-"
            + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temporaryDirectory);
        _databasePath = Path.Join(_temporaryDirectory, "graph.db");
        _store = new SqliteGraphStore(_databasePath);
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
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task Replaces_all_native_flavors_atomically_and_preserves_other_facts()
    {
        var unrelated = await SeedOwnerAsync(
            "managed.cs",
            "csharp:M:Managed.Run",
            "Run",
            "method");
        await _store!.BulkInsertAnnotationsAsync(
        [
            new AnnotationRecord(
                unrelated.SymbolId,
                "Obsolete",
                "System.ObsoleteAttribute",
                "csharp-attribute",
                "{}",
                AttributeSymbolId: null),
        ]);
        var old = await SeedNativeAnnotationAsync(
            "old.h",
            "c:E:old.h::old");
        var first = NativeFile(
            "new/export.h",
            NativeExportFact(
                "c:E:new/export.h::run",
                PathFor("new/export.h")));
        var second = NativeFile(
            "new/types.h",
            RecordFact(
                "cpp:T:new/types.h::Payload",
                PathFor("new/types.h")));

        var result = await _store.ReplaceNativeInteropSnapshotAsync(
            Replacement(first, second));

        result.FilesUpdated.Should().Be(2);
        result.SymbolsUpdated.Should().Be(2);
        result.AnnotationsUpdated.Should().Be(2);
        result.PriorCanonicalKeys.Should().Equal(old.Key);
        result.CurrentCanonicalKeys.Should().Equal(
            "c:E:new/export.h::run",
            "cpp:T:new/types.h::Payload");

        var exports =
            await InteropFactStoreReader.ReadNativeExportsAsync(_store);
        var records = await InteropFactStoreReader.ReadAbiRecordsAsync(_store);
        exports.IsComplete.Should().BeTrue();
        exports.Facts.Should().ContainSingle()
            .Which.Fact.SymbolCanonicalKey.Should().Be(
                "c:E:new/export.h::run");
        records.IsComplete.Should().BeTrue();
        records.Facts.Should().ContainSingle()
            .Which.Fact.SymbolCanonicalKey.Should().Be(
                "cpp:T:new/types.h::Payload");

        (await _store.GetAnnotationsForSymbolAsync(unrelated.SymbolId))
            .Should().ContainSingle()
            .Which.Flavor.Should().Be("csharp-attribute");
        (await _store.GetSymbolByIdAsync(old.SymbolId))
            .Should().NotBeNull(
                "prior native declarations stay resolvable until boundary refresh succeeds");
    }

    [Fact]
    public async Task Empty_snapshot_clears_native_annotations_but_retains_prior_symbols()
    {
        var old = await SeedNativeAnnotationAsync(
            "old.h",
            "c:E:old.h::old");

        var result = await _store!.ReplaceNativeInteropSnapshotAsync(
            Replacement());

        result.PriorCanonicalKeys.Should().Equal(old.Key);
        result.CurrentCanonicalKeys.Should().BeEmpty();
        (await InteropFactStoreReader.ReadNativeExportsAsync(_store))
            .Facts.Should().BeEmpty();
        (await InteropFactStoreReader.ReadAbiRecordsAsync(_store))
            .Facts.Should().BeEmpty();
        (await _store.GetSymbolByIdAsync(old.SymbolId)).Should().NotBeNull();
    }

    [Fact]
    public async Task Native_snapshot_indexes_matching_source_content_forCoverageAndSearch()
    {
        var candidate = NativeFile(
            "native/export.cpp",
            NativeExportFact(
                "cpp:E:native/export.cpp::run",
                PathFor("native/export.cpp")));
        candidate = candidate with
        {
            ContentSha256 = SHA256.HashData(
                await File.ReadAllBytesAsync(candidate.Path)),
        };

        await _store!.ReplaceNativeInteropSnapshotAsync(Replacement(candidate));

        var coverage = await _store.GetSourceDocumentCoverageAsync();
        coverage.EligibleGraphFiles.Should().ContainSingle(candidate.Path);
        coverage.IndexedSourceDocuments.Should().ContainSingle(candidate.Path);
        coverage.MissingSourceDocuments.Should().BeEmpty();
        var search = await _store.SearchSourceTextAsync(
            "candidate",
            SourceTextSearchMode.Literal,
            caseSensitive: true,
            fileGlob: "*.cpp",
            contextLines: 0,
            maxResults: 10);
        search.Hits.Should().ContainSingle(hit => hit.FilePath == candidate.Path);
    }

    [Fact]
    public async Task Native_snapshot_replacement_preserves_managed_abi_record()
    {
        var managed = await SeedManagedRecordAnnotationAsync(
            "Managed.cs",
            "csharp:T:Managed.Payload");
        var oldNative = await SeedNativeAnnotationAsync(
            "old.h",
            "c:E:old.h::old");

        var result = await _store!.ReplaceNativeInteropSnapshotAsync(
            Replacement());

        result.PriorCanonicalKeys.Should().Equal(oldNative.Key);
        result.CurrentCanonicalKeys.Should().BeEmpty();
        var records = await InteropFactStoreReader.ReadAbiRecordsAsync(_store);
        records.IsComplete.Should().BeTrue();
        records.Facts.Should().ContainSingle()
            .Which.Fact.SymbolCanonicalKey.Should().Be(managed.Key);
        (await _store.GetAnnotationsForSymbolAsync(managed.SymbolId))
            .Should().ContainSingle()
            .Which.Flavor.Should().Be(InteropAnnotationFlavors.AbiRecord);
        (await InteropFactStoreReader.ReadNativeExportsAsync(_store))
            .Facts.Should().BeEmpty();
    }

    [Fact]
    public async Task Candidate_with_managed_canonical_key_is_rejected()
    {
        var record = RecordFact(
            "csharp:T:Managed.Payload",
            PathFor("Managed.cs"));
        var candidate = NativeFile("Managed.cs", record);

        var act = () => _store!.ReplaceNativeInteropSnapshotAsync(
            Replacement(candidate));

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*c: or cpp:*");
    }

    [Fact]
    public async Task Database_failure_after_cleanup_rolls_back_entire_snapshot()
    {
        var old = await SeedNativeAnnotationAsync(
            "old.h",
            "c:E:old.h::old");
        await using (var connection = new SqliteConnection(
                         $"Data Source={_databasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                """
                CREATE TRIGGER fail_native_snapshot_insert
                BEFORE INSERT ON annotations
                WHEN NEW.flavor = 'interop-native-export'
                BEGIN
                    SELECT RAISE(ABORT, 'injected native snapshot failure');
                END;
                """);
        }
        var candidate = NativeFile(
            "new.h",
            NativeExportFact(
                "c:E:new.h::new",
                PathFor("new.h")));

        var act = () => _store!.ReplaceNativeInteropSnapshotAsync(
            Replacement(candidate));

        await act.Should().ThrowAsync<SqliteException>();
        var retained =
            await InteropFactStoreReader.ReadNativeExportsAsync(_store!);
        retained.IsComplete.Should().BeTrue();
        retained.Facts.Should().ContainSingle()
            .Which.Fact.SymbolCanonicalKey.Should().Be(old.Key);
    }

    [Fact]
    public async Task Invalid_candidate_is_rejected_before_prior_snapshot_changes()
    {
        var old = await SeedNativeAnnotationAsync(
            "old.h",
            "c:E:old.h::old");
        var valid = NativeFile(
            "new.h",
            NativeExportFact(
                "c:E:new.h::new",
                PathFor("new.h")));
        var invalid = valid with
        {
            ContentSha256 = [1, 2, 3],
        };

        var act = () => _store!.ReplaceNativeInteropSnapshotAsync(
            Replacement(invalid));

        await act.Should().ThrowAsync<ArgumentException>();
        (await InteropFactStoreReader.ReadNativeExportsAsync(_store!))
            .Facts.Should().ContainSingle()
            .Which.Fact.SymbolCanonicalKey.Should().Be(old.Key);
    }

    [Fact]
    public async Task Cancellation_before_write_retains_prior_snapshot()
    {
        var old = await SeedNativeAnnotationAsync(
            "old.h",
            "c:E:old.h::old");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var act = () => _store!.ReplaceNativeInteropSnapshotAsync(
            Replacement(
                NativeFile(
                    "new.h",
                    NativeExportFact(
                        "c:E:new.h::new",
                        PathFor("new.h")))),
            cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await InteropFactStoreReader.ReadNativeExportsAsync(_store!))
            .Facts.Should().ContainSingle()
            .Which.Fact.SymbolCanonicalKey.Should().Be(old.Key);
    }

    private async Task<Owner> SeedNativeAnnotationAsync(
        string relativePath,
        string canonicalKey)
    {
        var owner = await SeedOwnerAsync(
            relativePath,
            canonicalKey,
            "old",
            "native-export");
        var fact = NativeExportFact(canonicalKey, owner.Path);
        await _store!.BulkInsertAnnotationsAsync(
        [
            new AnnotationRecord(
                owner.SymbolId,
                "InteropFact",
                "MedInterop.InteropFact",
                InteropAnnotationFlavors.NativeExport,
                InteropFactPayloadCodec.EncodeNativeExport(
                    fact with
                    {
                        Evidence = fact.Evidence with
                        {
                            ProducingFileId = owner.FileId,
                        },
                    }),
                AttributeSymbolId: null),
        ]);
        return owner;
    }

    private async Task<Owner> SeedManagedRecordAnnotationAsync(
        string relativePath,
        string canonicalKey)
    {
        var owner = await SeedOwnerAsync(
            relativePath,
            canonicalKey,
            "Payload",
            "struct");
        var fact = new AbiRecordLayout(
            canonicalKey,
            AbiRecordKind.Sequential,
            SizeBytes: 4,
            AlignmentBytes: 4,
            Pack: 8,
            Fields: [],
            Target,
            Evidence(owner.Path) with
            {
                ProducingFileId = owner.FileId,
            });
        await _store!.BulkInsertAnnotationsAsync(
        [
            new AnnotationRecord(
                owner.SymbolId,
                "InteropFact",
                "MedInterop.AbiRecord",
                InteropAnnotationFlavors.AbiRecord,
                InteropFactPayloadCodec.EncodeAbiRecord(fact),
                AttributeSymbolId: null),
        ]);
        return owner;
    }

    private async Task<Owner> SeedOwnerAsync(
        string relativePath,
        string canonicalKey,
        string name,
        string kind)
    {
        var path = PathFor(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "// fixture");
        var fileId = await _store!.UpsertFileAsync(
            path,
            new byte[32],
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
                1,
                5,
                null,
                null));
        return new Owner(fileId, symbolId, canonicalKey, path);
    }

    private NativeInteropFileFacts NativeFile(
        string relativePath,
        NativeExport export)
    {
        var symbol = new FileSymbolFact(
            export.SymbolCanonicalKey,
            export.ExportName,
            export.ExportName,
            "native-export",
            export.Evidence.Location.StartLine,
            export.Evidence.Location.StartColumn,
            export.Evidence.Location.EndLine,
            export.Evidence.Location.EndColumn,
            $"{export.ExportName}()",
            ContainerCanonicalKey: null,
            Modifiers: null,
            Accessibility: 0,
            XmlSummary: null);
        var annotation = new FileAnnotationFact(
            export.SymbolCanonicalKey,
            "InteropFact",
            "MedInterop.InteropFact",
            InteropAnnotationFlavors.NativeExport,
            InteropFactPayloadCodec.EncodeNativeExport(export),
            AttributeCanonicalKey: null);
        return CreateFileFacts(relativePath, [symbol], [annotation]);
    }

    private NativeInteropFileFacts NativeFile(
        string relativePath,
        AbiRecordLayout record)
    {
        var name = record.SymbolCanonicalKey[(record.SymbolCanonicalKey
            .LastIndexOf("::", StringComparison.Ordinal) + 2)..];
        var symbol = new FileSymbolFact(
            record.SymbolCanonicalKey,
            name,
            name,
            "struct",
            record.Evidence.Location.StartLine,
            record.Evidence.Location.StartColumn,
            record.Evidence.Location.EndLine,
            record.Evidence.Location.EndColumn,
            $"struct {name}",
            ContainerCanonicalKey: null,
            Modifiers: null,
            Accessibility: 0,
            XmlSummary: null);
        var annotation = new FileAnnotationFact(
            record.SymbolCanonicalKey,
            "InteropFact",
            "MedInterop.InteropFact",
            InteropAnnotationFlavors.AbiRecord,
            InteropFactPayloadCodec.EncodeAbiRecord(record),
            AttributeCanonicalKey: null);
        return CreateFileFacts(relativePath, [symbol], [annotation]);
    }

    private NativeInteropFileFacts CreateFileFacts(
        string relativePath,
        IReadOnlyList<FileSymbolFact> symbols,
        IReadOnlyList<FileAnnotationFact> annotations)
    {
        var path = PathFor(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "// candidate");
        return new NativeInteropFileFacts(
            path,
            Enumerable.Repeat((byte)7, 32).ToArray(),
            DateTimeOffset.UtcNow,
            symbols,
            annotations);
    }

    private static NativeInteropSnapshotReplacement Replacement(
        params NativeInteropFileFacts[] files) =>
        new(
            [
                InteropAnnotationFlavors.NativeExport,
                InteropAnnotationFlavors.AbiRecord,
            ],
            files);

    private static NativeExport NativeExportFact(
        string canonicalKey,
        string path) =>
        new(
            canonicalKey,
            canonicalKey[(canonicalKey.LastIndexOf("::", StringComparison.Ordinal) + 2)..],
            InteropCallingConvention.Cdecl,
            new AbiTypeRef("void", AbiTypeCategory.Void),
            [],
            HasCLinkage: true,
            IsBinaryVerified: true,
            Target,
            Evidence(path))
        {
            LibraryName = "native.dll",
            ModuleIdentitySource = NativeModuleIdentitySource.Binary,
        };

    private static AbiRecordLayout RecordFact(
        string canonicalKey,
        string path) =>
        new(
            canonicalKey,
            AbiRecordKind.Native,
            SizeBytes: 4,
            AlignmentBytes: 4,
            Pack: null,
            Fields: [],
            Target,
            Evidence(path));

    private static Evidence Evidence(string path) =>
        new(
            ProducingFileId: 1,
            new SourceLocation(path, 1, 1, 1, 5),
            EvidenceConfidence.Exact,
            "native-snapshot-test");

    private string PathFor(string relativePath) =>
        Path.GetFullPath(Path.Join(_temporaryDirectory, relativePath));

    private static InteropTarget Target { get; } =
        InteropTarget.WindowsX64Msvc;

    private sealed record Owner(
        long FileId,
        long SymbolId,
        string Key,
        string Path);
}
