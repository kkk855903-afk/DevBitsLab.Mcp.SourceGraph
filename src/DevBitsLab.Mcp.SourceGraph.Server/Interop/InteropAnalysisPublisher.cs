using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Interop;
using DevBitsLab.Mcp.SourceGraph.Storage;
using EdgeKinds = DevBitsLab.Mcp.SourceGraph.Sdk.EdgeKinds;

namespace DevBitsLab.Mcp.SourceGraph.Server.Interop;

internal sealed record InteropAnalysisPublicationFailure(
    string? FilePath,
    string Stage,
    string Message);

internal sealed record InteropAnalysisPublicationResult(
    bool IsComplete,
    int FilesPublished,
    int MatchesPublished,
    int FindingsPublished,
    int EdgesPublished,
    IReadOnlyList<InteropAnalysisPublicationFailure> Failures);

/// <summary>
/// Rebuilds the persisted managed/native boundary projection from strict stored facts. Every
/// managed file is replaced through one annotation-and-edge transaction. Incomplete inputs cause
/// no writes, preserving the prior successful projection instead of publishing an absence claim.
/// </summary>
internal sealed class InteropAnalysisPublisher
{
    internal const string Producer = InteropFactProducers.Analysis;
    private const int ProjectionScanPageSize = 1_000;
    private const int MaximumProjectionRows = 100_000;
    private static readonly string[] _annotationFlavors =
    [
        InteropAnnotationFlavors.Match,
        InteropAnnotationFlavors.Finding,
    ];

    private readonly IGraphStore _store;
    private readonly InteropMatcher _matcher;
    private readonly InteropRuleEngine _rules;
    private readonly SemaphoreSlim _publicationLock = new(1, 1);

    public InteropAnalysisPublisher(
        IGraphStore store,
        InteropMatcher? matcher = null,
        InteropRuleEngine? rules = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _matcher = matcher ?? new InteropMatcher();
        _rules = rules ?? InteropRuleEngine.CreatePhase2();
    }

    public async Task<InteropAnalysisPublicationResult> PublishAsync(
        InteropTarget target,
        bool isExportUniverseComplete,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();

        await _publicationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var managed = await InteropFactStoreReader.ReadManagedImportsAsync(
                    _store,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var callbackUsages =
                await InteropFactStoreReader.ReadManagedCallbackUsagesAsync(
                        _store,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            var returnReleases =
                await InteropFactStoreReader.ReadManagedReturnReleasesAsync(
                        _store,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            var native = await InteropFactStoreReader.ReadNativeExportsAsync(
                    _store,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var abiRecords = await InteropFactStoreReader.ReadAbiRecordsAsync(
                    _store,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var previousMatches = await InteropFactStoreReader.ReadMatchesAsync(
                    _store,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var previousFindings = await InteropFactStoreReader.ReadFindingsAsync(
                    _store,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var inputFailures = LoadFailures(
                managed,
                callbackUsages,
                returnReleases,
                native,
                abiRecords,
                previousMatches,
                previousFindings);
            if (!isExportUniverseComplete)
            {
                inputFailures.Add(new InteropAnalysisPublicationFailure(
                    FilePath: null,
                    Stage: "native-snapshot",
                    Message:
                        "The native export universe is incomplete; the last successful "
                        + "analysis projection was retained."));
            }
            if (inputFailures.Count > 0)
            {
                return Failed(inputFailures);
            }

            var targetFailures = ValidateTargets(
                target,
                managed.Facts,
                callbackUsages.Facts,
                returnReleases.Facts,
                native.Facts,
                abiRecords.Facts);
            if (targetFailures.Count > 0)
            {
                return Failed(targetFailures);
            }

            IReadOnlyList<ManagedFileProjection> projections;
            try
            {
                var nativeImplementations =
                    await FindNativeImplementationsAsync(
                            native.Facts,
                            cancellationToken)
                        .ConfigureAwait(false);
                projections = BuildProjections(
                    target,
                    managed.Facts,
                    callbackUsages.Facts,
                    returnReleases.Facts,
                    native.Facts,
                    abiRecords.Facts,
                    previousMatches.Facts,
                    previousFindings.Facts,
                    nativeImplementations);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (
                ex is ArgumentException
                    or InvalidOperationException
                    or InteropFactPayloadException
                    or OverflowException)
            {
                return Failed(
                [
                    new InteropAnalysisPublicationFailure(
                        FilePath: null,
                        Stage: "projection",
                        Message: BoundedMessage(ex)),
                ]);
            }

            try
            {
                await _store.ReplaceFileDerivedProjectionsAsync(
                        projections
                            .Select(projection =>
                                new FileDerivedProjectionReplacement(
                                    projection.FilePath,
                                    Producer,
                                    _annotationFlavors,
                                    projection.Annotations,
                                    projection.Edges))
                            .ToArray(),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (
                ex is ArgumentException
                    or InvalidOperationException
                    or NotSupportedException
                    or OverflowException)
            {
                return Failed(
                [
                    new InteropAnalysisPublicationFailure(
                        FilePath: null,
                        Stage: "storage",
                        Message: BoundedMessage(ex)),
                ]);
            }

            return new InteropAnalysisPublicationResult(
                IsComplete: true,
                FilesPublished: projections.Count,
                MatchesPublished: projections.Sum(item => item.MatchCount),
                FindingsPublished: projections.Sum(item => item.FindingCount),
                EdgesPublished: projections.Sum(item => item.Edges.Count),
                Failures: []);
        }
        finally
        {
            _publicationLock.Release();
        }
    }

    /// <summary>
    /// Removes only the producer-owned match, finding, and P/Invoke projection while preserving
    /// managed import declarations. This is used when a scope explicitly removes its native
    /// interop configuration, where retaining a last-good boundary would be a stale claim.
    /// </summary>
    public async Task<InteropAnalysisPublicationResult> ClearAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _publicationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var ownerScan = await ListProjectionOwnerPathsAsync(cancellationToken)
                .ConfigureAwait(false);
            if (ownerScan.Failure is not null)
            {
                return Failed([ownerScan.Failure]);
            }

            try
            {
                await _store.ReplaceFileDerivedProjectionsAsync(
                        ownerScan.FilePaths
                            .Select(filePath =>
                                new FileDerivedProjectionReplacement(
                                    filePath,
                                    Producer,
                                    _annotationFlavors,
                                    Annotations: [],
                                    Edges: []))
                            .ToArray(),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (
                ex is ArgumentException
                    or InvalidOperationException
                    or NotSupportedException
                    or OverflowException)
            {
                return Failed(
                [
                    new InteropAnalysisPublicationFailure(
                        FilePath: null,
                        Stage: "clear-storage",
                        Message: BoundedMessage(ex)),
                ]);
            }

            return new InteropAnalysisPublicationResult(
                IsComplete: true,
                FilesPublished: ownerScan.FilePaths.Count,
                MatchesPublished: 0,
                FindingsPublished: 0,
                EdgesPublished: 0,
                Failures: []);
        }
        finally
        {
            _publicationLock.Release();
        }
    }

    private async Task<ProjectionOwnerScan> ListProjectionOwnerPathsAsync(
        CancellationToken cancellationToken)
    {
        var paths = new HashSet<string>(PathComparer);
        var rowsRead = 0;
        foreach (var flavor in _annotationFlavors)
        {
            long afterId = 0;
            while (rowsRead < MaximumProjectionRows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var limit = Math.Min(
                    ProjectionScanPageSize,
                    MaximumProjectionRows - rowsRead);
                var page = await _store.ListAnnotationsByFlavorAsync(
                        flavor,
                        afterId,
                        limit,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (page.Count == 0)
                {
                    break;
                }

                foreach (var row in page)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    rowsRead++;
                    afterId = row.AnnotationId;
                    if (!string.IsNullOrWhiteSpace(row.FilePath))
                    {
                        paths.Add(row.FilePath);
                    }
                }
                if (page.Count < limit)
                {
                    break;
                }
            }

            if (rowsRead == MaximumProjectionRows)
            {
                var probe = await _store.ListAnnotationsByFlavorAsync(
                        flavor,
                        afterId,
                        limit: 1,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (probe.Count > 0)
                {
                    return new ProjectionOwnerScan(
                        [],
                        new InteropAnalysisPublicationFailure(
                            FilePath: null,
                            Stage: "clear-scan",
                            Message:
                                "Interop analysis projection scan exceeded the "
                                + $"{MaximumProjectionRows}-row limit."));
                }
            }
        }

        return new ProjectionOwnerScan(
            paths
                .OrderBy(path => path, PathComparer)
                .ThenBy(path => path, StringComparer.Ordinal)
                .ToArray(),
            Failure: null);
    }

    private IReadOnlyList<ManagedFileProjection> BuildProjections(
        InteropTarget target,
        IReadOnlyList<StoredInteropFact<ManagedImport>> managedFacts,
        IReadOnlyList<StoredInteropFact<ManagedCallbackUsageProjection>>
            callbackUsageFacts,
        IReadOnlyList<StoredInteropFact<ManagedReturnReleaseProjection>>
            returnReleaseFacts,
        IReadOnlyList<StoredInteropFact<NativeExport>> nativeFacts,
        IReadOnlyList<StoredInteropFact<AbiRecordLayout>> abiRecordFacts,
        IReadOnlyList<StoredInteropFact<InteropMatchProjection>> previousMatches,
        IReadOnlyList<StoredInteropFact<InteropFindingProjection>> previousFindings,
        IReadOnlyList<NativeImplementationLink> nativeImplementations)
    {
        var nativeExports = nativeFacts
            .Select(item => item.Fact)
            .OrderBy(item => item.SymbolCanonicalKey, StringComparer.Ordinal)
            .ToArray();
        var nativeByKey = nativeExports.ToDictionary(
            item => item.SymbolCanonicalKey,
            StringComparer.Ordinal);
        var recordIndex = AbiRecordIndex.Create(abiRecordFacts);
        var callbacksByImport = callbackUsageFacts
            .GroupBy(
                item => item.Fact.ManagedImportSymbolCanonicalKey,
                StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(
                        item => item.Fact.Usage.CallerSymbolCanonicalKey,
                        StringComparer.Ordinal)
                    .ThenBy(item => item.Fact.Usage.ParameterPosition)
                    .ThenBy(item => item.Row.AnnotationId)
                    .ToArray(),
                StringComparer.Ordinal);
        var releasesByImport = returnReleaseFacts
            .GroupBy(
                item => item.Fact.ManagedImportSymbolCanonicalKey,
                StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(
                        item => item.Fact.Release.CallerSymbolCanonicalKey,
                        StringComparer.Ordinal)
                    .ThenBy(item => item.Row.AnnotationId)
                    .ToArray(),
                StringComparer.Ordinal);

        var annotationsByPath =
            new Dictionary<string, List<FileAnnotationFact>>(PathComparer);
        var edgesByPath =
            new Dictionary<string, List<ProducerEdgeEvidenceFact>>(PathComparer);
        var matchCounts = new Dictionary<string, int>(PathComparer);
        var findingCounts = new Dictionary<string, int>(PathComparer);
        var allFilePaths = new HashSet<string>(PathComparer);

        void EnsureFile(string filePath)
        {
            allFilePaths.Add(filePath);
            annotationsByPath.TryAdd(filePath, []);
            edgesByPath.TryAdd(filePath, []);
            matchCounts.TryAdd(filePath, 0);
            findingCounts.TryAdd(filePath, 0);
        }

        foreach (var path in managedFacts.Select(item => item.Row.FilePath)
                     .Concat(previousMatches.Select(item => item.Row.FilePath))
                     .Concat(previousFindings.Select(item => item.Row.FilePath))
                     .Concat(nativeImplementations.Select(item => item.OwnerPath)))
        {
            EnsureFile(path);
        }

        foreach (var implementation in nativeImplementations)
        {
            var metadata = new Dictionary<string, string>(
                StringComparer.Ordinal)
            {
                ["confidence"] = "inferred",
                ["source"] = "syntax-only",
            };
            edgesByPath[implementation.OwnerPath].Add(
                new ProducerEdgeEvidenceFact(
                    implementation.ExportCanonicalKey,
                    implementation.Implementation.CanonicalKey!,
                    EdgeKinds.NativeImplementation,
                    metadata,
                    new FileEvidenceFact(
                        new SourceLocation(
                            implementation.Implementation.FilePath,
                            implementation.Implementation.StartLine,
                            implementation.Implementation.StartCol,
                            implementation.Implementation.EndLine,
                            implementation.Implementation.EndCol),
                        EvidenceConfidence.Inferred,
                        Producer,
                        metadata)));
        }

        foreach (var stored in managedFacts
                     .OrderBy(
                         item => item.Fact.SymbolCanonicalKey,
                         StringComparer.Ordinal))
        {
            ValidateManagedOwnership(stored);
            var managed = stored.Fact;
            var importFilePath = stored.Row.FilePath;
            EnsureFile(importFilePath);
            var match = _matcher.Match(
                managed,
                nativeExports,
                isExportUniverseComplete: true);
            var matchProjection = new InteropMatchProjection(
                match.ManagedSymbolCanonicalKey,
                match.NativeSymbolCanonicalKey,
                match.Status,
                match.Confidence,
                match.Reasons,
                target,
                match.CandidateCount,
                SnapshotComplete: true,
                ProjectEvidence(match.Evidence));
            annotationsByPath[importFilePath].Add(new FileAnnotationFact(
                managed.SymbolCanonicalKey,
                "InteropMatch",
                "MedInterop.InteropMatch",
                InteropAnnotationFlavors.Match,
                InteropFactPayloadCodec.EncodeMatch(matchProjection),
                AttributeCanonicalKey: null));
            matchCounts[importFilePath]++;

            if (match.Status != InteropMatchStatus.Matched)
            {
                continue;
            }
            if (match.NativeSymbolCanonicalKey is null
                || !nativeByKey.TryGetValue(
                    match.NativeSymbolCanonicalKey,
                    out var native))
            {
                throw new InvalidOperationException(
                    "A matched result did not resolve to one stored native export.");
            }

            var metadata = new Dictionary<string, string>(
                StringComparer.Ordinal)
            {
                ["runtimeIdentifier"] = target.RuntimeIdentifier,
                ["status"] = "matched",
                ["confidence"] = ConfidenceToken(match.Confidence),
            };
            edgesByPath[importFilePath].Add(new ProducerEdgeEvidenceFact(
                managed.SymbolCanonicalKey,
                native.SymbolCanonicalKey,
                EdgeKinds.PInvokeMapsTo,
                metadata,
                new FileEvidenceFact(
                    managed.Evidence.Location,
                    match.Confidence,
                    Producer,
                    metadata)));

            foreach (var mapping in FindRecordMappings(
                         managed,
                         native,
                         recordIndex,
                         match.Confidence))
            {
                edgesByPath[importFilePath].Add(
                    new ProducerEdgeEvidenceFact(
                        mapping.Managed.SymbolCanonicalKey,
                        mapping.Native.SymbolCanonicalKey,
                        EdgeKinds.StructMapsTo,
                        mapping.Metadata,
                        new FileEvidenceFact(
                            mapping.Location,
                            mapping.Confidence,
                            Producer,
                            mapping.Metadata)));
            }

            callbacksByImport.TryGetValue(
                managed.SymbolCanonicalKey,
                out var callbackRows);
            releasesByImport.TryGetValue(
                managed.SymbolCanonicalKey,
                out var releaseRows);
            var boundary = new InteropBoundary(managed, native)
            {
                CallbackUsages = (callbackRows ?? [])
                    .Select(item => item.Fact.Usage)
                    .ToArray(),
                ReturnReleases = (releaseRows ?? [])
                    .Select(item => item.Fact.Release)
                    .ToArray(),
            };
            var findings = _rules.Evaluate(boundary)
                .Where(finding => finding.RuleId
                    is InteropRuleIds.CallingConvention
                        or InteropRuleIds.ParameterTypeRisk
                        or InteropRuleIds.CallbackGcRisk
                        or InteropRuleIds.NativeException
                        or InteropRuleIds.AllocatorMismatch)
                .OrderBy(finding => finding.RuleId, StringComparer.Ordinal)
                .ThenBy(finding => finding.Message, StringComparer.Ordinal)
                .ToArray();
            foreach (var finding in findings)
            {
                if (string.IsNullOrWhiteSpace(
                        finding.ManagedSymbolCanonicalKey)
                    || !string.Equals(
                        finding.NativeSymbolCanonicalKey,
                        native.SymbolCanonicalKey,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "A boundary finding is not owned by its matched import/export.");
                }

                var findingOwnerPath = FindFindingOwnerPath(
                    finding,
                    stored,
                    callbackRows ?? [],
                    releaseRows ?? []);
                EnsureFile(findingOwnerPath);
                var findingProjection = new InteropFindingProjection(
                    finding.RuleId,
                    finding.Severity,
                    finding.Message,
                    finding.ManagedSymbolCanonicalKey,
                    native.SymbolCanonicalKey,
                    target,
                    finding.Confidence,
                    ProjectEvidence(finding.Evidence))
                {
                    BoundaryManagedSymbolCanonicalKey =
                        managed.SymbolCanonicalKey,
                };
                annotationsByPath[findingOwnerPath].Add(
                    new FileAnnotationFact(
                        finding.ManagedSymbolCanonicalKey,
                        finding.RuleId,
                        $"MedInterop.{finding.RuleId}",
                        InteropAnnotationFlavors.Finding,
                        InteropFactPayloadCodec.EncodeFinding(
                            findingProjection),
                        AttributeCanonicalKey: null));
                findingCounts[findingOwnerPath]++;
            }
        }

        return allFilePaths
            .OrderBy(path => path, PathComparer)
            .ThenBy(path => path, StringComparer.Ordinal)
            .Select(filePath => new ManagedFileProjection(
                filePath,
                annotationsByPath[filePath]
                    .OrderBy(
                        item => item.SymbolCanonicalKey,
                        StringComparer.Ordinal)
                    .ThenBy(item => item.Flavor, StringComparer.Ordinal)
                    .ThenBy(item => item.Name, StringComparer.Ordinal)
                    .ThenBy(item => item.ArgsJson, StringComparer.Ordinal)
                    .ToArray(),
                edgesByPath[filePath]
                    .OrderBy(
                        item => item.SourceCanonicalKey,
                        StringComparer.Ordinal)
                    .ThenBy(
                        item => item.TargetCanonicalKey,
                        StringComparer.Ordinal)
                    .ToArray(),
                matchCounts[filePath],
                findingCounts[filePath]))
            .ToArray();
    }

    private async Task<IReadOnlyList<NativeImplementationLink>>
        FindNativeImplementationsAsync(
            IReadOnlyList<StoredInteropFact<NativeExport>> nativeFacts,
            CancellationToken cancellationToken)
    {
        const int maximumCandidatesPerExport = 100;
        var links = new List<NativeImplementationLink>(nativeFacts.Count);
        foreach (var stored in nativeFacts)
        {
            var candidates = await _store.FindSymbolsAsync(
                    stored.Fact.ExportName,
                    filePathHint: null,
                    maximumCandidatesPerExport,
                    cancellationToken)
                .ConfigureAwait(false);
            var implementations = candidates
                .Where(candidate =>
                    string.Equals(
                        candidate.Name,
                        stored.Fact.ExportName,
                        StringComparison.Ordinal)
                    && string.Equals(
                        candidate.Kind,
                        DevBitsLab.Mcp.SourceGraph.Sdk.SymbolKinds.Function,
                        StringComparison.Ordinal)
                    && HasModifier(candidate.Modifiers, "syntax-only")
                    && IsImplementationSource(candidate.FilePath)
                    && candidate.CanonicalKey is not null)
                .DistinctBy(candidate => candidate.CanonicalKey)
                .ToArray();
            if (implementations.Length == 1
                && candidates.Count < maximumCandidatesPerExport)
            {
                links.Add(new NativeImplementationLink(
                    stored.Fact.SymbolCanonicalKey,
                    implementations[0].FilePath,
                    implementations[0]));
            }
        }
        return links;
    }

    private static bool HasModifier(string? modifiers, string expected) =>
        modifiers?.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries
                    | StringSplitOptions.TrimEntries)
            .Contains(expected, StringComparer.Ordinal) == true;

    private static bool IsImplementationSource(string path) =>
        Path.GetExtension(path).ToLowerInvariant()
            is ".c" or ".cc" or ".cpp" or ".cxx" or ".c++";

    private static List<InteropAnalysisPublicationFailure> LoadFailures(
        StoredInteropFactSnapshot<ManagedImport> managed,
        StoredInteropFactSnapshot<ManagedCallbackUsageProjection>
            callbackUsages,
        StoredInteropFactSnapshot<ManagedReturnReleaseProjection>
            returnReleases,
        StoredInteropFactSnapshot<NativeExport> native,
        StoredInteropFactSnapshot<AbiRecordLayout> abiRecords,
        StoredInteropFactSnapshot<InteropMatchProjection> matches,
        StoredInteropFactSnapshot<InteropFindingProjection> findings)
    {
        var failures = new List<InteropAnalysisPublicationFailure>();
        AddLoadFailures(failures, "managed-imports", managed.Failures);
        AddLoadFailures(
            failures,
            "managed-callback-usages",
            callbackUsages.Failures);
        AddLoadFailures(
            failures,
            "managed-return-releases",
            returnReleases.Failures);
        AddLoadFailures(failures, "native-exports", native.Failures);
        AddLoadFailures(failures, "abi-records", abiRecords.Failures);
        AddLoadFailures(failures, "previous-matches", matches.Failures);
        AddLoadFailures(failures, "previous-findings", findings.Failures);
        return failures;
    }

    private static void AddLoadFailures(
        ICollection<InteropAnalysisPublicationFailure> destination,
        string stage,
        IReadOnlyList<InteropFactLoadFailure> failures)
    {
        foreach (var failure in failures)
        {
            destination.Add(new InteropAnalysisPublicationFailure(
                string.IsNullOrEmpty(failure.FilePath)
                    ? null
                    : failure.FilePath,
                stage,
                failure.Reason));
        }
    }

    private static List<InteropAnalysisPublicationFailure> ValidateTargets(
        InteropTarget target,
        IReadOnlyList<StoredInteropFact<ManagedImport>> managed,
        IReadOnlyList<StoredInteropFact<ManagedCallbackUsageProjection>>
            callbackUsages,
        IReadOnlyList<StoredInteropFact<ManagedReturnReleaseProjection>>
            returnReleases,
        IReadOnlyList<StoredInteropFact<NativeExport>> native,
        IReadOnlyList<StoredInteropFact<AbiRecordLayout>> abiRecords)
    {
        var failures = new List<InteropAnalysisPublicationFailure>();
        var managedByKey = managed.ToDictionary(
            item => item.Fact.SymbolCanonicalKey,
            StringComparer.Ordinal);
        foreach (var item in managed)
        {
            if (!item.Fact.Target.IsAbiEquivalentTo(target))
            {
                failures.Add(new InteropAnalysisPublicationFailure(
                    item.Row.FilePath,
                "target-validation",
                "A managed import does not match the configured interop target."));
            }
        }
        foreach (var item in callbackUsages)
        {
            var usage = item.Fact.Usage;
            if (!PathsEquivalent(
                    item.Row.FilePath,
                    usage.Evidence.Location.FilePath))
            {
                failures.Add(new InteropAnalysisPublicationFailure(
                    item.Row.FilePath,
                    "usage-validation",
                    "A managed callback usage is not owned by its annotation file."));
            }
            if (!managedByKey.TryGetValue(
                    item.Fact.ManagedImportSymbolCanonicalKey,
                    out var import))
            {
                failures.Add(new InteropAnalysisPublicationFailure(
                    item.Row.FilePath,
                    "usage-validation",
                    "A managed callback usage targets no current managed import."));
                continue;
            }
            if (!usage.Target.IsAbiEquivalentTo(target)
                || !usage.Target.IsAbiEquivalentTo(import.Fact.Target))
            {
                failures.Add(new InteropAnalysisPublicationFailure(
                    item.Row.FilePath,
                    "target-validation",
                    "A managed callback usage does not match its import target."));
            }
            if (usage.Rooting == CallbackGcRooting.Unknown
                || !import.Fact.Parameters.Any(parameter =>
                    parameter.Position == usage.ParameterPosition
                    && parameter.Type.Category
                        == AbiTypeCategory.FunctionPointer))
            {
                failures.Add(new InteropAnalysisPublicationFailure(
                    item.Row.FilePath,
                    "usage-validation",
                    "A managed callback usage has no matching callback parameter."));
            }
        }
        foreach (var item in returnReleases)
        {
            var release = item.Fact.Release;
            if (!PathsEquivalent(
                    item.Row.FilePath,
                    release.Evidence.Location.FilePath))
            {
                failures.Add(new InteropAnalysisPublicationFailure(
                    item.Row.FilePath,
                    "usage-validation",
                    "A managed return release is not owned by its annotation file."));
            }
            if (!managedByKey.TryGetValue(
                    item.Fact.ManagedImportSymbolCanonicalKey,
                    out var import))
            {
                failures.Add(new InteropAnalysisPublicationFailure(
                    item.Row.FilePath,
                    "usage-validation",
                    "A managed return release targets no current managed import."));
                continue;
            }
            if (release.ReleaseFamily == InteropAllocatorFamily.Unknown)
            {
                failures.Add(new InteropAnalysisPublicationFailure(
                    item.Row.FilePath,
                    "usage-validation",
                    "A managed return release has no proven allocator family."));
            }
            if (!release.Target.IsAbiEquivalentTo(target)
                || !release.Target.IsAbiEquivalentTo(import.Fact.Target))
            {
                failures.Add(new InteropAnalysisPublicationFailure(
                    item.Row.FilePath,
                    "target-validation",
                    "A managed return release does not match its import target."));
            }
        }
        foreach (var item in native)
        {
            if (!item.Fact.Target.IsAbiEquivalentTo(target))
            {
                failures.Add(new InteropAnalysisPublicationFailure(
                    item.Row.FilePath,
                    "target-validation",
                "A native export does not match the configured interop target."));
            }
        }
        foreach (var item in abiRecords)
        {
            if (!item.Fact.Target.IsAbiEquivalentTo(target))
            {
                failures.Add(new InteropAnalysisPublicationFailure(
                    item.Row.FilePath,
                    "target-validation",
                    "An ABI record does not match the configured interop target."));
            }
        }
        return failures;
    }

    private static IReadOnlyList<ProvenRecordMapping> FindRecordMappings(
        ManagedImport managed,
        NativeExport native,
        AbiRecordIndex records,
        EvidenceConfidence boundaryConfidence)
    {
        var mappings = new List<ProvenRecordMapping>();
        var seen = new HashSet<(string Managed, string Native)>();

        Add(
            managed.ReturnType,
            native.ReturnType,
            managed.Evidence.Location,
            "return");

        var nativeParameters = native.Parameters.ToDictionary(
            parameter => parameter.Position);
        foreach (var managedParameter in managed.Parameters
                     .OrderBy(parameter => parameter.Position))
        {
            if (!nativeParameters.TryGetValue(
                    managedParameter.Position,
                    out var nativeParameter))
            {
                continue;
            }

            Add(
                managedParameter.Type,
                nativeParameter.Type,
                managedParameter.Location,
                $"parameter:{managedParameter.Position}");
        }

        return mappings;

        void Add(
            AbiTypeRef managedType,
            AbiTypeRef nativeType,
            SourceLocation location,
            string position)
        {
            var managedRecordType = FindRecordType(managedType);
            var nativeRecordType = FindRecordType(nativeType);
            if (managedRecordType is null
                || nativeRecordType is null
                || !records.TryResolveManaged(
                    managedRecordType.CanonicalName,
                    out var managedRecord)
                || !records.TryResolveNative(
                    nativeRecordType.CanonicalName,
                    out var nativeRecord)
                || !seen.Add((
                    managedRecord.SymbolCanonicalKey,
                    nativeRecord.SymbolCanonicalKey)))
            {
                return;
            }

            if (!PathsEquivalent(
                    location.FilePath,
                    managed.Evidence.Location.FilePath))
            {
                throw new InvalidOperationException(
                    "A managed record-boundary location is not owned by its import file.");
            }

            var confidence = (EvidenceConfidence)Math.Min(
                (int)boundaryConfidence,
                Math.Min(
                    (int)managedRecord.Evidence.Confidence,
                    (int)nativeRecord.Evidence.Confidence));
            var compatibility =
                new AbiStructCompatibilityEngine().Compare(
                    managedRecord,
                    nativeRecord);
            var metadata = new Dictionary<string, string>(
                StringComparer.Ordinal)
            {
                ["boundaryManagedSymbol"] = managed.SymbolCanonicalKey,
                ["boundaryNativeSymbol"] = native.SymbolCanonicalKey,
                ["compatibility"] =
                    CompatibilityToken(compatibility.Compatibility),
                ["confidence"] = ConfidenceToken(confidence),
                ["managedType"] = managedRecordType.CanonicalName,
                ["nativeType"] = nativeRecordType.CanonicalName,
                ["position"] = position,
                ["runtimeIdentifier"] =
                    managed.Target.RuntimeIdentifier,
                ["status"] = "matched",
            };
            mappings.Add(new ProvenRecordMapping(
                managedRecord,
                nativeRecord,
                location,
                confidence,
                metadata));
        }
    }

    private static AbiTypeRef? FindRecordType(
        AbiTypeRef type,
        int depth = 0)
    {
        if (depth > 8)
        {
            return null;
        }
        if (type.Category == AbiTypeCategory.Record)
        {
            return type;
        }
        if (type.Category == AbiTypeCategory.Pointer
            && type.PointeeType is not null)
        {
            return FindRecordType(type.PointeeType, depth + 1);
        }
        return null;
    }

    private static string FindFindingOwnerPath(
        InteropFinding finding,
        StoredInteropFact<ManagedImport> managed,
        IReadOnlyList<StoredInteropFact<ManagedCallbackUsageProjection>>
            callbackUsages,
        IReadOnlyList<StoredInteropFact<ManagedReturnReleaseProjection>>
            returnReleases)
    {
        var managedKey = finding.ManagedSymbolCanonicalKey
            ?? throw new InvalidOperationException(
                "An interop finding has no managed symbol.");
        if (string.Equals(
                managedKey,
                managed.Fact.SymbolCanonicalKey,
                StringComparison.Ordinal))
        {
            return managed.Row.FilePath;
        }

        IEnumerable<string> candidates = finding.RuleId switch
        {
            InteropRuleIds.CallbackGcRisk => callbackUsages
                .Where(item => string.Equals(
                    item.Fact.Usage.CallerSymbolCanonicalKey,
                    managedKey,
                    StringComparison.Ordinal))
                .Select(item => item.Row.FilePath),
            InteropRuleIds.AllocatorMismatch => returnReleases
                .Where(item => string.Equals(
                    item.Fact.Release.CallerSymbolCanonicalKey,
                    managedKey,
                    StringComparison.Ordinal))
                .Select(item => item.Row.FilePath),
            _ => [],
        };
        var paths = candidates
            .Distinct(PathComparer)
            .ToArray();
        if (paths.Length != 1)
        {
            throw new InvalidOperationException(
                $"Finding {finding.RuleId} has no unique caller-owned usage fact.");
        }
        return paths[0];
    }

    private static void ValidateManagedOwnership(
        StoredInteropFact<ManagedImport> stored)
    {
        if (!PathsEquivalent(
                stored.Row.FilePath,
                stored.Fact.Evidence.Location.FilePath))
        {
            throw new InvalidOperationException(
                "A managed import's declaration evidence is not owned by its annotation file.");
        }
    }

    private static IReadOnlyList<InteropEvidenceProjection> ProjectEvidence(
        IReadOnlyList<Evidence> evidence) =>
        evidence
            .Select(item => new InteropEvidenceProjection(
                item.Location,
                item.Confidence,
                item.Producer,
                item.Metadata))
            .ToArray();

    private static InteropAnalysisPublicationResult Failed(
        IReadOnlyList<InteropAnalysisPublicationFailure> failures) =>
        new(
            IsComplete: false,
            FilesPublished: 0,
            MatchesPublished: 0,
            FindingsPublished: 0,
            EdgesPublished: 0,
            OrderFailures(failures));

    private static IReadOnlyList<InteropAnalysisPublicationFailure>
        OrderFailures(
            IEnumerable<InteropAnalysisPublicationFailure> failures) =>
        failures
            .Distinct()
            .OrderBy(item => item.FilePath, PathComparer)
            .ThenBy(item => item.Stage, StringComparer.Ordinal)
            .ThenBy(item => item.Message, StringComparer.Ordinal)
            .ToArray();

    private static string ConfidenceToken(EvidenceConfidence confidence) =>
        confidence switch
        {
            EvidenceConfidence.Inferred => "inferred",
            EvidenceConfidence.Semantic => "semantic",
            EvidenceConfidence.Exact => "exact",
            _ => throw new InvalidOperationException(
                $"Unsupported evidence confidence `{confidence}`."),
        };

    private static string CompatibilityToken(
        InteropCompatibility compatibility) =>
        compatibility switch
        {
            InteropCompatibility.Unknown => "unknown",
            InteropCompatibility.Compatible => "compatible",
            InteropCompatibility.Warning => "warning",
            InteropCompatibility.Error => "error",
            _ => throw new InvalidOperationException(
                $"Unsupported interop compatibility `{compatibility}`."),
        };

    private static bool PathsEquivalent(string left, string right)
    {
        try
        {
            return PathComparer.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right));
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or IOException
                or NotSupportedException
                or PathTooLongException)
        {
            return false;
        }
    }

    private static string BoundedMessage(Exception exception)
    {
        const int maximumCharacters = 512;
        var message = $"{exception.GetType().Name}: {exception.Message}";
        return message.Length <= maximumCharacters
            ? message
            : message[..maximumCharacters];
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private sealed record ManagedFileProjection(
        string FilePath,
        IReadOnlyList<FileAnnotationFact> Annotations,
        IReadOnlyList<ProducerEdgeEvidenceFact> Edges,
        int MatchCount,
        int FindingCount);

    private sealed record NativeImplementationLink(
        string ExportCanonicalKey,
        string OwnerPath,
        SymbolHit Implementation);

    private sealed record ProvenRecordMapping(
        AbiRecordLayout Managed,
        AbiRecordLayout Native,
        SourceLocation Location,
        EvidenceConfidence Confidence,
        IReadOnlyDictionary<string, string> Metadata);

    private sealed class AbiRecordIndex
    {
        private readonly IReadOnlyDictionary<
            string,
            IReadOnlyList<AbiRecordLayout>> _managed;
        private readonly IReadOnlyDictionary<
            string,
            IReadOnlyList<AbiRecordLayout>> _native;

        private AbiRecordIndex(
            IReadOnlyDictionary<string, IReadOnlyList<AbiRecordLayout>>
                managed,
            IReadOnlyDictionary<string, IReadOnlyList<AbiRecordLayout>>
                native)
        {
            _managed = managed;
            _native = native;
        }

        public static AbiRecordIndex Create(
            IReadOnlyList<StoredInteropFact<AbiRecordLayout>> facts)
        {
            var managed = new Dictionary<
                string,
                List<AbiRecordLayout>>(StringComparer.Ordinal);
            var native = new Dictionary<
                string,
                List<AbiRecordLayout>>(StringComparer.Ordinal);

            foreach (var stored in facts)
            {
                var record = stored.Fact;
                if (TryManagedIdentity(
                        record.SymbolCanonicalKey,
                        out var managedIdentity))
                {
                    if (record.Kind is not (
                            AbiRecordKind.Sequential
                                or AbiRecordKind.Explicit))
                    {
                        throw new InvalidOperationException(
                            "A managed ABI record has an invalid record kind.");
                    }
                    Add(managed, managedIdentity, record);
                    continue;
                }

                if (TryNativeIdentity(
                        record.SymbolCanonicalKey,
                        out var nativeIdentity))
                {
                    if (record.Kind != AbiRecordKind.Native)
                    {
                        throw new InvalidOperationException(
                            "A native ABI record has an invalid record kind.");
                    }
                    Add(native, nativeIdentity, record);
                    continue;
                }

                throw new InvalidOperationException(
                    "An ABI record has no supported canonical type identity.");
            }

            return new AbiRecordIndex(
                Freeze(managed),
                Freeze(native));
        }

        public bool TryResolveManaged(
            string canonicalTypeName,
            out AbiRecordLayout record) =>
            TryResolve(_managed, canonicalTypeName, out record);

        public bool TryResolveNative(
            string canonicalTypeName,
            out AbiRecordLayout record) =>
            TryResolve(_native, canonicalTypeName, out record);

        private static bool TryResolve(
            IReadOnlyDictionary<string, IReadOnlyList<AbiRecordLayout>>
                index,
            string canonicalTypeName,
            out AbiRecordLayout record)
        {
            if (index.TryGetValue(canonicalTypeName, out var candidates)
                && candidates.Count == 1)
            {
                record = candidates[0];
                return true;
            }

            record = null!;
            return false;
        }

        private static bool TryManagedIdentity(
            string canonicalKey,
            out string identity)
        {
            const string prefix = "csharp:T:";
            if (canonicalKey.StartsWith(prefix, StringComparison.Ordinal)
                && canonicalKey.Length > prefix.Length)
            {
                identity = canonicalKey[prefix.Length..];
                return true;
            }

            identity = string.Empty;
            return false;
        }

        private static bool TryNativeIdentity(
            string canonicalKey,
            out string identity)
        {
            var prefixLength = canonicalKey.StartsWith(
                    "c:T:",
                    StringComparison.Ordinal)
                ? "c:T:".Length
                : canonicalKey.StartsWith(
                    "cpp:T:",
                    StringComparison.Ordinal)
                    ? "cpp:T:".Length
                    : -1;
            if (prefixLength >= 0)
            {
                var separator = canonicalKey.IndexOf(
                    "::",
                    prefixLength,
                    StringComparison.Ordinal);
                if (separator > prefixLength
                    && separator + 2 < canonicalKey.Length)
                {
                    identity = canonicalKey[(separator + 2)..];
                    return true;
                }
            }

            identity = string.Empty;
            return false;
        }

        private static void Add(
            IDictionary<string, List<AbiRecordLayout>> index,
            string identity,
            AbiRecordLayout record)
        {
            if (!index.TryGetValue(identity, out var candidates))
            {
                candidates = [];
                index.Add(identity, candidates);
            }
            candidates.Add(record);
        }

        private static IReadOnlyDictionary<
            string,
            IReadOnlyList<AbiRecordLayout>> Freeze(
                IReadOnlyDictionary<string, List<AbiRecordLayout>> source) =>
            source.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<AbiRecordLayout>)pair.Value
                    .OrderBy(
                        record => record.SymbolCanonicalKey,
                        StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal);
    }

    private sealed record ProjectionOwnerScan(
        IReadOnlyList<string> FilePaths,
        InteropAnalysisPublicationFailure? Failure);
}
