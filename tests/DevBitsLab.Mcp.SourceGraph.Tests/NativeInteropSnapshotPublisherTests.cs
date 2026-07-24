using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Interop;
using DevBitsLab.Mcp.SourceGraph.Indexing.Clang;
using DevBitsLab.Mcp.SourceGraph.Server.Interop;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class NativeInteropSnapshotPublisherTests : IAsyncLifetime
{
    private string _temporaryDirectory = string.Empty;
    private SqliteGraphStore? _store;

    public async Task InitializeAsync()
    {
        _temporaryDirectory = Path.Join(
            Path.GetTempPath(),
            "sourcegraph-native-snapshot-publisher-"
            + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temporaryDirectory);
        _store = new SqliteGraphStore(
            Path.Join(_temporaryDirectory, "graph.db"));
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
    public async Task Complete_snapshot_publishes_content_bound_effective_facts()
    {
        var header = PathFor("native/api.h");
        var types = PathFor("native/types.h");
        var source = Export(
            "c:E:native/api.h::run",
            "configured.dll",
            binaryVerified: false,
            header);
        var verified = Export(
            source.SymbolCanonicalKey,
            "verified.dll",
            binaryVerified: true,
            header);
        var record = Record("cpp:T:native/types.h::Payload", types);
        var headerHash = Hash(1);
        var typesHash = Hash(2);

        var result = await Publisher().PublishAsync(Snapshot(
            hashes:
            [
                ContentHash(header, headerHash),
                ContentHash(types, typesHash),
            ],
            sourceExports: [source],
            verifiedExports: [verified],
            records: [record]));

        result.IsComplete.Should().BeTrue();
        result.FilesPublished.Should().Be(2);
        result.SymbolsPublished.Should().Be(2);
        result.AnnotationsPublished.Should().Be(2);
        result.StaleCanonicalKeys.Should().BeEmpty();
        result.Failure.Should().BeNull();

        var exports =
            await InteropFactStoreReader.ReadNativeExportsAsync(_store!);
        exports.IsComplete.Should().BeTrue();
        var storedExport = exports.Facts.Should().ContainSingle().Subject.Fact;
        storedExport.SymbolCanonicalKey.Should().Be(
            verified.SymbolCanonicalKey);
        storedExport.LibraryName.Should().Be(verified.LibraryName);
        storedExport.IsBinaryVerified.Should().BeTrue();
        storedExport.Evidence.Location.Should().Be(
            verified.Evidence.Location);
        var records =
            await InteropFactStoreReader.ReadAbiRecordsAsync(_store!);
        records.IsComplete.Should().BeTrue();
        var storedRecord = records.Facts.Should().ContainSingle().Subject.Fact;
        storedRecord.SymbolCanonicalKey.Should().Be(
            record.SymbolCanonicalKey);
        storedRecord.SizeBytes.Should().Be(record.SizeBytes);
        storedRecord.Fields.Should().ContainSingle()
            .Which.Name.Should().Be("value");
        storedRecord.Evidence.Location.Should().Be(
            record.Evidence.Location);
        (await _store!.GetFileContentHashAsync(header))
            .Should().Equal(headerHash);
        (await _store.GetFileContentHashAsync(types))
            .Should().Equal(typesHash);
    }

    [Fact]
    public async Task Complete_replacement_reports_stale_keys_and_clears_old_annotations()
    {
        var oldPath = PathFor("native/old.h");
        var old = Export(
            "c:E:native/old.h::old",
            "native.dll",
            binaryVerified: false,
            oldPath);
        (await Publisher().PublishAsync(Snapshot(
            hashes: [ContentHash(oldPath, Hash(3))],
            sourceExports: [old])))
            .IsComplete.Should().BeTrue();

        var newPath = PathFor("native/new.h");
        var current = Export(
            "c:E:native/new.h::run",
            "native.dll",
            binaryVerified: false,
            newPath);
        var result = await Publisher().PublishAsync(Snapshot(
            hashes: [ContentHash(newPath, Hash(4))],
            sourceExports: [current]));

        result.IsComplete.Should().BeTrue();
        result.StaleCanonicalKeys.Should().Equal(old.SymbolCanonicalKey);
        var stored =
            await InteropFactStoreReader.ReadNativeExportsAsync(_store!);
        stored.Facts.Should().ContainSingle()
            .Which.Fact.SymbolCanonicalKey.Should().Be(
                current.SymbolCanonicalKey);
        (await _store!.GetAllSymbolKeysAsync())
            .Should().Contain(item =>
                item.CanonicalKey == old.SymbolCanonicalKey,
                "stale declarations support last-good edges until rematching succeeds");
    }

    [Fact]
    public async Task Incomplete_candidate_retains_the_last_complete_snapshot()
    {
        var path = PathFor("native/api.h");
        var prior = Export(
            "c:E:native/api.h::run",
            "native.dll",
            binaryVerified: false,
            path);
        (await Publisher().PublishAsync(Snapshot(
            hashes: [ContentHash(path, Hash(5))],
            sourceExports: [prior])))
            .IsComplete.Should().BeTrue();
        var failure = new NativeInteropSnapshotFailure(
            NativeInteropSnapshotFailureKind.ExtractionFailed,
            TranslationUnitIndex: 0,
            ConfiguredPath: "native/api.cpp",
            Message: "worker failed");

        var result = await Publisher().PublishAsync(Snapshot(
            hashes: [],
            sourceExports: [],
            complete: false,
            failures: [failure]));

        result.IsComplete.Should().BeFalse();
        result.FilesPublished.Should().Be(0);
        result.SnapshotFailures.Should().Equal(failure);
        var stored =
            await InteropFactStoreReader.ReadNativeExportsAsync(_store!);
        stored.Facts.Should().ContainSingle()
            .Which.Fact.Should().BeEquivalentTo(prior);
    }

    [Fact]
    public async Task Fact_without_a_content_hash_is_rejected_before_storage_changes()
    {
        var priorPath = PathFor("native/prior.h");
        var prior = Export(
            "c:E:native/prior.h::prior",
            "native.dll",
            binaryVerified: false,
            priorPath);
        (await Publisher().PublishAsync(Snapshot(
            hashes: [ContentHash(priorPath, Hash(6))],
            sourceExports: [prior])))
            .IsComplete.Should().BeTrue();
        var unboundPath = PathFor("native/unbound.h");
        var unbound = Export(
            "c:E:native/unbound.h::run",
            "native.dll",
            binaryVerified: false,
            unboundPath);

        var result = await Publisher().PublishAsync(Snapshot(
            hashes: [],
            sourceExports: [unbound]));

        result.IsComplete.Should().BeFalse();
        result.FilesPublished.Should().Be(0);
        result.Failure.Should().Contain(
            "not owned by a content-bound included file");
        var stored =
            await InteropFactStoreReader.ReadNativeExportsAsync(_store!);
        stored.Facts.Should().ContainSingle()
            .Which.Fact.Should().BeEquivalentTo(prior);
    }

    [Fact]
    public async Task Complete_zero_fact_snapshot_clears_native_annotations()
    {
        var priorPath = PathFor("native/prior.h");
        var prior = Export(
            "c:E:native/prior.h::prior",
            "native.dll",
            binaryVerified: false,
            priorPath);
        (await Publisher().PublishAsync(Snapshot(
            hashes: [ContentHash(priorPath, Hash(7))],
            sourceExports: [prior])))
            .IsComplete.Should().BeTrue();

        var result = await Publisher().PublishAsync(Snapshot(
            hashes: [],
            sourceExports: []));

        result.IsComplete.Should().BeTrue();
        result.StaleCanonicalKeys.Should().Equal(prior.SymbolCanonicalKey);
        (await InteropFactStoreReader.ReadNativeExportsAsync(_store!))
            .Facts.Should().BeEmpty();
    }

    [Fact]
    public async Task Complete_snapshot_publishes_exact_native_function_call_edges()
    {
        var exportPath = PathFor("native/exports.cpp");
        var algorithmPath = PathFor("native/algorithm.cpp");
        var nativeExport = Export(
            "c:E:native/exports.cpp::calculate",
            "native.dll",
            binaryVerified: false,
            exportPath);
        var function = Function(
            "cpp:F:native/algorithm.cpp::Algorithm::Calculate(int)",
            "c:@S@Algorithm@F@Calculate#I#",
            "Calculate",
            "Algorithm::Calculate",
            algorithmPath,
            isMethod: true);
        var call = new NativeCallFact(
            nativeExport.SymbolCanonicalKey,
            function.DeclarationUsr,
            function.GraphCanonicalKey,
            Target,
            new Evidence(
                1,
                new SourceLocation(exportPath, 4, 12, 4, 34),
                EvidenceConfidence.Exact,
                "clang-native-call",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["callKind"] = "direct",
                    ["target"] = Target.RuntimeIdentifier,
                }));

        var result = await Publisher().PublishAsync(Snapshot(
            hashes:
            [
                ContentHash(exportPath, Hash(8)),
                ContentHash(algorithmPath, Hash(9)),
            ],
            sourceExports: [nativeExport],
            functions: [function],
            calls: [call]));

        result.IsComplete.Should().BeTrue();
        result.SymbolsPublished.Should().Be(2);
        result.EdgesPublished.Should().Be(1);
        var keys = await _store!.GetAllSymbolKeysAsync();
        var source = keys.Single(item =>
            item.CanonicalKey == nativeExport.SymbolCanonicalKey);
        var edge = (await _store.ListCalleesAsync(
                source.Id,
                edgeKind: "calls"))
            .Should().ContainSingle().Subject;
        edge.CanonicalKey.Should().Be(function.SymbolCanonicalKey);
        (await _store.ListEdgeEvidenceAsync(
                source.Id,
                edge.Id,
                "calls"))
            .Should().ContainSingle()
            .Which.Producer.Should().Be("clang-native-call");

        var partial = await Publisher().PublishAsync(Snapshot(
            hashes: [],
            sourceExports: [],
            complete: false,
            failures:
            [
                new NativeInteropSnapshotFailure(
                    NativeInteropSnapshotFailureKind.CallGraphIncomplete,
                    0,
                    "native/exports.cpp",
                    "indirect call"),
            ]));
        partial.IsComplete.Should().BeFalse();
        (await _store.ListCalleesAsync(source.Id, edgeKind: "calls"))
            .Should().ContainSingle(
                "a partial candidate must retain the last-good native call graph");

        var managedPath = PathFor("Managed.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(managedPath)!);
        await File.WriteAllTextAsync(managedPath, "// managed");
        var managedFileId = await _store.UpsertFileAsync(
            managedPath,
            Hash(10),
            DateTimeOffset.UtcNow);
        var managedSourceId = await _store.UpsertSymbolAsync(
            "csharp:M:Managed.Source",
            new Symbol(
                0,
                "Source",
                "Managed.Source",
                "method",
                managedFileId,
                1,
                1,
                1,
                5,
                null,
                null));
        var managedTargetId = await _store.UpsertSymbolAsync(
            "csharp:M:Managed.Target",
            new Symbol(
                0,
                "Target",
                "Managed.Target",
                "method",
                managedFileId,
                2,
                1,
                2,
                5,
                null,
                null));
        var legacyTargetId = await _store.UpsertSymbolAsync(
            "csharp:M:Managed.LegacyTarget",
            new Symbol(
                0,
                "LegacyTarget",
                "Managed.LegacyTarget",
                "method",
                managedFileId,
                3,
                1,
                3,
                5,
                null,
                null));
        await _store.BulkInsertEdgesAsync(
        [
            new Edge(
                managedSourceId,
                managedTargetId,
                "calls")
            {
                Evidence = new Evidence(
                    managedFileId,
                    new SourceLocation(managedPath, 1, 1, 1, 5),
                    EvidenceConfidence.Exact,
                    "roslyn-call"),
            },
            new Edge(
                managedSourceId,
                legacyTargetId,
                "calls"),
        ]);

        var cleared = await Publisher().ClearAsync();
        cleared.IsComplete.Should().BeTrue();
        cleared.StaleCanonicalKeys.Should().BeEquivalentTo(
            nativeExport.SymbolCanonicalKey,
            function.SymbolCanonicalKey);
        (await _store.ListCalleesAsync(source.Id, edgeKind: "calls"))
            .Should().BeEmpty();
        (await _store.ListCalleesAsync(
                 managedSourceId,
                 edgeKind: "calls"))
            .Should().HaveCount(2)
            .And.Contain(
                symbol => symbol.Id == managedTargetId,
                "native replacement cannot remove independently evidenced managed calls")
            .And.Contain(
                symbol => symbol.Id == legacyTargetId,
                "native replacement cannot remove unrelated legacy calls without evidence");
    }

    private NativeInteropSnapshotPublisher Publisher() =>
        new(_store!);

    private NativeInteropSnapshot Snapshot(
        IReadOnlyList<NativeInteropFileContentHash> hashes,
        IReadOnlyList<NativeExport> sourceExports,
        IReadOnlyList<NativeExport>? verifiedExports = null,
        IReadOnlyList<AbiRecordLayout>? records = null,
        IReadOnlyList<NativeFunctionFact>? functions = null,
        IReadOnlyList<NativeCallFact>? calls = null,
        bool complete = true,
        IReadOnlyList<NativeInteropSnapshotFailure>? failures = null)
    {
        var byPath = hashes.ToDictionary(
            item => item.FilePath,
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        return new NativeInteropSnapshot(
            Target,
            Contributions: [],
            IncludedFiles: hashes.Select(item => item.FilePath).ToArray(),
            DependencyFanout:
                new Dictionary<string, IReadOnlyList<string>>(
                    byPath.Comparer),
            ContentHashes: byPath,
            SourceExports: sourceExports,
            VerifiedExports: verifiedExports ?? [],
            RecordLayouts: records ?? [],
            Diagnostics: [],
            IsSourceComplete: complete,
            IsExportUniverseComplete: complete,
            IsComplete: complete,
            Failures: failures ?? [])
        {
            Functions = functions ?? [],
            Calls = calls ?? [],
        };
    }

    private static NativeInteropFileContentHash ContentHash(
        string path,
        byte[] sha256) =>
        new(path, LengthBytes: 16, sha256);

    private static NativeExport Export(
        string key,
        string library,
        bool binaryVerified,
        string path) =>
        new(
            key,
            key.EndsWith("::old", StringComparison.Ordinal)
                ? "old"
                : key.EndsWith("::prior", StringComparison.Ordinal)
                    ? "prior"
                    : "run",
            InteropCallingConvention.Cdecl,
            new AbiTypeRef("void", AbiTypeCategory.Void),
            [],
            HasCLinkage: true,
            IsBinaryVerified: binaryVerified,
            Target,
            EvidenceAt(path))
        {
            LibraryName = library,
            ModuleIdentitySource = binaryVerified
                ? NativeModuleIdentitySource.Binary
                : NativeModuleIdentitySource.Configuration,
        };

    private static AbiRecordLayout Record(string key, string path)
    {
        var evidence = EvidenceAt(path);
        return new AbiRecordLayout(
            key,
            AbiRecordKind.Native,
            SizeBytes: 4,
            AlignmentBytes: 4,
            Pack: null,
            [
                new AbiFieldLayout(
                    0,
                    "value",
                    new AbiTypeRef(
                        "int",
                        AbiTypeCategory.SignedInteger,
                        sizeBytes: 4,
                        alignmentBytes: 4,
                        isSigned: true),
                    OffsetBytes: 0,
                    SizeBytes: 4,
                    evidence),
            ],
            Target,
            evidence);
    }

    private static NativeFunctionFact Function(
        string key,
        string usr,
        string name,
        string qualifiedName,
        string path,
        bool isMethod) =>
        new(
            key,
            name,
            qualifiedName,
            InteropCallingConvention.Cdecl,
            new AbiTypeRef(
                "int",
                AbiTypeCategory.SignedInteger,
                sizeBytes: 4,
                alignmentBytes: 4,
                isSigned: true),
            [],
            HasCLinkage: false,
            IsExported: false,
            IsDefinition: true,
            EvidenceAt(path))
        {
            DeclarationUsr = usr,
            GraphCanonicalKey = key,
            IsMethod = isMethod,
            Target = Target,
        };

    private static Evidence EvidenceAt(string path) =>
        new(
            ProducingFileId: 1,
            new SourceLocation(path, 1, 1, 1, 8),
            EvidenceConfidence.Exact,
            "native-snapshot-publisher-test");

    private string PathFor(string relativePath) =>
        Path.GetFullPath(Path.Join(
            _temporaryDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static byte[] Hash(byte value) =>
        Enumerable.Repeat(value, 32).ToArray();

    private static InteropTarget Target { get; } =
        InteropTarget.WindowsX64Msvc;
}
