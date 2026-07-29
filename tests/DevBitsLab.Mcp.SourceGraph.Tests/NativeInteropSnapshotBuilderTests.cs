using System.Security.Cryptography;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Indexing.Clang;
using DevBitsLab.Mcp.SourceGraph.Interop;
using DevBitsLab.Mcp.SourceGraph.Server.Interop;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

public sealed class NativeInteropSnapshotBuilderTests : IDisposable
{
    private readonly string _root = Path.Join(
        Path.GetTempPath(),
        "sg-native-snapshot-tests-" + Guid.NewGuid().ToString("N"));

    public NativeInteropSnapshotBuilderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task BuildAsync_processes_translation_units_in_configuration_order()
    {
        Write("native/one.cpp");
        Write("native/two.cpp");
        var calls = new List<string>();
        var config = Config(
            Tu("native/one.cpp", "one", "artifacts/one.dll"),
            Tu("native/two.cpp", "two", "artifacts/two.dll"));
        var builder = new NativeInteropSnapshotBuilder(
            (request, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileNameWithoutExtension(request.SourceFilePath);
                calls.Add("extract:" + name);
                return Task.FromResult(Extraction(
                    request,
                    [Export(request, $"c:E:native/{name}.cpp::{name}", name)]));
            },
            (path, _, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileNameWithoutExtension(path);
                calls.Add("binary:" + name);
                return Task.FromResult(CompleteBinary(name + ".dll", name));
            });

        var snapshot = await builder.BuildAsync(
            _root,
            config,
            new ScopePathPolicy(_root),
            CancellationToken.None);

        calls.Should().Equal(
            "extract:one",
            "extract:one",
            "binary:one",
            "extract:two",
            "extract:two",
            "binary:two");
        snapshot.Contributions.Select(contribution => contribution.ConfigurationIndex)
            .Should().Equal(0, 1);
        snapshot.IsComplete.Should().BeTrue();
        snapshot.VerifiedExports.Select(export => export.ExportName)
            .Should().Equal("one", "two");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Rejected_source_or_binary_path_never_reaches_delegates(
        bool rejectSource)
    {
        Write("native/allowed.cpp");
        Write("PatientData/private.cpp");
        Write("PatientData/private.dll");
        var extractorCalls = 0;
        var verifierCalls = 0;
        var config = Config(new InteropTranslationUnitConfig(
            rejectSource ? "PatientData/private.cpp" : "native/allowed.cpp",
            "medical.dll",
            ["-x", "c++"],
            rejectSource ? "artifacts/allowed.dll" : "PatientData/private.dll"));
        var builder = new NativeInteropSnapshotBuilder(
            (_, _) =>
            {
                extractorCalls++;
                throw new InvalidOperationException("must not extract");
            },
            (_, _, _) =>
            {
                verifierCalls++;
                throw new InvalidOperationException("must not verify");
            });

        var snapshot = await builder.BuildAsync(
            _root,
            config,
            new ScopePathPolicy(_root),
            CancellationToken.None);

        extractorCalls.Should().Be(0);
        verifierCalls.Should().Be(0);
        snapshot.IsComplete.Should().BeFalse();
        snapshot.SourceExports.Should().BeEmpty();
        snapshot.Failures.Should().ContainSingle(failure =>
            failure.Kind == (rejectSource
                ? NativeInteropSnapshotFailureKind.TranslationUnitPathRejected
                : NativeInteropSnapshotFailureKind.BinaryPathRejected));
    }

    [Fact]
    public async Task Complete_binary_exact_name_marks_only_exact_source_export_verified()
    {
        Write("native/api.cpp");
        var config = Config(Tu("native/api.cpp", "medical", "artifacts/medical.dll"));
        var builder = new NativeInteropSnapshotBuilder(
            (request, _) => Task.FromResult(Extraction(
                request,
                [Export(request, "c:E:native/api.cpp::medical", "medical")])),
            (_, _, _) => Task.FromResult(CompleteBinary("medical.dll", "medical")));

        var snapshot = await builder.BuildAsync(
            _root,
            config,
            new ScopePathPolicy(_root),
            CancellationToken.None);

        snapshot.IsComplete.Should().BeTrue();
        snapshot.SourceExports.Should().ContainSingle(export =>
            !export.IsBinaryVerified
            && export.ModuleIdentitySource == NativeModuleIdentitySource.Configuration);
        snapshot.VerifiedExports.Should().ContainSingle(export =>
            export.IsBinaryVerified
            && export.LibraryName == "medical.dll"
            && export.ModuleIdentitySource == NativeModuleIdentitySource.Binary);
    }

    [Fact]
    public async Task Omitted_optional_binary_keeps_source_export_universe_complete()
    {
        Write("native/api.cpp");
        var verifierCalled = false;
        var builder = new NativeInteropSnapshotBuilder(
            (request, _) => Task.FromResult(Extraction(
                request,
                [
                    Export(
                        request,
                        "c:E:native/api.cpp::medical",
                        "medical"),
                ])),
            (_, _, _) =>
            {
                verifierCalled = true;
                throw new InvalidOperationException("must not verify");
            });

        var snapshot = await builder.BuildAsync(
            _root,
            Config(new InteropTranslationUnitConfig(
                "native/api.cpp",
                "medical.dll",
                ["-x", "c++"],
                BinaryPath: null)),
            new ScopePathPolicy(_root),
            CancellationToken.None);

        verifierCalled.Should().BeFalse();
        snapshot.IsSourceComplete.Should().BeTrue();
        snapshot.IsExportUniverseComplete.Should().BeTrue();
        snapshot.IsComplete.Should().BeTrue();
        snapshot.SourceExports.Should().ContainSingle();
        snapshot.VerifiedExports.Should().BeEmpty();
        snapshot.ContentHashes.Should().ContainSingle();
        snapshot.Failures.Should().BeEmpty();
    }

    [Fact]
    public async Task Complete_snapshot_carries_content_bound_native_types()
    {
        Write("native/types.hpp");
        var builder = new NativeInteropSnapshotBuilder(
            (request, _) => Task.FromResult(Extraction(
                request,
                types:
                [
                    Type(
                        request,
                        "cpp:T:native/types.hpp::Payload",
                        NativeTypeDeclarationKind.Struct,
                        "Payload"),
                    Type(
                        request,
                        "cpp:T:native/types.hpp::Value",
                        NativeTypeDeclarationKind.Union,
                        "Value"),
                    Type(
                        request,
                        "cpp:T:native/types.hpp::Status",
                        NativeTypeDeclarationKind.Enum,
                        "Status"),
                    Type(
                        request,
                        "cpp:A:native/types.hpp::PayloadHandle",
                        NativeTypeDeclarationKind.Typedef,
                        "PayloadHandle"),
                ])));

        var snapshot = await builder.BuildAsync(
            _root,
            Config(new InteropTranslationUnitConfig(
                "native/types.hpp",
                "native.dll",
                ["-x", "c++"],
                BinaryPath: null)),
            new ScopePathPolicy(_root),
            CancellationToken.None);

        snapshot.IsComplete.Should().BeTrue();
        snapshot.Types.Select(type => type.Kind).Should().Equal(
            NativeTypeDeclarationKind.Typedef,
            NativeTypeDeclarationKind.Struct,
            NativeTypeDeclarationKind.Enum,
            NativeTypeDeclarationKind.Union);
        snapshot.Contributions.Should().ContainSingle()
            .Which.Types.Should().HaveCount(4);
        snapshot.Types.Should().OnlyContain(type =>
            type.Evidence.Confidence == EvidenceConfidence.Exact
            && type.Evidence.Producer == "clang-native"
            && type.Evidence.Location.FilePath
                == Path.GetFullPath(Path.Join(_root, "native/types.hpp")));
    }

    [Fact]
    public async Task Changed_native_type_between_double_parse_is_rejected()
    {
        Write("native/types.hpp");
        var extractionCount = 0;
        var builder = new NativeInteropSnapshotBuilder(
            (request, _) =>
            {
                extractionCount++;
                var type = Type(
                    request,
                    "cpp:T:native/types.hpp::Payload",
                    NativeTypeDeclarationKind.Struct,
                    "Payload") with
                {
                    DeclaredType = new AbiTypeRef(
                        "Payload",
                        AbiTypeCategory.Record,
                        sizeBytes: extractionCount == 1 ? 4 : 8,
                        alignmentBytes: 4),
                };
                return Task.FromResult(Extraction(
                    request,
                    types: [type]));
            });

        var snapshot = await builder.BuildAsync(
            _root,
            Config(new InteropTranslationUnitConfig(
                "native/types.hpp",
                "native.dll",
                ["-x", "c++"],
                BinaryPath: null)),
            new ScopePathPolicy(_root),
            CancellationToken.None);

        snapshot.IsComplete.Should().BeFalse();
        snapshot.Types.Should().BeEmpty();
        snapshot.Failures.Should().ContainSingle(failure =>
            failure.Kind == NativeInteropSnapshotFailureKind.FactSetChanged);
    }

    [Fact]
    public async Task Identical_native_type_declarations_are_deduplicated()
    {
        Write("native/types.hpp");
        var builder = new NativeInteropSnapshotBuilder(
            (request, _) =>
            {
                var type = Type(
                    request,
                    "cpp:T:native/types.hpp::Payload",
                    NativeTypeDeclarationKind.Struct,
                    "Payload");
                return Task.FromResult(Extraction(
                    request,
                    types: [type, type]));
            });

        var snapshot = await builder.BuildAsync(
            _root,
            Config(new InteropTranslationUnitConfig(
                "native/types.hpp",
                "native.dll",
                ["-x", "c++"],
                BinaryPath: null)),
            new ScopePathPolicy(_root),
            CancellationToken.None);

        snapshot.IsComplete.Should().BeTrue();
        snapshot.Types.Should().ContainSingle()
            .Which.SymbolCanonicalKey.Should().Be(
                "cpp:T:native/types.hpp::Payload");
    }

    [Fact]
    public async Task Conflicting_native_type_duplicates_are_rejected()
    {
        Write("native/types.hpp");
        var builder = new NativeInteropSnapshotBuilder(
            (request, _) =>
            {
                var type = Type(
                    request,
                    "cpp:T:native/types.hpp::Payload",
                    NativeTypeDeclarationKind.Struct,
                    "Payload");
                return Task.FromResult(Extraction(
                    request,
                    types:
                    [
                        type,
                        type with
                        {
                            DeclaredType = new AbiTypeRef(
                                "Payload",
                                AbiTypeCategory.Record,
                                sizeBytes: 8,
                                alignmentBytes: 4),
                        },
                    ]));
            });

        var snapshot = await builder.BuildAsync(
            _root,
            Config(new InteropTranslationUnitConfig(
                "native/types.hpp",
                "native.dll",
                ["-x", "c++"],
                BinaryPath: null)),
            new ScopePathPolicy(_root),
            CancellationToken.None);

        snapshot.IsComplete.Should().BeFalse();
        snapshot.Types.Should().BeEmpty();
        snapshot.Failures.Should().ContainSingle(failure =>
            failure.Kind == NativeInteropSnapshotFailureKind.TypeConflict);
    }

    [Fact]
    public async Task Decorated_binary_name_is_unsupported_partial_and_never_guessed()
    {
        Write("native/api.cpp");
        var config = Config(Tu("native/api.cpp", "medical", "artifacts/medical.dll"));
        var builder = new NativeInteropSnapshotBuilder(
            (request, _) => Task.FromResult(Extraction(
                request,
                [Export(request, "c:E:native/api.cpp::medical", "medical")])),
            (_, _, _) => Task.FromResult(
                CompleteBinary("medical.dll", "_medical@4")));

        var snapshot = await builder.BuildAsync(
            _root,
            config,
            new ScopePathPolicy(_root),
            CancellationToken.None);

        snapshot.IsComplete.Should().BeFalse();
        snapshot.VerifiedExports.Should().BeEmpty();
        snapshot.Failures.Should().Contain(failure =>
            failure.Kind
                == NativeInteropSnapshotFailureKind.UnsupportedBinaryAssociation);
    }

    [Fact]
    public async Task Missing_translation_unit_and_incomplete_binary_are_structured_failures()
    {
        Write("native/present.cpp");
        var config = Config(
            Tu("native/missing.cpp", "missing", "artifacts/missing.dll"),
            Tu("native/present.cpp", "present", "artifacts/present.dll"));
        var extractorCalls = 0;
        var builder = new NativeInteropSnapshotBuilder(
            (request, _) =>
            {
                extractorCalls++;
                return Task.FromResult(Extraction(
                    request,
                    [Export(request, "c:E:native/present.cpp::present", "present")]));
            },
            (_, _, _) => Task.FromResult(new BinaryExportVerificationResult(
                BinaryExportVerificationStatus.Unavailable,
                null,
                null,
                null,
                [],
                "artifact unavailable")));

        var snapshot = await builder.BuildAsync(
            _root,
            config,
            new ScopePathPolicy(_root),
            CancellationToken.None);

        extractorCalls.Should().Be(2);
        snapshot.IsComplete.Should().BeFalse();
        snapshot.SourceExports.Should().ContainSingle();
        snapshot.VerifiedExports.Should().BeEmpty();
        snapshot.Failures.Should().Contain(failure =>
            failure.Kind == NativeInteropSnapshotFailureKind.TranslationUnitMissing);
        snapshot.Failures.Should().Contain(failure =>
            failure.Kind == NativeInteropSnapshotFailureKind.BinaryVerificationIncomplete);
    }

    [Theory]
    [InlineData(
        BinaryExportVerificationStatus.Invalid,
        (int)NativeInteropSnapshotFailureKind.BinaryVerificationInvalid)]
    [InlineData(
        BinaryExportVerificationStatus.TargetMismatch,
        (int)NativeInteropSnapshotFailureKind.BinaryTargetMismatch)]
    public async Task Invalid_or_target_mismatched_binary_never_proves_exports(
        BinaryExportVerificationStatus status,
        int expectedFailure)
    {
        Write("native/api.cpp");
        var builder = new NativeInteropSnapshotBuilder(
            (request, _) => Task.FromResult(Extraction(
                request,
                [Export(request, "c:E:native/api.cpp::medical", "medical")])),
            (_, _, _) => Task.FromResult(new BinaryExportVerificationResult(
                status,
                status == BinaryExportVerificationStatus.TargetMismatch
                    ? InteropArchitecture.X86
                    : null,
                null,
                null,
                [],
                "binary is not authoritative")));

        var snapshot = await builder.BuildAsync(
            _root,
            Config(Tu(
                "native/api.cpp",
                "medical",
                "artifacts/medical.dll")),
            new ScopePathPolicy(_root),
            CancellationToken.None);

        snapshot.IsComplete.Should().BeFalse();
        snapshot.IsExportUniverseComplete.Should().BeFalse();
        snapshot.VerifiedExports.Should().BeEmpty();
        snapshot.Failures.Should().ContainSingle(failure =>
            (int)failure.Kind == expectedFailure);
    }

    [Fact]
    public async Task Extraction_error_discards_partial_facts_and_skips_binary_verification()
    {
        var sourcePath = Write("native/api.cpp");
        var verifierCalled = false;
        var builder = new NativeInteropSnapshotBuilder(
            (request, _) => Task.FromResult(Extraction(
                request,
                [Export(request, "c:E:native/api.cpp::medical", "medical")],
                diagnostics:
                [
                    new ClangExtractionDiagnostic(
                        "CLANG1000",
                        ClangExtractionDiagnosticSeverity.Error,
                        "parse error",
                        new SourceLocation(sourcePath, 1, 1, 1, 2))
                ])),
            (_, _, _) =>
            {
                verifierCalled = true;
                return Task.FromResult(CompleteBinary("medical.dll", "medical"));
            });

        var snapshot = await builder.BuildAsync(
            _root,
            Config(Tu(
                "native/api.cpp",
                "medical",
                "artifacts/medical.dll")),
            new ScopePathPolicy(_root),
            CancellationToken.None);

        verifierCalled.Should().BeFalse();
        snapshot.IsSourceComplete.Should().BeFalse();
        snapshot.SourceExports.Should().BeEmpty();
        snapshot.Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Diagnostic.Code == "CLANG1000");
        snapshot.Failures.Should().ContainSingle(failure =>
            failure.Kind == NativeInteropSnapshotFailureKind.ExtractionDiagnostics);
    }

    [Fact]
    public async Task Conflicting_payloads_for_one_canonical_key_are_not_arbitrarily_selected()
    {
        var firstPath = Write("native/one.cpp");
        var secondPath = Write("native/two.cpp");
        var sharedPath = Write("include/shared.h");
        var config = Config(
            Tu("native/one.cpp", "medical", "artifacts/one.dll"),
            Tu("native/two.cpp", "medical", "artifacts/two.dll"));
        var builder = new NativeInteropSnapshotBuilder(
            (request, _) =>
            {
                var isFirst = string.Equals(
                    request.SourceFilePath,
                    firstPath,
                    PathComparison);
                var export = Export(
                    request,
                    "c:E:include/shared.h::medical",
                    "medical",
                    sharedPath,
                    isFirst ? 4 : 8);
                return Task.FromResult(Extraction(
                    request,
                    [export],
                    includedFiles: [request.SourceFilePath, sharedPath]));
            },
            (_, _, _) => Task.FromResult(CompleteBinary("medical.dll", "medical")));

        var snapshot = await builder.BuildAsync(
            _root,
            config,
            new ScopePathPolicy(_root),
            CancellationToken.None);

        snapshot.IsComplete.Should().BeFalse();
        snapshot.SourceExports.Should().BeEmpty();
        snapshot.VerifiedExports.Should().BeEmpty();
        snapshot.Contributions.Should().OnlyContain(contribution =>
            contribution.SourceExports.Count == 1);
        snapshot.Failures.Should().Contain(failure =>
            failure.Kind == NativeInteropSnapshotFailureKind.ExportConflict
            && failure.CanonicalKey == "c:E:include/shared.h::medical");
        File.Exists(secondPath).Should().BeTrue();
    }

    [Fact]
    public async Task Matching_export_declaration_and_definition_prefer_definition_evidence()
    {
        var definitionPath = Write("native/api.cpp");
        var consumerPath = Write("native/consumer.cpp");
        var headerPath = Write("include/api.h");
        var config = Config(
            Tu("native/api.cpp", "medical", "artifacts/medical.dll"),
            Tu("native/consumer.cpp", "medical", "artifacts/medical.dll"));
        var builder = new NativeInteropSnapshotBuilder(
            (request, _) =>
            {
                var evidencePath = string.Equals(
                    request.SourceFilePath,
                    definitionPath,
                    PathComparison)
                    ? definitionPath
                    : headerPath;
                var canonicalKey = string.Equals(
                    evidencePath,
                    definitionPath,
                    PathComparison)
                    ? "c:E:native/api.cpp::medical"
                    : "c:E:include/api.h::medical";
                return Task.FromResult(Extraction(
                    request,
                    [
                        Export(
                            request,
                            canonicalKey,
                            "medical",
                            evidencePath)
                    ],
                    includedFiles:
                    [
                        request.SourceFilePath,
                        headerPath,
                    ]));
            },
            (_, _, _) => Task.FromResult(
                CompleteBinary("medical.dll", "medical")));

        var snapshot = await builder.BuildAsync(
            _root,
            config,
            new ScopePathPolicy(_root),
            CancellationToken.None);

        File.Exists(consumerPath).Should().BeTrue();
        snapshot.SourceExports.Should().ContainSingle()
            .Which.Evidence.Location.FilePath.Should().Be(definitionPath);
        snapshot.Failures.Should().NotContain(failure =>
            failure.Kind == NativeInteropSnapshotFailureKind.ExportConflict);
    }

    [Fact]
    public async Task Identical_header_fact_deduplicates_and_fans_out_to_every_owner()
    {
        var firstPath = Write("native/one.cpp");
        var secondPath = Write("native/two.cpp");
        var sharedPath = Write("include/shared.h");
        var config = Config(
            Tu("native/one.cpp", "medical", "artifacts/medical.dll"),
            Tu("native/two.cpp", "medical", "artifacts/medical.dll"));
        var builder = new NativeInteropSnapshotBuilder(
            (request, _) => Task.FromResult(Extraction(
                request,
                [
                    Export(
                        request,
                        "c:E:include/shared.h::medical",
                        "medical",
                        sharedPath)
                ],
                includedFiles: [request.SourceFilePath, sharedPath])),
            (_, _, _) => Task.FromResult(CompleteBinary("medical.dll", "medical")));

        var snapshot = await builder.BuildAsync(
            _root,
            config,
            new ScopePathPolicy(_root),
            CancellationToken.None);

        snapshot.IsComplete.Should().BeTrue();
        snapshot.SourceExports.Should().ContainSingle();
        snapshot.VerifiedExports.Should().ContainSingle();
        snapshot.IncludedFiles.Should().Equal(
            new[] { firstPath, secondPath, sharedPath }
                .OrderBy(path => path, PathComparer)
                .ThenBy(path => path, StringComparer.Ordinal));
        snapshot.DependencyFanout[sharedPath].Should().Equal(
            new[] { firstPath, secondPath }
                .OrderBy(path => path, PathComparer)
                .ThenBy(path => path, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("export")]
    [InlineData("parameter")]
    [InlineData("retention")]
    [InlineData("exception")]
    [InlineData("allocation")]
    [InlineData("record")]
    [InlineData("field")]
    public async Task Unreported_nested_fact_locations_are_rejected(
        string locationKind)
    {
        var sourcePath = Write("native/api.cpp");
        var unreportedPath = Write("include/unreported.h");
        var verifierCalled = false;
        var builder = new NativeInteropSnapshotBuilder(
            (request, _) =>
            {
                var sourceEvidence = TestEvidence(request, sourcePath);
                var unreportedEvidence = TestEvidence(request, unreportedPath);
                var export = Export(
                    request,
                    "c:E:native/api.cpp::medical",
                    "medical");
                var record = Record(
                    request,
                    "c:T:native/api.cpp::Payload",
                    sourceEvidence);
                switch (locationKind)
                {
                    case "export":
                        export = export with { Evidence = unreportedEvidence };
                        break;
                    case "parameter":
                        export = export with
                        {
                            Parameters =
                            [
                                new AbiParameter(
                                    0,
                                    "value",
                                    IntType(),
                                    AbiParameterDirection.In,
                                    TestLocation(unreportedPath)),
                            ],
                        };
                        break;
                    case "retention":
                        export = export with
                        {
                            RetainedCallbacks =
                            [
                                new NativeCallbackRetention(
                                    0,
                                    request.Target,
                                    unreportedEvidence),
                            ],
                        };
                        break;
                    case "exception":
                        export = export with
                        {
                            ExceptionEscape = new NativeExceptionEscape(
                                request.Target,
                                unreportedEvidence),
                        };
                        break;
                    case "allocation":
                        export = export with
                        {
                            ReturnAllocation = new NativeReturnAllocation(
                                InteropAllocatorFamily.CrtHeap,
                                request.Target,
                                unreportedEvidence),
                        };
                        break;
                    case "record":
                        record = record with { Evidence = unreportedEvidence };
                        break;
                    case "field":
                        record = record with
                        {
                            Fields =
                            [
                                record.Fields[0] with
                                {
                                    Evidence = unreportedEvidence,
                                },
                            ],
                        };
                        break;
                }
                return Task.FromResult(Extraction(
                    request,
                    locationKind is "record" or "field" ? [] : [export],
                    locationKind is "record" or "field" ? [record] : []));
            },
            (_, _, _) =>
            {
                verifierCalled = true;
                return Task.FromResult(CompleteBinary("medical.dll", "medical"));
            });

        var snapshot = await builder.BuildAsync(
            _root,
            Config(Tu("native/api.cpp", "medical", "artifacts/medical.dll")),
            new ScopePathPolicy(_root),
            CancellationToken.None);

        verifierCalled.Should().BeFalse();
        snapshot.IsComplete.Should().BeFalse();
        snapshot.IsSourceComplete.Should().BeFalse();
        snapshot.SourceExports.Should().BeEmpty();
        snapshot.RecordLayouts.Should().BeEmpty();
        snapshot.Failures.Should().ContainSingle(failure =>
            failure.Kind == NativeInteropSnapshotFailureKind.FactLocationRejected);
    }

    [Fact]
    public async Task Exact_native_risk_facts_survive_double_parse_and_aggregation()
    {
        Write("native/api.cpp");
        var builder = new NativeInteropSnapshotBuilder(
            (request, _) => Task.FromResult(Extraction(
                request,
                exports: [RiskExport(request)])));

        var snapshot = await builder.BuildAsync(
            _root,
            Config(new InteropTranslationUnitConfig(
                "native/api.cpp",
                "native.dll",
                ["-x", "c++"],
                BinaryPath: null)),
            new ScopePathPolicy(_root),
            CancellationToken.None);

        snapshot.IsComplete.Should().BeTrue();
        var export = snapshot.SourceExports.Should()
            .ContainSingle()
            .Subject;
        export.RetainedCallbacks.Should().ContainSingle(retention =>
            retention.ParameterPosition == 0
            && retention.Target.IsAbiEquivalentTo(export.Target));
        export.ExceptionEscape.Should().NotBeNull();
        export.ExceptionEscape!.Target.IsAbiEquivalentTo(export.Target)
            .Should().BeTrue();
        export.ReturnAllocation.Should().Match<NativeReturnAllocation>(
            allocation =>
                allocation.AllocatorFamily
                    == InteropAllocatorFamily.CrtHeap
                && allocation.Target.IsAbiEquivalentTo(export.Target));
    }

    [Theory]
    [InlineData("position")]
    [InlineData("parameter-kind")]
    [InlineData("target")]
    [InlineData("allocator")]
    [InlineData("duplicate-parameter-position")]
    [InlineData("null-parameter-type")]
    public async Task Malformed_native_risk_fact_is_rejected(
        string malformedKind)
    {
        Write("native/api.cpp");
        var builder = new NativeInteropSnapshotBuilder(
            (request, _) =>
            {
                var export = RiskExport(request);
                export = malformedKind switch
                {
                    "position" => export with
                    {
                        RetainedCallbacks =
                        [
                            export.RetainedCallbacks[0] with
                            {
                                ParameterPosition = 1,
                            },
                        ],
                    },
                    "parameter-kind" => export with
                    {
                        Parameters =
                        [
                            export.Parameters[0] with
                            {
                                Type = IntType(),
                            },
                        ],
                    },
                    "target" => export with
                    {
                        ExceptionEscape =
                            export.ExceptionEscape! with
                            {
                                Target = InteropTarget.WindowsX86Msvc,
                            },
                    },
                    "allocator" => export with
                    {
                        ReturnAllocation =
                            export.ReturnAllocation! with
                            {
                                AllocatorFamily =
                                    InteropAllocatorFamily.Unknown,
                            },
                    },
                    "duplicate-parameter-position" => export with
                    {
                        Parameters =
                        [
                            export.Parameters[0],
                            new AbiParameter(
                                0,
                                "value",
                                IntType(),
                                AbiParameterDirection.In,
                                TestLocation(request.SourceFilePath)),
                        ],
                    },
                    "null-parameter-type" => export with
                    {
                        Parameters =
                        [
                            export.Parameters[0] with
                            {
                                Type = null!,
                            },
                        ],
                    },
                    _ => throw new InvalidOperationException(),
                };
                return Task.FromResult(Extraction(
                    request,
                    exports: [export]));
            });

        var snapshot = await builder.BuildAsync(
            _root,
            Config(new InteropTranslationUnitConfig(
                "native/api.cpp",
                "native.dll",
                ["-x", "c++"],
                BinaryPath: null)),
            new ScopePathPolicy(_root),
            CancellationToken.None);

        snapshot.IsComplete.Should().BeFalse();
        snapshot.SourceExports.Should().BeEmpty();
        snapshot.Failures.Should().ContainSingle(failure =>
            failure.Kind == NativeInteropSnapshotFailureKind.InvalidFact);
    }

    [Theory]
    [InlineData("non-positive")]
    [InlineData("reversed")]
    public async Task Invalid_native_fact_coordinates_are_rejected(
        string malformedKind)
    {
        Write("native/api.cpp");
        var builder = new NativeInteropSnapshotBuilder(
            (request, _) =>
            {
                var export = RiskExport(request);
                var location = malformedKind switch
                {
                    "non-positive" => new SourceLocation(
                        request.SourceFilePath,
                        0,
                        1,
                        1,
                        2),
                    "reversed" => new SourceLocation(
                        request.SourceFilePath,
                        2,
                        8,
                        2,
                        4),
                    _ => throw new InvalidOperationException(),
                };
                export = export with
                {
                    Parameters =
                    [
                        export.Parameters[0] with
                        {
                            Location = location,
                        },
                    ],
                };
                return Task.FromResult(Extraction(
                    request,
                    exports: [export]));
            });

        var snapshot = await builder.BuildAsync(
            _root,
            Config(new InteropTranslationUnitConfig(
                "native/api.cpp",
                "native.dll",
                ["-x", "c++"],
                BinaryPath: null)),
            new ScopePathPolicy(_root),
            CancellationToken.None);

        snapshot.IsComplete.Should().BeFalse();
        snapshot.SourceExports.Should().BeEmpty();
        snapshot.Failures.Should().ContainSingle(failure =>
            failure.Kind
                == NativeInteropSnapshotFailureKind.FactLocationRejected);
    }

    [Fact]
    public async Task Changed_native_risk_fact_between_double_parse_is_rejected()
    {
        Write("native/api.cpp");
        var extractionCount = 0;
        var builder = new NativeInteropSnapshotBuilder(
            (request, _) =>
            {
                extractionCount++;
                var export = RiskExport(request);
                if (extractionCount == 2)
                {
                    export = export with { RetainedCallbacks = [] };
                }
                return Task.FromResult(Extraction(
                    request,
                    exports: [export]));
            });

        var snapshot = await builder.BuildAsync(
            _root,
            Config(new InteropTranslationUnitConfig(
                "native/api.cpp",
                "native.dll",
                ["-x", "c++"],
                BinaryPath: null)),
            new ScopePathPolicy(_root),
            CancellationToken.None);

        snapshot.IsComplete.Should().BeFalse();
        snapshot.Failures.Should().ContainSingle(failure =>
            failure.Kind == NativeInteropSnapshotFailureKind.FactSetChanged);
    }

    [Fact]
    public async Task Approved_location_is_normalized_to_its_physical_path()
    {
        var sourcePath = Write("native/api.cpp");
        var nonNormalizedPath = Path.Join(
            _root,
            "native",
            "..",
            "native",
            "api.cpp");
        var builder = new NativeInteropSnapshotBuilder(
            (request, _) => Task.FromResult(Extraction(
                request,
                [
                    Export(
                        request,
                        "c:E:native/api.cpp::medical",
                        "medical",
                        nonNormalizedPath),
                ])),
            (_, _, _) => Task.FromResult(CompleteBinary("medical.dll", "medical")));

        var snapshot = await builder.BuildAsync(
            _root,
            Config(Tu("native/api.cpp", "medical", "artifacts/medical.dll")),
            new ScopePathPolicy(_root),
            CancellationToken.None);

        snapshot.IsComplete.Should().BeTrue();
        snapshot.SourceExports.Should().ContainSingle();
        snapshot.SourceExports[0].Evidence.Location.FilePath.Should().Be(sourcePath);
    }

    [Fact]
    public async Task Physically_escaped_fact_location_is_rejected()
    {
        Write("native/api.cpp");
        var escapedPath = Path.GetTempFileName();
        try
        {
            var builder = new NativeInteropSnapshotBuilder(
                (request, _) => Task.FromResult(Extraction(
                    request,
                    [
                        Export(
                            request,
                            "c:E:native/api.cpp::medical",
                            "medical",
                            escapedPath),
                    ])),
                (_, _, _) => Task.FromResult(
                    CompleteBinary("medical.dll", "medical")));

            var snapshot = await builder.BuildAsync(
                _root,
                Config(Tu(
                    "native/api.cpp",
                    "medical",
                    "artifacts/medical.dll")),
                new ScopePathPolicy(_root),
                CancellationToken.None);

            snapshot.IsComplete.Should().BeFalse();
            snapshot.SourceExports.Should().BeEmpty();
            snapshot.Failures.Should().ContainSingle(failure =>
                failure.Kind
                    == NativeInteropSnapshotFailureKind.FactLocationRejected);
        }
        finally
        {
            File.Delete(escapedPath);
        }
    }

    [Fact]
    public async Task Complete_snapshot_carries_stable_source_and_header_hashes()
    {
        var sourcePath = Write("native/api.cpp", "// source");
        var headerPath = Write("include/api.h", "// header");
        var extractorCalls = 0;
        var builder = new NativeInteropSnapshotBuilder(
            (request, _) =>
            {
                extractorCalls++;
                return Task.FromResult(Extraction(
                    request,
                    [
                        Export(
                            request,
                            "c:E:include/api.h::medical",
                            "medical",
                            headerPath),
                    ],
                    includedFiles: [request.SourceFilePath, headerPath]));
            },
            (_, _, _) => Task.FromResult(
                CompleteBinary("medical.dll", "medical")));

        var snapshot = await builder.BuildAsync(
            _root,
            Config(Tu("native/api.cpp", "medical", "artifacts/medical.dll")),
            new ScopePathPolicy(_root),
            CancellationToken.None);

        extractorCalls.Should().Be(2);
        snapshot.IsComplete.Should().BeTrue();
        snapshot.ContentHashes.Keys.Should().BeEquivalentTo(
            sourcePath,
            headerPath);
        snapshot.ContentHashes[sourcePath].Sha256.Should().Equal(
            SHA256.HashData(File.ReadAllBytes(sourcePath)));
        snapshot.ContentHashes[headerPath].Sha256.Should().Equal(
            SHA256.HashData(File.ReadAllBytes(headerPath)));
        snapshot.Contributions.Should().ContainSingle();
        snapshot.Contributions[0].ContentHashes.Should().HaveCount(2);
    }

    [Fact]
    public async Task Translation_unit_change_during_discovery_is_partial()
    {
        Write("native/api.cpp", "// original");
        var extractorCalls = 0;
        var verifierCalled = false;
        var builder = new NativeInteropSnapshotBuilder(
            (request, _) =>
            {
                extractorCalls++;
                var result = Extraction(
                    request,
                    [
                        Export(
                            request,
                            "c:E:native/api.cpp::medical",
                            "medical"),
                    ]);
                File.AppendAllText(request.SourceFilePath, "// changed");
                return Task.FromResult(result);
            },
            (_, _, _) =>
            {
                verifierCalled = true;
                return Task.FromResult(CompleteBinary("medical.dll", "medical"));
            });

        var snapshot = await builder.BuildAsync(
            _root,
            Config(Tu("native/api.cpp", "medical", "artifacts/medical.dll")),
            new ScopePathPolicy(_root),
            CancellationToken.None);

        extractorCalls.Should().Be(1);
        verifierCalled.Should().BeFalse();
        snapshot.IsSourceComplete.Should().BeFalse();
        snapshot.SourceExports.Should().BeEmpty();
        snapshot.ContentHashes.Should().BeEmpty();
        snapshot.Failures.Should().ContainSingle(failure =>
            failure.Kind == NativeInteropSnapshotFailureKind.InputContentChanged);
    }

    [Fact]
    public async Task Header_change_during_reparse_is_partial()
    {
        Write("native/api.cpp");
        var headerPath = Write("include/api.h", "// original");
        var extractorCalls = 0;
        var verifierCalled = false;
        var builder = new NativeInteropSnapshotBuilder(
            (request, _) =>
            {
                extractorCalls++;
                var result = Extraction(
                    request,
                    [
                        Export(
                            request,
                            "c:E:include/api.h::medical",
                            "medical",
                            headerPath),
                    ],
                    includedFiles: [request.SourceFilePath, headerPath]);
                if (extractorCalls == 2)
                {
                    File.AppendAllText(headerPath, "// changed");
                }
                return Task.FromResult(result);
            },
            (_, _, _) =>
            {
                verifierCalled = true;
                return Task.FromResult(CompleteBinary("medical.dll", "medical"));
            });

        var snapshot = await builder.BuildAsync(
            _root,
            Config(Tu("native/api.cpp", "medical", "artifacts/medical.dll")),
            new ScopePathPolicy(_root),
            CancellationToken.None);

        extractorCalls.Should().Be(2);
        verifierCalled.Should().BeFalse();
        snapshot.IsSourceComplete.Should().BeFalse();
        snapshot.SourceExports.Should().BeEmpty();
        snapshot.ContentHashes.Should().BeEmpty();
        snapshot.Failures.Should().ContainSingle(failure =>
            failure.Kind == NativeInteropSnapshotFailureKind.InputContentChanged);
    }

    [Fact]
    public async Task Included_file_set_change_between_parses_is_partial()
    {
        Write("native/api.cpp");
        var headerPath = Write("include/api.h");
        var extractorCalls = 0;
        var verifierCalled = false;
        var builder = new NativeInteropSnapshotBuilder(
            (request, _) =>
            {
                extractorCalls++;
                return Task.FromResult(Extraction(
                    request,
                    includedFiles: extractorCalls == 1
                        ? [request.SourceFilePath, headerPath]
                        : [request.SourceFilePath]));
            },
            (_, _, _) =>
            {
                verifierCalled = true;
                return Task.FromResult(CompleteBinary("medical.dll"));
            });

        var snapshot = await builder.BuildAsync(
            _root,
            Config(Tu("native/api.cpp", "medical", "artifacts/medical.dll")),
            new ScopePathPolicy(_root),
            CancellationToken.None);

        extractorCalls.Should().Be(2);
        verifierCalled.Should().BeFalse();
        snapshot.IsSourceComplete.Should().BeFalse();
        snapshot.ContentHashes.Should().BeEmpty();
        snapshot.Failures.Should().ContainSingle(failure =>
            failure.Kind == NativeInteropSnapshotFailureKind.DependencySetChanged);
    }

    [Fact]
    public async Task Input_change_during_binary_verification_is_partial()
    {
        var sourcePath = Write("native/api.cpp", "// original");
        var builder = new NativeInteropSnapshotBuilder(
            (request, _) => Task.FromResult(Extraction(
                request,
                [
                    Export(
                        request,
                        "c:E:native/api.cpp::medical",
                        "medical"),
                ])),
            (_, _, _) =>
            {
                File.AppendAllText(sourcePath, "// changed");
                return Task.FromResult(CompleteBinary("medical.dll", "medical"));
            });

        var snapshot = await builder.BuildAsync(
            _root,
            Config(Tu("native/api.cpp", "medical", "artifacts/medical.dll")),
            new ScopePathPolicy(_root),
            CancellationToken.None);

        snapshot.IsSourceComplete.Should().BeFalse();
        snapshot.SourceExports.Should().BeEmpty();
        snapshot.VerifiedExports.Should().BeEmpty();
        snapshot.ContentHashes.Should().BeEmpty();
        snapshot.Failures.Should().ContainSingle(failure =>
            failure.Kind == NativeInteropSnapshotFailureKind.InputContentChanged);
    }

    [Fact]
    public async Task Cross_translation_unit_hash_conflict_is_partial()
    {
        var firstPath = Write("native/one.cpp");
        var secondPath = Write("native/two.cpp");
        var headerPath = Write("include/shared.h", "// version one");
        var changedForSecondUnit = false;
        var builder = new NativeInteropSnapshotBuilder(
            (request, _) =>
            {
                if (!changedForSecondUnit
                    && string.Equals(
                        request.SourceFilePath,
                        secondPath,
                        PathComparison))
                {
                    File.AppendAllText(headerPath, "// version two");
                    changedForSecondUnit = true;
                }
                return Task.FromResult(Extraction(
                    request,
                    [
                        Export(
                            request,
                            "c:E:include/shared.h::medical",
                            "medical",
                            headerPath),
                    ],
                    includedFiles: [request.SourceFilePath, headerPath]));
            },
            (_, _, _) => Task.FromResult(
                CompleteBinary("medical.dll", "medical")));

        var snapshot = await builder.BuildAsync(
            _root,
            Config(
                Tu("native/one.cpp", "medical", "artifacts/medical.dll"),
                Tu("native/two.cpp", "medical", "artifacts/medical.dll")),
            new ScopePathPolicy(_root),
            CancellationToken.None);

        File.Exists(firstPath).Should().BeTrue();
        snapshot.Contributions.Should().HaveCount(2);
        snapshot.Contributions.Should().OnlyContain(
            contribution => contribution.IsComplete);
        snapshot.IsSourceComplete.Should().BeFalse();
        snapshot.IsComplete.Should().BeFalse();
        snapshot.ContentHashes.Should().ContainKey(firstPath);
        snapshot.ContentHashes.Should().ContainKey(secondPath);
        snapshot.ContentHashes.Should().NotContainKey(headerPath);
        snapshot.Failures.Should().ContainSingle(failure =>
            failure.Kind == NativeInteropSnapshotFailureKind.InputContentChanged);
    }

    [Fact]
    public async Task Oversized_translation_unit_is_rejected_before_extraction()
    {
        var sourcePath = Write("native/api.cpp");
        using (var stream = new FileStream(
                   sourcePath,
                   FileMode.Open,
                   FileAccess.Write,
                   FileShare.Read))
        {
            stream.SetLength(
                NativeInteropSnapshotBuilder.MaximumHashedFileBytes + 1);
        }
        var extractorCalled = false;
        var builder = new NativeInteropSnapshotBuilder(
            (_, _) =>
            {
                extractorCalled = true;
                throw new InvalidOperationException("must not extract");
            });

        var snapshot = await builder.BuildAsync(
            _root,
            Config(Tu("native/api.cpp", "medical", "artifacts/medical.dll")),
            new ScopePathPolicy(_root),
            CancellationToken.None);

        extractorCalled.Should().BeFalse();
        snapshot.IsSourceComplete.Should().BeFalse();
        snapshot.ContentHashes.Should().BeEmpty();
        snapshot.Failures.Should().ContainSingle(failure =>
            failure.Kind
                == NativeInteropSnapshotFailureKind.ContentHashLimitExceeded);
    }

    [Fact]
    public async Task Translation_unit_count_over_limit_fails_before_extraction()
    {
        var extractorCalled = false;
        var units = Enumerable
            .Repeat(
                Tu("native/not-read.cpp", "medical", "artifacts/medical.dll"),
                NativeInteropSnapshotBuilder.MaximumTranslationUnits + 1)
            .ToArray();
        var builder = new NativeInteropSnapshotBuilder(
            (_, _) =>
            {
                extractorCalled = true;
                throw new InvalidOperationException("must not extract");
            });

        var snapshot = await builder.BuildAsync(
            _root,
            Config(units),
            new ScopePathPolicy(_root),
            CancellationToken.None);

        extractorCalled.Should().BeFalse();
        snapshot.IsComplete.Should().BeFalse();
        snapshot.Failures.Should().ContainSingle(failure =>
            failure.Kind
                == NativeInteropSnapshotFailureKind.CollectionLimitExceeded);
    }

    [Theory]
    [InlineData("symbols")]
    [InlineData("calls")]
    public async Task Cumulative_projection_limit_stops_before_aggregate(
        string collectionKind)
    {
        Write("native/api.cpp");
        var extractionCalls = 0;
        var factsPerContribution = collectionKind == "symbols"
            ? NativeInteropSnapshotBuilder.MaximumFunctionsPerTranslationUnit
            : NativeInteropSnapshotBuilder.MaximumCallsPerTranslationUnit;
        var snapshotLimit = collectionKind == "symbols"
            ? NativeInteropSnapshotBuilder.MaximumSymbolsPerSnapshot
            : NativeInteropSnapshotBuilder.MaximumCallsPerSnapshot;
        var attemptedContributions = snapshotLimit / factsPerContribution + 1;
        var unit = new InteropTranslationUnitConfig(
            "native/api.cpp",
            "native.dll",
            ["-x", "c++"],
            BinaryPath: null);
        var units = Enumerable.Repeat(
                unit,
                attemptedContributions + 1)
            .ToArray();
        var builder = new NativeInteropSnapshotBuilder(
            (request, _) =>
            {
                extractionCalls++;
                if (collectionKind == "symbols")
                {
                    var functions = Enumerable.Range(
                            0,
                            NativeInteropSnapshotBuilder
                                .MaximumFunctionsPerTranslationUnit)
                        .Select(value => Function(
                            request,
                            $"cpp:F:native/api.cpp::Native::f{value}()",
                            $"usr:f{value}",
                            $"f{value}",
                            $"Native::f{value}",
                            graphKey: null,
                            isMethod: true))
                        .ToArray();
                    return Task.FromResult(Extraction(
                        request,
                        functions: functions));
                }

                const string callerKey =
                    "cpp:F:native/api.cpp::Native::run()";
                const string callerUsr = "usr:run";
                var caller = Function(
                    request,
                    callerKey,
                    callerUsr,
                    "run",
                    "Native::run",
                    graphKey: null,
                    isMethod: true);
                var calls = Enumerable.Range(
                        0,
                        NativeInteropSnapshotBuilder
                            .MaximumCallsPerTranslationUnit)
                    .Select(value => DirectCall(
                        request,
                        callerKey,
                        callerUsr,
                        callerKey,
                        value + 1))
                    .ToArray();
                return Task.FromResult(Extraction(
                    request,
                    functions: [caller],
                    calls: calls));
            });

        var snapshot = await builder.BuildAsync(
            _root,
            Config(units),
            new ScopePathPolicy(_root),
            CancellationToken.None);

        extractionCalls.Should().Be(
            attemptedContributions * 2,
            "the double parse must stop at the first cumulative overflow");
        snapshot.Contributions.Should().BeEmpty();
        snapshot.Functions.Should().BeEmpty();
        snapshot.Calls.Should().BeEmpty();
        snapshot.IsComplete.Should().BeFalse();
        snapshot.Failures.Should().ContainSingle(failure =>
            failure.Kind
                == NativeInteropSnapshotFailureKind.CollectionLimitExceeded
            && failure.TranslationUnitIndex
                == attemptedContributions - 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Snapshot_nested_fact_budget_accepts_boundary_and_rejects_overflow(
        bool exceedLimit)
    {
        Write("native/api.cpp");
        const int exactFactCount = 15;
        var builder = new NativeInteropSnapshotBuilder(
            (request, _) =>
            {
                var caller = Function(
                    request,
                    "cpp:F:native/api.cpp::caller()",
                    "usr:caller",
                    "caller",
                    "caller",
                    graphKey: null,
                    isMethod: false);
                var callee = Function(
                    request,
                    "cpp:F:native/api.cpp::callee()",
                    "usr:callee",
                    "callee",
                    "callee",
                    graphKey: null,
                    isMethod: false);
                var functions = exceedLimit
                    ? new[]
                    {
                        caller,
                        callee,
                        Function(
                            request,
                            "cpp:F:native/api.cpp::extra()",
                            "usr:extra",
                            "extra",
                            "extra",
                            graphKey: null,
                            isMethod: false),
                    }
                    : [caller, callee];
                return Task.FromResult(Extraction(
                    request,
                    exports: [RiskExport(request)],
                    records:
                    [
                        Record(
                            request,
                            "cpp:T:native/api.cpp::Payload",
                            TestEvidence(
                                request,
                                request.SourceFilePath)),
                    ],
                    functions: functions,
                    calls:
                    [
                        DirectCall(
                            request,
                            caller.GraphCanonicalKey,
                            callee.DeclarationUsr,
                            callee.GraphCanonicalKey,
                            line: 2),
                    ]));
            },
            (_, _, _) => Task.FromResult(
                CompleteBinary("medical.dll", "risk")),
            maximumNestedFactsPerSnapshot: exactFactCount);

        var snapshot = await builder.BuildAsync(
            _root,
            Config(Tu(
                "native/api.cpp",
                "medical",
                "artifacts/medical.dll")),
            new ScopePathPolicy(_root),
            CancellationToken.None);

        if (!exceedLimit)
        {
            snapshot.IsComplete.Should().BeTrue();
            snapshot.Contributions.Should().ContainSingle();
            snapshot.Failures.Should().BeEmpty();
            return;
        }

        snapshot.IsComplete.Should().BeFalse();
        snapshot.IsSourceComplete.Should().BeFalse();
        snapshot.Contributions.Should().BeEmpty();
        snapshot.SourceExports.Should().BeEmpty();
        snapshot.VerifiedExports.Should().BeEmpty();
        snapshot.RecordLayouts.Should().BeEmpty();
        snapshot.Functions.Should().BeEmpty();
        snapshot.Calls.Should().BeEmpty();
        snapshot.Failures.Should().ContainSingle(failure =>
            failure.Kind
                == NativeInteropSnapshotFailureKind.CollectionLimitExceeded
            && failure.TranslationUnitIndex == 0
            && failure.Message.Contains(
                exactFactCount.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cumulative_nested_fact_budget_stops_on_second_translation_unit()
    {
        Write("native/first.cpp");
        Write("native/second.cpp");
        var extractionCalls = 0;
        var builder = new NativeInteropSnapshotBuilder(
            (request, _) =>
            {
                extractionCalls++;
                return Task.FromResult(Extraction(
                    request,
                    exports: [RiskExport(request)]));
            },
            binaryVerifier: null,
            maximumNestedFactsPerSnapshot: 9);

        var snapshot = await builder.BuildAsync(
            _root,
            Config(
                new InteropTranslationUnitConfig(
                    "native/first.cpp",
                    "medical.dll",
                    ["-x", "c++"],
                    BinaryPath: null),
                new InteropTranslationUnitConfig(
                    "native/second.cpp",
                    "medical.dll",
                    ["-x", "c++"],
                    BinaryPath: null)),
            new ScopePathPolicy(_root),
            CancellationToken.None);

        extractionCalls.Should().Be(
            4,
            "each translation unit is double-parsed before the cumulative budget is evaluated");
        snapshot.IsComplete.Should().BeFalse();
        snapshot.IsSourceComplete.Should().BeFalse();
        snapshot.Contributions.Should().BeEmpty();
        snapshot.SourceExports.Should().BeEmpty();
        snapshot.Failures.Should().ContainSingle(failure =>
            failure.Kind
                == NativeInteropSnapshotFailureKind.CollectionLimitExceeded
            && failure.TranslationUnitIndex == 1);
    }

    [Theory]
    [InlineData("included-files")]
    [InlineData("exports")]
    [InlineData("records")]
    [InlineData("types")]
    [InlineData("diagnostics")]
    public async Task Top_level_extraction_collection_over_limit_is_rejected(
        string collectionKind)
    {
        Write("native/api.cpp");
        var verifierCalled = false;
        var builder = new NativeInteropSnapshotBuilder(
            (request, _) =>
            {
                var export = Export(
                    request,
                    "c:E:native/api.cpp::medical",
                    "medical");
                var record = Record(
                    request,
                    "c:T:native/api.cpp::Payload",
                    TestEvidence(request, request.SourceFilePath));
                var type = Type(
                    request,
                    "cpp:T:native/api.cpp::Payload",
                    NativeTypeDeclarationKind.Struct,
                    "Payload");
                return Task.FromResult(collectionKind switch
                {
                    "included-files" => Extraction(
                        request,
                        includedFiles: Enumerable.Repeat(
                                request.SourceFilePath,
                                NativeInteropSnapshotBuilder
                                    .MaximumIncludedFilesPerTranslationUnit + 1)
                            .ToArray()),
                    "exports" => Extraction(
                        request,
                        Enumerable.Repeat(
                                export,
                                NativeInteropSnapshotBuilder
                                    .MaximumExportsPerTranslationUnit + 1)
                            .ToArray()),
                    "records" => Extraction(
                        request,
                        records: Enumerable.Repeat(
                                record,
                                NativeInteropSnapshotBuilder
                                    .MaximumRecordLayoutsPerTranslationUnit + 1)
                            .ToArray()),
                    "types" => Extraction(
                        request,
                        types: Enumerable.Repeat(
                                type,
                                NativeInteropSnapshotBuilder
                                    .MaximumTypesPerTranslationUnit + 1)
                            .ToArray()),
                    "diagnostics" => Extraction(
                        request,
                        diagnostics: Enumerable.Repeat(
                                new ClangExtractionDiagnostic(
                                    "CLANG1000",
                                    ClangExtractionDiagnosticSeverity.Warning,
                                    "bounded"),
                                NativeInteropSnapshotBuilder
                                    .MaximumDiagnosticsPerTranslationUnit + 1)
                            .ToArray()),
                    _ => throw new InvalidOperationException(),
                });
            },
            (_, _, _) =>
            {
                verifierCalled = true;
                return Task.FromResult(CompleteBinary("medical.dll", "medical"));
            });

        var snapshot = await builder.BuildAsync(
            _root,
            Config(Tu("native/api.cpp", "medical", "artifacts/medical.dll")),
            new ScopePathPolicy(_root),
            CancellationToken.None);

        verifierCalled.Should().BeFalse();
        snapshot.IsComplete.Should().BeFalse();
        snapshot.IsSourceComplete.Should().BeFalse();
        snapshot.SourceExports.Should().BeEmpty();
        snapshot.RecordLayouts.Should().BeEmpty();
        snapshot.Failures.Should().ContainSingle(failure =>
            failure.Kind
                == NativeInteropSnapshotFailureKind.CollectionLimitExceeded);
    }

    [Theory]
    [InlineData("parameters")]
    [InlineData("retention")]
    [InlineData("fields")]
    [InlineData("metadata")]
    public async Task Nested_fact_collection_over_limit_rejects_the_fact(
        string collectionKind)
    {
        Write("native/api.cpp");
        var verifierCalled = false;
        var builder = new NativeInteropSnapshotBuilder(
            (request, _) =>
            {
                var evidence = TestEvidence(request, request.SourceFilePath);
                var export = Export(
                    request,
                    "c:E:native/api.cpp::medical",
                    "medical");
                var record = Record(
                    request,
                    "c:T:native/api.cpp::Payload",
                    evidence);
                if (collectionKind == "parameters")
                {
                    var parameter = new AbiParameter(
                        0,
                        "value",
                        IntType(),
                        AbiParameterDirection.In,
                        TestLocation(request.SourceFilePath));
                    export = export with
                    {
                        Parameters = Enumerable.Repeat(
                                parameter,
                                NativeInteropSnapshotBuilder
                                    .MaximumParametersPerExport + 1)
                            .ToArray(),
                    };
                }
                else if (collectionKind == "retention")
                {
                    var retention = new NativeCallbackRetention(
                        0,
                        request.Target,
                        evidence);
                    export = export with
                    {
                        RetainedCallbacks = Enumerable.Repeat(
                                retention,
                                NativeInteropSnapshotBuilder
                                    .MaximumRetainedCallbacksPerExport + 1)
                            .ToArray(),
                    };
                }
                else if (collectionKind == "metadata")
                {
                    export = export with
                    {
                        Evidence = evidence with
                        {
                            Metadata = Enumerable.Range(
                                    0,
                                    NativeInteropSnapshotBuilder
                                        .MaximumEvidenceMetadataEntries + 1)
                                .ToDictionary(
                                    value => "key-" + value,
                                    value => "value-" + value,
                                    StringComparer.Ordinal),
                        },
                    };
                }
                else
                {
                    record = record with
                    {
                        Fields = Enumerable.Repeat(
                                record.Fields[0],
                                NativeInteropSnapshotBuilder
                                    .MaximumFieldsPerRecord + 1)
                            .ToArray(),
                    };
                }
                return Task.FromResult(Extraction(
                    request,
                    collectionKind == "fields" ? [] : [export],
                    collectionKind == "fields" ? [record] : []));
            },
            (_, _, _) =>
            {
                verifierCalled = true;
                return Task.FromResult(CompleteBinary("medical.dll", "medical"));
            });

        var snapshot = await builder.BuildAsync(
            _root,
            Config(Tu("native/api.cpp", "medical", "artifacts/medical.dll")),
            new ScopePathPolicy(_root),
            CancellationToken.None);

        verifierCalled.Should().BeFalse();
        snapshot.IsComplete.Should().BeFalse();
        snapshot.SourceExports.Should().BeEmpty();
        snapshot.RecordLayouts.Should().BeEmpty();
        snapshot.Failures.Should().ContainSingle(failure =>
            failure.Kind
                == NativeInteropSnapshotFailureKind.CollectionLimitExceeded);
    }

    [Fact]
    public async Task Aggregate_nested_fact_budget_is_enforced_before_validation()
    {
        Write("native/api.cpp");
        var verifierCalled = false;
        var builder = new NativeInteropSnapshotBuilder(
            (request, _) =>
            {
                var parameter = new AbiParameter(
                    0,
                    "value",
                    IntType(),
                    AbiParameterDirection.In,
                    TestLocation(request.SourceFilePath));
                var export = Export(
                    request,
                    "c:E:native/api.cpp::medical",
                    "medical") with
                {
                    Parameters = Enumerable.Repeat(parameter, 16).ToArray(),
                };
                return Task.FromResult(Extraction(
                    request,
                    Enumerable.Repeat(
                            export,
                            NativeInteropSnapshotBuilder
                                .MaximumExportsPerTranslationUnit)
                        .ToArray()));
            },
            (_, _, _) =>
            {
                verifierCalled = true;
                return Task.FromResult(CompleteBinary("medical.dll", "medical"));
            });

        var snapshot = await builder.BuildAsync(
            _root,
            Config(Tu("native/api.cpp", "medical", "artifacts/medical.dll")),
            new ScopePathPolicy(_root),
            CancellationToken.None);

        verifierCalled.Should().BeFalse();
        snapshot.IsComplete.Should().BeFalse();
        snapshot.SourceExports.Should().BeEmpty();
        snapshot.Failures.Should().ContainSingle(failure =>
            failure.Kind
                == NativeInteropSnapshotFailureKind.CollectionLimitExceeded);
    }

    [Fact]
    public async Task Binary_export_collection_over_limit_is_not_associated()
    {
        Write("native/api.cpp");
        var binaryEntry = new BinaryExportEntry(
            1,
            0x1000,
            ["medical"],
            IsForwarder: false,
            Forwarder: null);
        var builder = new NativeInteropSnapshotBuilder(
            (request, _) => Task.FromResult(Extraction(
                request,
                [
                    Export(
                        request,
                        "c:E:native/api.cpp::medical",
                        "medical"),
                ])),
            (_, _, _) => Task.FromResult(new BinaryExportVerificationResult(
                BinaryExportVerificationStatus.Complete,
                InteropArchitecture.X64,
                0x8664,
                "medical.dll",
                Enumerable.Repeat(
                        binaryEntry,
                        NativeInteropSnapshotBuilder.MaximumBinaryExports + 1)
                    .ToArray(),
                "complete")));

        var snapshot = await builder.BuildAsync(
            _root,
            Config(Tu("native/api.cpp", "medical", "artifacts/medical.dll")),
            new ScopePathPolicy(_root),
            CancellationToken.None);

        snapshot.IsComplete.Should().BeFalse();
        snapshot.IsSourceComplete.Should().BeTrue();
        snapshot.VerifiedExports.Should().BeEmpty();
        snapshot.Failures.Should().ContainSingle(failure =>
            failure.Kind
                == NativeInteropSnapshotFailureKind.BinaryCollectionLimitExceeded);
    }

    [Fact]
    public async Task Cancellation_is_propagated_between_delegate_steps()
    {
        Write("native/api.cpp");
        using var cancellation = new CancellationTokenSource();
        var verifierCalled = false;
        var builder = new NativeInteropSnapshotBuilder(
            (request, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult(Extraction(request));
            },
            (_, _, _) =>
            {
                verifierCalled = true;
                return Task.FromResult(CompleteBinary("medical.dll"));
            });

        Func<Task> act = () => builder.BuildAsync(
            _root,
            Config(Tu("native/api.cpp", "medical", "artifacts/medical.dll")),
            new ScopePathPolicy(_root),
            cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        verifierCalled.Should().BeFalse();
    }

    [Fact]
    public async Task Resolves_direct_call_to_out_of_line_definition_by_clang_usr()
    {
        var exportPath = Write("native/exports.cpp");
        var algorithmPath = Write("native/algorithm.cpp");
        const string definitionUsr = "c:@S@Algorithm@F@Calculate#I#";
        var builder = new NativeInteropSnapshotBuilder(
            (request, _) =>
            {
                if (PathComparer.Equals(
                        request.SourceFilePath,
                        exportPath))
                {
                    var export = Export(
                        request,
                        "c:E:native/exports.cpp::calculate",
                        "calculate");
                    var caller = Function(
                        request,
                        "c:F:native/exports.cpp::calculate(int)",
                        "c:@F@calculate#I#",
                        "calculate",
                        "calculate",
                        graphKey: export.SymbolCanonicalKey,
                        isMethod: false);
                    var call = new NativeCallFact(
                        caller.GraphCanonicalKey,
                        definitionUsr,
                        CalleeSymbolCanonicalKey: null,
                        request.Target,
                        new Evidence(
                            request.ProducingFileId,
                            new SourceLocation(exportPath, 2, 10, 2, 32),
                            EvidenceConfidence.Exact,
                            "clang-native-call",
                            new Dictionary<string, string>(
                                StringComparer.Ordinal)
                            {
                                ["callKind"] = "direct",
                                ["target"] =
                                    request.Target.RuntimeIdentifier,
                            }));
                    return Task.FromResult(Extraction(
                        request,
                        exports: [export],
                        functions: [caller],
                        calls: [call]));
                }
                var definition = Function(
                    request,
                    "cpp:F:native/algorithm.cpp::Algorithm::Calculate(int)",
                    definitionUsr,
                    "Calculate",
                    "Algorithm::Calculate",
                    graphKey: null,
                    isMethod: true);
                return Task.FromResult(Extraction(
                    request,
                    functions: [definition]));
            });

        var snapshot = await builder.BuildAsync(
            _root,
            Config(
                new InteropTranslationUnitConfig(
                    "native/exports.cpp",
                    "native.dll",
                    ["-x", "c++"],
                    BinaryPath: null),
                new InteropTranslationUnitConfig(
                    "native/algorithm.cpp",
                    "native.dll",
                    ["-x", "c++"],
                    BinaryPath: null)),
            new ScopePathPolicy(_root),
            CancellationToken.None);

        snapshot.IsComplete.Should().BeTrue();
        snapshot.Functions.Should().HaveCount(2);
        snapshot.Calls.Should().ContainSingle(call =>
            call.CallerSymbolCanonicalKey
                == "c:E:native/exports.cpp::calculate"
            && call.CalleeSymbolCanonicalKey
                == "cpp:F:native/algorithm.cpp::Algorithm::Calculate(int)"
            && call.ReferencedDeclarationUsr == definitionUsr);
    }

    [Fact]
    public async Task Resolves_direct_call_to_definition_when_same_usr_has_header_declaration()
    {
        var exportPath = Write("native/exports.cpp");
        var algorithmPath = Write("native/algorithm.cpp");
        const string definitionUsr = "c:@S@Algorithm@F@Calculate#I#";
        var builder = new NativeInteropSnapshotBuilder(
            (request, _) =>
            {
                var declaration = Function(
                    request,
                    "cpp:F:native/algorithm.hpp::Algorithm::Calculate(int)",
                    definitionUsr,
                    "Calculate",
                    "Algorithm::Calculate",
                    graphKey: null,
                    isMethod: true,
                    isDefinition: false);
                if (PathComparer.Equals(request.SourceFilePath, exportPath))
                {
                    var caller = Function(
                        request,
                        "cpp:F:native/exports.cpp::run()",
                        "c:@F@run#",
                        "run",
                        "run",
                        graphKey: null,
                        isMethod: false);
                    return Task.FromResult(Extraction(
                        request,
                        functions: [caller, declaration],
                        calls:
                        [
                            new NativeCallFact(
                                caller.GraphCanonicalKey,
                                definitionUsr,
                                CalleeSymbolCanonicalKey: null,
                                request.Target,
                                new Evidence(
                                    request.ProducingFileId,
                                    new SourceLocation(exportPath, 2, 10, 2, 32),
                                    EvidenceConfidence.Exact,
                                    "clang-native-call",
                                    new Dictionary<string, string>(
                                        StringComparer.Ordinal)
                                    {
                                        ["callKind"] = "direct",
                                        ["target"] =
                                            request.Target.RuntimeIdentifier,
                                    }))
                        ]));
                }

                var definition = Function(
                    request,
                    "cpp:F:native/algorithm.cpp::Algorithm::Calculate(int)",
                    definitionUsr,
                    "Calculate",
                    "Algorithm::Calculate",
                    graphKey: null,
                    isMethod: true);
                return Task.FromResult(Extraction(
                    request,
                    functions: [declaration, definition]));
            });

        var snapshot = await builder.BuildAsync(
            _root,
            Config(
                new InteropTranslationUnitConfig(
                    "native/exports.cpp",
                    "native.dll",
                    ["-x", "c++"],
                    BinaryPath: null),
                new InteropTranslationUnitConfig(
                    "native/algorithm.cpp",
                    "native.dll",
                    ["-x", "c++"],
                    BinaryPath: null)),
            new ScopePathPolicy(_root),
            CancellationToken.None);

        snapshot.IsComplete.Should().BeTrue();
        snapshot.Calls.Should().ContainSingle(call =>
            call.CalleeSymbolCanonicalKey
                == "cpp:F:native/algorithm.cpp::Algorithm::Calculate(int)");
    }

    [Fact]
    public async Task Changed_call_projection_between_double_parse_is_rejected()
    {
        Write("native/api.cpp");
        var extractionCount = 0;
        var builder = new NativeInteropSnapshotBuilder(
            (request, _) =>
            {
                extractionCount++;
                var function = Function(
                    request,
                    "cpp:F:native/api.cpp::run()",
                    "c:@F@run#",
                    extractionCount == 1 ? "run" : "changed",
                    extractionCount == 1 ? "run" : "changed",
                    graphKey: null,
                    isMethod: false);
                return Task.FromResult(Extraction(
                    request,
                    functions: [function]));
            });

        var snapshot = await builder.BuildAsync(
            _root,
            Config(new InteropTranslationUnitConfig(
                "native/api.cpp",
                "native.dll",
                ["-x", "c++"],
                BinaryPath: null)),
            new ScopePathPolicy(_root),
            CancellationToken.None);

        snapshot.IsComplete.Should().BeFalse();
        snapshot.Failures.Should().ContainSingle(failure =>
            failure.Kind == NativeInteropSnapshotFailureKind.FactSetChanged);
    }

    [Fact]
    public async Task Incomplete_call_graph_retains_stable_positive_facts()
    {
        Write("native/api.cpp");
        var builder = new NativeInteropSnapshotBuilder(
            (request, _) =>
            {
                var caller = Function(
                    request,
                    "cpp:F:native/api.cpp::initialize()",
                    "c:@F@initialize#",
                    "initialize",
                    "initialize",
                    graphKey: null,
                    isMethod: false);
                var callee = Function(
                    request,
                    "cpp:F:native/api.cpp::shutdown()",
                    "c:@F@shutdown#",
                    "shutdown",
                    "shutdown",
                    graphKey: null,
                    isMethod: false);
                return Task.FromResult(Extraction(
                    request,
                    exports:
                    [
                        Export(
                            request,
                            "c:E:native/api.cpp::initialize",
                            "initialize"),
                    ],
                    functions: [caller, callee],
                    calls:
                    [
                        DirectCall(
                            request,
                            caller.SymbolCanonicalKey,
                            callee.DeclarationUsr,
                            callee.SymbolCanonicalKey,
                            line: 2),
                    ],
                    callGraphComplete: false));
            });

        var snapshot = await builder.BuildAsync(
            _root,
            Config(new InteropTranslationUnitConfig(
                "native/api.cpp",
                "native.dll",
                ["-x", "c++"],
                BinaryPath: null)),
            new ScopePathPolicy(_root),
            CancellationToken.None);

        snapshot.IsComplete.Should().BeFalse();
        snapshot.HasPublishableFacts.Should().BeTrue();
        snapshot.SourceExports.Should().ContainSingle();
        snapshot.Functions.Should().HaveCount(2);
        snapshot.Calls.Should().ContainSingle();
        snapshot.Failures.Should().ContainSingle(failure =>
            failure.Kind == NativeInteropSnapshotFailureKind.CallGraphIncomplete);
    }

    private ScopeInteropConfig Config(params InteropTranslationUnitConfig[] units) =>
        new(InteropTarget.WindowsX64Msvc, units);

    private static InteropTranslationUnitConfig Tu(
        string path,
        string exportName,
        string binaryPath) =>
        new(path, exportName + ".dll", ["-x", "c++"], binaryPath);

    private string Write(string relativePath, string content = "// fixture")
    {
        var path = Path.GetFullPath(Path.Join(
            _root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static ClangNativeExtractionResult Extraction(
        ClangNativeExtractionRequest request,
        IReadOnlyList<NativeExport>? exports = null,
        IReadOnlyList<AbiRecordLayout>? records = null,
        IReadOnlyList<ClangExtractionDiagnostic>? diagnostics = null,
        IReadOnlyList<string>? includedFiles = null,
        IReadOnlyList<NativeFunctionFact>? functions = null,
        IReadOnlyList<NativeCallFact>? calls = null,
        bool callGraphComplete = true,
        IReadOnlyList<NativeTypeDeclarationFact>? types = null) =>
        new(
            functions ?? [],
            types ?? [],
            exports ?? [],
            records ?? [],
            diagnostics ?? [])
        {
            IncludedFiles = includedFiles ?? [request.SourceFilePath],
            Calls = calls ?? [],
            IsCallGraphComplete = callGraphComplete,
        };

    private static NativeTypeDeclarationFact Type(
        ClangNativeExtractionRequest request,
        string canonicalKey,
        NativeTypeDeclarationKind kind,
        string name,
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
            canonicalKey,
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
                request.ProducingFileId,
                TestLocation(request.SourceFilePath),
                EvidenceConfidence.Exact,
                "clang-native",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["declarationKind"] = declarationKind,
                    ["isDefinition"] = isDefinition ? "true" : "false",
                    ["target"] = request.Target.RuntimeIdentifier,
                }));
    }

    private static NativeExport Export(
        ClangNativeExtractionRequest request,
        string canonicalKey,
        string exportName,
        string? evidencePath = null,
        int returnSize = 4) =>
        new(
            canonicalKey,
            exportName,
            InteropCallingConvention.Cdecl,
            new AbiTypeRef(
                returnSize == 4 ? "int" : "long long",
                AbiTypeCategory.SignedInteger,
                sizeBytes: returnSize,
                alignmentBytes: returnSize,
                isSigned: true),
            [],
            HasCLinkage: true,
            IsBinaryVerified: false,
            request.Target,
            new Evidence(
                request.ProducingFileId,
                new SourceLocation(
                    evidencePath ?? request.SourceFilePath,
                    1,
                    1,
                    1,
                    8),
                EvidenceConfidence.Exact,
                "snapshot-test"))
        {
            LibraryName = request.LibraryName,
            ModuleIdentitySource = NativeModuleIdentitySource.Configuration,
        };

    private static NativeExport RiskExport(
        ClangNativeExtractionRequest request)
    {
        var retentionEvidence = NativeRiskEvidence(
            request,
            "clang-native-retention",
            "parameterPosition",
            "0");
        var exceptionEvidence = NativeRiskEvidence(
            request,
            "clang-native-exception",
            "escapeKind",
            "direct-throw");
        var allocationEvidence = NativeRiskEvidence(
            request,
            "clang-native-allocation",
            "allocatorFamily",
            "crt_heap",
            ("allocator", "malloc"));
        return Export(
            request,
            "c:E:native/api.cpp::risk",
            "risk") with
        {
            Parameters =
            [
                new AbiParameter(
                    0,
                    "callback",
                    FunctionPointerType(),
                    AbiParameterDirection.In,
                    TestLocation(request.SourceFilePath)),
            ],
            RetainedCallbacks =
            [
                new NativeCallbackRetention(
                    0,
                    request.Target,
                    retentionEvidence),
            ],
            ExceptionEscape = new NativeExceptionEscape(
                request.Target,
                exceptionEvidence),
            ReturnAllocation = new NativeReturnAllocation(
                InteropAllocatorFamily.CrtHeap,
                request.Target,
                allocationEvidence),
        };
    }

    private static Evidence NativeRiskEvidence(
        ClangNativeExtractionRequest request,
        string producer,
        string factKey,
        string factValue,
        params (string Key, string Value)[] extraMetadata)
    {
        var metadata = new Dictionary<string, string>(
            StringComparer.Ordinal)
        {
            ["target"] = request.Target.RuntimeIdentifier,
            [factKey] = factValue,
        };
        foreach (var item in extraMetadata)
        {
            metadata.Add(item.Key, item.Value);
        }
        return new Evidence(
            request.ProducingFileId,
            TestLocation(request.SourceFilePath),
            EvidenceConfidence.Exact,
            producer,
            metadata);
    }

    private static AbiRecordLayout Record(
        ClangNativeExtractionRequest request,
        string canonicalKey,
        Evidence evidence) =>
        new(
            canonicalKey,
            AbiRecordKind.Native,
            SizeBytes: 4,
            AlignmentBytes: 4,
            Pack: null,
            [
                new AbiFieldLayout(
                    0,
                    "value",
                    IntType(),
                    OffsetBytes: 0,
                    SizeBytes: 4,
                    evidence),
            ],
            request.Target,
            evidence);

    private static AbiTypeRef IntType() =>
        new(
            "int",
            AbiTypeCategory.SignedInteger,
            sizeBytes: 4,
            alignmentBytes: 4,
            isSigned: true);

    private static AbiTypeRef FunctionPointerType() =>
        new(
            "void (*)(int)",
            AbiTypeCategory.FunctionPointer,
            pointerDepth: 1,
            sizeBytes: 8,
            alignmentBytes: 8);

    private static NativeFunctionFact Function(
        ClangNativeExtractionRequest request,
        string key,
        string usr,
        string name,
        string qualifiedName,
        string? graphKey,
        bool isMethod,
        bool isDefinition = true) =>
        new(
            key,
            name,
            qualifiedName,
            InteropCallingConvention.Cdecl,
            IntType(),
            [],
            HasCLinkage: !isMethod,
            IsExported: graphKey is not null,
            IsDefinition: isDefinition,
            new Evidence(
                request.ProducingFileId,
                new SourceLocation(
                    request.SourceFilePath,
                    1,
                    1,
                    1,
                    8),
                EvidenceConfidence.Exact,
                "clang-native",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["target"] = request.Target.RuntimeIdentifier,
                }))
        {
            DeclarationUsr = usr,
            GraphCanonicalKey = graphKey ?? key,
            IsMethod = isMethod,
            Target = request.Target,
        };

    private static NativeCallFact DirectCall(
        ClangNativeExtractionRequest request,
        string callerKey,
        string referencedUsr,
        string calleeKey,
        int line) =>
        new(
            callerKey,
            referencedUsr,
            calleeKey,
            request.Target,
            new Evidence(
                request.ProducingFileId,
                new SourceLocation(
                    request.SourceFilePath,
                    line,
                    1,
                    line,
                    8),
                EvidenceConfidence.Exact,
                "clang-native-call",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["callKind"] = "direct",
                    ["target"] = request.Target.RuntimeIdentifier,
                }));

    private static Evidence TestEvidence(
        ClangNativeExtractionRequest request,
        string path) =>
        new(
            request.ProducingFileId,
            TestLocation(path),
            EvidenceConfidence.Exact,
            "snapshot-test");

    private static SourceLocation TestLocation(string path) =>
        new(path, 1, 1, 1, 8);

    private static BinaryExportVerificationResult CompleteBinary(
        string? moduleName,
        params string[] names) =>
        new(
            BinaryExportVerificationStatus.Complete,
            InteropArchitecture.X64,
            0x8664,
            moduleName,
            names.Length == 0
                ? []
                :
                [
                    new BinaryExportEntry(
                        1,
                        0x1000,
                        names,
                        IsForwarder: false,
                        Forwarder: null)
                ],
            "complete");

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
