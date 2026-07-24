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
            "binary:one",
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

        extractorCalls.Should().Be(1);
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
