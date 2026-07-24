using System.Text.Json.Serialization;

namespace DevBitsLab.Mcp.SourceGraph.Core;

/// <summary>
/// Persistable interop evidence without a database-local producing file id.
/// </summary>
public sealed record InteropEvidenceProjection(
    SourceLocation Location,
    EvidenceConfidence Confidence,
    string Producer,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>
/// Persistable outcome of matching one managed import against one explicit native snapshot.
/// </summary>
public sealed record InteropMatchProjection(
    string ManagedSymbolCanonicalKey,
    string? NativeSymbolCanonicalKey,
    InteropMatchStatus Status,
    EvidenceConfidence Confidence,
    IReadOnlyList<string> Reasons,
    InteropTarget Target,
    int CandidateCount,
    bool SnapshotComplete,
    IReadOnlyList<InteropEvidenceProjection> Evidence);

/// <summary>
/// Persistable finding for one proven managed/native boundary and explicit target.
/// </summary>
public sealed record InteropFindingProjection(
    string RuleId,
    InteropFindingSeverity Severity,
    string Message,
    string ManagedSymbolCanonicalKey,
    string NativeSymbolCanonicalKey,
    InteropTarget Target,
    EvidenceConfidence Confidence,
    IReadOnlyList<InteropEvidenceProjection> Evidence)
{
    /// <summary>
    /// Import declaration whose matched boundary produced this finding. This differs from
    /// <see cref="ManagedSymbolCanonicalKey"/> for caller-attributed rules such as Interop004
    /// and Interop006.
    /// </summary>
    public string BoundaryManagedSymbolCanonicalKey { get; init; } =
        ManagedSymbolCanonicalKey;
}

public static partial class InteropFactPayloadCodec
{
    private const int FindingCurrentVersion = 2;
    private const string MatchKindToken = "match";
    private const string FindingKindToken = "finding";

    public static string EncodeMatch(InteropMatchProjection match)
    {
        ArgumentNullException.ThrowIfNull(match);
        var payload = ToPayload(match);
        Validate(payload);
        return Serialize(payload);
    }

    public static InteropMatchProjection DecodeMatch(string json) =>
        Validate(Deserialize<MatchPayload>(json));

    public static string EncodeFinding(InteropFindingProjection finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        var payload = ToPayload(finding);
        Validate(payload);
        return Serialize(payload);
    }

    public static InteropFindingProjection DecodeFinding(string json) =>
        Validate(Deserialize<FindingPayload>(json));

    private static MatchPayload ToPayload(InteropMatchProjection match) =>
        new()
        {
            Version = CurrentVersion,
            Kind = MatchKindToken,
            ManagedSymbolCanonicalKey = match.ManagedSymbolCanonicalKey,
            NativeSymbolCanonicalKey = match.NativeSymbolCanonicalKey,
            Status = ToToken(match.Status),
            Confidence = ToToken(match.Confidence),
            Reasons = RequireCollection(match.Reasons, "match.reasons").ToList(),
            Target = ToPayload(match.Target),
            CandidateCount = match.CandidateCount,
            SnapshotComplete = match.SnapshotComplete,
            Evidence = RequireCollection(match.Evidence, "match.evidence")
                .Select(ToPayload)
                .ToList(),
        };

    private static FindingPayload ToPayload(InteropFindingProjection finding) =>
        new()
        {
            Version = FindingCurrentVersion,
            Kind = FindingKindToken,
            RuleId = finding.RuleId,
            Severity = ToToken(finding.Severity),
            Message = finding.Message,
            ManagedSymbolCanonicalKey = finding.ManagedSymbolCanonicalKey,
            BoundaryManagedSymbolCanonicalKey =
                finding.BoundaryManagedSymbolCanonicalKey,
            NativeSymbolCanonicalKey = finding.NativeSymbolCanonicalKey,
            Target = ToPayload(finding.Target),
            Confidence = ToToken(finding.Confidence),
            Evidence = RequireCollection(finding.Evidence, "finding.evidence")
                .Select(ToPayload)
                .ToList(),
        };

    private static EvidencePayload ToPayload(InteropEvidenceProjection evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return new EvidencePayload
        {
            Location = ToPayload(evidence.Location),
            Confidence = ToToken(evidence.Confidence),
            Producer = evidence.Producer,
            Metadata = SortMetadata(evidence.Metadata),
        };
    }

    private static InteropMatchProjection Validate(MatchPayload payload)
    {
        ValidateHeader(payload.Version, payload.Kind, MatchKindToken);
        var status = ParseMatchStatus(payload.Status);
        var managedKey = RequireString(
            payload.ManagedSymbolCanonicalKey,
            "match.managed_symbol_canonical_key");
        var nativeKey = OptionalString(
            payload.NativeSymbolCanonicalKey,
            "match.native_symbol_canonical_key");
        if (payload.CandidateCount < 0)
        {
            throw Invalid("match.candidate_count must be non-negative.");
        }

        switch (status)
        {
            case InteropMatchStatus.Matched or InteropMatchStatus.SourceMatched
                when !payload.SnapshotComplete
                    || nativeKey is null
                    || payload.CandidateCount < 1:
                throw Invalid(
                    "A matched or source-matched result requires a complete snapshot, "
                    + "a native symbol, and at least one candidate.");

            case InteropMatchStatus.Unmatched
                when !payload.SnapshotComplete
                    || nativeKey is not null
                    || payload.CandidateCount != 0:
                throw Invalid(
                    "An unmatched result requires a complete snapshot, no selected "
                    + "native symbol, and zero candidates.");

            case InteropMatchStatus.Ambiguous
                when nativeKey is not null || payload.CandidateCount < 2:
                throw Invalid(
                    "An ambiguous result cannot select a native symbol and requires "
                    + "at least two candidates.");

            case InteropMatchStatus.Unknown when nativeKey is not null:
                throw Invalid("An unknown result cannot select a native symbol.");
        }

        var reasons = RequireNonEmptyStrings(
            payload.Reasons,
            "match.reasons");
        var evidence = FromProjectionPayloads(
            payload.Evidence,
            "match.evidence",
            requireNonEmpty: true);

        return new InteropMatchProjection(
            managedKey,
            nativeKey,
            status,
            ParseEvidenceConfidence(payload.Confidence),
            reasons,
            FromPayload(RequireObject(payload.Target, "match.target")),
            payload.CandidateCount,
            payload.SnapshotComplete,
            evidence);
    }

    private static InteropFindingProjection Validate(FindingPayload payload)
    {
        if (payload.Version is not (1 or FindingCurrentVersion)
            || !string.Equals(
                payload.Kind,
                FindingKindToken,
                StringComparison.Ordinal))
        {
            throw Invalid(
                $"Expected {FindingKindToken} payload version 1 or "
                + $"{FindingCurrentVersion}.");
        }
        var ruleId = RequireString(payload.RuleId, "finding.rule_id");
        ValidateRuleId(ruleId);
        var managedKey = RequireString(
            payload.ManagedSymbolCanonicalKey,
            "finding.managed_symbol_canonical_key");
        if (payload.Version == 1
            && payload.BoundaryManagedSymbolCanonicalKeyWasSpecified)
        {
            throw Invalid(
                "finding.boundary_managed_symbol_canonical_key is not valid "
                + "in a version 1 payload.");
        }
        var boundaryManagedKey = payload.Version == 1
            ? managedKey
            : RequireString(
                payload.BoundaryManagedSymbolCanonicalKey,
                "finding.boundary_managed_symbol_canonical_key");

        return new InteropFindingProjection(
            ruleId,
            ParseFindingSeverity(payload.Severity),
            RequireString(payload.Message, "finding.message"),
            managedKey,
            RequireString(
                payload.NativeSymbolCanonicalKey,
                "finding.native_symbol_canonical_key"),
            FromPayload(RequireObject(payload.Target, "finding.target")),
            ParseEvidenceConfidence(payload.Confidence),
            FromProjectionPayloads(
                payload.Evidence,
                "finding.evidence",
                requireNonEmpty: true))
        {
            BoundaryManagedSymbolCanonicalKey = boundaryManagedKey,
        };
    }

    private static IReadOnlyList<string> RequireNonEmptyStrings(
        IReadOnlyList<string>? values,
        string path)
    {
        var bounded = RequireCollection(values, path);
        if (bounded.Count == 0)
        {
            throw Invalid($"{path} must contain at least one item.");
        }

        return bounded
            .Select((value, index) =>
                RequireString(value, $"{path}[{index}]"))
            .ToArray();
    }

    private static IReadOnlyList<InteropEvidenceProjection> FromProjectionPayloads(
        IReadOnlyList<EvidencePayload>? values,
        string path,
        bool requireNonEmpty)
    {
        var bounded = RequireCollection(values, path);
        if (requireNonEmpty && bounded.Count == 0)
        {
            throw Invalid($"{path} must contain at least one item.");
        }

        return bounded
            .Select((value, index) => FromProjectionPayload(
                RequireObject(value, $"{path}[{index}]")))
            .ToArray();
    }

    private static InteropEvidenceProjection FromProjectionPayload(
        EvidencePayload payload) =>
        new(
            FromPayload(RequireObject(payload.Location, "evidence.location")),
            ParseEvidenceConfidence(payload.Confidence),
            RequireString(payload.Producer, "evidence.producer"),
            ValidateMetadata(payload.Metadata));

    private static void ValidateRuleId(string ruleId)
    {
        if (ruleId.Length != 10
            || !ruleId.StartsWith("Interop", StringComparison.Ordinal)
            || ruleId[7] is < '0' or > '9'
            || ruleId[8] is < '0' or > '9'
            || ruleId[9] is < '0' or > '9'
            || ruleId.AsSpan(7).SequenceEqual("000"))
        {
            throw Invalid(
                "finding.rule_id must use the form Interop001 through Interop999.");
        }
    }

    private static string ToToken(InteropMatchStatus value) => value switch
    {
        InteropMatchStatus.Matched => "matched",
        InteropMatchStatus.SourceMatched => "source_matched",
        InteropMatchStatus.Unmatched => "unmatched",
        InteropMatchStatus.Ambiguous => "ambiguous",
        InteropMatchStatus.Unknown => "unknown",
        _ => throw Invalid($"Unknown interop match status `{value}`."),
    };

    private static InteropMatchStatus ParseMatchStatus(string? value) =>
        value switch
        {
            "matched" => InteropMatchStatus.Matched,
            "source_matched" => InteropMatchStatus.SourceMatched,
            "unmatched" => InteropMatchStatus.Unmatched,
            "ambiguous" => InteropMatchStatus.Ambiguous,
            "unknown" => InteropMatchStatus.Unknown,
            _ => throw Invalid($"Unknown interop match status `{value}`."),
        };

    private static string ToToken(InteropFindingSeverity value) => value switch
    {
        InteropFindingSeverity.Info => "info",
        InteropFindingSeverity.Warning => "warning",
        InteropFindingSeverity.Error => "error",
        _ => throw Invalid($"Unknown interop finding severity `{value}`."),
    };

    private static InteropFindingSeverity ParseFindingSeverity(string? value) =>
        value switch
        {
            "info" => InteropFindingSeverity.Info,
            "warning" => InteropFindingSeverity.Warning,
            "error" => InteropFindingSeverity.Error,
            _ => throw Invalid($"Unknown interop finding severity `{value}`."),
        };

    private sealed class MatchPayload
    {
        [JsonPropertyOrder(0)] public required int Version { get; init; }
        [JsonPropertyOrder(1)] public required string? Kind { get; init; }
        [JsonPropertyOrder(2)] public required string? ManagedSymbolCanonicalKey { get; init; }
        [JsonPropertyOrder(3)] public required string? NativeSymbolCanonicalKey { get; init; }
        [JsonPropertyOrder(4)] public required string? Status { get; init; }
        [JsonPropertyOrder(5)] public required string? Confidence { get; init; }
        [JsonPropertyOrder(6)] public required List<string>? Reasons { get; init; }
        [JsonPropertyOrder(7)] public required TargetPayload? Target { get; init; }
        [JsonPropertyOrder(8)] public required int CandidateCount { get; init; }
        [JsonPropertyOrder(9)] public required bool SnapshotComplete { get; init; }
        [JsonPropertyOrder(10)] public required List<EvidencePayload>? Evidence { get; init; }
    }

    private sealed class FindingPayload
    {
        private string? _boundaryManagedSymbolCanonicalKey;

        [JsonPropertyOrder(0)] public required int Version { get; init; }
        [JsonPropertyOrder(1)] public required string? Kind { get; init; }
        [JsonPropertyOrder(2)] public required string? RuleId { get; init; }
        [JsonPropertyOrder(3)] public required string? Severity { get; init; }
        [JsonPropertyOrder(4)] public required string? Message { get; init; }
        [JsonPropertyOrder(5)] public required string? ManagedSymbolCanonicalKey { get; init; }
        [JsonPropertyOrder(6)]
        public string? BoundaryManagedSymbolCanonicalKey
        {
            get => _boundaryManagedSymbolCanonicalKey;
            init
            {
                _boundaryManagedSymbolCanonicalKey = value;
                BoundaryManagedSymbolCanonicalKeyWasSpecified = true;
            }
        }
        [JsonIgnore]
        public bool BoundaryManagedSymbolCanonicalKeyWasSpecified
        {
            get;
            private set;
        }
        [JsonPropertyOrder(7)] public required string? NativeSymbolCanonicalKey { get; init; }
        [JsonPropertyOrder(8)] public required TargetPayload? Target { get; init; }
        [JsonPropertyOrder(9)] public required string? Confidence { get; init; }
        [JsonPropertyOrder(10)] public required List<EvidencePayload>? Evidence { get; init; }
    }
}
