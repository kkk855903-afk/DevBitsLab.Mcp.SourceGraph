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
    [InlineData("included-files")]
    [InlineData("exports")]
    [InlineData("records")]
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
        IReadOnlyList<string>? includedFiles = null) =>
        new(
            [],
            [],
            exports ?? [],
            records ?? [],
            diagnostics ?? [])
        {
            IncludedFiles = includedFiles ?? [request.SourceFilePath],
        };

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
