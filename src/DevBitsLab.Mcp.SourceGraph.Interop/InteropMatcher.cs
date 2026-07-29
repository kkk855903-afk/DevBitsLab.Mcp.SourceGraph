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
        IEnumerable<NativeExport> nativeExports) =>
        Match(managed, nativeExports, isExportUniverseComplete: true);

    /// <summary>
    /// Matches against one target-specific export snapshot. A partial snapshot may still prove
    /// a concrete source/binary match or ambiguity, but it cannot prove absence. Callers must
    /// retain the partial-state marker because unavailable translation units can contain
    /// additional candidates.
    /// </summary>
    public InteropMatch Match(
        ManagedImport managed,
        IEnumerable<NativeExport> nativeExports,
        bool isExportUniverseComplete)
    {
        var result = MatchCompleteSnapshot(managed, nativeExports);
        if (isExportUniverseComplete
            || result.Status is InteropMatchStatus.Matched
                or InteropMatchStatus.SourceMatched
                or InteropMatchStatus.Ambiguous
                or InteropMatchStatus.Unknown)
        {
            return isExportUniverseComplete
                ? result
                : result with
                {
                    Reasons = result.Reasons
                        .Append(
                            "The native export snapshot is incomplete; this positive result covers only the successfully indexed projects.")
                        .ToArray(),
                };
        }

        return result with
        {
            NativeSymbolCanonicalKey = null,
            Status = InteropMatchStatus.Unknown,
            Confidence = EvidenceConfidence.Inferred,
            Reasons = result.Reasons
                .Append(
                    "The native export snapshot is incomplete, so absence and candidate uniqueness cannot be proven.")
                .ToArray(),
        };
    }

    private static InteropMatch MatchCompleteSnapshot(
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
                lookup.DecoratedLookupIncomplete
                    ? InteropMatchStatus.Unknown
                    : InteropMatchStatus.Unmatched,
                lookup.DecoratedLookupIncomplete
                    ? EvidenceConfidence.Inferred
                    : managed.Evidence.Confidence,
                [
                    lookup.Description,
                    lookup.DecoratedLookupIncomplete
                        ? $"No undecorated native export matches '{managed.EntryPoint}', and the x86 stdcall stack-byte count is unknown, so decorated lookup cannot be decided."
                        : $"No native export has a runtime-legal entry-point spelling for '{managed.EntryPoint}'.",
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
                    sameEntryPoint.Select(item => item.Evidence)),
                sameEntryPoint.Length);
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
                        libraryMatches.Select(item => item.Evidence)),
                    libraryMatches.Length);
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
                        spellingCandidates.Select(item => item.Evidence)),
                    spellingCandidates.Length);
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
                evidence,
                candidateCount: 1);
        }

        return Result(
            managed,
            native: null,
            lookup.DecoratedLookupIncomplete
                ? InteropMatchStatus.Unknown
                : InteropMatchStatus.Unmatched,
            lookup.DecoratedLookupIncomplete
                ? EvidenceConfidence.Inferred
                : Weakest(
                    Combine(
                        managed.Evidence,
                        sameTarget.Select(item => item.Evidence))),
            [
                lookup.Description,
                lookup.DecoratedLookupIncomplete
                    ? $"Undecorated entry points do not prove a candidate in module '{managed.LibraryName}', and a possible x86 stdcall decoration cannot be calculated."
                    : $"Runtime-legal entry points exist for the target ABI, but no candidate belongs to managed module '{managed.LibraryName}'.",
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
                "Managed entry-point lookup policy is unknown; no export spelling can be selected safely.",
                DecoratedLookupIncomplete: false);
        }

        IReadOnlyList<string> baseSpellings;
        string description;
        if (managed.ExactSpelling.Value)
        {
            baseSpellings = [managed.EntryPoint];
            description =
                $"Character-set lookup requires the exact entry-point spelling '{managed.EntryPoint}'.";
        }
        else if (!IsWindows(managed.Target))
        {
            baseSpellings = [managed.EntryPoint];
            description =
                $"Runtime lookup for {managed.Target.RuntimeIdentifier} uses '{managed.EntryPoint}' without Windows A/W suffix probing.";
        }
        else
        {
            switch (managed.CharacterSet)
            {
                case "ansi":
                    baseSpellings =
                        [managed.EntryPoint, managed.EntryPoint + "A"];
                    description =
                        $"Windows ANSI runtime lookup order is '{managed.EntryPoint}', then '{managed.EntryPoint}A'.";
                    break;
                case "utf-16":
                    baseSpellings =
                        [managed.EntryPoint + "W", managed.EntryPoint];
                    description =
                        $"Windows Unicode runtime lookup order is '{managed.EntryPoint}W', then '{managed.EntryPoint}'.";
                    break;
                default:
                    return new EntryPointLookupPlan(
                        [],
                        "",
                        $"Character set '{managed.CharacterSet ?? "<unknown>"}' does not prove a Windows entry-point lookup sequence.",
                        DecoratedLookupIncomplete: false);
            }
        }

        if (!RequiresX86StdCallDecorationPlan(managed))
        {
            return new EntryPointLookupPlan(
                baseSpellings,
                description,
                Error: null,
                DecoratedLookupIncomplete: false);
        }

        if (!TryCalculateX86StackArgumentBytes(managed, out var stackBytes))
        {
            return new EntryPointLookupPlan(
                baseSpellings,
                description
                + " The x86 stdcall decoration byte count is unknown.",
                Error: null,
                DecoratedLookupIncomplete: true);
        }

        var spellings = baseSpellings
            .SelectMany(spelling => new[]
            {
                spelling,
                $"_{spelling}@{stackBytes}",
            })
            .ToArray();
        return new EntryPointLookupPlan(
            spellings,
            description
            + $" Each x86 stdcall lookup step probes its undecorated spelling before the proven @{stackBytes} decoration.",
            Error: null,
            DecoratedLookupIncomplete: false);
    }

    private static bool RequiresX86StdCallDecorationPlan(
        ManagedImport managed) =>
        IsWindows(managed.Target)
        && managed.Target.Architecture == InteropArchitecture.X86
        && managed.Target.CompilerAbi == InteropCompilerAbi.Msvc
        && managed.CallingConvention == InteropCallingConvention.StdCall;

    private static bool TryCalculateX86StackArgumentBytes(
        ManagedImport managed,
        out int stackBytes)
    {
        stackBytes = 0;
        foreach (var parameter in managed.Parameters)
        {
            var size = parameter.Type.SizeBytes;
            if (size is null
                || parameter.Type.Category is AbiTypeCategory.Void
                    or AbiTypeCategory.Opaque
                || size > ushort.MaxValue)
            {
                stackBytes = 0;
                return false;
            }

            int slotSize;
            try
            {
                slotSize = checked((size.Value + 3) & ~3);
                stackBytes = checked(stackBytes + slotSize);
            }
            catch (OverflowException)
            {
                stackBytes = 0;
                return false;
            }

            if (stackBytes > ushort.MaxValue)
            {
                stackBytes = 0;
                return false;
            }
        }

        return true;
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
        IReadOnlyList<Evidence> evidence,
        int candidateCount = 0) =>
        new(
            managed.SymbolCanonicalKey,
            native?.SymbolCanonicalKey,
            status,
            confidence,
            reasons,
            evidence)
        {
            CandidateCount = candidateCount,
        };

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
        string? Error,
        bool DecoratedLookupIncomplete);
}
