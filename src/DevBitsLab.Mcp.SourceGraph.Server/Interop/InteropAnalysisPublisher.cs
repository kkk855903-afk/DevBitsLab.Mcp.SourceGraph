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
    internal const string Producer = "interop-analysis";
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
            var native = await InteropFactStoreReader.ReadNativeExportsAsync(
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
                native,
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
                native.Facts);
            if (targetFailures.Count > 0)
            {
                return Failed(targetFailures);
            }

            IReadOnlyList<ManagedFileProjection> projections;
            try
            {
                projections = BuildProjections(
                    target,
                    managed.Facts,
                    native.Facts,
                    previousMatches.Facts,
                    previousFindings.Facts);
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

            var failures = new List<InteropAnalysisPublicationFailure>();
            var filesPublished = 0;
            var matchesPublished = 0;
            var findingsPublished = 0;
            var edgesPublished = 0;
            foreach (var projection in projections)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await _store.ReplaceFileDerivedProjectionAsync(
                            projection.FilePath,
                            Producer,
                            _annotationFlavors,
                            projection.Annotations,
                            projection.Edges,
                            cancellationToken)
                        .ConfigureAwait(false);
                    filesPublished++;
                    matchesPublished += projection.MatchCount;
                    findingsPublished += projection.FindingCount;
                    edgesPublished += projection.Edges.Count;
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
                    failures.Add(new InteropAnalysisPublicationFailure(
                        projection.FilePath,
                        "storage",
                        BoundedMessage(ex)));
                }
            }

            return new InteropAnalysisPublicationResult(
                failures.Count == 0,
                filesPublished,
                matchesPublished,
                findingsPublished,
                edgesPublished,
                OrderFailures(failures));
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

            var failures = new List<InteropAnalysisPublicationFailure>();
            var filesPublished = 0;
            foreach (var filePath in ownerScan.FilePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await _store.ReplaceFileDerivedProjectionAsync(
                            filePath,
                            Producer,
                            _annotationFlavors,
                            annotations: [],
                            edges: [],
                            cancellationToken)
                        .ConfigureAwait(false);
                    filesPublished++;
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
                    failures.Add(new InteropAnalysisPublicationFailure(
                        filePath,
                        "clear-storage",
                        BoundedMessage(ex)));
                }
            }

            return new InteropAnalysisPublicationResult(
                failures.Count == 0,
                filesPublished,
                MatchesPublished: 0,
                FindingsPublished: 0,
                EdgesPublished: 0,
                OrderFailures(failures));
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
        IReadOnlyList<StoredInteropFact<NativeExport>> nativeFacts,
        IReadOnlyList<StoredInteropFact<InteropMatchProjection>> previousMatches,
        IReadOnlyList<StoredInteropFact<InteropFindingProjection>> previousFindings)
    {
        var nativeExports = nativeFacts
            .Select(item => item.Fact)
            .OrderBy(item => item.SymbolCanonicalKey, StringComparer.Ordinal)
            .ToArray();
        var nativeByKey = nativeExports.ToDictionary(
            item => item.SymbolCanonicalKey,
            StringComparer.Ordinal);
        var managedByPath = managedFacts
            .GroupBy(item => item.Row.FilePath, PathComparer)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(
                        item => item.Fact.SymbolCanonicalKey,
                        StringComparer.Ordinal)
                    .ToArray(),
                PathComparer);

        var allFilePaths = managedByPath.Keys
            .Concat(previousMatches.Select(item => item.Row.FilePath))
            .Concat(previousFindings.Select(item => item.Row.FilePath))
            .Distinct(PathComparer)
            .OrderBy(path => path, PathComparer)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var projections = new List<ManagedFileProjection>(allFilePaths.Length);
        foreach (var filePath in allFilePaths)
        {
            managedByPath.TryGetValue(filePath, out var fileFacts);
            var annotations = new List<FileAnnotationFact>();
            var edges = new List<ProducerEdgeEvidenceFact>();
            var matchCount = 0;
            var findingCount = 0;
            foreach (var stored in fileFacts ?? [])
            {
                ValidateManagedOwnership(stored);
                var managed = stored.Fact;
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
                annotations.Add(new FileAnnotationFact(
                    managed.SymbolCanonicalKey,
                    "InteropMatch",
                    "MedInterop.InteropMatch",
                    InteropAnnotationFlavors.Match,
                    InteropFactPayloadCodec.EncodeMatch(matchProjection),
                    AttributeCanonicalKey: null));
                matchCount++;

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
                edges.Add(new ProducerEdgeEvidenceFact(
                    managed.SymbolCanonicalKey,
                    native.SymbolCanonicalKey,
                    EdgeKinds.PInvokeMapsTo,
                    metadata,
                    new FileEvidenceFact(
                        managed.Evidence.Location,
                        match.Confidence,
                        Producer,
                        metadata)));

                var findings = _rules.Evaluate(
                        new InteropBoundary(managed, native))
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
                    if (!string.Equals(
                            finding.ManagedSymbolCanonicalKey,
                            managed.SymbolCanonicalKey,
                            StringComparison.Ordinal)
                        || !string.Equals(
                            finding.NativeSymbolCanonicalKey,
                            native.SymbolCanonicalKey,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "A boundary finding is not owned by its matched import/export.");
                    }

                    var findingProjection = new InteropFindingProjection(
                        finding.RuleId,
                        finding.Severity,
                        finding.Message,
                        managed.SymbolCanonicalKey,
                        native.SymbolCanonicalKey,
                        target,
                        finding.Confidence,
                        ProjectEvidence(finding.Evidence));
                    annotations.Add(new FileAnnotationFact(
                        managed.SymbolCanonicalKey,
                        finding.RuleId,
                        $"MedInterop.{finding.RuleId}",
                        InteropAnnotationFlavors.Finding,
                        InteropFactPayloadCodec.EncodeFinding(findingProjection),
                        AttributeCanonicalKey: null));
                    findingCount++;
                }
            }

            projections.Add(new ManagedFileProjection(
                filePath,
                annotations
                    .OrderBy(item => item.SymbolCanonicalKey, StringComparer.Ordinal)
                    .ThenBy(item => item.Flavor, StringComparer.Ordinal)
                    .ThenBy(item => item.Name, StringComparer.Ordinal)
                    .ThenBy(item => item.ArgsJson, StringComparer.Ordinal)
                    .ToArray(),
                edges
                    .OrderBy(item => item.SourceCanonicalKey, StringComparer.Ordinal)
                    .ThenBy(item => item.TargetCanonicalKey, StringComparer.Ordinal)
                    .ToArray(),
                matchCount,
                findingCount));
        }

        return projections;
    }

    private static List<InteropAnalysisPublicationFailure> LoadFailures(
        StoredInteropFactSnapshot<ManagedImport> managed,
        StoredInteropFactSnapshot<NativeExport> native,
        StoredInteropFactSnapshot<InteropMatchProjection> matches,
        StoredInteropFactSnapshot<InteropFindingProjection> findings)
    {
        var failures = new List<InteropAnalysisPublicationFailure>();
        AddLoadFailures(failures, "managed-imports", managed.Failures);
        AddLoadFailures(failures, "native-exports", native.Failures);
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
        IReadOnlyList<StoredInteropFact<NativeExport>> native)
    {
        var failures = new List<InteropAnalysisPublicationFailure>();
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
        return failures;
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

    private sealed record ProjectionOwnerScan(
        IReadOnlyList<string> FilePaths,
        InteropAnalysisPublicationFailure? Failure);
}
