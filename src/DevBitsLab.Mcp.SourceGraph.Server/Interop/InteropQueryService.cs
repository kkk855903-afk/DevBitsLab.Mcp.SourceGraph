using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Interop;
using DevBitsLab.Mcp.SourceGraph.Storage;

namespace DevBitsLab.Mcp.SourceGraph.Server.Interop;

/// <summary>
/// Reads one scope's persisted interop projection and produces an uncertainty-preserving query
/// block shared by <c>match_pinvoke</c> and <c>analyze_native_boundary</c>.
/// </summary>
internal sealed class InteropQueryService
{
    private const int MaximumSearchHits = 10_000;
    private const int MaximumQueryCharacters = 4096;

    private static readonly HashSet<string> Phase2RuleIds =
        new(StringComparer.Ordinal)
        {
            InteropRuleIds.CallingConvention,
            InteropRuleIds.ParameterTypeRisk,
            InteropRuleIds.CallbackGcRisk,
            InteropRuleIds.NativeException,
            InteropRuleIds.AllocatorMismatch,
        };

    public async Task<BoundedInteropScopeQuery> QueryAsync(
        string scopeId,
        IGraphStore store,
        NativeInteropRuntimeState runtimeState,
        string symbolQuery,
        InteropQuerySelectionMode selectionMode,
        bool includeFindings,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(runtimeState);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolQuery);
        if (!Enum.IsDefined(selectionMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(selectionMode),
                selectionMode,
                "Unknown interop query selection mode.");
        }

        var query = symbolQuery.Trim();
        if (query.Length > MaximumQueryCharacters)
        {
            throw new ArgumentOutOfRangeException(
                nameof(symbolQuery),
                query.Length,
                $"Interop symbol queries are limited to {MaximumQueryCharacters} characters.");
        }

        // These reads intentionally remain sequential. A scope store owns one SQLite connection,
        // and concurrent commands against it would turn a read-only query into provider-specific
        // behavior.
        var managed = await InteropFactStoreReader.ReadManagedImportsAsync(
                store,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var native = await InteropFactStoreReader.ReadNativeExportsAsync(
                store,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var matches = await InteropFactStoreReader.ReadMatchesAsync(
                store,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        StoredInteropFactSnapshot<InteropFindingProjection>? findings = null;
        if (includeFindings)
        {
            findings = await InteropFactStoreReader.ReadFindingsAsync(
                    store,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        var failures = RuntimeFailures(runtimeState)
            .Concat(FactFailures(managed.Failures))
            .Concat(FactFailures(native.Failures))
            .Concat(FactFailures(matches.Failures))
            .Concat(findings is null
                ? []
                : FactFailures(findings.Failures))
            .OrderBy(failure => failure.Stage, StringComparer.Ordinal)
            .ThenBy(failure => failure.Code, StringComparer.Ordinal)
            .ThenBy(failure => failure.ConfiguredPath ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(failure => failure.TranslationUnitIndex ?? int.MaxValue)
            .ThenBy(failure => failure.Message, StringComparer.Ordinal)
            .ToList();

        var factsComplete = managed.IsComplete
            && native.IsComplete
            && matches.IsComplete
            && (findings?.IsComplete ?? true);
        var runtimeComplete =
            runtimeState.Status == NativeInteropRuntimeStatus.Complete
            && runtimeState.IsExportUniverseComplete;
        var currentProjectionAvailable = runtimeComplete && factsComplete;

        if (runtimeState.Status != NativeInteropRuntimeStatus.Complete)
        {
            failures.Add(new InteropQueryFailureRow(
                "runtime",
                "native-state-not-current",
                $"Native interop state is {RuntimeStatusToken(runtimeState.Status)}; "
                + "no complete current projection is available."));
        }
        else if (!runtimeState.IsExportUniverseComplete)
        {
            failures.Add(new InteropQueryFailureRow(
                "runtime",
                "export-universe-incomplete",
                "The native export universe is incomplete."));
        }

        var selection = await SelectAsync(
                store,
                query,
                selectionMode,
                managed.Facts,
                native.Facts,
                cancellationToken)
            .ConfigureAwait(false);
        if (selectionMode == InteropQuerySelectionMode.ManagedOrNativeBoundary)
        {
            selection = CoalesceOneToOneBoundarySelection(
                selection,
                matches.Facts);
        }
        if (selection.Failure is not null)
        {
            failures.Add(selection.Failure);
            currentProjectionAvailable = false;
        }

        if (selection.Status == SelectionStatus.NotFound)
        {
            var absenceProven = currentProjectionAvailable;
            var result = EmptyResult(
                scopeId,
                query,
                runtimeState,
                status: absenceProven ? "not_found" : "partial",
                selectionStatus: absenceProven ? "not_found" : "unknown",
                partial: !absenceProven,
                selection.Candidates,
                failures);
            return InteropQueryBudget.Apply(result);
        }

        if (selection.Status == SelectionStatus.Unknown)
        {
            var result = EmptyResult(
                scopeId,
                query,
                runtimeState,
                status: "partial",
                selectionStatus: "unknown",
                partial: true,
                selection.Candidates,
                failures);
            return InteropQueryBudget.Apply(result);
        }

        if (selection.Status == SelectionStatus.Ambiguous)
        {
            var result = EmptyResult(
                scopeId,
                query,
                runtimeState,
                status: "ambiguous_selection",
                selectionStatus: "ambiguous",
                partial: !currentProjectionAvailable,
                selection.Candidates,
                failures);
            return InteropQueryBudget.Apply(result);
        }

        var selected = selection.Selected
            ?? throw new InvalidOperationException(
                "A selected interop query has no selected symbol.");

        if (!currentProjectionAvailable)
        {
            if (selected.Native is not null)
            {
                failures.Add(new InteropQueryFailureRow(
                    "selection",
                    "native-selection-not-current",
                    "Persisted native symbols may be retained from the last-good snapshot; "
                    + "no native selection is current while the scope is partial."));
                var unavailable = EmptyResult(
                    scopeId,
                    query,
                    runtimeState,
                    status: "partial",
                    selectionStatus: "unknown",
                    partial: true,
                    candidates: [],
                    failures);
                return InteropQueryBudget.Apply(unavailable);
            }

            InteropQueryMatchRow[] partialMatches =
            [
                UnknownMatch(
                    selected.Managed!.Fact,
                    runtimeState,
                    failures),
            ];
            var result = Result(
                scopeId,
                query,
                runtimeState,
                status: "partial",
                selectionStatus: "selected",
                partial: true,
                selection.Candidates,
                partialMatches,
                [],
                failures);
            return InteropQueryBudget.Apply(result);
        }

        var managedByKey = managed.Facts.ToDictionary(
            item => item.Fact.SymbolCanonicalKey,
            StringComparer.Ordinal);
        var nativeByKey = native.Facts.ToDictionary(
            item => item.Fact.SymbolCanonicalKey,
            StringComparer.Ordinal);
        var matchByManaged = matches.Facts.ToDictionary(
            item => item.Fact.ManagedSymbolCanonicalKey,
            StringComparer.Ordinal);

        var selectedMatches = selected.Managed is not null
            ? SelectManagedMatch(
                selected,
                matchByManaged,
                managedByKey,
                nativeByKey,
                runtimeState.Target)
            : SelectNativeMatches(
                selected,
                matches.Facts,
                managedByKey,
                nativeByKey,
                runtimeState.Target);

        if (selectedMatches.Failures.Count > 0)
        {
            failures.AddRange(selectedMatches.Failures);
            var unknownRows = selectedMatches.ManagedFacts
                .OrderBy(item => item.Fact.SymbolCanonicalKey, StringComparer.Ordinal)
                .Select(item => UnknownMatch(
                    item.Fact,
                    runtimeState,
                    failures))
                .ToArray();
            var result = Result(
                scopeId,
                query,
                runtimeState,
                status: "partial",
                selectionStatus: "selected",
                partial: true,
                selection.Candidates,
                unknownRows,
                [],
                failures);
            return InteropQueryBudget.Apply(result);
        }

        var matchRows = selectedMatches.Matches
            .Select(item => ProjectMatch(item.Fact))
            .ToArray();
        var findingRows = includeFindings
            ? SelectFindings(
                selectedMatches.Matches,
                findings!.Facts,
                runtimeState.Target)
            : FindingSelection.Empty;
        if (findingRows.Failures.Count > 0)
        {
            failures.AddRange(findingRows.Failures);
            var unknownRows = selectedMatches.ManagedFacts
                .OrderBy(item => item.Fact.SymbolCanonicalKey, StringComparer.Ordinal)
                .Select(item => UnknownMatch(
                    item.Fact,
                    runtimeState,
                    failures))
                .ToArray();
            var result = Result(
                scopeId,
                query,
                runtimeState,
                status: "partial",
                selectionStatus: "selected",
                partial: true,
                selection.Candidates,
                unknownRows,
                [],
                failures);
            return InteropQueryBudget.Apply(result);
        }

        return InteropQueryBudget.Apply(Result(
            scopeId,
            query,
            runtimeState,
            status: "ok",
            selectionStatus: "selected",
            partial: false,
            selection.Candidates,
            matchRows,
            findingRows.Rows,
            failures));
    }

    private static async Task<SelectionOutcome> SelectAsync(
        IGraphStore store,
        string query,
        InteropQuerySelectionMode mode,
        IReadOnlyList<StoredInteropFact<ManagedImport>> managed,
        IReadOnlyList<StoredInteropFact<NativeExport>> native,
        CancellationToken cancellationToken)
    {
        var selectables = managed
            .Select(item => Selectable.FromManaged(item))
            .Concat(mode == InteropQuerySelectionMode.ManagedOrNativeBoundary
                ? native.Select(item => Selectable.FromNative(item))
                : [])
            .OrderBy(item => item.CanonicalKey, StringComparer.Ordinal)
            .ThenBy(item => item.SymbolType, StringComparer.Ordinal)
            .ToArray();

        var exact = selectables
            .Where(item => string.Equals(
                item.CanonicalKey,
                query,
                StringComparison.Ordinal))
            .ToArray();
        if (exact.Length > 0)
        {
            return SelectionOutcome.From(exact);
        }

        var byKeyAndType = selectables.ToDictionary(
            item => (item.CanonicalKey, item.SymbolType));
        var hits = await store.FindSymbolsAsync(
                query,
                filePathHint: null,
                limit: MaximumSearchHits,
                cancellationToken)
            .ConfigureAwait(false);
        var candidates = hits
            .Where(hit => hit.CanonicalKey is not null)
            .SelectMany(hit =>
            {
                var managedKey = (hit.CanonicalKey!, ManagedSymbolType);
                var nativeKey = (hit.CanonicalKey!, NativeSymbolType);
                var found = new List<Selectable>(2);
                if (byKeyAndType.TryGetValue(managedKey, out var managedFact))
                {
                    found.Add(managedFact with { Hit = hit });
                }
                if (byKeyAndType.TryGetValue(nativeKey, out var nativeFact))
                {
                    found.Add(nativeFact with { Hit = hit });
                }
                return found;
            })
            .DistinctBy(item => (item.CanonicalKey, item.SymbolType))
            .OrderBy(item => item.CanonicalKey, StringComparer.Ordinal)
            .ThenBy(item => item.SymbolType, StringComparer.Ordinal)
            .ToArray();

        if (hits.Count < MaximumSearchHits)
        {
            return SelectionOutcome.From(candidates);
        }

        if (candidates.Length >= 2)
        {
            return new SelectionOutcome(
                SelectionStatus.Ambiguous,
                candidates,
                Selected: null,
                new InteropQueryFailureRow(
                    "selection",
                    "symbol-search-limit",
                    $"Symbol selection reached the {MaximumSearchHits}-row search bound; "
                    + $"at least {candidates.Length} interop candidates are ambiguous."));
        }

        return new SelectionOutcome(
            SelectionStatus.Unknown,
            candidates,
            Selected: null,
            new InteropQueryFailureRow(
                "selection",
                "symbol-search-limit",
                $"Symbol selection reached the {MaximumSearchHits}-row search bound; "
                + "zero or one observed interop candidate cannot prove a unique result."));
    }

    private static MatchSelection SelectManagedMatch(
        Selectable selected,
        IReadOnlyDictionary<string, StoredInteropFact<InteropMatchProjection>>
            matches,
        IReadOnlyDictionary<string, StoredInteropFact<ManagedImport>>
            managedByKey,
        IReadOnlyDictionary<string, StoredInteropFact<NativeExport>>
            nativeByKey,
        InteropTarget currentTarget)
    {
        var managed = selected.Managed
            ?? throw new InvalidOperationException(
                "A managed selection has no managed fact.");
        if (!matches.TryGetValue(
                managed.Fact.SymbolCanonicalKey,
                out var match))
        {
            return MatchSelection.Invalid(
                [managed],
                ProjectionFailure(
                    managed.Fact.SymbolCanonicalKey,
                    "missing-match",
                    "No persisted match projection exists for the selected managed import."));
        }

        var failure = ValidateMatch(
            match.Fact,
            managedByKey,
            nativeByKey,
            currentTarget);
        return failure is null
            ? new MatchSelection([match], [managed], [])
            : MatchSelection.Invalid([managed], failure);
    }

    private static SelectionOutcome CoalesceOneToOneBoundarySelection(
        SelectionOutcome selection,
        IReadOnlyList<StoredInteropFact<InteropMatchProjection>> matches)
    {
        if (selection.Status != SelectionStatus.Ambiguous
            || selection.Failure is not null
            || selection.Candidates.Count != 2)
        {
            return selection;
        }

        var managedCandidates = selection.Candidates.Where(candidate =>
            string.Equals(
                candidate.SymbolType,
                ManagedSymbolType,
                StringComparison.Ordinal)).ToArray();
        var nativeCandidates = selection.Candidates.Where(candidate =>
            string.Equals(
                candidate.SymbolType,
                NativeSymbolType,
                StringComparison.Ordinal)).ToArray();
        if (managedCandidates.Length != 1 || nativeCandidates.Length != 1)
        {
            return selection;
        }
        var managed = managedCandidates[0];
        var native = nativeCandidates[0];

        var nativeMatches = matches
            .Where(match => string.Equals(
                match.Fact.NativeSymbolCanonicalKey,
                native.CanonicalKey,
                StringComparison.Ordinal))
            .ToArray();
        if (nativeMatches.Length != 1
            || !string.Equals(
                nativeMatches[0].Fact.ManagedSymbolCanonicalKey,
                managed.CanonicalKey,
                StringComparison.Ordinal))
        {
            return selection;
        }

        return selection with
        {
            Status = SelectionStatus.Selected,
            Selected = managed,
        };
    }

    private static MatchSelection SelectNativeMatches(
        Selectable selected,
        IReadOnlyList<StoredInteropFact<InteropMatchProjection>> matches,
        IReadOnlyDictionary<string, StoredInteropFact<ManagedImport>>
            managedByKey,
        IReadOnlyDictionary<string, StoredInteropFact<NativeExport>>
            nativeByKey,
        InteropTarget currentTarget)
    {
        var native = selected.Native
            ?? throw new InvalidOperationException(
                "A native selection has no native fact.");
        if (!native.Fact.Target.IsAbiEquivalentTo(currentTarget))
        {
            return new MatchSelection(
                [],
                [],
                [
                    new InteropQueryFailureRow(
                        "projection",
                        "native-target-mismatch",
                        "The selected native export target differs from the "
                        + "current runtime target."),
                ]);
        }
        var related = matches
            .Where(item => string.Equals(
                item.Fact.NativeSymbolCanonicalKey,
                native.Fact.SymbolCanonicalKey,
                StringComparison.Ordinal))
            .OrderBy(
                item => item.Fact.ManagedSymbolCanonicalKey,
                StringComparer.Ordinal)
            .ToArray();
        var managedFacts = related
            .Select(item => managedByKey.GetValueOrDefault(
                item.Fact.ManagedSymbolCanonicalKey))
            .Where(item => item is not null)
            .Cast<StoredInteropFact<ManagedImport>>()
            .OrderBy(item => item.Fact.SymbolCanonicalKey, StringComparer.Ordinal)
            .ToArray();
        var failures = related
            .Select(item => ValidateMatch(
                item.Fact,
                managedByKey,
                nativeByKey,
                currentTarget))
            .Where(failure => failure is not null)
            .Cast<InteropQueryFailureRow>()
            .ToArray();
        return new MatchSelection(related, managedFacts, failures);
    }

    private static InteropQueryFailureRow? ValidateMatch(
        InteropMatchProjection match,
        IReadOnlyDictionary<string, StoredInteropFact<ManagedImport>>
            managedByKey,
        IReadOnlyDictionary<string, StoredInteropFact<NativeExport>>
            nativeByKey,
        InteropTarget currentTarget)
    {
        if (!managedByKey.TryGetValue(
                match.ManagedSymbolCanonicalKey,
                out var managed))
        {
            return ProjectionFailure(
                match.ManagedSymbolCanonicalKey,
                "orphan-match",
                "The persisted match projection has no current managed import.");
        }
        if (!match.SnapshotComplete)
        {
            return ProjectionFailure(
                match.ManagedSymbolCanonicalKey,
                "incomplete-match-snapshot",
                "The persisted match projection was not produced from a complete snapshot.");
        }
        if (!match.Target.IsAbiEquivalentTo(currentTarget))
        {
            return ProjectionFailure(
                match.ManagedSymbolCanonicalKey,
                "runtime-target-mismatch",
                "The persisted match target differs from the current runtime target.");
        }
        if (!match.Target.IsAbiEquivalentTo(managed.Fact.Target))
        {
            return ProjectionFailure(
                match.ManagedSymbolCanonicalKey,
                "match-target-mismatch",
                "The persisted match target differs from the current managed import target.");
        }
        if (match.NativeSymbolCanonicalKey is null)
        {
            return null;
        }
        if (!nativeByKey.TryGetValue(match.NativeSymbolCanonicalKey, out var native))
        {
            return ProjectionFailure(
                match.ManagedSymbolCanonicalKey,
                "missing-native-fact",
                "The persisted match selects a native symbol with no current export fact.");
        }
        if (!match.Target.IsAbiEquivalentTo(native.Fact.Target))
        {
            return ProjectionFailure(
                match.ManagedSymbolCanonicalKey,
                "native-target-mismatch",
                "The persisted match target differs from the selected native export target.");
        }

        return null;
    }

    private static FindingSelection SelectFindings(
        IReadOnlyList<StoredInteropFact<InteropMatchProjection>> matches,
        IReadOnlyList<StoredInteropFact<InteropFindingProjection>> findings,
        InteropTarget currentTarget)
    {
        var rows = new List<InteropQueryFindingRow>();
        var failures = new List<InteropQueryFailureRow>();
        foreach (var match in matches
                     .OrderBy(
                         item => item.Fact.ManagedSymbolCanonicalKey,
                         StringComparer.Ordinal))
        {
            var projection = match.Fact;
            var relevant = findings
                .Where(item =>
                    string.Equals(
                        item.Fact.BoundaryManagedSymbolCanonicalKey,
                        projection.ManagedSymbolCanonicalKey,
                        StringComparison.Ordinal)
                    && Phase2RuleIds.Contains(item.Fact.RuleId))
                .OrderBy(item => item.Fact.RuleId, StringComparer.Ordinal)
                .ThenBy(item => item.Fact.Message, StringComparer.Ordinal)
                .ThenBy(
                    item => item.Fact.NativeSymbolCanonicalKey,
                    StringComparer.Ordinal)
                .ToArray();

            if (projection.Status != InteropMatchStatus.Matched)
            {
                continue;
            }

            foreach (var finding in relevant)
            {
                if (!string.Equals(
                        finding.Fact.NativeSymbolCanonicalKey,
                        projection.NativeSymbolCanonicalKey,
                        StringComparison.Ordinal)
                    || !finding.Fact.Target.IsAbiEquivalentTo(projection.Target)
                    || !finding.Fact.Target.IsAbiEquivalentTo(currentTarget))
                {
                    failures.Add(ProjectionFailure(
                        projection.ManagedSymbolCanonicalKey,
                        "finding-boundary-mismatch",
                        $"Persisted {finding.Fact.RuleId} does not belong to the "
                        + "selected current boundary."));
                    continue;
                }

                rows.Add(ProjectFinding(finding.Fact));
            }
        }

        return new FindingSelection(
            rows
                .OrderBy(row => row.ManagedSymbol, StringComparer.Ordinal)
                .ThenBy(row => row.RuleId, StringComparer.Ordinal)
                .ThenBy(row => row.Message, StringComparer.Ordinal)
                .ThenBy(row => row.NativeSymbol, StringComparer.Ordinal)
                .ToArray(),
            failures);
    }

    private static InteropQueryMatchRow ProjectMatch(
        InteropMatchProjection match) =>
        new(
            match.ManagedSymbolCanonicalKey,
            match.NativeSymbolCanonicalKey,
            "pinvoke-maps-to",
            MatchStatusToken(match.Status),
            ConfidenceToken(match.Confidence),
            match.Reasons,
            match.CandidateCount,
            ProjectTarget(match.Target),
            ProjectEvidence(match.Evidence),
            EvidenceOmittedCount: 0,
            ReasonOmittedCount: 0);

    private static InteropQueryFindingRow ProjectFinding(
        InteropFindingProjection finding) =>
        new(
            finding.RuleId,
            SeverityToken(finding.Severity),
            finding.Message,
            finding.ManagedSymbolCanonicalKey,
            finding.NativeSymbolCanonicalKey,
            "diagnoses-boundary",
            ConfidenceToken(finding.Confidence),
            ProjectTarget(finding.Target),
            ProjectEvidence(finding.Evidence),
            EvidenceOmittedCount: 0);

    private static InteropQueryMatchRow UnknownMatch(
        ManagedImport managed,
        NativeInteropRuntimeState runtimeState,
        IReadOnlyList<InteropQueryFailureRow> failures)
    {
        var reasons = new List<string>
        {
            $"Native interop state is {RuntimeStatusToken(runtimeState.Status)}; "
            + "no current native match conclusion is available.",
        };
        if (runtimeState.RetainedLastGood)
        {
            reasons.Add(
                "Last-good interop projections are retained in storage but were not "
                + "used as current results.");
        }
        reasons.AddRange(failures.Select(failure =>
            $"{failure.Stage}/{failure.Code}: {failure.Message}"));

        return new InteropQueryMatchRow(
            managed.SymbolCanonicalKey,
            NativeSymbol: null,
            Relation: "pinvoke-maps-to",
            Status: "unknown",
            Confidence: "inferred",
            Reasons: reasons.Distinct(StringComparer.Ordinal).ToArray(),
            CandidateCount: 0,
            ProjectTarget(runtimeState.Target),
            ProjectEvidence(
            [
                new InteropEvidenceProjection(
                    managed.Evidence.Location,
                    managed.Evidence.Confidence,
                    managed.Evidence.Producer,
                    managed.Evidence.Metadata),
            ]),
            EvidenceOmittedCount: 0,
            ReasonOmittedCount: 0);
    }

    private static IReadOnlyList<InteropQueryEvidenceRow> ProjectEvidence(
        IReadOnlyList<InteropEvidenceProjection> evidence) =>
        evidence
            .OrderBy(item => item.Location.FilePath, StringComparer.Ordinal)
            .ThenBy(item => item.Location.StartLine)
            .ThenBy(item => item.Location.StartColumn)
            .ThenBy(item => item.Location.EndLine)
            .ThenBy(item => item.Location.EndColumn)
            .ThenBy(item => item.Producer, StringComparer.Ordinal)
            .ThenBy(item => item.Confidence)
            .Select(item => new InteropQueryEvidenceRow(
                item.Location.FilePath,
                item.Location.StartLine,
                item.Location.StartColumn,
                item.Location.EndLine,
                item.Location.EndColumn,
                ConfidenceToken(item.Confidence),
                item.Producer,
                item.Metadata is null
                    ? null
                    : new SortedDictionary<string, string>(
                        item.Metadata.ToDictionary(
                            pair => pair.Key,
                            pair => pair.Value,
                            StringComparer.Ordinal),
                        StringComparer.Ordinal),
                MetadataOmittedCount: 0))
            .ToArray();

    private static InteropQueryTarget ProjectTarget(InteropTarget target) =>
        new(
            target.RuntimeIdentifier,
            ArchitectureToken(target.Architecture),
            CompilerAbiToken(target.CompilerAbi),
            target.PointerSizeBytes,
            target.DefaultPack);

    private static InteropScopeQueryResult EmptyResult(
        string scopeId,
        string query,
        NativeInteropRuntimeState state,
        string status,
        string selectionStatus,
        bool partial,
        IReadOnlyList<Selectable> candidates,
        IReadOnlyList<InteropQueryFailureRow> failures) =>
        Result(
            scopeId,
            query,
            state,
            status,
            selectionStatus,
            partial,
            candidates,
            [],
            [],
            failures);

    private static InteropScopeQueryResult Result(
        string scopeId,
        string query,
        NativeInteropRuntimeState state,
        string status,
        string selectionStatus,
        bool partial,
        IReadOnlyList<Selectable> candidates,
        IReadOnlyList<InteropQueryMatchRow> matches,
        IReadOnlyList<InteropQueryFindingRow> findings,
        IReadOnlyList<InteropQueryFailureRow> failures) =>
        new(
            scopeId,
            query,
            RuntimeStatusToken(state.Status),
            status,
            partial,
            state.RetainedLastGood,
            selectionStatus,
            candidates.Select(item => item.ToOutput()).ToArray(),
            candidates.Count,
            matches,
            matches.Count,
            findings,
            findings.Count,
            failures
                .OrderBy(failure => failure.Stage, StringComparer.Ordinal)
                .ThenBy(failure => failure.Code, StringComparer.Ordinal)
                .ThenBy(
                    failure => failure.ConfiguredPath ?? string.Empty,
                    StringComparer.Ordinal)
                .ThenBy(
                    failure => failure.TranslationUnitIndex ?? int.MaxValue)
                .ThenBy(failure => failure.Message, StringComparer.Ordinal)
                .Distinct()
                .ToArray(),
            failures.Distinct().Count(),
            Truncated: false,
            OmittedCount: 0,
            OmittedEvidenceCount: 0,
            OmittedReasonCount: 0,
            OmittedMetadataCount: 0,
            OmittedCharacterCount: 0);

    private static IEnumerable<InteropQueryFailureRow> RuntimeFailures(
        NativeInteropRuntimeState state) =>
        state.Failures.Select(failure => new InteropQueryFailureRow(
            failure.Stage,
            failure.Code,
            failure.Message,
            failure.TranslationUnitIndex,
            failure.ConfiguredPath));

    private static IEnumerable<InteropQueryFailureRow> FactFailures(
        IReadOnlyList<InteropFactLoadFailure> failures) =>
        failures.Select(failure => new InteropQueryFailureRow(
            "fact-read",
            "invalid-" + failure.Flavor,
            failure.AnnotationId is null
                ? failure.Reason
                : $"Annotation {failure.AnnotationId}: {failure.Reason}",
            TranslationUnitIndex: null,
            string.IsNullOrEmpty(failure.FilePath)
                ? null
                : failure.FilePath));

    private static InteropQueryFailureRow ProjectionFailure(
        string managedKey,
        string code,
        string message) =>
        new(
            "projection",
            code,
            $"{message} Managed symbol: {managedKey}.");

    private const string ManagedSymbolType = "managed_import";
    private const string NativeSymbolType = "native_export";

    private static string MatchStatusToken(InteropMatchStatus status) =>
        status switch
        {
            InteropMatchStatus.Matched => "matched",
            InteropMatchStatus.SourceMatched => "source_matched",
            InteropMatchStatus.Unmatched => "unmatched",
            InteropMatchStatus.Ambiguous => "ambiguous",
            InteropMatchStatus.Unknown => "unknown",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
        };

    private static string RuntimeStatusToken(NativeInteropRuntimeStatus status) =>
        status switch
        {
            NativeInteropRuntimeStatus.NotStarted => "not_started",
            NativeInteropRuntimeStatus.Indexing => "indexing",
            NativeInteropRuntimeStatus.Complete => "complete",
            NativeInteropRuntimeStatus.Partial => "partial",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
        };

    private static string ConfidenceToken(EvidenceConfidence confidence) =>
        confidence switch
        {
            EvidenceConfidence.Inferred => "inferred",
            EvidenceConfidence.Semantic => "semantic",
            EvidenceConfidence.Exact => "exact",
            _ => throw new ArgumentOutOfRangeException(
                nameof(confidence),
                confidence,
                null),
        };

    private static string SeverityToken(InteropFindingSeverity severity) =>
        severity switch
        {
            InteropFindingSeverity.Info => "info",
            InteropFindingSeverity.Warning => "warning",
            InteropFindingSeverity.Error => "error",
            _ => throw new ArgumentOutOfRangeException(
                nameof(severity),
                severity,
                null),
        };

    private static string ArchitectureToken(InteropArchitecture architecture) =>
        architecture switch
        {
            InteropArchitecture.X86 => "x86",
            InteropArchitecture.X64 => "x64",
            InteropArchitecture.Arm64 => "arm64",
            _ => throw new ArgumentOutOfRangeException(
                nameof(architecture),
                architecture,
                null),
        };

    private static string CompilerAbiToken(InteropCompilerAbi compilerAbi) =>
        compilerAbi switch
        {
            InteropCompilerAbi.Msvc => "msvc",
            InteropCompilerAbi.Itanium => "itanium",
            _ => throw new ArgumentOutOfRangeException(
                nameof(compilerAbi),
                compilerAbi,
                null),
        };

    private enum SelectionStatus
    {
        Selected,
        NotFound,
        Ambiguous,
        Unknown,
    }

    private sealed record SelectionOutcome(
        SelectionStatus Status,
        IReadOnlyList<Selectable> Candidates,
        Selectable? Selected,
        InteropQueryFailureRow? Failure)
    {
        public static SelectionOutcome From(IReadOnlyList<Selectable> candidates) =>
            candidates.Count switch
            {
                0 => new(
                    SelectionStatus.NotFound,
                    candidates,
                    Selected: null,
                    Failure: null),
                1 => new(
                    SelectionStatus.Selected,
                    candidates,
                    candidates[0],
                    Failure: null),
                _ => new(
                    SelectionStatus.Ambiguous,
                    candidates,
                    Selected: null,
                    Failure: null),
            };
    }

    private sealed record Selectable(
        string CanonicalKey,
        string SymbolType,
        StoredInteropFact<ManagedImport>? Managed,
        StoredInteropFact<NativeExport>? Native,
        SymbolHit? Hit)
    {
        public static Selectable FromManaged(
            StoredInteropFact<ManagedImport> fact) =>
            new(
                fact.Fact.SymbolCanonicalKey,
                ManagedSymbolType,
                fact,
                Native: null,
                Hit: null);

        public static Selectable FromNative(
            StoredInteropFact<NativeExport> fact) =>
            new(
                fact.Fact.SymbolCanonicalKey,
                NativeSymbolType,
                Managed: null,
                fact,
                Hit: null);

        public InteropQuerySelectionCandidate ToOutput()
        {
            var row = Managed?.Row ?? Native!.Row;
            var location = Managed?.Fact.Evidence.Location
                ?? Native!.Fact.Evidence.Location;
            return new InteropQuerySelectionCandidate(
                row.SymbolId,
                CanonicalKey,
                SymbolType,
                Hit?.Fqn ?? CanonicalKey,
                Hit?.FilePath ?? row.FilePath,
                Hit?.StartLine ?? location.StartLine,
                Hit?.StartCol ?? location.StartColumn);
        }
    }

    private sealed record MatchSelection(
        IReadOnlyList<StoredInteropFact<InteropMatchProjection>> Matches,
        IReadOnlyList<StoredInteropFact<ManagedImport>> ManagedFacts,
        IReadOnlyList<InteropQueryFailureRow> Failures)
    {
        public static MatchSelection Invalid(
            IReadOnlyList<StoredInteropFact<ManagedImport>> managed,
            InteropQueryFailureRow failure) =>
            new([], managed, [failure]);
    }

    private sealed record FindingSelection(
        IReadOnlyList<InteropQueryFindingRow> Rows,
        IReadOnlyList<InteropQueryFailureRow> Failures)
    {
        public static FindingSelection Empty { get; } = new([], []);
    }
}
