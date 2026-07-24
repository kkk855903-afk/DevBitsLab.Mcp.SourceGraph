using DevBitsLab.Mcp.SourceGraph.Core;

namespace DevBitsLab.Mcp.SourceGraph.Interop;

/// <summary>
/// Deterministically matches a normalized managed import to native exports. Candidate generation
/// is deliberately strict: an entry-point spelling, module identity, and target ABI must all be
/// proven before a match can be reported.
/// </summary>
public sealed class InteropMatcher
{
    public InteropMatch Match(
        ManagedImport managed,
        IEnumerable<NativeExport> nativeExports)
    {
        ArgumentNullException.ThrowIfNull(managed);
        ArgumentNullException.ThrowIfNull(nativeExports);

        var candidates = nativeExports.ToArray();
        var lookup = CreateLookupPlan(managed);
        if (lookup.Error is not null)
        {
            return Result(
                managed,
                native: null,
                InteropMatchStatus.Unknown,
                EvidenceConfidence.Inferred,
                [lookup.Error],
                [managed.Evidence]);
        }

        var sameEntryPoint = candidates
            .Where(native => lookup.Spellings.Contains(
                native.ExportName,
                StringComparer.Ordinal))
            .ToArray();
        if (sameEntryPoint.Length == 0)
        {
            return Result(
                managed,
                native: null,
                InteropMatchStatus.Unmatched,
                managed.Evidence.Confidence,
                [
                    lookup.Description,
                    $"No native export has a runtime-legal entry-point spelling for '{managed.EntryPoint}'.",
                ],
                [managed.Evidence]);
        }

        var sameTarget = sameEntryPoint
            .Where(native => SameTarget(managed.Target, native.Target))
            .ToArray();
        if (sameTarget.Length == 0)
        {
            return Result(
                managed,
                native: null,
                InteropMatchStatus.Unknown,
                EvidenceConfidence.Inferred,
                ["Entry-point candidates exist, but none were analyzed for the managed target ABI."],
                Combine(
                    managed.Evidence,
                    sameEntryPoint.Select(item => item.Evidence)));
        }

        foreach (var spelling in lookup.Spellings)
        {
            var spellingCandidates = sameTarget
                .Where(native => string.Equals(
                    native.ExportName,
                    spelling,
                    StringComparison.Ordinal))
                .ToArray();
            if (spellingCandidates.Length == 0) continue;

            var candidatesWithLibrary = spellingCandidates
                .Where(native =>
                    !string.IsNullOrWhiteSpace(native.LibraryName)
                    && (native.IsBinaryVerified
                        || native.ModuleIdentitySource
                            != NativeModuleIdentitySource.Unknown))
                .ToArray();
            var candidatesWithoutLibrary = spellingCandidates
                .Where(native =>
                    string.IsNullOrWhiteSpace(native.LibraryName)
                    || (!native.IsBinaryVerified
                        && native.ModuleIdentitySource
                            == NativeModuleIdentitySource.Unknown))
                .ToArray();
            var libraryMatches = candidatesWithLibrary
                .Where(native => SameLibrary(
                    managed.LibraryName,
                    native.LibraryName!,
                    managed.Target))
                .ToArray();
            if (libraryMatches.Length > 1)
            {
                return Result(
                    managed,
                    native: null,
                    InteropMatchStatus.Ambiguous,
                    EvidenceConfidence.Inferred,
                    [
                        lookup.Description,
                        $"{libraryMatches.Length} native exports share module '{managed.LibraryName}', runtime-selected spelling '{spelling}', and target ABI.",
                    ],
                    Combine(
                        managed.Evidence,
                        libraryMatches.Select(item => item.Evidence)));
            }
            if (candidatesWithoutLibrary.Length > 0)
            {
                return Result(
                    managed,
                    native: null,
                    InteropMatchStatus.Unknown,
                    EvidenceConfidence.Inferred,
                    [
                        lookup.Description,
                        libraryMatches.Length == 1
                            ? $"One '{spelling}' candidate exists in the requested module, but another candidate at the same runtime lookup step has unknown module ownership, so uniqueness is not proven."
                            : $"At runtime lookup spelling '{spelling}', known modules do not match and at least one candidate has unknown module ownership.",
                    ],
                    Combine(
                        managed.Evidence,
                        spellingCandidates.Select(item => item.Evidence)));
            }
            if (libraryMatches.Length == 0) continue;

            var native = libraryMatches[0];
            var evidence = Combine(managed.Evidence, [native.Evidence]);
            var isFinalBinaryMatch = native.IsBinaryVerified;
            return Result(
                managed,
                native,
                isFinalBinaryMatch
                    ? InteropMatchStatus.Matched
                    : InteropMatchStatus.SourceMatched,
                isFinalBinaryMatch
                    ? Weakest(evidence)
                    : EvidenceConfidence.Inferred,
                [
                    lookup.Description,
                    managed.ExactSpelling == true
                        ? $"Entry point matches exactly: {managed.EntryPoint}."
                        : $"Runtime entry-point lookup resolves to '{spelling}'.",
                    native.ModuleIdentitySource == NativeModuleIdentitySource.Configuration
                        && !native.IsBinaryVerified
                        ? $"Module matches configured build context after target-aware normalization: {managed.LibraryName} -> {native.LibraryName}."
                        : $"Module matches after target-aware normalization: {managed.LibraryName} -> {native.LibraryName}.",
                    $"Target ABI matches: {managed.Target.RuntimeIdentifier}/{managed.Target.CompilerAbi}.",
                    native.IsBinaryVerified
                        ? "The export was verified in the native binary."
                        : "A unique source export matches, but it has not been verified in the final native binary.",
                ],
                evidence);
        }

        return Result(
            managed,
            native: null,
            InteropMatchStatus.Unmatched,
            Weakest(
                Combine(
                    managed.Evidence,
                    sameTarget.Select(item => item.Evidence))),
            [
                lookup.Description,
                $"Runtime-legal entry points exist for the target ABI, but no candidate belongs to managed module '{managed.LibraryName}'.",
            ],
            Combine(
                managed.Evidence,
                sameTarget.Select(item => item.Evidence)));
    }

    private static EntryPointLookupPlan CreateLookupPlan(ManagedImport managed)
    {
        if (managed.ExactSpelling is null)
        {
            return new EntryPointLookupPlan(
                [],
                "",
                "Managed entry-point lookup policy is unknown; no export spelling can be selected safely.");
        }

        if (managed.ExactSpelling.Value)
        {
            return new EntryPointLookupPlan(
                [managed.EntryPoint],
                $"Runtime lookup requires the exact entry-point spelling '{managed.EntryPoint}'.",
                Error: null);
        }

        if (!IsWindows(managed.Target))
        {
            return new EntryPointLookupPlan(
                [managed.EntryPoint],
                $"Runtime lookup for {managed.Target.RuntimeIdentifier} uses '{managed.EntryPoint}' without Windows A/W suffix probing.",
                Error: null);
        }

        return managed.CharacterSet switch
        {
            "ansi" => new EntryPointLookupPlan(
                [managed.EntryPoint, managed.EntryPoint + "A"],
                $"Windows ANSI runtime lookup order is '{managed.EntryPoint}', then '{managed.EntryPoint}A'.",
                Error: null),
            "utf-16" => new EntryPointLookupPlan(
                [managed.EntryPoint + "W", managed.EntryPoint],
                $"Windows Unicode runtime lookup order is '{managed.EntryPoint}W', then '{managed.EntryPoint}'.",
                Error: null),
            _ => new EntryPointLookupPlan(
                [],
                "",
                $"Character set '{managed.CharacterSet ?? "<unknown>"}' does not prove a Windows entry-point lookup sequence."),
        };
    }

    private static bool IsWindows(InteropTarget target) =>
        target.RuntimeIdentifier.StartsWith(
            "win-",
            StringComparison.OrdinalIgnoreCase);

    private static InteropMatch Result(
        ManagedImport managed,
        NativeExport? native,
        InteropMatchStatus status,
        EvidenceConfidence confidence,
        IReadOnlyList<string> reasons,
        IReadOnlyList<Evidence> evidence) =>
        new(
            managed.SymbolCanonicalKey,
            native?.SymbolCanonicalKey,
            status,
            confidence,
            reasons,
            evidence);

    private static bool SameTarget(InteropTarget left, InteropTarget right) =>
        left.IsAbiEquivalentTo(right);

    private static bool SameLibrary(
        string managedLibrary,
        string nativeLibrary,
        InteropTarget target)
    {
        var managed = NormalizeLibrary(managedLibrary, target);
        var native = NormalizeLibrary(nativeLibrary, target);
        return managed is not null
            && native is not null
            && string.Equals(
                managed,
                native,
                target.RuntimeIdentifier.StartsWith(
                    "win-",
                    StringComparison.OrdinalIgnoreCase)
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
    }

    private static string? NormalizeLibrary(
        string value,
        InteropTarget target)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        if (slash >= 0) normalized = normalized[(slash + 1)..];
        if (target.RuntimeIdentifier.StartsWith(
                "win-",
                StringComparison.OrdinalIgnoreCase)
            && normalized.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^4];
        }
        return normalized.Length == 0 ? null : normalized;
    }

    private static IReadOnlyList<Evidence> Combine(
        Evidence managed,
        IEnumerable<Evidence> native) =>
        new[] { managed }
            .Concat(native)
            .Distinct()
            .ToArray();

    private static EvidenceConfidence Weakest(
        IReadOnlyList<Evidence> evidence) =>
        evidence.Count == 0
            ? EvidenceConfidence.Inferred
            : evidence.Min(item => item.Confidence);

    private sealed record EntryPointLookupPlan(
        IReadOnlyList<string> Spellings,
        string Description,
        string? Error);
}
