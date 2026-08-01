using System.Text.Json;

namespace DevBitsLab.Mcp.SourceGraph.Server.Interop;

/// <summary>Applies the Phase 2 serialized-character budget to one scope result.</summary>
internal static class InteropQueryBudget
{
    public const int MaximumSerializedCharacters = 50_000;

    /// <summary>
    /// Progressively reduce optional detail until the actual serialized response fits the MCP
    /// transport budget. This measures the final JSON instead of estimating object sizes, because
    /// escaped paths and metadata can make otherwise similar rows materially different in size.
    /// </summary>
    public static BoundedInteropScopeQuery Apply(
        InteropScopeQueryResult result,
        int maximumSerializedCharacters = MaximumSerializedCharacters)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (maximumSerializedCharacters is < 1_500
            or > MaximumSerializedCharacters)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumSerializedCharacters),
                maximumSerializedCharacters,
                $"Interop scope output budgets must be between 1500 and "
                + $"{MaximumSerializedCharacters} characters.");
        }

        result = Limit(result, Limits.Initial);
        var json = Serialize(result);
        foreach (var limits in Limits.ReductionStages)
        {
            if (json.Length <= maximumSerializedCharacters)
            {
                break;
            }
            result = Limit(result, limits);
            json = Serialize(result);
        }

        // The last reduction keeps the scope, target, status, at least one reason for every
        // retained match, and the minimum candidate cardinality needed to preserve ambiguity.
        // If that fixed core ever exceeds the budget, fail rather than return host-truncated JSON.
        if (json.Length > maximumSerializedCharacters)
        {
            throw new InvalidOperationException(
                "The required interop query core exceeds its allocated output budget.");
        }

        return new BoundedInteropScopeQuery(result, json);
    }

    private static string Serialize(InteropScopeQueryResult result) =>
        JsonSerializer.Serialize(
            result,
            InteropQueryJsonContext.Default.InteropScopeQueryResult);

    private static InteropScopeQueryResult Limit(
        InteropScopeQueryResult result,
        Limits limits)
    {
        var omittedCharacters = result.OmittedCharacterCount;
        string Bound(string value, int maximum)
        {
            if (value.Length <= maximum)
            {
                return value;
            }
            omittedCharacters = SaturatingAdd(
                omittedCharacters,
                value.Length - maximum);
            return value[..maximum];
        }

        string? BoundOptional(string? value, int maximum) =>
            value is null ? null : Bound(value, maximum);

        InteropQueryTarget BoundTarget(InteropQueryTarget target) =>
            target with
            {
                RuntimeIdentifier = Bound(
                    target.RuntimeIdentifier,
                    limits.IdentifierCharacters),
            };

        InteropQueryEvidenceRow BoundEvidence(
            InteropQueryEvidenceRow evidence)
        {
            IReadOnlyDictionary<string, string>? metadata = null;
            var originalMetadataCount =
                (evidence.Metadata?.Count ?? 0)
                + evidence.MetadataOmittedCount;
            if (evidence.Metadata is not null
                && limits.MetadataPerEvidence > 0)
            {
                var bounded = new SortedDictionary<string, string>(
                    StringComparer.Ordinal);
                foreach (var pair in evidence.Metadata
                             .OrderBy(item => item.Key, StringComparer.Ordinal)
                             .Take(limits.MetadataPerEvidence))
                {
                    var key = Bound(pair.Key, limits.MetadataKeyCharacters);
                    var value = Bound(
                        pair.Value,
                        limits.MetadataValueCharacters);
                    // Truncating two distinct keys may make them equal. Retaining only the first
                    // is deterministic, and the omitted-entry count discloses the collision.
                    bounded.TryAdd(key, value);
                }
                if (bounded.Count > 0)
                {
                    metadata = bounded;
                }
            }

            return evidence with
            {
                FilePath = Bound(
                    evidence.FilePath,
                    limits.PathCharacters),
                Producer = Bound(
                    evidence.Producer,
                    limits.IdentifierCharacters),
                Metadata = metadata,
                MetadataOmittedCount =
                    Math.Max(0, originalMetadataCount - (metadata?.Count ?? 0)),
            };
        }

        (IReadOnlyList<InteropQueryEvidenceRow> Rows, int Omitted)
            BoundEvidenceList(
                IReadOnlyList<InteropQueryEvidenceRow> evidence,
                int alreadyOmitted)
        {
            var total = SaturatingAdd(evidence.Count, alreadyOmitted);
            var rows = evidence
                .Take(limits.EvidencePerRow)
                .Select(BoundEvidence)
                .ToArray();
            return (rows, Math.Max(0, total - rows.Length));
        }

        var candidates = result.SelectionCandidates
            .Take(CandidateLimit(result, limits.SelectionCandidates))
            .Select(candidate => candidate with
            {
                CanonicalKey = Bound(
                    candidate.CanonicalKey,
                    limits.CanonicalKeyCharacters),
                SymbolType = Bound(
                    candidate.SymbolType,
                    limits.IdentifierCharacters),
                Display = Bound(
                    candidate.Display,
                    limits.DisplayCharacters),
                FilePath = Bound(
                    candidate.FilePath,
                    limits.PathCharacters),
            })
            .ToArray();

        var matches = result.Matches
            .Take(limits.Matches)
            .Select(match =>
            {
                var evidence = BoundEvidenceList(
                    match.Evidence,
                    match.EvidenceOmittedCount);
                var totalReasons = SaturatingAdd(
                    match.Reasons.Count,
                    match.ReasonOmittedCount);
                var reasons = match.Reasons
                    .Take(Math.Max(1, limits.ReasonsPerMatch))
                    .Select(reason => Bound(
                        reason,
                        limits.MessageCharacters))
                    .ToArray();
                return match with
                {
                    ManagedSymbol = Bound(
                        match.ManagedSymbol,
                        limits.CanonicalKeyCharacters),
                    NativeSymbol = BoundOptional(
                        match.NativeSymbol,
                        limits.CanonicalKeyCharacters),
                    Reasons = reasons,
                    ReasonOmittedCount =
                        Math.Max(0, totalReasons - reasons.Length),
                    Target = BoundTarget(match.Target),
                    Evidence = evidence.Rows,
                    EvidenceOmittedCount = evidence.Omitted,
                };
            })
            .ToArray();

        var findings = result.Findings
            .Take(limits.Findings)
            .Select(finding =>
            {
                var evidence = BoundEvidenceList(
                    finding.Evidence,
                    finding.EvidenceOmittedCount);
                return finding with
                {
                    RuleId = Bound(
                        finding.RuleId,
                        limits.IdentifierCharacters),
                    Message = Bound(
                        finding.Message,
                        limits.MessageCharacters),
                    ManagedSymbol = Bound(
                        finding.ManagedSymbol,
                        limits.CanonicalKeyCharacters),
                    NativeSymbol = Bound(
                        finding.NativeSymbol,
                        limits.CanonicalKeyCharacters),
                    Target = BoundTarget(finding.Target),
                    Evidence = evidence.Rows,
                    EvidenceOmittedCount = evidence.Omitted,
                };
            })
            .ToArray();

        var minimumFailures = result.Partial && result.Failures.Count > 0 ? 1 : 0;
        var failures = result.Failures
            .Take(Math.Max(minimumFailures, limits.Failures))
            .Select(failure => failure with
            {
                Stage = Bound(
                    failure.Stage,
                    limits.IdentifierCharacters),
                Code = Bound(
                    failure.Code,
                    limits.IdentifierCharacters),
                Message = Bound(
                    failure.Message,
                    limits.MessageCharacters),
                ConfiguredPath = BoundOptional(
                    failure.ConfiguredPath,
                    limits.PathCharacters),
            })
            .ToArray();

        var scopeId = Bound(result.ScopeId, limits.IdentifierCharacters);
        var query = Bound(result.Query, limits.QueryCharacters);
        var scopeStatus = Bound(
            result.ScopeStatus,
            limits.IdentifierCharacters);
        var status = Bound(result.Status, limits.IdentifierCharacters);
        var selectionStatus = Bound(
            result.SelectionStatus,
            limits.IdentifierCharacters);

        var evidenceOmitted = matches.Sum(item => item.EvidenceOmittedCount);
        evidenceOmitted = SaturatingAdd(
            evidenceOmitted,
            findings.Sum(item => item.EvidenceOmittedCount));
        var reasonOmitted = matches.Sum(item => item.ReasonOmittedCount);
        var metadataOmitted = matches
            .SelectMany(item => item.Evidence)
            .Sum(item => item.MetadataOmittedCount);
        metadataOmitted = SaturatingAdd(
            metadataOmitted,
            findings
                .SelectMany(item => item.Evidence)
                .Sum(item => item.MetadataOmittedCount));

        var omittedRows = Math.Max(
            0,
            result.TotalSelectionCandidateCount - candidates.Length);
        omittedRows = SaturatingAdd(
            omittedRows,
            Math.Max(0, result.TotalMatchCount - matches.Length));
        omittedRows = SaturatingAdd(
            omittedRows,
            Math.Max(0, result.TotalFindingCount - findings.Length));
        omittedRows = SaturatingAdd(
            omittedRows,
            Math.Max(0, result.TotalFailureCount - failures.Length));
        var omitted = SaturatingAdd(omittedRows, evidenceOmitted);
        omitted = SaturatingAdd(omitted, reasonOmitted);
        omitted = SaturatingAdd(omitted, metadataOmitted);
        omitted = SaturatingAdd(omitted, omittedCharacters);

        return result with
        {
            ScopeId = scopeId,
            Query = query,
            ScopeStatus = scopeStatus,
            Status = status,
            SelectionStatus = selectionStatus,
            SelectionCandidates = candidates,
            Matches = matches,
            Findings = findings,
            Failures = failures,
            Truncated = omitted > 0,
            OmittedCount = omitted,
            OmittedEvidenceCount = evidenceOmitted,
            OmittedReasonCount = reasonOmitted,
            OmittedMetadataCount = metadataOmitted,
            OmittedCharacterCount = omittedCharacters,
        };
    }

    private static int CandidateLimit(
        InteropScopeQueryResult result,
        int requested)
    {
        var minimum = result.SelectionStatus switch
        {
            "selected" => 1,
            // Keeping two candidates is what prevents rendering an ambiguous selection as if it
            // were a unique match.
            "ambiguous" => 2,
            _ => 0,
        };
        return Math.Max(minimum, requested);
    }

    private static int SaturatingAdd(int left, int right)
    {
        var sum = (long)left + right;
        return sum >= int.MaxValue ? int.MaxValue : (int)sum;
    }

    private sealed record Limits(
        int SelectionCandidates,
        int Matches,
        int Findings,
        int Failures,
        int ReasonsPerMatch,
        int EvidencePerRow,
        int MetadataPerEvidence,
        int QueryCharacters,
        int CanonicalKeyCharacters,
        int PathCharacters,
        int DisplayCharacters,
        int MessageCharacters,
        int IdentifierCharacters,
        int MetadataKeyCharacters,
        int MetadataValueCharacters)
    {
        public static Limits Initial { get; } = new(
            SelectionCandidates: 64,
            Matches: 64,
            Findings: 128,
            Failures: 64,
            ReasonsPerMatch: 32,
            EvidencePerRow: 64,
            MetadataPerEvidence: 16,
            QueryCharacters: 1024,
            CanonicalKeyCharacters: 4096,
            PathCharacters: 2048,
            DisplayCharacters: 1024,
            MessageCharacters: 1024,
            IdentifierCharacters: 256,
            MetadataKeyCharacters: 256,
            MetadataValueCharacters: 512);

        public static IReadOnlyList<Limits> ReductionStages { get; } =
        [
            Initial with { MetadataPerEvidence = 0 },
            Initial with
            {
                MetadataPerEvidence = 0,
                EvidencePerRow = 4,
            },
            Initial with
            {
                MetadataPerEvidence = 0,
                EvidencePerRow = 2,
                Findings = 32,
                Matches = 32,
                SelectionCandidates = 16,
                Failures = 16,
            },
            Initial with
            {
                MetadataPerEvidence = 0,
                EvidencePerRow = 1,
                Findings = 8,
                Matches = 8,
                SelectionCandidates = 8,
                Failures = 8,
                ReasonsPerMatch = 8,
            },
            Initial with
            {
                MetadataPerEvidence = 0,
                EvidencePerRow = 0,
                Findings = 0,
                Matches = 1,
                SelectionCandidates = 2,
                Failures = 1,
                ReasonsPerMatch = 1,
                QueryCharacters = 256,
                CanonicalKeyCharacters = 1024,
                PathCharacters = 512,
                DisplayCharacters = 256,
                MessageCharacters = 256,
                IdentifierCharacters = 128,
                MetadataKeyCharacters = 128,
                MetadataValueCharacters = 256,
            },
            Initial with
            {
                MetadataPerEvidence = 0,
                EvidencePerRow = 0,
                Findings = 0,
                Matches = 1,
                SelectionCandidates = 2,
                Failures = 1,
                ReasonsPerMatch = 1,
                QueryCharacters = 128,
                CanonicalKeyCharacters = 512,
                PathCharacters = 256,
                DisplayCharacters = 128,
                MessageCharacters = 128,
                IdentifierCharacters = 64,
                MetadataKeyCharacters = 64,
                MetadataValueCharacters = 128,
            },
        ];
    }
}
