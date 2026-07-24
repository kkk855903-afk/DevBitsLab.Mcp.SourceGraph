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
    BinaryPathNotConfigured,
    BinaryPathRejected,
    ExtractionFailed,
    ExtractionDiagnostics,
    DependencyPathRejected,
    BinaryVerificationFailed,
    BinaryVerificationIncomplete,
    BinaryVerificationInvalid,
    BinaryTargetMismatch,
    BinaryModuleMismatch,
    UnsupportedBinaryAssociation,
    InvalidFact,
    ExportConflict,
    RecordConflict,
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

internal sealed record NativeInteropTranslationUnitContribution(
    int ConfigurationIndex,
    InteropTranslationUnitConfig Configuration,
    string? SourceFilePath,
    string? BinaryFilePath,
    IReadOnlyList<string> IncludedFiles,
    IReadOnlyList<NativeExport> SourceExports,
    IReadOnlyList<NativeExport> VerifiedExports,
    IReadOnlyList<AbiRecordLayout> RecordLayouts,
    IReadOnlyList<ClangExtractionDiagnostic> Diagnostics,
    BinaryExportVerificationResult? BinaryVerification,
    bool IsComplete,
    IReadOnlyList<NativeInteropSnapshotFailure> Failures);

internal sealed record NativeInteropSnapshot(
    InteropTarget Target,
    IReadOnlyList<NativeInteropTranslationUnitContribution> Contributions,
    IReadOnlyList<string> IncludedFiles,
    IReadOnlyDictionary<string, IReadOnlyList<string>> DependencyFanout,
    IReadOnlyList<NativeExport> SourceExports,
    IReadOnlyList<NativeExport> VerifiedExports,
    IReadOnlyList<AbiRecordLayout> RecordLayouts,
    IReadOnlyList<NativeInteropSnapshotDiagnostic> Diagnostics,
    bool IsSourceComplete,
    bool IsExportUniverseComplete,
    bool IsComplete,
    IReadOnlyList<NativeInteropSnapshotFailure> Failures);

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
    private readonly NativeInteropExtractor _extractor;
    private readonly NativeInteropBinaryVerifier _binaryVerifier;

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

        var contributions =
            new List<NativeInteropTranslationUnitContribution>(
                configuration.TranslationUnits.Count);
        foreach (var entry in configuration.TranslationUnits.Select(
                     (value, index) => (Value: value, Index: index)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            contributions.Add(await BuildContributionAsync(
                    lexicalRoot,
                    effectivePolicy,
                    configuration.Target,
                    entry.Value,
                    entry.Index,
                    cancellationToken)
                .ConfigureAwait(false));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return BuildAggregate(configuration.Target, contributions);
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
            || translationUnit.Arguments is not { Count: > 0 }
            || translationUnit.Arguments.Any(string.IsNullOrWhiteSpace))
        {
            failures.Add(Failure(
                NativeInteropSnapshotFailureKind.InvalidConfiguration,
                index,
                translationUnit?.Path,
                "Translation-unit path, library, and compiler arguments are required."));
            return EmptyContribution(index, translationUnit!, failures);
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

        ClangNativeExtractionResult extraction;
        try
        {
            extraction = await _extractor(
                    new ClangNativeExtractionRequest(
                        sourceFilePath,
                        scopeRoot,
                        checked((long)index + 1),
                        target,
                        translationUnit.Arguments,
                        translationUnit.Library,
                        pathPolicy.ConfiguredExcludePatterns),
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
                NativeInteropSnapshotFailureKind.ExtractionFailed,
                index,
                translationUnit.Path,
                $"Native extraction failed ({ex.GetType().Name})."));
            return EmptyContribution(
                index,
                translationUnit,
                failures,
                sourceFilePath,
                binaryFilePath);
        }

        if (extraction is null)
        {
            failures.Add(Failure(
                NativeInteropSnapshotFailureKind.ExtractionFailed,
                index,
                translationUnit.Path,
                "Native extractor returned no result."));
            return EmptyContribution(
                index,
                translationUnit,
                failures,
                sourceFilePath,
                binaryFilePath);
        }

        var diagnostics = OrderDiagnostics(extraction.Diagnostics ?? []);
        if (diagnostics.Any(diagnostic =>
                diagnostic.Severity is ClangExtractionDiagnosticSeverity.Error
                    or ClangExtractionDiagnosticSeverity.Fatal))
        {
            failures.Add(Failure(
                NativeInteropSnapshotFailureKind.ExtractionDiagnostics,
                index,
                translationUnit.Path,
                "Native extraction returned one or more error diagnostics."));
            return new NativeInteropTranslationUnitContribution(
                index,
                translationUnit,
                sourceFilePath,
                binaryFilePath,
                [],
                [],
                [],
                [],
                diagnostics,
                null,
                IsComplete: false,
                OrderFailures(failures));
        }

        if (extraction.IncludedFiles is null
            || !TryAuthorizeDependencies(
                sourceFilePath,
                extraction.IncludedFiles,
                pathPolicy,
                out var includedFiles))
        {
            failures.Add(Failure(
                NativeInteropSnapshotFailureKind.DependencyPathRejected,
                index,
                translationUnit.Path,
                "Extractor dependency paths were incomplete or outside the approved scope."));
            return new NativeInteropTranslationUnitContribution(
                index,
                translationUnit,
                sourceFilePath,
                binaryFilePath,
                [],
                [],
                [],
                [],
                diagnostics,
                null,
                IsComplete: false,
                OrderFailures(failures));
        }

        if (extraction.Exports is null || extraction.RecordLayouts is null)
        {
            failures.Add(Failure(
                NativeInteropSnapshotFailureKind.InvalidFact,
                index,
                translationUnit.Path,
                "Native extractor returned null fact collections."));
        }
        var sourceExports = OrderExports((extraction.Exports ?? [])
            .Where(export =>
            {
                var valid = export is not null
                    && export.Target.IsAbiEquivalentTo(target)
                    && !export.IsBinaryVerified
                    && export.ModuleIdentitySource
                        != NativeModuleIdentitySource.Binary
                    && export.LibraryName is not null
                    && LibrariesMatch(
                        translationUnit.Library,
                        export.LibraryName,
                        target);
                if (!valid)
                {
                    failures.Add(Failure(
                        NativeInteropSnapshotFailureKind.InvalidFact,
                        index,
                        translationUnit.Path,
                        "Source export does not match the configured target and module.",
                        export?.SymbolCanonicalKey));
                }
                return valid;
            }));
        var records = OrderRecords((extraction.RecordLayouts ?? [])
            .Where(record =>
            {
                var valid = record is not null
                    && record.Target.IsAbiEquivalentTo(target);
                if (!valid)
                {
                    failures.Add(Failure(
                        NativeInteropSnapshotFailureKind.InvalidFact,
                        index,
                        translationUnit.Path,
                        "Native record does not match the configured target.",
                        record?.SymbolCanonicalKey));
                }
                return valid;
            }));
        if (translationUnit.BinaryPath is null)
        {
            failures.Add(Failure(
                NativeInteropSnapshotFailureKind.BinaryPathNotConfigured,
                index,
                translationUnit.Path,
                "No authoritative binary artifact is configured."));
            return new NativeInteropTranslationUnitContribution(
                index,
                translationUnit,
                sourceFilePath,
                null,
                includedFiles,
                sourceExports,
                [],
                records,
                diagnostics,
                null,
                IsComplete: false,
                OrderFailures(failures));
        }

        BinaryExportVerificationResult binaryVerification;
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
                NativeInteropSnapshotFailureKind.BinaryVerificationFailed,
                index,
                translationUnit.Path,
                $"Binary verification failed ({ex.GetType().Name})."));
            return new NativeInteropTranslationUnitContribution(
                index,
                translationUnit,
                sourceFilePath,
                binaryFilePath,
                includedFiles,
                sourceExports,
                [],
                records,
                diagnostics,
                null,
                IsComplete: false,
                OrderFailures(failures));
        }

        if (binaryVerification is null)
        {
            failures.Add(Failure(
                NativeInteropSnapshotFailureKind.BinaryVerificationFailed,
                index,
                translationUnit.Path,
                "Binary verifier returned no result."));
        }
        else if (!binaryVerification.IsComplete)
        {
            failures.Add(Failure(
                FailureKindFor(binaryVerification.Status),
                index,
                translationUnit.Path,
                binaryVerification.Reason));
        }
        else if (binaryVerification.ImageArchitecture != target.Architecture)
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

        var verifiedExports = Array.Empty<NativeExport>();
        if (failures.Count == 0)
        {
            verifiedExports = AssociateExactBinaryExports(
                sourceExports,
                binaryVerification!,
                translationUnit,
                index,
                failures);
        }

        return new NativeInteropTranslationUnitContribution(
            index,
            translationUnit,
            sourceFilePath,
            binaryFilePath,
            includedFiles,
            sourceExports,
            verifiedExports,
            records,
            diagnostics,
            binaryVerification,
            failures.Count == 0,
            OrderFailures(failures));
    }

    private static NativeExport[] AssociateExactBinaryExports(
        IReadOnlyList<NativeExport> sourceExports,
        BinaryExportVerificationResult verification,
        InteropTranslationUnitConfig translationUnit,
        int index,
        List<NativeInteropSnapshotFailure> failures)
    {
        var entriesByName = new Dictionary<string, List<BinaryExportEntry>>(
            StringComparer.Ordinal);
        foreach (var entry in verification.Exports ?? [])
        {
            if (entry.Names is not { Count: > 0 })
            {
                failures.Add(Failure(
                    NativeInteropSnapshotFailureKind.UnsupportedBinaryAssociation,
                    index,
                    translationUnit.Path,
                    "Ordinal-only PE exports are not associated with source declarations."));
                continue;
            }
            foreach (var name in entry.Names)
            {
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
        IReadOnlyList<NativeInteropTranslationUnitContribution> contributions)
    {
        var failures = contributions
            .SelectMany(contribution => contribution.Failures)
            .ToList();
        var sourceExports = AggregateFacts(
            contributions.SelectMany(contribution => contribution.SourceExports),
            export => export.SymbolCanonicalKey,
            InteropFactPayloadCodec.EncodeNativeExport,
            NativeInteropSnapshotFailureKind.ExportConflict,
            failures,
            out var rejectedSourceExportKeys);
        var verifiedExports = AggregateFacts(
            contributions.SelectMany(contribution => contribution.VerifiedExports),
            export => export.SymbolCanonicalKey,
            InteropFactPayloadCodec.EncodeNativeExport,
            NativeInteropSnapshotFailureKind.ExportConflict,
            failures,
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
            out _);

        var fanoutSets = new Dictionary<string, HashSet<string>>(_pathComparer);
        foreach (var contribution in contributions)
        {
            if (contribution.SourceFilePath is null)
            {
                continue;
            }
            foreach (var dependency in contribution.IncludedFiles)
            {
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
            dependencyFanout[pair.Key] = OrderPaths(pair.Value);
        }

        var orderedFailures = OrderFailures(failures);
        var sourceComplete = contributions.All(contribution =>
                !contribution.Failures.Any(failure => failure.Kind is
                    NativeInteropSnapshotFailureKind.InvalidScopeRoot
                    or NativeInteropSnapshotFailureKind.InvalidConfiguration
                    or NativeInteropSnapshotFailureKind.TranslationUnitPathRejected
                    or NativeInteropSnapshotFailureKind.TranslationUnitMissing
                    or NativeInteropSnapshotFailureKind.BinaryPathRejected
                    or NativeInteropSnapshotFailureKind.ExtractionFailed
                    or NativeInteropSnapshotFailureKind.ExtractionDiagnostics
                    or NativeInteropSnapshotFailureKind.DependencyPathRejected
                    or NativeInteropSnapshotFailureKind.InvalidFact))
            && !orderedFailures.Any(failure => failure.Kind
                is NativeInteropSnapshotFailureKind.ExportConflict
                or NativeInteropSnapshotFailureKind.RecordConflict);
        var exportUniverseComplete = sourceComplete
            && contributions.All(contribution => contribution.IsComplete);

        return new NativeInteropSnapshot(
            target,
            contributions.ToArray(),
            OrderPaths(fanoutSets.Keys),
            dependencyFanout,
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
            orderedFailures);
    }

    private static T[] AggregateFacts<T>(
        IEnumerable<T> facts,
        Func<T, string> keySelector,
        Func<T, string> payloadEncoder,
        NativeInteropSnapshotFailureKind conflictKind,
        List<NativeInteropSnapshotFailure> failures,
        out HashSet<string> rejectedKeys)
    {
        var result = new List<T>();
        rejectedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in facts
                     .GroupBy(keySelector, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var payloads = new List<(T Fact, string Payload)>();
            var invalid = false;
            foreach (var fact in group)
            {
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
        out IReadOnlyList<string> includedFiles)
    {
        var approved = new HashSet<string>(_pathComparer)
        {
            sourceFilePath,
        };
        foreach (var dependency in dependencies)
        {
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
                or PathTooLongException)
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
                or PathTooLongException)
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
                or PathTooLongException)
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
