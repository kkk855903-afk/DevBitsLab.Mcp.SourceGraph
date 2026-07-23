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
        var sameEntryPoint = candidates
            .Where(native => string.Equals(
                native.ExportName,
                managed.EntryPoint,
                StringComparison.Ordinal))
            .ToArray();
        if (sameEntryPoint.Length == 0)
        {
            return Result(
                managed,
                native: null,
                InteropMatchStatus.Unmatched,
                managed.Evidence.Confidence,
                [$"No native export has the exact entry-point spelling '{managed.EntryPoint}'."],
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

        var candidatesWithLibrary = sameTarget
            .Where(native => !string.IsNullOrWhiteSpace(native.LibraryName))
            .ToArray();
        var candidatesWithoutLibrary = sameTarget
            .Where(native => string.IsNullOrWhiteSpace(native.LibraryName))
            .ToArray();
        if (candidatesWithLibrary.Length == 0)
        {
            return Result(
                managed,
                native: null,
                InteropMatchStatus.Unknown,
                EvidenceConfidence.Inferred,
                ["The entry point exists, but its owning native module is unknown."],
                Combine(
                    managed.Evidence,
                    sameTarget.Select(item => item.Evidence)));
        }

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
                    $"{libraryMatches.Length} native exports share the exact module, entry point, and target ABI.",
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
                    libraryMatches.Length == 1
                        ? "One exact candidate exists, but another same-entry-point candidate has unknown module ownership, so uniqueness is not proven."
                        : "Known modules do not match and at least one same-entry-point candidate has unknown module ownership.",
                ],
                Combine(
                    managed.Evidence,
                    sameTarget.Select(item => item.Evidence)));
        }
        if (libraryMatches.Length == 0)
        {
            return Result(
                managed,
                native: null,
                InteropMatchStatus.Unmatched,
                Weakest(
                    Combine(
                        managed.Evidence,
                        candidatesWithLibrary.Select(item => item.Evidence))),
                [
                    $"The exact entry point exists, but no candidate belongs to managed module '{managed.LibraryName}'.",
                ],
                Combine(
                    managed.Evidence,
                    candidatesWithLibrary.Select(item => item.Evidence)));
        }
        var native = libraryMatches[0];
        var evidence = Combine(managed.Evidence, [native.Evidence]);
        return Result(
            managed,
            native,
            InteropMatchStatus.Matched,
            Weakest(evidence),
            [
                $"Entry point matches exactly: {managed.EntryPoint}.",
                $"Module matches after target-aware normalization: {managed.LibraryName} -> {native.LibraryName}.",
                $"Target ABI matches: {managed.Target.RuntimeIdentifier}/{managed.Target.CompilerAbi}.",
                native.IsBinaryVerified
                    ? "The export was verified in the native binary."
                    : "The export is source-derived and has not been verified in a native binary.",
            ],
            evidence);
    }

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
}
