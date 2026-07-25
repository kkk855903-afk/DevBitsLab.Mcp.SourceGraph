using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Interop;
using DevBitsLab.Mcp.SourceGraph.Indexing.Clang;
using DevBitsLab.Mcp.SourceGraph.Server.Interop;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;
using SymbolKinds = DevBitsLab.Mcp.SourceGraph.Sdk.SymbolKinds;

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
    public async Task Complete_snapshot_publishes_queryable_native_type_symbols()
    {
        var path = PathFor("native/types.hpp");
        var declarations = new[]
        {
            Type(
                "cpp:T:native/types.hpp::Payload",
                NativeTypeDeclarationKind.Struct,
                "Payload",
                path,
                line: 2),
            Type(
                "cpp:T:native/types.hpp::Value",
                NativeTypeDeclarationKind.Union,
                "Value",
                path,
                line: 4),
            Type(
                "cpp:T:native/types.hpp::Status",
                NativeTypeDeclarationKind.Enum,
                "Status",
                path,
                line: 6),
            Type(
                "cpp:A:native/types.hpp::PayloadHandle",
                NativeTypeDeclarationKind.Typedef,
                "PayloadHandle",
                path,
                line: 8),
        };
        var record = Record(
            declarations[0].SymbolCanonicalKey,
            path) with
        {
            Evidence = declarations[0].Evidence,
        };

        var result = await Publisher().PublishAsync(Snapshot(
            hashes: [ContentHash(path, Hash(21))],
            sourceExports: [],
            records: [record],
            types: declarations));

        result.IsComplete.Should().BeTrue();
        result.SymbolsPublished.Should().Be(4,
            "the ABI layout shares its struct declaration symbol");
        result.AnnotationsPublished.Should().Be(1);
        var expectedKinds = new Dictionary<string, string>(
            StringComparer.Ordinal)
        {
            ["Payload"] = SymbolKinds.Struct,
            ["Value"] = SymbolKinds.Union,
            ["Status"] = SymbolKinds.Enum,
            ["PayloadHandle"] = SymbolKinds.TypeAlias,
        };
        foreach (var expected in expectedKinds)
        {
            var hit = (await _store!.FindSymbolsAsync(expected.Key))
                .Single(item => item.Name == expected.Key);
            hit.Kind.Should().Be(expected.Value);
            hit.FilePath.Should().Be(path);
        }
        (await _store!.SearchSymbolsAsync("PayloadHandle"))
            .Should().ContainSingle(hit =>
                hit.CanonicalKey
                    == "cpp:A:native/types.hpp::PayloadHandle");
        (await InteropFactStoreReader.ReadAbiRecordsAsync(_store))
            .Facts.Should().ContainSingle()
            .Which.Fact.SymbolCanonicalKey.Should().Be(
                declarations[0].SymbolCanonicalKey);
    }

    [Fact]
    public async Task Extractor_result_types_flow_through_builder_and_publisher()
    {
        var path = PathFor("native/extracted.hpp");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "// content-bound extractor input");
        var builder = new NativeInteropSnapshotBuilder(
            (request, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(new ClangNativeExtractionResult(
                    Functions: [],
                    Types:
                    [
                        Type(
                            "cpp:T:native/extracted.hpp::Payload",
                            NativeTypeDeclarationKind.Struct,
                            "Payload",
                            request.SourceFilePath,
                            line: 2),
                        Type(
                            "cpp:T:native/extracted.hpp::Value",
                            NativeTypeDeclarationKind.Union,
                            "Value",
                            request.SourceFilePath,
                            line: 4),
                        Type(
                            "cpp:T:native/extracted.hpp::Status",
                            NativeTypeDeclarationKind.Enum,
                            "Status",
                            request.SourceFilePath,
                            line: 6),
                        Type(
                            "cpp:A:native/extracted.hpp::StatusCode",
                            NativeTypeDeclarationKind.Typedef,
                            "StatusCode",
                            request.SourceFilePath,
                            line: 8),
                    ],
                    Exports: [],
                    RecordLayouts: [],
                    Diagnostics: [])
                {
                    IncludedFiles = [request.SourceFilePath],
                });
            });

        var snapshot = await builder.BuildAsync(
            _temporaryDirectory,
            new ScopeInteropConfig(
                Target,
                [
                    new InteropTranslationUnitConfig(
                        "native/extracted.hpp",
                        "native.dll",
                        [
                            "-x",
                            "c++",
                        ],
                        BinaryPath: null),
                ]),
            new ScopePathPolicy(_temporaryDirectory));

        snapshot.IsComplete.Should().BeTrue(
            string.Join(
                "; ",
                snapshot.Failures.Select(failure => failure.Message)
                    .Concat(snapshot.Diagnostics.Select(diagnostic =>
                        $"{diagnostic.Diagnostic.Code}: "
                        + diagnostic.Diagnostic.Message))));
        snapshot.Types.Select(type => type.Name).Should().BeEquivalentTo(
            "Payload",
            "Value",
            "Status",
            "StatusCode");
        snapshot.Types.Should().OnlyContain(type =>
            type.Evidence.Confidence == EvidenceConfidence.Exact
            && type.Evidence.Producer == "clang-native");

        var publication = await Publisher().PublishAsync(snapshot);

        publication.IsComplete.Should().BeTrue();
        publication.SymbolsPublished.Should().Be(4);
        (await _store!.FindSymbolsAsync("Payload"))
            .Should().Contain(hit =>
                hit.Name == "Payload"
                && hit.Kind == SymbolKinds.Struct);
        (await _store.SearchSymbolsAsync("StatusCode"))
            .Should().Contain(hit =>
                hit.Name == "StatusCode"
                && hit.Kind == SymbolKinds.TypeAlias);
    }

    [Fact]
    public async Task Native_type_replacement_reports_and_cleans_stale_aliases()
    {
        var path = PathFor("native/types.hpp");
        var enumType = Type(
            "cpp:T:native/types.hpp::Status",
            NativeTypeDeclarationKind.Enum,
            "Status",
            path,
            line: 2);
        var alias = Type(
            "cpp:A:native/types.hpp::StatusCode",
            NativeTypeDeclarationKind.Typedef,
            "StatusCode",
            path,
            line: 4);
        (await Publisher().PublishAsync(Snapshot(
            hashes: [ContentHash(path, Hash(22))],
            sourceExports: [],
            types: [enumType, alias])))
            .IsComplete.Should().BeTrue();

        var updatedEnum = enumType with
        {
            IsDefinition = false,
            Evidence = enumType.Evidence with
            {
                Location = new SourceLocation(path, 12, 1, 12, 8),
                Metadata = new Dictionary<string, string>(
                    StringComparer.Ordinal)
                {
                    ["declarationKind"] = "enum",
                    ["isDefinition"] = "false",
                    ["target"] = Target.RuntimeIdentifier,
                },
            },
        };
        var replacement = await Publisher().PublishAsync(Snapshot(
            hashes: [ContentHash(path, Hash(23))],
            sourceExports: [],
            types: [updatedEnum]));

        replacement.IsComplete.Should().BeTrue();
        replacement.StaleCanonicalKeys.Should().Equal(
            alias.SymbolCanonicalKey);
        var current = (await _store!.FindSymbolsAsync("Status"))
            .Single(hit => hit.CanonicalKey == enumType.SymbolCanonicalKey);
        current.StartLine.Should().Be(12);
        current.Modifiers.Should().Be("declaration");

        var cleanup =
            await _store.DeleteOrphanedNativeInteropSymbolsAsync(
                replacement.StaleCanonicalKeys);
        cleanup.DeletedCanonicalKeys.Should().Equal(alias.SymbolCanonicalKey);
        (await _store.GetSymbolByCanonicalKeyAsync(alias.SymbolCanonicalKey))
            .Should().BeNull();
    }

    [Fact]
    public async Task Complete_snapshot_round_trips_native_risk_fact_payload()
    {
        var sourcePath = PathFor("native/risk.cpp");
        var source = RiskExport(sourcePath);

        var result = await Publisher().PublishAsync(Snapshot(
            hashes: [ContentHash(sourcePath, Hash(11))],
            sourceExports: [source]));

        result.IsComplete.Should().BeTrue();
        var stored =
            (await InteropFactStoreReader.ReadNativeExportsAsync(_store!))
            .Facts.Should().ContainSingle().Subject.Fact;
        stored.RetainedCallbacks.Should().BeEquivalentTo(
            source.RetainedCallbacks);
        stored.ExceptionEscape.Should().BeEquivalentTo(
            source.ExceptionEscape);
        stored.ReturnAllocation.Should().BeEquivalentTo(
            source.ReturnAllocation);
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
    public async Task Syntax_only_symbols_are_not_native_stale_candidates()
    {
        var implementationPath = PathFor("native/api.cpp");
        var fileId = await _store!.UpsertFileAsync(
            implementationPath,
            Hash(20),
            DateTimeOffset.UtcNow);
        var syntaxKey = "cpp:F:native/api.cpp::syntax::run()";
        await _store.UpsertSymbolAsync(
            syntaxKey,
            new Symbol(
                0,
                "run",
                "run",
                SymbolKinds.Function,
                fileId,
                2,
                1,
                2,
                20,
                "int run()",
                null,
                "syntax-only"));
        var headerPath = PathFor("native/api.h");
        var export = Export(
            "c:E:native/api.h::run",
            "native.dll",
            binaryVerified: false,
            headerPath);

        var result = await Publisher().PublishAsync(Snapshot(
            hashes: [ContentHash(headerPath, Hash(21))],
            sourceExports: [export]));

        result.IsComplete.Should().BeTrue();
        result.StaleCanonicalKeys.Should().NotContain(syntaxKey);
        (await _store.GetSymbolByCanonicalKeyAsync(syntaxKey))
            .Should().NotBeNull();
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
    public async Task Incomplete_candidate_retains_all_native_risk_payloads_and_annotation()
    {
        var path = PathFor("native/risk.cpp");
        var prior = RiskExport(path);
        (await Publisher().PublishAsync(Snapshot(
            hashes: [ContentHash(path, Hash(12))],
            sourceExports: [prior])))
            .IsComplete.Should().BeTrue();
        var symbol = (await _store!.GetAllSymbolKeysAsync())
            .Single(item =>
                item.CanonicalKey == prior.SymbolCanonicalKey);
        var priorAnnotation =
            (await _store.GetAnnotationsForSymbolAsync(symbol.Id))
            .Should().ContainSingle(annotation =>
                annotation.Flavor
                    == InteropAnnotationFlavors.NativeExport)
            .Subject;
        priorAnnotation.ArgsJson.Should().NotBeNullOrWhiteSpace();
        var failure = new NativeInteropSnapshotFailure(
            NativeInteropSnapshotFailureKind.ExtractionFailed,
            TranslationUnitIndex: 0,
            ConfiguredPath: "native/risk.cpp",
            Message: "candidate incomplete");

        var result = await Publisher().PublishAsync(Snapshot(
            hashes: [],
            sourceExports: [],
            complete: false,
            failures: [failure]));

        result.IsComplete.Should().BeFalse();
        result.SnapshotFailures.Should().Equal(failure);
        var retainedAnnotation =
            (await _store.GetAnnotationsForSymbolAsync(symbol.Id))
            .Should().ContainSingle(annotation =>
                annotation.Flavor
                    == InteropAnnotationFlavors.NativeExport)
            .Subject;
        retainedAnnotation.Should().Be(priorAnnotation);
        var retained =
            (await InteropFactStoreReader.ReadNativeExportsAsync(_store))
            .Facts.Should().ContainSingle().Subject.Fact;
        retained.RetainedCallbacks.Should().BeEquivalentTo(
            prior.RetainedCallbacks);
        retained.ExceptionEscape.Should().BeEquivalentTo(
            prior.ExceptionEscape);
        retained.ReturnAllocation.Should().BeEquivalentTo(
            prior.ReturnAllocation);
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
        IReadOnlyList<NativeInteropSnapshotFailure>? failures = null,
        IReadOnlyList<NativeTypeDeclarationFact>? types = null)
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
            Types = types ?? [],
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

    private static NativeExport RiskExport(string path)
    {
        var location = new SourceLocation(path, 1, 1, 1, 8);
        return new NativeExport(
            "c:E:native/risk.cpp::risk",
            "risk",
            InteropCallingConvention.Cdecl,
            new AbiTypeRef(
                "int",
                AbiTypeCategory.SignedInteger,
                sizeBytes: 4,
                alignmentBytes: 4,
                isSigned: true),
            [
                new AbiParameter(
                    0,
                    "callback",
                    new AbiTypeRef(
                        "void (*)(int)",
                        AbiTypeCategory.FunctionPointer,
                        pointerDepth: 1,
                        sizeBytes: 8,
                        alignmentBytes: 8),
                    AbiParameterDirection.In,
                    location),
            ],
            HasCLinkage: true,
            IsBinaryVerified: false,
            Target,
            EvidenceAt(path))
        {
            LibraryName = "native.dll",
            ModuleIdentitySource =
                NativeModuleIdentitySource.Configuration,
            RetainedCallbacks =
            [
                new NativeCallbackRetention(
                    0,
                    Target,
                    NativeRiskEvidence(
                        path,
                        "clang-native-retention",
                        "parameterPosition",
                        "0")),
            ],
            ExceptionEscape = new NativeExceptionEscape(
                Target,
                NativeRiskEvidence(
                    path,
                    "clang-native-exception",
                    "escapeKind",
                    "direct-throw")),
            ReturnAllocation = new NativeReturnAllocation(
                InteropAllocatorFamily.CrtHeap,
                Target,
                NativeRiskEvidence(
                    path,
                    "clang-native-allocation",
                    "allocatorFamily",
                    "crt_heap",
                    ("allocator", "malloc"))),
        };
    }

    private static Evidence NativeRiskEvidence(
        string path,
        string producer,
        string factKey,
        string factValue,
        params (string Key, string Value)[] extraMetadata)
    {
        var metadata = new Dictionary<string, string>(
            StringComparer.Ordinal)
        {
            ["target"] = Target.RuntimeIdentifier,
            [factKey] = factValue,
        };
        foreach (var item in extraMetadata)
        {
            metadata.Add(item.Key, item.Value);
        }
        return new Evidence(
            ProducingFileId: 1,
            new SourceLocation(path, 1, 1, 1, 8),
            EvidenceConfidence.Exact,
            producer,
            metadata);
    }

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

    private static NativeTypeDeclarationFact Type(
        string key,
        NativeTypeDeclarationKind kind,
        string name,
        string path,
        int line,
        bool isDefinition = true)
    {
        var declarationKind = kind switch
        {
            NativeTypeDeclarationKind.Struct => "record",
            NativeTypeDeclarationKind.Union => "union",
            NativeTypeDeclarationKind.Enum => "enum",
            NativeTypeDeclarationKind.Typedef => "typedef",
            _ => throw new InvalidOperationException(),
        };
        return new NativeTypeDeclarationFact(
            key,
            kind,
            name,
            name,
            new AbiTypeRef(
                name,
                kind == NativeTypeDeclarationKind.Enum
                    ? AbiTypeCategory.Enum
                    : kind == NativeTypeDeclarationKind.Typedef
                        ? AbiTypeCategory.Opaque
                        : AbiTypeCategory.Record,
                sizeBytes: 4,
                alignmentBytes: 4),
            isDefinition,
            new Evidence(
                ProducingFileId: 1,
                new SourceLocation(path, line, 1, line, 8),
                EvidenceConfidence.Exact,
                "clang-native",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["declarationKind"] = declarationKind,
                    ["isDefinition"] = isDefinition ? "true" : "false",
                    ["target"] = Target.RuntimeIdentifier,
                }));
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
