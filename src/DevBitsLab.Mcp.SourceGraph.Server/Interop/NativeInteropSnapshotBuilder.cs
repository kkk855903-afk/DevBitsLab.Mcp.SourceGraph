using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Indexing.Clang;
using DevBitsLab.Mcp.SourceGraph.Interop;

namespace DevBitsLab.Mcp.SourceGraph.Server.Interop;

internal enum NativeInteropSnapshotFailureKind
{
    InvalidScopeRoot,
    InvalidConfiguration,
    TranslationUnitPathRejected,
    TranslationUnitMissing,
    BinaryPathRejected,
    ExtractionFailed,
    ExtractionDiagnostics,
    DependencyPathRejected,
    DependencySetChanged,
    FactLocationRejected,
    CollectionLimitExceeded,
    BinaryCollectionLimitExceeded,
    ContentHashFailed,
    ContentHashLimitExceeded,
    InputContentChanged,
    BinaryVerificationFailed,
    BinaryVerificationIncomplete,
    BinaryVerificationInvalid,
    BinaryTargetMismatch,
    BinaryModuleMismatch,
    UnsupportedBinaryAssociation,
    InvalidFact,
    ExportConflict,
    RecordConflict,
    FunctionConflict,
    CallConflict,
    CallGraphIncomplete,
    FactSetChanged,
}

internal sealed record NativeInteropSnapshotFailure(
    NativeInteropSnapshotFailureKind Kind,
    int? TranslationUnitIndex,
    string? ConfiguredPath,
    string Message,
    string? CanonicalKey = null);

internal sealed record NativeInteropSnapshotDiagnostic(
    int TranslationUnitIndex,
    string ConfiguredPath,
    ClangExtractionDiagnostic Diagnostic);

internal sealed record NativeInteropFileContentHash(
    string FilePath,
    long LengthBytes,
    byte[] Sha256);

internal sealed record NativeInteropTranslationUnitContribution(
    int ConfigurationIndex,
    InteropTranslationUnitConfig Configuration,
    string? SourceFilePath,
    string? BinaryFilePath,
    IReadOnlyList<string> IncludedFiles,
    IReadOnlyList<NativeInteropFileContentHash> ContentHashes,
    IReadOnlyList<NativeExport> SourceExports,
    IReadOnlyList<NativeExport> VerifiedExports,
    IReadOnlyList<AbiRecordLayout> RecordLayouts,
    IReadOnlyList<ClangExtractionDiagnostic> Diagnostics,
    BinaryExportVerificationResult? BinaryVerification,
    bool IsComplete,
    IReadOnlyList<NativeInteropSnapshotFailure> Failures)
{
    public IReadOnlyList<NativeFunctionFact> Functions { get; init; } = [];
    public IReadOnlyList<NativeCallFact> Calls { get; init; } = [];
}

internal sealed record NativeInteropSnapshot(
    InteropTarget Target,
    IReadOnlyList<NativeInteropTranslationUnitContribution> Contributions,
    IReadOnlyList<string> IncludedFiles,
    IReadOnlyDictionary<string, IReadOnlyList<string>> DependencyFanout,
    IReadOnlyDictionary<string, NativeInteropFileContentHash> ContentHashes,
    IReadOnlyList<NativeExport> SourceExports,
    IReadOnlyList<NativeExport> VerifiedExports,
    IReadOnlyList<AbiRecordLayout> RecordLayouts,
    IReadOnlyList<NativeInteropSnapshotDiagnostic> Diagnostics,
    bool IsSourceComplete,
    bool IsExportUniverseComplete,
    bool IsComplete,
    IReadOnlyList<NativeInteropSnapshotFailure> Failures)
{
    public IReadOnlyList<NativeFunctionFact> Functions { get; init; } = [];
    public IReadOnlyList<NativeCallFact> Calls { get; init; } = [];
}

internal delegate Task<ClangNativeExtractionResult> NativeInteropExtractor(
    ClangNativeExtractionRequest request,
    CancellationToken cancellationToken);

internal delegate Task<BinaryExportVerificationResult> NativeInteropBinaryVerifier(
    string binaryPath,
    InteropTarget target,
    CancellationToken cancellationToken);

/// <summary>
/// Builds one immutable candidate native snapshot without publishing it. Callers decide whether a
/// complete candidate replaces the last successful snapshot; partial candidates retain failures
/// and must not be treated as an authoritative absence.
/// </summary>
internal sealed class NativeInteropSnapshotBuilder
{
    internal const int MaximumTranslationUnits = 256;
    internal const int MaximumCompilerArguments = 4096;
    internal const int MaximumIncludedFilesPerTranslationUnit = 4096;
    internal const int MaximumDiagnosticsPerTranslationUnit = 4096;
    internal const int MaximumFunctionsPerTranslationUnit = 4096;
    internal const int MaximumCallsPerTranslationUnit = 8192;
    internal const int MaximumSymbolsPerSnapshot = 100_000;
    internal const int MaximumCallsPerSnapshot = 200_000;
    internal const int MaximumExportsPerTranslationUnit = 4096;
    internal const int MaximumRecordLayoutsPerTranslationUnit = 4096;
    internal const int MaximumParametersPerExport = 4096;
    internal const int MaximumFieldsPerRecord = 4096;
    internal const int MaximumRetainedCallbacksPerExport = 4096;
    internal const int MaximumNestedFactsPerTranslationUnit = 65_536;
    internal const int MaximumEvidenceMetadataEntries = 256;
    internal const int MaximumBinaryExports = 65_536;
    internal const int MaximumBinaryExportNames = 65_536;
    internal const long MaximumHashedFileBytes = 32L * 1024 * 1024;
    internal const long MaximumHashBytesPerTranslationUnit =
        256L * 1024 * 1024;

    private const int HashReadBufferBytes = 64 * 1024;

    private readonly NativeInteropExtractor _extractor;
    private readonly NativeInteropBinaryVerifier _binaryVerifier;

    private sealed record ExtractionAttempt(
        ClangNativeExtractionResult? Extraction,
        NativeInteropSnapshotFailure? Failure);

    private sealed record PreparedExtraction(
        IReadOnlyList<string> IncludedFiles,
        IReadOnlyList<ClangExtractionDiagnostic> Diagnostics,
        NativeInteropSnapshotFailure? Failure);

    private sealed record ContentHashBatch(
        IReadOnlyList<NativeInteropFileContentHash> Hashes,
        NativeInteropSnapshotFailure? Failure);

    private static readonly StringComparer _pathComparer =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    public NativeInteropSnapshotBuilder(
        NativeInteropExtractor? extractor = null,
        NativeInteropBinaryVerifier? binaryVerifier = null)
    {
        _extractor = extractor ?? ExtractAsync;
        _binaryVerifier = binaryVerifier ?? BinaryExportVerifier.VerifyAsync;
    }

    public async Task<NativeInteropSnapshot> BuildAsync(
        string scopeRoot,
        ScopeInteropConfig configuration,
        ScopePathPolicy pathPolicy,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeRoot);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(configuration.Target);
        ArgumentNullException.ThrowIfNull(configuration.TranslationUnits);
        ArgumentNullException.ThrowIfNull(pathPolicy);
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryCreateEffectivePolicy(
                scopeRoot,
                pathPolicy.ConfiguredExcludePatterns,
                out var lexicalRoot,
                out var effectivePolicy))
        {
            var failure = new NativeInteropSnapshotFailure(
                NativeInteropSnapshotFailureKind.InvalidScopeRoot,
                null,
                null,
                "Scope root could not be resolved as an existing approved directory.");
            return EmptySnapshot(configuration.Target, [failure]);
        }

        if (configuration.TranslationUnits.Count == 0)
        {
            var failure = new NativeInteropSnapshotFailure(
                NativeInteropSnapshotFailureKind.InvalidConfiguration,
                null,
                null,
                "At least one native translation unit is required.");
            return EmptySnapshot(configuration.Target, [failure]);
        }
        if (configuration.TranslationUnits.Count > MaximumTranslationUnits)
        {
            var failure = new NativeInteropSnapshotFailure(
                NativeInteropSnapshotFailureKind.CollectionLimitExceeded,
                null,
                null,
                $"Translation-unit count exceeds the {MaximumTranslationUnits}-item limit.");
            return EmptySnapshot(configuration.Target, [failure]);
        }

        var contributions =
            new List<NativeInteropTranslationUnitContribution>(
                configuration.TranslationUnits.Count);
        var symbolCount = 0;
        var callCount = 0;
        for (var index = 0;
             index < configuration.TranslationUnits.Count;
             index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var contribution = await BuildContributionAsync(
                    lexicalRoot,
                    effectivePolicy,
                    configuration.Target,
                    configuration.TranslationUnits[index],
                    index,
                    cancellationToken)
                .ConfigureAwait(false);
            int nextSymbolCount;
            int nextCallCount;
            try
            {
                nextSymbolCount = checked(
                    symbolCount
                    + contribution.Functions.Count
                    + contribution.SourceExports.Count
                    + contribution.RecordLayouts.Count);
                nextCallCount = checked(
                    callCount + contribution.Calls.Count);
            }
            catch (OverflowException)
            {
                return EmptySnapshot(
                    configuration.Target,
                    [
                        CollectionLimitFailure(
                            index,
                            configuration.TranslationUnits[index]?.Path,
                            "Native snapshot collection",
                            MaximumSymbolsPerSnapshot),
                    ]);
            }

            if (nextSymbolCount > MaximumSymbolsPerSnapshot)
            {
                return EmptySnapshot(
                    configuration.Target,
                    [
                        CollectionLimitFailure(
                            index,
                            configuration.TranslationUnits[index]?.Path,
                            "Native snapshot symbol",
                            MaximumSymbolsPerSnapshot),
                    ]);
            }
            if (nextCallCount > MaximumCallsPerSnapshot)
            {
                return EmptySnapshot(
                    configuration.Target,
                    [
                        CollectionLimitFailure(
                            index,
                            configuration.TranslationUnits[index]?.Path,
                            "Native snapshot direct-call",
                            MaximumCallsPerSnapshot),
                    ]);
            }

            symbolCount = nextSymbolCount;
            callCount = nextCallCount;
            contributions.Add(contribution);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return BuildAggregate(
            configuration.Target,
            contributions,
            cancellationToken);
    }

    private async Task<NativeInteropTranslationUnitContribution>
        BuildContributionAsync(
            string scopeRoot,
            ScopePathPolicy pathPolicy,
            InteropTarget target,
            InteropTranslationUnitConfig translationUnit,
            int index,
            CancellationToken cancellationToken)
    {
        var failures = new List<NativeInteropSnapshotFailure>();
        if (translationUnit is null
            || string.IsNullOrWhiteSpace(translationUnit.Path)
            || string.IsNullOrWhiteSpace(translationUnit.Library)
            || translationUnit.Arguments is not { Count: > 0 })
        {
            failures.Add(Failure(
                NativeInteropSnapshotFailureKind.InvalidConfiguration,
                index,
                translationUnit?.Path,
                "Translation-unit path, library, and compiler arguments are required."));
            return EmptyContribution(index, translationUnit!, failures);
        }
        if (translationUnit.Arguments.Count > MaximumCompilerArguments)
        {
            failures.Add(CollectionLimitFailure(
                index,
                translationUnit.Path,
                "Compiler argument",
                MaximumCompilerArguments));
            return EmptyContribution(index, translationUnit, failures);
        }
        for (var argumentIndex = 0;
             argumentIndex < translationUnit.Arguments.Count;
             argumentIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(
                    translationUnit.Arguments[argumentIndex]))
            {
                failures.Add(Failure(
                    NativeInteropSnapshotFailureKind.InvalidConfiguration,
                    index,
                    translationUnit.Path,
                    "Compiler arguments cannot contain blank values."));
                return EmptyContribution(index, translationUnit, failures);
            }
        }

        if (!TryAuthorizeConfiguredPath(
                scopeRoot,
                translationUnit.Path,
                pathPolicy,
                requireExistingFile: true,
                out var sourceFilePath,
                out var sourceMissing))
        {
            failures.Add(Failure(
                sourceMissing
                    ? NativeInteropSnapshotFailureKind.TranslationUnitMissing
                    : NativeInteropSnapshotFailureKind.TranslationUnitPathRejected,
                index,
                translationUnit.Path,
                sourceMissing
                    ? "Configured translation unit does not exist."
                    : "Configured translation-unit path is outside the approved scope."));
            return EmptyContribution(index, translationUnit, failures);
        }

        string? binaryFilePath = null;
        if (translationUnit.BinaryPath is not null
            && !TryAuthorizeConfiguredPath(
                scopeRoot,
                translationUnit.BinaryPath,
                pathPolicy,
                requireExistingFile: false,
                out binaryFilePath,
                out _))
        {
            failures.Add(Failure(
                NativeInteropSnapshotFailureKind.BinaryPathRejected,
                index,
                translationUnit.Path,
                "Configured binary path is outside the approved scope."));
            return EmptyContribution(
                index,
                translationUnit,
                failures,
                sourceFilePath);
        }

        var extractionRequest = new ClangNativeExtractionRequest(
            sourceFilePath,
            scopeRoot,
            checked((long)index + 1),
            target,
            translationUnit.Arguments,
            translationUnit.Library,
            pathPolicy.ConfiguredExcludePatterns);
        var sourceHashBeforeDiscovery = await HashApprovedFilesAsync(
                [sourceFilePath],
                pathPolicy,
                index,
                translationUnit.Path,
                cancellationToken)
            .ConfigureAwait(false);
        if (sourceHashBeforeDiscovery.Failure is not null)
        {
            return EmptyContribution(
                index,
                translationUnit,
                [sourceHashBeforeDiscovery.Failure],
                sourceFilePath,
                binaryFilePath);
        }

        var discoveryAttempt = await ExtractOnceAsync(
                extractionRequest,
                index,
                translationUnit.Path,
                cancellationToken)
            .ConfigureAwait(false);
        if (discoveryAttempt.Failure is not null)
        {
            return EmptyContribution(
                index,
                translationUnit,
                [discoveryAttempt.Failure],
                sourceFilePath,
                binaryFilePath);
        }
        var discovery = PrepareExtraction(
            discoveryAttempt.Extraction!,
            sourceFilePath,
            pathPolicy,
            index,
            translationUnit.Path,
            cancellationToken);
        if (discovery.Failure is not null)
        {
            return PreparedFailureContribution(
                index,
                translationUnit,
                sourceFilePath,
                binaryFilePath,
                discovery);
        }

        var hashesBeforeReparse = await HashApprovedFilesAsync(
                discovery.IncludedFiles,
                pathPolicy,
                index,
                translationUnit.Path,
                cancellationToken)
            .ConfigureAwait(false);
        if (hashesBeforeReparse.Failure is not null)
        {
            return EmptyContribution(
                index,
                translationUnit,
                [hashesBeforeReparse.Failure],
                sourceFilePath,
                binaryFilePath);
        }
        if (!HasSameHash(
                sourceHashBeforeDiscovery.Hashes[0],
                hashesBeforeReparse.Hashes.Single(hash =>
                    _pathComparer.Equals(hash.FilePath, sourceFilePath))))
        {
            return EmptyContribution(
                index,
                translationUnit,
                [
                    Failure(
                        NativeInteropSnapshotFailureKind.InputContentChanged,
                        index,
                        translationUnit.Path,
                        "Translation-unit content changed during dependency discovery."),
                ],
                sourceFilePath,
                binaryFilePath);
        }

        var reparseAttempt = await ExtractOnceAsync(
                extractionRequest,
                index,
                translationUnit.Path,
                cancellationToken)
            .ConfigureAwait(false);
        if (reparseAttempt.Failure is not null)
        {
            return EmptyContribution(
                index,
                translationUnit,
                [reparseAttempt.Failure],
                sourceFilePath,
                binaryFilePath);
        }
        var reparse = PrepareExtraction(
            reparseAttempt.Extraction!,
            sourceFilePath,
            pathPolicy,
            index,
            translationUnit.Path,
            cancellationToken);
        if (reparse.Failure is not null)
        {
            return PreparedFailureContribution(
                index,
                translationUnit,
                sourceFilePath,
                binaryFilePath,
                reparse);
        }
        if (!PathSetsEqual(
                discovery.IncludedFiles,
                reparse.IncludedFiles))
        {
            return EmptyContribution(
                index,
                translationUnit,
                [
                    Failure(
                        NativeInteropSnapshotFailureKind.DependencySetChanged,
                        index,
                        translationUnit.Path,
                        "Included-file set changed between dependency discovery and reparse."),
                ],
                sourceFilePath,
                binaryFilePath);
        }

        var hashesAfterReparse = await HashApprovedFilesAsync(
                reparse.IncludedFiles,
                pathPolicy,
                index,
                translationUnit.Path,
                cancellationToken)
            .ConfigureAwait(false);
        if (hashesAfterReparse.Failure is not null)
        {
            return EmptyContribution(
                index,
                translationUnit,
                [hashesAfterReparse.Failure],
                sourceFilePath,
                binaryFilePath);
        }
        if (!HashSetsEqual(
                hashesBeforeReparse.Hashes,
                hashesAfterReparse.Hashes))
        {
            return EmptyContribution(
                index,
                translationUnit,
                [
                    Failure(
                        NativeInteropSnapshotFailureKind.InputContentChanged,
                        index,
                        translationUnit.Path,
                        "Translation-unit or included-file content changed during reparse."),
                ],
                sourceFilePath,
                binaryFilePath);
        }

        var extraction = reparseAttempt.Extraction!;
        var includedFiles = reparse.IncludedFiles;
        var includedFileSet = new HashSet<string>(
            includedFiles,
            _pathComparer);
        var diagnostics = reparse.Diagnostics;
        NativeFunctionFact[] functions = [];
        NativeCallFact[] calls = [];
        var discoveryProjectionValid = TryBuildCallProjection(
            discoveryAttempt.Extraction!,
            includedFileSet,
            pathPolicy,
            target,
            cancellationToken,
            out var discoveryFunctions,
            out var discoveryCalls,
            out var discoveryProjectionKind,
            out var discoveryProjectionMessage);
        if (!discoveryProjectionValid)
        {
            failures.Add(Failure(
                discoveryProjectionKind,
                index,
                translationUnit.Path,
                discoveryProjectionMessage));
        }
        var reparseProjectionValid = TryBuildCallProjection(
            extraction,
            includedFileSet,
            pathPolicy,
            target,
            cancellationToken,
            out functions,
            out calls,
            out var reparseProjectionKind,
            out var reparseProjectionMessage);
        if (!reparseProjectionValid)
        {
            failures.Add(Failure(
                reparseProjectionKind,
                index,
                translationUnit.Path,
                reparseProjectionMessage));
        }
        if (discoveryProjectionValid
            && reparseProjectionValid
            && !CallProjectionsEqual(
                discoveryFunctions,
                discoveryCalls,
                functions,
                calls))
        {
            failures.Add(Failure(
                NativeInteropSnapshotFailureKind.FactSetChanged,
                index,
                translationUnit.Path,
                "Native function or direct-call facts changed between the two content-bound parses."));
            functions = [];
            calls = [];
        }

        var sourceExportList = new List<NativeExport>(extraction.Exports!.Count);
        for (var exportIndex = 0;
             exportIndex < extraction.Exports.Count;
             exportIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var export = extraction.Exports[exportIndex];
            if (export is null
                || export.Target is null
                || !export.Target.IsAbiEquivalentTo(target)
                || export.IsBinaryVerified
                || export.ModuleIdentitySource
                    == NativeModuleIdentitySource.Binary
                || export.LibraryName is null
                || !LibrariesMatch(
                    translationUnit.Library,
                    export.LibraryName,
                    target))
            {
                failures.Add(Failure(
                    NativeInteropSnapshotFailureKind.InvalidFact,
                    index,
                    translationUnit.Path,
                    "Source export does not match the configured target and module.",
                    export?.SymbolCanonicalKey));
                continue;
            }
            if (!TryNormalizeExport(
                    export,
                    includedFileSet,
                    pathPolicy,
                    cancellationToken,
                    out var normalizedExport,
                    out var rejectionKind,
                    out var rejectionMessage))
            {
                failures.Add(Failure(
                    rejectionKind,
                    index,
                    translationUnit.Path,
                    rejectionMessage,
                    export.SymbolCanonicalKey));
                continue;
            }
            sourceExportList.Add(normalizedExport);
        }
        var sourceExports = OrderExports(sourceExportList);

        var recordList = new List<AbiRecordLayout>(
            extraction.RecordLayouts!.Count);
        for (var recordIndex = 0;
             recordIndex < extraction.RecordLayouts.Count;
             recordIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = extraction.RecordLayouts[recordIndex];
            if (record is null
                || record.Target is null
                || !record.Target.IsAbiEquivalentTo(target))
            {
                failures.Add(Failure(
                    NativeInteropSnapshotFailureKind.InvalidFact,
                    index,
                    translationUnit.Path,
                    "Native record does not match the configured target.",
                    record?.SymbolCanonicalKey));
                continue;
            }
            if (!TryNormalizeRecord(
                    record,
                    includedFileSet,
                    pathPolicy,
                    cancellationToken,
                    out var normalizedRecord,
                    out var rejectionKind,
                    out var rejectionMessage))
            {
                failures.Add(Failure(
                    rejectionKind,
                    index,
                    translationUnit.Path,
                    rejectionMessage,
                    record.SymbolCanonicalKey));
                continue;
            }
            recordList.Add(normalizedRecord);
        }
        var records = OrderRecords(recordList);

        BinaryExportVerificationResult? binaryVerification = null;
        var verifiedExports = Array.Empty<NativeExport>();
        if (failures.Count == 0 && translationUnit.BinaryPath is not null)
        {
            try
            {
                binaryVerification = await _binaryVerifier(
                        binaryFilePath!,
                        target,
                        cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add(Failure(
                    NativeInteropSnapshotFailureKind
                        .BinaryVerificationFailed,
                    index,
                    translationUnit.Path,
                    $"Binary verification failed ({ex.GetType().Name})."));
            }
        }

        if (failures.Count == 0 && translationUnit.BinaryPath is not null)
        {
            if (binaryVerification is null)
            {
                failures.Add(Failure(
                    NativeInteropSnapshotFailureKind.BinaryVerificationFailed,
                    index,
                    translationUnit.Path,
                    "Binary verifier returned no result."));
            }
            else if (!TryValidateBinaryCollectionBounds(
                         binaryVerification,
                         cancellationToken,
                         out var binaryCollectionFailureKind,
                         out var binaryCollectionMessage))
            {
                failures.Add(Failure(
                    binaryCollectionFailureKind,
                    index,
                    translationUnit.Path,
                    binaryCollectionMessage));
            }
            else if (!binaryVerification.IsComplete)
            {
                failures.Add(Failure(
                    FailureKindFor(binaryVerification.Status),
                    index,
                    translationUnit.Path,
                    binaryVerification.Reason));
            }
            else if (binaryVerification.ImageArchitecture
                     != target.Architecture)
            {
                failures.Add(Failure(
                    NativeInteropSnapshotFailureKind.BinaryTargetMismatch,
                    index,
                    translationUnit.Path,
                    "Complete binary result does not match the configured target."));
            }
            else if (binaryVerification.ModuleName is not null
                     && !LibrariesMatch(
                         translationUnit.Library,
                         binaryVerification.ModuleName,
                         target))
            {
                failures.Add(Failure(
                    NativeInteropSnapshotFailureKind.BinaryModuleMismatch,
                    index,
                    translationUnit.Path,
                    "PE module name does not match the configured native library."));
            }
        }

        if (failures.Count == 0 && binaryVerification is not null)
        {
            verifiedExports = AssociateExactBinaryExports(
                sourceExports,
                binaryVerification!,
                translationUnit,
                index,
                failures,
                cancellationToken);
        }

        IReadOnlyList<NativeInteropFileContentHash> contentHashes = [];
        var hashesAfterBuild = await HashApprovedFilesAsync(
                reparse.IncludedFiles,
                pathPolicy,
                index,
                translationUnit.Path,
                cancellationToken)
            .ConfigureAwait(false);
        if (hashesAfterBuild.Failure is not null)
        {
            failures.Add(hashesAfterBuild.Failure);
            sourceExports = [];
            verifiedExports = [];
            records = [];
            functions = [];
            calls = [];
        }
        else if (!HashSetsEqual(
                     hashesAfterReparse.Hashes,
                     hashesAfterBuild.Hashes))
        {
            failures.Add(Failure(
                NativeInteropSnapshotFailureKind.InputContentChanged,
                index,
                translationUnit.Path,
                "Translation-unit or included-file content changed during fact validation or binary verification."));
            sourceExports = [];
            verifiedExports = [];
            records = [];
            functions = [];
            calls = [];
        }
        else
        {
            contentHashes = hashesAfterBuild.Hashes;
        }

        return new NativeInteropTranslationUnitContribution(
            index,
            translationUnit,
            sourceFilePath,
            binaryFilePath,
            includedFiles,
            contentHashes,
            sourceExports,
            verifiedExports,
            records,
            diagnostics,
            binaryVerification,
            failures.Count == 0,
            OrderFailures(failures))
        {
            Functions = functions,
            Calls = calls,
        };
    }

    private async Task<ExtractionAttempt> ExtractOnceAsync(
        ClangNativeExtractionRequest request,
        int index,
        string configuredPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var extraction = await _extractor(request, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return extraction is null
                ? new ExtractionAttempt(
                    null,
                    Failure(
                        NativeInteropSnapshotFailureKind.ExtractionFailed,
                        index,
                        configuredPath,
                        "Native extractor returned no result."))
                : new ExtractionAttempt(extraction, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ExtractionAttempt(
                null,
                Failure(
                    NativeInteropSnapshotFailureKind.ExtractionFailed,
                    index,
                    configuredPath,
                    $"Native extraction failed ({ex.GetType().Name})."));
        }
    }

    private static PreparedExtraction PrepareExtraction(
        ClangNativeExtractionResult extraction,
        string sourceFilePath,
        ScopePathPolicy pathPolicy,
        int index,
        string configuredPath,
        CancellationToken cancellationToken)
    {
        if (!TryValidateTopLevelCollectionBounds(
                extraction,
                index,
                configuredPath,
                out var collectionFailure))
        {
            return new PreparedExtraction([], [], collectionFailure);
        }
        if (!TryValidateNestedFactBounds(
                extraction,
                index,
                configuredPath,
                cancellationToken,
                out var nestedCollectionFailure))
        {
            return new PreparedExtraction(
                [],
                [],
                nestedCollectionFailure);
        }
        if (extraction.IncludedFiles is null
            || !TryAuthorizeDependencies(
                sourceFilePath,
                extraction.IncludedFiles,
                pathPolicy,
                cancellationToken,
                out var includedFiles))
        {
            return new PreparedExtraction(
                [],
                [],
                Failure(
                    NativeInteropSnapshotFailureKind.DependencyPathRejected,
                    index,
                    configuredPath,
                    "Extractor dependency paths were incomplete or outside the approved scope."));
        }

        var includedFileSet = new HashSet<string>(
            includedFiles,
            _pathComparer);
        if (!TryNormalizeDiagnostics(
                extraction.Diagnostics!,
                includedFileSet,
                pathPolicy,
                cancellationToken,
                out var diagnostics))
        {
            return new PreparedExtraction(
                includedFiles,
                [],
                Failure(
                    NativeInteropSnapshotFailureKind.FactLocationRejected,
                    index,
                    configuredPath,
                    "Extractor diagnostic location is not one of the approved included files."));
        }
        if (!extraction.IsCallGraphComplete)
        {
            return new PreparedExtraction(
                includedFiles,
                diagnostics,
                Failure(
                    NativeInteropSnapshotFailureKind.CallGraphIncomplete,
                    index,
                    configuredPath,
                    "Native call extraction is partial; the prior complete snapshot was retained."));
        }
        if (diagnostics.Any(diagnostic =>
                diagnostic.Severity is ClangExtractionDiagnosticSeverity.Error
                    or ClangExtractionDiagnosticSeverity.Fatal))
        {
            return new PreparedExtraction(
                includedFiles,
                diagnostics,
                Failure(
                    NativeInteropSnapshotFailureKind.ExtractionDiagnostics,
                    index,
                    configuredPath,
                    "Native extraction returned one or more error diagnostics."));
        }
        return new PreparedExtraction(includedFiles, diagnostics, null);
    }

    private static NativeInteropTranslationUnitContribution
        PreparedFailureContribution(
            int index,
            InteropTranslationUnitConfig translationUnit,
            string sourceFilePath,
            string? binaryFilePath,
            PreparedExtraction prepared) =>
        new(
            index,
            translationUnit,
            sourceFilePath,
            binaryFilePath,
            prepared.IncludedFiles,
            [],
            [],
            [],
            [],
            prepared.Diagnostics,
            null,
            IsComplete: false,
            [prepared.Failure!]);

    private static async Task<ContentHashBatch> HashApprovedFilesAsync(
        IReadOnlyList<string> paths,
        ScopePathPolicy pathPolicy,
        int index,
        string configuredPath,
        CancellationToken cancellationToken)
    {
        if (paths.Count > MaximumIncludedFilesPerTranslationUnit)
        {
            return new ContentHashBatch(
                [],
                Failure(
                    NativeInteropSnapshotFailureKind.ContentHashLimitExceeded,
                    index,
                    configuredPath,
                    $"Content-hash file count exceeds the {MaximumIncludedFilesPerTranslationUnit}-item limit."));
        }

        var hashes = new List<NativeInteropFileContentHash>(paths.Count);
        var buffer = ArrayPool<byte>.Shared.Rent(HashReadBufferBytes);
        long totalBytes = 0;
        try
        {
            for (var pathIndex = 0;
                 pathIndex < paths.Count;
                 pathIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = paths[pathIndex];
                if (!TryAuthorizeExistingAbsoluteFile(
                        path,
                        pathPolicy,
                        out var physicalPath)
                    || !_pathComparer.Equals(path, physicalPath))
                {
                    return new ContentHashBatch(
                        [],
                        Failure(
                            NativeInteropSnapshotFailureKind.ContentHashFailed,
                            index,
                            configuredPath,
                            "A content-hash input is no longer an approved physical file."));
                }

                try
                {
                    var writeTimeBefore = File.GetLastWriteTimeUtc(physicalPath);
                    await using var stream = new FileStream(
                        physicalPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete,
                        HashReadBufferBytes,
                        FileOptions.Asynchronous
                            | FileOptions.SequentialScan);
                    var lengthBefore = stream.Length;
                    if (lengthBefore < 0
                        || lengthBefore > MaximumHashedFileBytes
                        || lengthBefore
                            > MaximumHashBytesPerTranslationUnit - totalBytes)
                    {
                        return new ContentHashBatch(
                            [],
                            Failure(
                                NativeInteropSnapshotFailureKind
                                    .ContentHashLimitExceeded,
                                index,
                                configuredPath,
                                $"Content hashing exceeds the {MaximumHashedFileBytes}-byte per-file or {MaximumHashBytesPerTranslationUnit}-byte per-translation-unit limit."));
                    }

                    using var incrementalHash = IncrementalHash.CreateHash(
                        HashAlgorithmName.SHA256);
                    long fileBytes = 0;
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var read = await stream.ReadAsync(
                                buffer.AsMemory(0, HashReadBufferBytes),
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }
                        if (read > MaximumHashedFileBytes - fileBytes
                            || read
                                > MaximumHashBytesPerTranslationUnit
                                    - totalBytes
                                    - fileBytes)
                        {
                            return new ContentHashBatch(
                                [],
                                Failure(
                                    NativeInteropSnapshotFailureKind
                                        .ContentHashLimitExceeded,
                                    index,
                                    configuredPath,
                                    "Content grew beyond the configured hashing limits."));
                        }
                        incrementalHash.AppendData(buffer, 0, read);
                        fileBytes += read;
                    }

                    var lengthAfter = stream.Length;
                    var writeTimeAfter =
                        File.GetLastWriteTimeUtc(physicalPath);
                    if (lengthBefore != lengthAfter
                        || fileBytes != lengthAfter
                        || writeTimeBefore != writeTimeAfter)
                    {
                        return new ContentHashBatch(
                            [],
                            Failure(
                                NativeInteropSnapshotFailureKind
                                    .InputContentChanged,
                                index,
                                configuredPath,
                                "A native input changed while it was being hashed."));
                    }

                    hashes.Add(new NativeInteropFileContentHash(
                        physicalPath,
                        fileBytes,
                        incrementalHash.GetHashAndReset()));
                    totalBytes += fileBytes;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (
                    ex is IOException
                        or UnauthorizedAccessException
                        or NotSupportedException
                        or System.Security.SecurityException)
                {
                    return new ContentHashBatch(
                        [],
                        Failure(
                            NativeInteropSnapshotFailureKind.ContentHashFailed,
                            index,
                            configuredPath,
                            $"Native input hashing failed ({ex.GetType().Name})."));
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return new ContentHashBatch(hashes, null);
    }

    private static bool PathSetsEqual(
        IReadOnlyList<string> first,
        IReadOnlyList<string> second)
    {
        if (first.Count != second.Count)
        {
            return false;
        }
        for (var index = 0; index < first.Count; index++)
        {
            if (!_pathComparer.Equals(first[index], second[index]))
            {
                return false;
            }
        }
        return true;
    }

    private static bool HashSetsEqual(
        IReadOnlyList<NativeInteropFileContentHash> first,
        IReadOnlyList<NativeInteropFileContentHash> second)
    {
        if (first.Count != second.Count)
        {
            return false;
        }
        for (var index = 0; index < first.Count; index++)
        {
            if (!HasSameHash(first[index], second[index]))
            {
                return false;
            }
        }
        return true;
    }

    private static bool HasSameHash(
        NativeInteropFileContentHash first,
        NativeInteropFileContentHash second) =>
        _pathComparer.Equals(first.FilePath, second.FilePath)
        && first.LengthBytes == second.LengthBytes
        && first.Sha256.AsSpan().SequenceEqual(second.Sha256);

    private static bool TryValidateTopLevelCollectionBounds(
        ClangNativeExtractionResult extraction,
        int index,
        string configuredPath,
        out NativeInteropSnapshotFailure failure)
    {
        if (extraction.Diagnostics is null
            || extraction.Functions is null
            || extraction.Calls is null
            || extraction.Exports is null
            || extraction.RecordLayouts is null)
        {
            failure = Failure(
                NativeInteropSnapshotFailureKind.InvalidFact,
                index,
                configuredPath,
                "Native extractor returned null fact collections.");
            return false;
        }
        if (extraction.Diagnostics.Count > MaximumDiagnosticsPerTranslationUnit)
        {
            failure = CollectionLimitFailure(
                index,
                configuredPath,
                "Extraction diagnostic",
                MaximumDiagnosticsPerTranslationUnit);
            return false;
        }
        if (extraction.Functions.Count > MaximumFunctionsPerTranslationUnit)
        {
            failure = CollectionLimitFailure(
                index,
                configuredPath,
                "Native function",
                MaximumFunctionsPerTranslationUnit);
            return false;
        }
        if (extraction.Calls.Count > MaximumCallsPerTranslationUnit)
        {
            failure = CollectionLimitFailure(
                index,
                configuredPath,
                "Native call",
                MaximumCallsPerTranslationUnit);
            return false;
        }
        if (extraction.IncludedFiles is not null
            && extraction.IncludedFiles.Count
                > MaximumIncludedFilesPerTranslationUnit)
        {
            failure = CollectionLimitFailure(
                index,
                configuredPath,
                "Included file",
                MaximumIncludedFilesPerTranslationUnit);
            return false;
        }
        if (extraction.Exports.Count > MaximumExportsPerTranslationUnit)
        {
            failure = CollectionLimitFailure(
                index,
                configuredPath,
                "Native export",
                MaximumExportsPerTranslationUnit);
            return false;
        }
        if (extraction.RecordLayouts.Count
            > MaximumRecordLayoutsPerTranslationUnit)
        {
            failure = CollectionLimitFailure(
                index,
                configuredPath,
                "ABI record",
                MaximumRecordLayoutsPerTranslationUnit);
            return false;
        }

        failure = null!;
        return true;
    }

    private static bool TryValidateNestedFactBounds(
        ClangNativeExtractionResult extraction,
        int index,
        string configuredPath,
        CancellationToken cancellationToken,
        out NativeInteropSnapshotFailure failure)
    {
        long nestedFactCount = 0;
        for (var functionIndex = 0;
             functionIndex < extraction.Functions.Count;
             functionIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var function = extraction.Functions[functionIndex];
            if (function?.Parameters is
                { Count: > MaximumParametersPerExport })
            {
                failure = CollectionLimitFailure(
                    index,
                    configuredPath,
                    "Native function parameter",
                    MaximumParametersPerExport);
                return false;
            }
            nestedFactCount += 1L + (function?.Parameters?.Count ?? 0);
            if (nestedFactCount > MaximumNestedFactsPerTranslationUnit)
            {
                failure = CollectionLimitFailure(
                    index,
                    configuredPath,
                    "Nested native fact",
                    MaximumNestedFactsPerTranslationUnit);
                return false;
            }
        }
        nestedFactCount += extraction.Calls.Count;
        if (nestedFactCount > MaximumNestedFactsPerTranslationUnit)
        {
            failure = CollectionLimitFailure(
                index,
                configuredPath,
                "Nested native fact",
                MaximumNestedFactsPerTranslationUnit);
            return false;
        }
        for (var exportIndex = 0;
             exportIndex < extraction.Exports.Count;
             exportIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var export = extraction.Exports[exportIndex];
            if (export is null)
            {
                continue;
            }
            if (export.Parameters is { Count: > MaximumParametersPerExport })
            {
                failure = CollectionLimitFailure(
                    index,
                    configuredPath,
                    "Native export parameter",
                    MaximumParametersPerExport);
                return false;
            }
            if (export.RetainedCallbacks is
                { Count: > MaximumRetainedCallbacksPerExport })
            {
                failure = CollectionLimitFailure(
                    index,
                    configuredPath,
                    "Retained callback",
                    MaximumRetainedCallbacksPerExport);
                return false;
            }
            nestedFactCount += 1L
                + (export.Parameters?.Count ?? 0)
                + (export.RetainedCallbacks?.Count ?? 0)
                + (export.ExceptionEscape is null ? 0 : 1)
                + (export.ReturnAllocation is null ? 0 : 1);
            if (nestedFactCount > MaximumNestedFactsPerTranslationUnit)
            {
                failure = CollectionLimitFailure(
                    index,
                    configuredPath,
                    "Nested native fact",
                    MaximumNestedFactsPerTranslationUnit);
                return false;
            }
        }

        for (var recordIndex = 0;
             recordIndex < extraction.RecordLayouts.Count;
             recordIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = extraction.RecordLayouts[recordIndex];
            if (record is null)
            {
                continue;
            }
            if (record.Fields is { Count: > MaximumFieldsPerRecord })
            {
                failure = CollectionLimitFailure(
                    index,
                    configuredPath,
                    "ABI record field",
                    MaximumFieldsPerRecord);
                return false;
            }
            nestedFactCount += 1L + (record.Fields?.Count ?? 0);
            if (nestedFactCount > MaximumNestedFactsPerTranslationUnit)
            {
                failure = CollectionLimitFailure(
                    index,
                    configuredPath,
                    "Nested native fact",
                    MaximumNestedFactsPerTranslationUnit);
                return false;
            }
        }

        failure = null!;
        return true;
    }

    private static bool TryBuildCallProjection(
        ClangNativeExtractionResult extraction,
        IReadOnlySet<string> includedFiles,
        ScopePathPolicy pathPolicy,
        InteropTarget target,
        CancellationToken cancellationToken,
        out NativeFunctionFact[] functions,
        out NativeCallFact[] calls,
        out NativeInteropSnapshotFailureKind rejectionKind,
        out string rejectionMessage)
    {
        functions = [];
        calls = [];
        var normalizedFunctions = new List<NativeFunctionFact>();
        var graphKeys = new HashSet<string>(StringComparer.Ordinal);
        var definitionsByUsr =
            new Dictionary<string, string>(StringComparer.Ordinal);
        var exportKeys = extraction.Exports
            .Where(export => export is not null)
            .Select(export => export.SymbolCanonicalKey)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var function in extraction.Functions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (function is null)
            {
                rejectionKind = NativeInteropSnapshotFailureKind.InvalidFact;
                rejectionMessage = "Native function collection contains a null item.";
                return false;
            }
            if (!function.IsDefinition)
            {
                continue;
            }
            if (!TryNormalizeFunction(
                    function,
                    includedFiles,
                    pathPolicy,
                    target,
                    cancellationToken,
                    out var normalized,
                    out rejectionKind,
                    out rejectionMessage))
            {
                return false;
            }
            if ((!string.Equals(
                     normalized.GraphCanonicalKey,
                     normalized.SymbolCanonicalKey,
                     StringComparison.Ordinal)
                 && !exportKeys.Contains(normalized.GraphCanonicalKey))
                || !graphKeys.Add(normalized.GraphCanonicalKey)
                || (definitionsByUsr.TryGetValue(
                        normalized.DeclarationUsr,
                        out var existing)
                    && !string.Equals(
                        existing,
                        normalized.GraphCanonicalKey,
                        StringComparison.Ordinal)))
            {
                rejectionKind =
                    NativeInteropSnapshotFailureKind.FunctionConflict;
                rejectionMessage =
                    "Native function identities or graph endpoints conflict within one translation unit.";
                return false;
            }
            definitionsByUsr[normalized.DeclarationUsr] =
                normalized.GraphCanonicalKey;
            normalizedFunctions.Add(normalized);
        }

        var normalizedCalls = new List<NativeCallFact>();
        var occurrences = new HashSet<string>(StringComparer.Ordinal);
        foreach (var call in extraction.Calls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (call is null
                || string.IsNullOrWhiteSpace(call.CallerSymbolCanonicalKey)
                || string.IsNullOrWhiteSpace(call.ReferencedDeclarationUsr)
                || call.Target is null
                || !call.Target.IsAbiEquivalentTo(target)
                || call.Evidence is null
                || call.Evidence.Confidence != EvidenceConfidence.Exact
                || !string.Equals(
                    call.Evidence.Producer,
                    "clang-native-call",
                    StringComparison.Ordinal)
                || call.Evidence.Metadata is null
                || !call.Evidence.Metadata.TryGetValue(
                    "callKind",
                    out var callKind)
                || !string.Equals(
                    callKind,
                    "direct",
                    StringComparison.Ordinal)
                || !call.Evidence.Metadata.TryGetValue(
                    "target",
                    out var callTarget)
                || !string.Equals(
                    callTarget,
                    target.RuntimeIdentifier,
                    StringComparison.Ordinal)
                || !graphKeys.Contains(call.CallerSymbolCanonicalKey)
                || (call.CalleeSymbolCanonicalKey is not null
                    && (!definitionsByUsr.TryGetValue(
                            call.ReferencedDeclarationUsr,
                            out var expectedTarget)
                        || !string.Equals(
                            expectedTarget,
                            call.CalleeSymbolCanonicalKey,
                            StringComparison.Ordinal))))
            {
                rejectionKind = NativeInteropSnapshotFailureKind.InvalidFact;
                rejectionMessage =
                    "A native direct call is malformed or does not match an exact function definition.";
                return false;
            }
            if (!TryNormalizeEvidence(
                    call.Evidence,
                    includedFiles,
                    pathPolicy,
                    cancellationToken,
                    out var evidence,
                    out rejectionKind,
                    out rejectionMessage))
            {
                return false;
            }
            var occurrence = string.Join(
                "\n",
                call.CallerSymbolCanonicalKey,
                call.ReferencedDeclarationUsr,
                evidence.Location.FilePath,
                evidence.Location.StartLine,
                evidence.Location.StartColumn,
                evidence.Location.EndLine,
                evidence.Location.EndColumn);
            if (!occurrences.Add(occurrence))
            {
                rejectionKind = NativeInteropSnapshotFailureKind.CallConflict;
                rejectionMessage =
                    "A native direct-call occurrence is duplicated.";
                return false;
            }
            normalizedCalls.Add(call with { Evidence = evidence });
        }

        functions = normalizedFunctions
            .OrderBy(
                function => function.SymbolCanonicalKey,
                StringComparer.Ordinal)
            .ToArray();
        calls = normalizedCalls
            .OrderBy(
                call => call.CallerSymbolCanonicalKey,
                StringComparer.Ordinal)
            .ThenBy(
                call => call.ReferencedDeclarationUsr,
                StringComparer.Ordinal)
            .ThenBy(call => call.Evidence.Location.FilePath, _pathComparer)
            .ThenBy(call => call.Evidence.Location.StartLine)
            .ThenBy(call => call.Evidence.Location.StartColumn)
            .ToArray();
        rejectionKind = default;
        rejectionMessage = string.Empty;
        return true;
    }

    private static bool TryNormalizeFunction(
        NativeFunctionFact function,
        IReadOnlySet<string> includedFiles,
        ScopePathPolicy pathPolicy,
        InteropTarget target,
        CancellationToken cancellationToken,
        out NativeFunctionFact normalized,
        out NativeInteropSnapshotFailureKind rejectionKind,
        out string rejectionMessage)
    {
        normalized = null!;
        if (string.IsNullOrWhiteSpace(function.SymbolCanonicalKey)
            || !IsNativeFunctionKey(function.SymbolCanonicalKey)
            || string.IsNullOrWhiteSpace(function.GraphCanonicalKey)
            || (!IsNativeFunctionKey(function.GraphCanonicalKey)
                && !IsNativeExportKey(function.GraphCanonicalKey))
            || string.IsNullOrWhiteSpace(function.Name)
            || string.IsNullOrWhiteSpace(function.QualifiedName)
            || string.IsNullOrWhiteSpace(function.DeclarationUsr)
            || function.Target is null
            || !function.Target.IsAbiEquivalentTo(target)
            || function.Parameters is null
            || function.Parameters.Count > MaximumParametersPerExport
            || function.ReturnType is null
            || function.Evidence is null
            || function.Evidence.Confidence != EvidenceConfidence.Exact
            || !string.Equals(
                function.Evidence.Producer,
                "clang-native",
                StringComparison.Ordinal)
            || function.Evidence.Metadata is null
            || !function.Evidence.Metadata.TryGetValue(
                "target",
                out var evidenceTarget)
            || !string.Equals(
                evidenceTarget,
                target.RuntimeIdentifier,
                StringComparison.Ordinal)
            || (function.IsMethod && function.HasCLinkage))
        {
            rejectionKind = NativeInteropSnapshotFailureKind.InvalidFact;
            rejectionMessage = "A native function definition is malformed.";
            return false;
        }
        if (!TryNormalizeEvidence(
                function.Evidence,
                includedFiles,
                pathPolicy,
                cancellationToken,
                out var evidence,
                out rejectionKind,
                out rejectionMessage))
        {
            return false;
        }

        var parameters = new List<AbiParameter>(function.Parameters.Count);
        for (var parameterIndex = 0;
             parameterIndex < function.Parameters.Count;
             parameterIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parameter = function.Parameters[parameterIndex];
            if (parameter is null
                || parameter.Position != parameterIndex
                || parameter.Type is null
                || parameter.Location is null
                || !TryNormalizeLocation(
                    parameter.Location,
                    includedFiles,
                    pathPolicy,
                    out var location))
            {
                rejectionKind = NativeInteropSnapshotFailureKind.InvalidFact;
                rejectionMessage =
                    "A native function parameter is malformed or outside the approved include graph.";
                return false;
            }
            parameters.Add(parameter with { Location = location });
        }
        normalized = function with
        {
            Parameters = parameters,
            Evidence = evidence,
        };
        rejectionKind = default;
        rejectionMessage = string.Empty;
        return true;
    }

    private static bool CallProjectionsEqual(
        IReadOnlyList<NativeFunctionFact> firstFunctions,
        IReadOnlyList<NativeCallFact> firstCalls,
        IReadOnlyList<NativeFunctionFact> secondFunctions,
        IReadOnlyList<NativeCallFact> secondCalls)
    {
        var first = JsonSerializer.Serialize(new
        {
            Functions = firstFunctions,
            Calls = firstCalls,
        });
        var second = JsonSerializer.Serialize(new
        {
            Functions = secondFunctions,
            Calls = secondCalls,
        });
        return string.Equals(first, second, StringComparison.Ordinal);
    }

    private static string FunctionFingerprint(NativeFunctionFact function) =>
        JsonSerializer.Serialize(function with
        {
            Evidence = function.Evidence with
            {
                ProducingFileId = 0,
            },
        });

    private static string CallOccurrenceIdentity(NativeCallFact call)
    {
        var location = call.Evidence.Location;
        return string.Join(
            "\n",
            call.CallerSymbolCanonicalKey,
            call.ReferencedDeclarationUsr,
            location.FilePath,
            location.StartLine,
            location.StartColumn,
            location.EndLine,
            location.EndColumn);
    }

    private static bool IsNativeFunctionKey(string key) =>
        key.StartsWith("c:F:", StringComparison.Ordinal)
        || key.StartsWith("cpp:F:", StringComparison.Ordinal);

    private static bool IsNativeExportKey(string key) =>
        key.StartsWith("c:E:", StringComparison.Ordinal)
        || key.StartsWith("cpp:E:", StringComparison.Ordinal);

    private static bool TryNormalizeDiagnostics(
        IReadOnlyList<ClangExtractionDiagnostic> diagnostics,
        IReadOnlySet<string> includedFiles,
        ScopePathPolicy pathPolicy,
        CancellationToken cancellationToken,
        out ClangExtractionDiagnostic[] normalized)
    {
        var result = new List<ClangExtractionDiagnostic>(diagnostics.Count);
        for (var diagnosticIndex = 0;
             diagnosticIndex < diagnostics.Count;
             diagnosticIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var diagnostic = diagnostics[diagnosticIndex];
            if (diagnostic is null)
            {
                normalized = [];
                return false;
            }
            if (diagnostic.Location is null)
            {
                result.Add(diagnostic);
                continue;
            }
            if (!TryNormalizeLocation(
                    diagnostic.Location,
                    includedFiles,
                    pathPolicy,
                    out var location))
            {
                normalized = [];
                return false;
            }
            result.Add(diagnostic with { Location = location });
        }
        normalized = OrderDiagnostics(result);
        return true;
    }

    private static bool TryNormalizeExport(
        NativeExport export,
        IReadOnlySet<string> includedFiles,
        ScopePathPolicy pathPolicy,
        CancellationToken cancellationToken,
        out NativeExport normalized,
        out NativeInteropSnapshotFailureKind rejectionKind,
        out string rejectionMessage)
    {
        normalized = null!;
        if (export.Parameters is null
            || export.RetainedCallbacks is null
            || export.Evidence is null)
        {
            rejectionKind = NativeInteropSnapshotFailureKind.InvalidFact;
            rejectionMessage = "Native export contains null nested fact collections or evidence.";
            return false;
        }
        if (export.Parameters.Count > MaximumParametersPerExport)
        {
            rejectionKind =
                NativeInteropSnapshotFailureKind.CollectionLimitExceeded;
            rejectionMessage =
                $"Native export parameter count exceeds the {MaximumParametersPerExport}-item limit.";
            return false;
        }
        if (export.RetainedCallbacks.Count
            > MaximumRetainedCallbacksPerExport)
        {
            rejectionKind =
                NativeInteropSnapshotFailureKind.CollectionLimitExceeded;
            rejectionMessage =
                $"Retained callback count exceeds the {MaximumRetainedCallbacksPerExport}-item limit.";
            return false;
        }
        if (!TryNormalizeEvidence(
                export.Evidence,
                includedFiles,
                pathPolicy,
                cancellationToken,
                out var evidence,
                out rejectionKind,
                out rejectionMessage))
        {
            return false;
        }

        var parameters = new List<AbiParameter>(export.Parameters.Count);
        for (var parameterIndex = 0;
             parameterIndex < export.Parameters.Count;
             parameterIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parameter = export.Parameters[parameterIndex];
            if (parameter is null || parameter.Location is null)
            {
                rejectionKind = NativeInteropSnapshotFailureKind.InvalidFact;
                rejectionMessage =
                    "Native export contains a null parameter or parameter location.";
                return false;
            }
            if (!TryNormalizeLocation(
                    parameter.Location,
                    includedFiles,
                    pathPolicy,
                    out var location))
            {
                rejectionKind =
                    NativeInteropSnapshotFailureKind.FactLocationRejected;
                rejectionMessage =
                    "Native export parameter location is not in the approved included-file set.";
                return false;
            }
            parameters.Add(parameter with { Location = location });
        }

        var retainedCallbacks = new List<NativeCallbackRetention>(
            export.RetainedCallbacks.Count);
        for (var retentionIndex = 0;
             retentionIndex < export.RetainedCallbacks.Count;
             retentionIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var retention = export.RetainedCallbacks[retentionIndex];
            if (retention is null || retention.Evidence is null)
            {
                rejectionKind = NativeInteropSnapshotFailureKind.InvalidFact;
                rejectionMessage =
                    "Native export contains null callback-retention evidence.";
                return false;
            }
            if (!TryNormalizeEvidence(
                    retention.Evidence,
                    includedFiles,
                    pathPolicy,
                    cancellationToken,
                    out var retentionEvidence,
                    out rejectionKind,
                    out rejectionMessage))
            {
                return false;
            }
            retainedCallbacks.Add(
                retention with { Evidence = retentionEvidence });
        }

        NativeExceptionEscape? exceptionEscape = null;
        if (export.ExceptionEscape is not null)
        {
            if (export.ExceptionEscape.Evidence is null)
            {
                rejectionKind = NativeInteropSnapshotFailureKind.InvalidFact;
                rejectionMessage =
                    "Native export contains null exception-escape evidence.";
                return false;
            }
            if (!TryNormalizeEvidence(
                    export.ExceptionEscape.Evidence,
                    includedFiles,
                    pathPolicy,
                    cancellationToken,
                    out var exceptionEvidence,
                    out rejectionKind,
                    out rejectionMessage))
            {
                return false;
            }
            exceptionEscape = export.ExceptionEscape with
            {
                Evidence = exceptionEvidence,
            };
        }

        NativeReturnAllocation? returnAllocation = null;
        if (export.ReturnAllocation is not null)
        {
            if (export.ReturnAllocation.Evidence is null)
            {
                rejectionKind = NativeInteropSnapshotFailureKind.InvalidFact;
                rejectionMessage =
                    "Native export contains null return-allocation evidence.";
                return false;
            }
            if (!TryNormalizeEvidence(
                    export.ReturnAllocation.Evidence,
                    includedFiles,
                    pathPolicy,
                    cancellationToken,
                    out var allocationEvidence,
                    out rejectionKind,
                    out rejectionMessage))
            {
                return false;
            }
            returnAllocation = export.ReturnAllocation with
            {
                Evidence = allocationEvidence,
            };
        }

        normalized = export with
        {
            Parameters = parameters,
            RetainedCallbacks = retainedCallbacks,
            ExceptionEscape = exceptionEscape,
            ReturnAllocation = returnAllocation,
            Evidence = evidence,
        };
        rejectionKind = default;
        rejectionMessage = string.Empty;
        return true;
    }

    private static bool TryNormalizeRecord(
        AbiRecordLayout record,
        IReadOnlySet<string> includedFiles,
        ScopePathPolicy pathPolicy,
        CancellationToken cancellationToken,
        out AbiRecordLayout normalized,
        out NativeInteropSnapshotFailureKind rejectionKind,
        out string rejectionMessage)
    {
        normalized = null!;
        if (record.Fields is null || record.Evidence is null)
        {
            rejectionKind = NativeInteropSnapshotFailureKind.InvalidFact;
            rejectionMessage = "ABI record contains null fields or evidence.";
            return false;
        }
        if (record.Fields.Count > MaximumFieldsPerRecord)
        {
            rejectionKind =
                NativeInteropSnapshotFailureKind.CollectionLimitExceeded;
            rejectionMessage =
                $"ABI record field count exceeds the {MaximumFieldsPerRecord}-item limit.";
            return false;
        }
        if (!TryNormalizeEvidence(
                record.Evidence,
                includedFiles,
                pathPolicy,
                cancellationToken,
                out var evidence,
                out rejectionKind,
                out rejectionMessage))
        {
            return false;
        }

        var fields = new List<AbiFieldLayout>(record.Fields.Count);
        for (var fieldIndex = 0;
             fieldIndex < record.Fields.Count;
             fieldIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var field = record.Fields[fieldIndex];
            if (field is null || field.Evidence is null)
            {
                rejectionKind = NativeInteropSnapshotFailureKind.InvalidFact;
                rejectionMessage =
                    "ABI record contains a null field or field evidence.";
                return false;
            }
            if (!TryNormalizeEvidence(
                    field.Evidence,
                    includedFiles,
                    pathPolicy,
                    cancellationToken,
                    out var fieldEvidence,
                    out rejectionKind,
                    out rejectionMessage))
            {
                return false;
            }
            fields.Add(field with { Evidence = fieldEvidence });
        }

        normalized = record with
        {
            Fields = fields,
            Evidence = evidence,
        };
        rejectionKind = default;
        rejectionMessage = string.Empty;
        return true;
    }

    private static bool TryNormalizeEvidence(
        Evidence evidence,
        IReadOnlySet<string> includedFiles,
        ScopePathPolicy pathPolicy,
        CancellationToken cancellationToken,
        out Evidence normalized,
        out NativeInteropSnapshotFailureKind rejectionKind,
        out string rejectionMessage)
    {
        normalized = null!;
        if (evidence.Location is null
            || !TryNormalizeLocation(
                evidence.Location,
                includedFiles,
                pathPolicy,
                out var location))
        {
            rejectionKind =
                NativeInteropSnapshotFailureKind.FactLocationRejected;
            rejectionMessage =
                "Evidence location is not in the approved included-file set.";
            return false;
        }

        IReadOnlyDictionary<string, string>? metadata = null;
        if (evidence.Metadata is not null)
        {
            if (evidence.Metadata.Count > MaximumEvidenceMetadataEntries)
            {
                rejectionKind =
                    NativeInteropSnapshotFailureKind.CollectionLimitExceeded;
                rejectionMessage =
                    $"Evidence metadata count exceeds the {MaximumEvidenceMetadataEntries}-item limit.";
                return false;
            }
            var boundedMetadata =
                new Dictionary<string, string>(StringComparer.Ordinal);
            var observedMetadataEntries = 0;
            foreach (var item in evidence.Metadata
                         .OrderBy(
                             item => item.Key,
                             StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (observedMetadataEntries
                        >= MaximumEvidenceMetadataEntries
                    || item.Key is null
                    || item.Value is null)
                {
                    rejectionKind = observedMetadataEntries
                        >= MaximumEvidenceMetadataEntries
                            ? NativeInteropSnapshotFailureKind
                                .CollectionLimitExceeded
                            : NativeInteropSnapshotFailureKind.InvalidFact;
                    rejectionMessage = observedMetadataEntries
                        >= MaximumEvidenceMetadataEntries
                            ? $"Evidence metadata count exceeds the {MaximumEvidenceMetadataEntries}-item limit."
                            : "Evidence metadata contains a null key or value.";
                    return false;
                }
                observedMetadataEntries++;
                boundedMetadata[item.Key] = item.Value;
            }
            metadata = boundedMetadata;
        }
        normalized = evidence with
        {
            Location = location,
            Metadata = metadata,
        };
        rejectionKind = default;
        rejectionMessage = string.Empty;
        return true;
    }

    private static bool TryNormalizeLocation(
        SourceLocation location,
        IReadOnlySet<string> includedFiles,
        ScopePathPolicy pathPolicy,
        out SourceLocation normalized)
    {
        normalized = null!;
        if (!TryAuthorizeExistingAbsoluteFile(
                location.FilePath,
                pathPolicy,
                out var physicalPath)
            || !includedFiles.Contains(physicalPath))
        {
            return false;
        }
        normalized = location with { FilePath = physicalPath };
        return true;
    }

    private static bool TryValidateBinaryCollectionBounds(
        BinaryExportVerificationResult verification,
        CancellationToken cancellationToken,
        out NativeInteropSnapshotFailureKind failureKind,
        out string message)
    {
        if (verification.Exports is null)
        {
            failureKind =
                NativeInteropSnapshotFailureKind.BinaryVerificationInvalid;
            message = "Binary verifier returned a null export collection.";
            return false;
        }
        if (verification.Exports.Count > MaximumBinaryExports)
        {
            failureKind =
                NativeInteropSnapshotFailureKind.BinaryCollectionLimitExceeded;
            message =
                $"Binary export count exceeds the {MaximumBinaryExports}-item limit.";
            return false;
        }

        var nameCount = 0;
        for (var entryIndex = 0;
             entryIndex < verification.Exports.Count;
             entryIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = verification.Exports[entryIndex];
            if (entry is null || entry.Names is null)
            {
                failureKind =
                    NativeInteropSnapshotFailureKind.BinaryVerificationInvalid;
                message =
                    "Binary verifier returned a null export entry or name collection.";
                return false;
            }
            if (entry.Names.Count > MaximumBinaryExportNames - nameCount)
            {
                failureKind =
                    NativeInteropSnapshotFailureKind.BinaryCollectionLimitExceeded;
                message =
                    $"Binary export-name count exceeds the {MaximumBinaryExportNames}-item limit.";
                return false;
            }
            nameCount += entry.Names.Count;
        }

        failureKind = default;
        message = string.Empty;
        return true;
    }

    private static NativeExport[] AssociateExactBinaryExports(
        IReadOnlyList<NativeExport> sourceExports,
        BinaryExportVerificationResult verification,
        InteropTranslationUnitConfig translationUnit,
        int index,
        List<NativeInteropSnapshotFailure> failures,
        CancellationToken cancellationToken)
    {
        var entriesByName = new Dictionary<string, List<BinaryExportEntry>>(
            StringComparer.Ordinal);
        for (var entryIndex = 0;
             entryIndex < verification.Exports.Count;
             entryIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = verification.Exports[entryIndex];
            if (entry.Names is not { Count: > 0 })
            {
                failures.Add(Failure(
                    NativeInteropSnapshotFailureKind.UnsupportedBinaryAssociation,
                    index,
                    translationUnit.Path,
                    "Ordinal-only PE exports are not associated with source declarations."));
                continue;
            }
            for (var nameIndex = 0;
                 nameIndex < entry.Names.Count;
                 nameIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = entry.Names[nameIndex];
                if (string.IsNullOrWhiteSpace(name))
                {
                    failures.Add(Failure(
                        NativeInteropSnapshotFailureKind.UnsupportedBinaryAssociation,
                        index,
                        translationUnit.Path,
                        "Blank PE export names cannot be associated."));
                    continue;
                }
                if (!entriesByName.TryGetValue(name, out var entries))
                {
                    entries = [];
                    entriesByName.Add(name, entries);
                }
                entries.Add(entry);
            }
        }

        var sourceByName = sourceExports
            .GroupBy(export => export.ExportName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var verified = new List<NativeExport>();
        var associatedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in sourceByName.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entriesByName.TryGetValue(pair.Key, out var entries))
            {
                continue;
            }
            if (pair.Value.Length != 1
                || entries
                    .Select(entry => (
                        entry.Ordinal,
                        entry.AddressRva,
                        entry.IsForwarder,
                        entry.Forwarder))
                    .Distinct()
                    .Count() != 1
                || entries[0].IsForwarder)
            {
                failures.Add(Failure(
                    NativeInteropSnapshotFailureKind.UnsupportedBinaryAssociation,
                    index,
                    translationUnit.Path,
                    "PE export name does not identify one exact non-forwarded source declaration.",
                    pair.Value.Length == 1
                        ? pair.Value[0].SymbolCanonicalKey
                        : null));
                continue;
            }

            var entry = entries[0];
            associatedNames.Add(pair.Key);
            verified.Add(WithBinaryEvidence(
                pair.Value[0],
                translationUnit,
                verification,
                entry));
        }

        foreach (var name in entriesByName.Keys
                     .Where(name => !associatedNames.Contains(name))
                     .OrderBy(name => name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            failures.Add(Failure(
                NativeInteropSnapshotFailureKind.UnsupportedBinaryAssociation,
                index,
                translationUnit.Path,
                $"PE export `{name}` has no exact source declaration association."));
        }
        return OrderExports(verified);
    }

    private static NativeExport WithBinaryEvidence(
        NativeExport source,
        InteropTranslationUnitConfig translationUnit,
        BinaryExportVerificationResult verification,
        BinaryExportEntry binaryEntry)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        if (source.Evidence.Metadata is not null)
        {
            foreach (var pair in source.Evidence.Metadata)
            {
                metadata[pair.Key] = pair.Value;
            }
        }
        metadata["binaryPath"] = translationUnit.BinaryPath!.Replace('\\', '/');
        metadata["binaryModule"] =
            verification.ModuleName ?? translationUnit.Library;
        metadata["binaryOrdinal"] = binaryEntry.Ordinal.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        metadata["binaryRva"] = $"0x{binaryEntry.AddressRva:x8}";

        return source with
        {
            IsBinaryVerified = true,
            LibraryName = translationUnit.Library,
            ModuleIdentitySource = NativeModuleIdentitySource.Binary,
            Evidence = source.Evidence with
            {
                Metadata = metadata,
            },
        };
    }

    private static NativeInteropSnapshot BuildAggregate(
        InteropTarget target,
        IReadOnlyList<NativeInteropTranslationUnitContribution> contributions,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var failures = contributions
            .SelectMany(contribution => contribution.Failures)
            .ToList();
        var sourceExports = AggregateFacts(
            contributions.SelectMany(contribution => contribution.SourceExports),
            export => export.SymbolCanonicalKey,
            InteropFactPayloadCodec.EncodeNativeExport,
            NativeInteropSnapshotFailureKind.ExportConflict,
            failures,
            cancellationToken,
            out var rejectedSourceExportKeys);
        var verifiedExports = AggregateFacts(
            contributions.SelectMany(contribution => contribution.VerifiedExports),
            export => export.SymbolCanonicalKey,
            InteropFactPayloadCodec.EncodeNativeExport,
            NativeInteropSnapshotFailureKind.ExportConflict,
            failures,
            cancellationToken,
            out _)
            .Where(export => !rejectedSourceExportKeys.Contains(
                export.SymbolCanonicalKey))
            .ToArray();
        var records = AggregateFacts(
            contributions.SelectMany(contribution => contribution.RecordLayouts),
            record => record.SymbolCanonicalKey,
            InteropFactPayloadCodec.EncodeAbiRecord,
            NativeInteropSnapshotFailureKind.RecordConflict,
            failures,
            cancellationToken,
            out _);
        var functions = AggregateFacts(
            contributions.SelectMany(contribution => contribution.Functions),
            function => function.SymbolCanonicalKey,
            FunctionFingerprint,
            NativeInteropSnapshotFailureKind.FunctionConflict,
            failures,
            cancellationToken,
            out _);
        var definitionsByUsr = new Dictionary<string, string>(
            StringComparer.Ordinal);
        foreach (var group in functions
                     .Where(function => function.IsDefinition)
                     .GroupBy(
                         function => function.DeclarationUsr,
                         StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var graphKeys = group
                .Select(function => function.GraphCanonicalKey)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (string.IsNullOrWhiteSpace(group.Key)
                || graphKeys.Length != 1)
            {
                failures.Add(Failure(
                    NativeInteropSnapshotFailureKind.FunctionConflict,
                    null,
                    null,
                    "A Clang declaration identity maps to conflicting native definitions."));
                continue;
            }
            definitionsByUsr[group.Key] = graphKeys[0];
        }

        var resolvedCalls = new List<NativeCallFact>();
        foreach (var rawCall in contributions
                     .SelectMany(contribution => contribution.Calls)
                     .OrderBy(
                         call => call.CallerSymbolCanonicalKey,
                         StringComparer.Ordinal)
                     .ThenBy(
                         call => call.ReferencedDeclarationUsr,
                         StringComparer.Ordinal)
                     .ThenBy(
                         call => call.Evidence.Location.FilePath,
                         _pathComparer)
                     .ThenBy(call => call.Evidence.Location.StartLine)
                     .ThenBy(call => call.Evidence.Location.StartColumn))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!definitionsByUsr.TryGetValue(
                    rawCall.ReferencedDeclarationUsr,
                    out var calleeGraphKey))
            {
                // A direct declaration without an in-scope definition (for example a CRT API)
                // is known but is outside this definition-only projection.
                continue;
            }
            if (rawCall.CalleeSymbolCanonicalKey is not null
                && !string.Equals(
                    rawCall.CalleeSymbolCanonicalKey,
                    calleeGraphKey,
                    StringComparison.Ordinal))
            {
                failures.Add(Failure(
                    NativeInteropSnapshotFailureKind.CallConflict,
                    null,
                    null,
                    "A direct call resolves to conflicting definition keys.",
                    rawCall.CallerSymbolCanonicalKey));
                continue;
            }
            resolvedCalls.Add(rawCall with
            {
                CalleeSymbolCanonicalKey = calleeGraphKey,
            });
        }
        var calls = new List<NativeCallFact>();
        foreach (var occurrence in resolvedCalls
                     .GroupBy(
                         call => CallOccurrenceIdentity(call),
                         StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targets = occurrence
                .Select(call => call.CalleeSymbolCanonicalKey)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (targets.Length != 1)
            {
                failures.Add(Failure(
                    NativeInteropSnapshotFailureKind.CallConflict,
                    null,
                    null,
                    "One native call occurrence resolves to conflicting definitions."));
                continue;
            }
            calls.Add(occurrence
                .OrderBy(
                    call => call.Evidence.ProducingFileId)
                .First());
        }

        var contentHashes =
            new Dictionary<string, NativeInteropFileContentHash>(_pathComparer);
        var rejectedHashPaths = new HashSet<string>(_pathComparer);
        foreach (var contribution in contributions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var hash in contribution.ContentHashes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (rejectedHashPaths.Contains(hash.FilePath))
                {
                    continue;
                }
                if (contentHashes.TryGetValue(
                        hash.FilePath,
                        out var existing)
                    && !HasSameHash(existing, hash))
                {
                    contentHashes.Remove(hash.FilePath);
                    rejectedHashPaths.Add(hash.FilePath);
                    failures.Add(Failure(
                        NativeInteropSnapshotFailureKind.InputContentChanged,
                        contribution.ConfigurationIndex,
                        contribution.Configuration.Path,
                        "One included file changed between translation-unit snapshots."));
                    continue;
                }
                contentHashes[hash.FilePath] =
                    new NativeInteropFileContentHash(
                        hash.FilePath,
                        hash.LengthBytes,
                        hash.Sha256.ToArray());
            }
        }
        var orderedContentHashes =
            new Dictionary<string, NativeInteropFileContentHash>(_pathComparer);
        foreach (var pair in contentHashes
                     .OrderBy(pair => pair.Key, _pathComparer)
                     .ThenBy(pair => pair.Key, StringComparer.Ordinal))
        {
            orderedContentHashes.Add(pair.Key, pair.Value);
        }

        var fanoutSets = new Dictionary<string, HashSet<string>>(_pathComparer);
        foreach (var contribution in contributions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (contribution.SourceFilePath is null)
            {
                continue;
            }
            foreach (var dependency in contribution.IncludedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!fanoutSets.TryGetValue(dependency, out var owners))
                {
                    owners = new HashSet<string>(_pathComparer);
                    fanoutSets.Add(dependency, owners);
                }
                owners.Add(contribution.SourceFilePath);
            }
        }
        var dependencyFanout =
            new Dictionary<string, IReadOnlyList<string>>(_pathComparer);
        foreach (var pair in fanoutSets
                     .OrderBy(pair => pair.Key, _pathComparer)
                     .ThenBy(pair => pair.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            dependencyFanout[pair.Key] = OrderPaths(pair.Value);
        }

        var orderedFailures = OrderFailures(failures);
        var sourceComplete = contributions.All(contribution =>
                contribution.ContentHashes.Count
                    == contribution.IncludedFiles.Count
                && !contribution.Failures.Any(failure => failure.Kind is
                    NativeInteropSnapshotFailureKind.InvalidScopeRoot
                    or NativeInteropSnapshotFailureKind.InvalidConfiguration
                    or NativeInteropSnapshotFailureKind.TranslationUnitPathRejected
                    or NativeInteropSnapshotFailureKind.TranslationUnitMissing
                    or NativeInteropSnapshotFailureKind.BinaryPathRejected
                    or NativeInteropSnapshotFailureKind.ExtractionFailed
                    or NativeInteropSnapshotFailureKind.ExtractionDiagnostics
                    or NativeInteropSnapshotFailureKind.DependencyPathRejected
                    or NativeInteropSnapshotFailureKind.DependencySetChanged
                    or NativeInteropSnapshotFailureKind.FactLocationRejected
                    or NativeInteropSnapshotFailureKind.CollectionLimitExceeded
                    or NativeInteropSnapshotFailureKind.ContentHashFailed
                    or NativeInteropSnapshotFailureKind.ContentHashLimitExceeded
                    or NativeInteropSnapshotFailureKind.InputContentChanged
                    or NativeInteropSnapshotFailureKind.InvalidFact
                    or NativeInteropSnapshotFailureKind.CallGraphIncomplete
                    or NativeInteropSnapshotFailureKind.FactSetChanged))
            && !orderedFailures.Any(failure => failure.Kind
                is NativeInteropSnapshotFailureKind.ExportConflict
                or NativeInteropSnapshotFailureKind.RecordConflict
                or NativeInteropSnapshotFailureKind.FunctionConflict
                or NativeInteropSnapshotFailureKind.CallConflict
                or NativeInteropSnapshotFailureKind.InputContentChanged);
        var exportUniverseComplete = sourceComplete
            && contributions.All(contribution => contribution.IsComplete);

        return new NativeInteropSnapshot(
            target,
            contributions.ToArray(),
            OrderPaths(fanoutSets.Keys),
            dependencyFanout,
            orderedContentHashes,
            sourceExports,
            verifiedExports,
            records,
            contributions
                .SelectMany(contribution => contribution.Diagnostics.Select(
                    diagnostic => new NativeInteropSnapshotDiagnostic(
                        contribution.ConfigurationIndex,
                        contribution.Configuration.Path,
                        diagnostic)))
                .OrderBy(diagnostic => diagnostic.TranslationUnitIndex)
                .ThenBy(diagnostic => diagnostic.Diagnostic.Code, StringComparer.Ordinal)
                .ThenBy(
                    diagnostic => diagnostic.Diagnostic.Location?.FilePath,
                    _pathComparer)
                .ThenBy(diagnostic =>
                    diagnostic.Diagnostic.Location?.StartLine ?? 0)
                .ToArray(),
            sourceComplete,
            exportUniverseComplete,
            sourceComplete && exportUniverseComplete && orderedFailures.Length == 0,
            orderedFailures)
        {
            Functions = functions,
            Calls = calls
                .OrderBy(
                    call => call.CallerSymbolCanonicalKey,
                    StringComparer.Ordinal)
                .ThenBy(
                    call => call.CalleeSymbolCanonicalKey,
                    StringComparer.Ordinal)
                .ThenBy(call => call.Evidence.Location.FilePath, _pathComparer)
                .ThenBy(call => call.Evidence.Location.StartLine)
                .ThenBy(call => call.Evidence.Location.StartColumn)
                .ToArray(),
        };
    }

    private static T[] AggregateFacts<T>(
        IEnumerable<T> facts,
        Func<T, string> keySelector,
        Func<T, string> payloadEncoder,
        NativeInteropSnapshotFailureKind conflictKind,
        List<NativeInteropSnapshotFailure> failures,
        CancellationToken cancellationToken,
        out HashSet<string> rejectedKeys)
    {
        var result = new List<T>();
        rejectedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in facts
                     .GroupBy(keySelector, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payloads = new List<(T Fact, string Payload)>();
            var invalid = false;
            foreach (var fact in group)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    payloads.Add((fact, payloadEncoder(fact)));
                }
                catch (Exception ex) when (
                    ex is ArgumentException
                        or FormatException
                        or InvalidOperationException
                        or NotSupportedException)
                {
                    invalid = true;
                }
            }

            if (invalid)
            {
                rejectedKeys.Add(group.Key);
                failures.Add(Failure(
                    NativeInteropSnapshotFailureKind.InvalidFact,
                    null,
                    null,
                    "An interop fact could not be normalized.",
                    group.Key));
                continue;
            }
            if (payloads
                    .Select(item => item.Payload)
                    .Distinct(StringComparer.Ordinal)
                    .Skip(1)
                    .Any())
            {
                rejectedKeys.Add(group.Key);
                failures.Add(Failure(
                    conflictKind,
                    null,
                    null,
                    "Canonical key has conflicting normalized payloads.",
                    group.Key));
                continue;
            }
            result.Add(payloads[0].Fact);
        }
        return result.ToArray();
    }

    private static bool TryAuthorizeDependencies(
        string sourceFilePath,
        IReadOnlyList<string> dependencies,
        ScopePathPolicy pathPolicy,
        CancellationToken cancellationToken,
        out IReadOnlyList<string> includedFiles)
    {
        var approved = new HashSet<string>(_pathComparer)
        {
            sourceFilePath,
        };
        for (var dependencyIndex = 0;
             dependencyIndex < dependencies.Count;
             dependencyIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dependency = dependencies[dependencyIndex];
            if (!TryAuthorizeExistingAbsoluteFile(
                    dependency,
                    pathPolicy,
                    out var physicalPath))
            {
                includedFiles = [];
                return false;
            }
            approved.Add(physicalPath);
        }
        includedFiles = OrderPaths(approved);
        return true;
    }

    private static bool TryCreateEffectivePolicy(
        string scopeRoot,
        IReadOnlyList<string> excludes,
        out string lexicalRoot,
        out ScopePathPolicy pathPolicy)
    {
        lexicalRoot = string.Empty;
        pathPolicy = null!;
        try
        {
            if (!Path.IsPathFullyQualified(scopeRoot)
                || !Directory.Exists(scopeRoot))
            {
                return false;
            }
            lexicalRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(scopeRoot));
            pathPolicy = new ScopePathPolicy(lexicalRoot, excludes);
            return !pathPolicy.IsExcludedForDiscovery(
                lexicalRoot,
                out _);
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or PathTooLongException
                or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool TryAuthorizeConfiguredPath(
        string scopeRoot,
        string configuredPath,
        ScopePathPolicy pathPolicy,
        bool requireExistingFile,
        out string physicalPath,
        out bool missing)
    {
        physicalPath = string.Empty;
        missing = false;
        try
        {
            if (string.IsNullOrWhiteSpace(configuredPath)
                || Path.IsPathRooted(configuredPath)
                || configuredPath.Split(
                        ['/', '\\'],
                        StringSplitOptions.RemoveEmptyEntries)
                    .Any(segment => segment is "." or ".."))
            {
                return false;
            }
            var candidate = Path.GetFullPath(
                Path.Join(
                    scopeRoot,
                    configuredPath
                        .Replace('\\', Path.DirectorySeparatorChar)
                        .Replace('/', Path.DirectorySeparatorChar)));
            if (pathPolicy.IsExcludedForDiscovery(
                    candidate,
                    out var resolvedPath)
                || resolvedPath is null)
            {
                return false;
            }
            physicalPath = Path.GetFullPath(resolvedPath);
            if (requireExistingFile && !File.Exists(physicalPath))
            {
                missing = true;
                return false;
            }
            return true;
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or PathTooLongException
                or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool TryAuthorizeExistingAbsoluteFile(
        string path,
        ScopePathPolicy pathPolicy,
        out string physicalPath)
    {
        physicalPath = string.Empty;
        try
        {
            if (string.IsNullOrWhiteSpace(path)
                || !Path.IsPathFullyQualified(path)
                || pathPolicy.IsExcludedForDiscovery(path, out var resolvedPath)
                || resolvedPath is null
                || !File.Exists(resolvedPath))
            {
                return false;
            }
            physicalPath = Path.GetFullPath(resolvedPath);
            return true;
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or PathTooLongException
                or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool LibrariesMatch(
        string configured,
        string binaryModule,
        InteropTarget target)
    {
        if (target.RuntimeIdentifier.StartsWith(
                "win-",
                StringComparison.OrdinalIgnoreCase))
        {
            static string NormalizeWindowsLibrary(string value)
            {
                var fileName = Path.GetFileName(value);
                return fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    ? fileName[..^4]
                    : fileName;
            }
            return string.Equals(
                NormalizeWindowsLibrary(configured),
                NormalizeWindowsLibrary(binaryModule),
                StringComparison.OrdinalIgnoreCase);
        }
        return string.Equals(
            Path.GetFileName(configured),
            Path.GetFileName(binaryModule),
            StringComparison.Ordinal);
    }

    private static NativeInteropSnapshotFailureKind FailureKindFor(
        BinaryExportVerificationStatus status) =>
        status switch
        {
            BinaryExportVerificationStatus.Invalid =>
                NativeInteropSnapshotFailureKind.BinaryVerificationInvalid,
            BinaryExportVerificationStatus.TargetMismatch =>
                NativeInteropSnapshotFailureKind.BinaryTargetMismatch,
            _ =>
                NativeInteropSnapshotFailureKind.BinaryVerificationIncomplete,
        };

    private static NativeInteropTranslationUnitContribution EmptyContribution(
        int index,
        InteropTranslationUnitConfig configuration,
        IReadOnlyList<NativeInteropSnapshotFailure> failures,
        string? sourceFilePath = null,
        string? binaryFilePath = null) =>
        new(
            index,
            configuration,
            sourceFilePath,
            binaryFilePath,
            [],
            [],
            [],
            [],
            [],
            [],
            null,
            IsComplete: false,
            OrderFailures(failures));

    private static NativeInteropSnapshot EmptySnapshot(
        InteropTarget target,
        IReadOnlyList<NativeInteropSnapshotFailure> failures) =>
        new(
            target,
            [],
            [],
            new Dictionary<string, IReadOnlyList<string>>(_pathComparer),
            new Dictionary<string, NativeInteropFileContentHash>(_pathComparer),
            [],
            [],
            [],
            [],
            IsSourceComplete: false,
            IsExportUniverseComplete: false,
            IsComplete: false,
            OrderFailures(failures));

    private static NativeInteropSnapshotFailure Failure(
        NativeInteropSnapshotFailureKind kind,
        int? index,
        string? configuredPath,
        string message,
        string? canonicalKey = null) =>
        new(kind, index, configuredPath, message, canonicalKey);

    private static NativeInteropSnapshotFailure CollectionLimitFailure(
        int? index,
        string? configuredPath,
        string collectionName,
        int limit) =>
        Failure(
            NativeInteropSnapshotFailureKind.CollectionLimitExceeded,
            index,
            configuredPath,
            $"{collectionName} count exceeds the {limit}-item limit.");

    private static NativeInteropSnapshotFailure[] OrderFailures(
        IEnumerable<NativeInteropSnapshotFailure> failures) =>
        failures
            .Distinct()
            .OrderBy(failure => failure.TranslationUnitIndex ?? int.MaxValue)
            .ThenBy(failure => failure.Kind)
            .ThenBy(failure => failure.ConfiguredPath, StringComparer.Ordinal)
            .ThenBy(failure => failure.CanonicalKey, StringComparer.Ordinal)
            .ThenBy(failure => failure.Message, StringComparer.Ordinal)
            .ToArray();

    private static NativeExport[] OrderExports(IEnumerable<NativeExport> exports) =>
        exports
            .OrderBy(export => export.SymbolCanonicalKey, StringComparer.Ordinal)
            .ThenBy(export => export.ExportName, StringComparer.Ordinal)
            .ToArray();

    private static AbiRecordLayout[] OrderRecords(
        IEnumerable<AbiRecordLayout> records) =>
        records
            .OrderBy(record => record.SymbolCanonicalKey, StringComparer.Ordinal)
            .ToArray();

    private static ClangExtractionDiagnostic[] OrderDiagnostics(
        IEnumerable<ClangExtractionDiagnostic> diagnostics) =>
        diagnostics
            .OrderBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Location?.FilePath, _pathComparer)
            .ThenBy(diagnostic => diagnostic.Location?.StartLine ?? 0)
            .ThenBy(diagnostic => diagnostic.Location?.StartColumn ?? 0)
            .ThenBy(diagnostic => diagnostic.Message, StringComparer.Ordinal)
            .ToArray();

    private static string[] OrderPaths(IEnumerable<string> paths) =>
        paths
            .Distinct(_pathComparer)
            .OrderBy(path => path, _pathComparer)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToArray();

    private static Task<ClangNativeExtractionResult> ExtractAsync(
        ClangNativeExtractionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ClangNativeExtractor.Extract(request));
    }
}
