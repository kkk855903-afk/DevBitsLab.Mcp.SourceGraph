using DevBitsLab.Mcp.SourceGraph.Core;

namespace DevBitsLab.Mcp.SourceGraph.Interop;

/// <summary>
/// Adapts a Phase 3 ABI record result into the stable Interop002 finding shape. Compatible
/// layouts produce no finding; incomplete comparisons remain warnings.
/// </summary>
public sealed class Interop002FindingAdapter
{
    private const int MaximumFindingEvidence = 4096;
    private const int MaximumReasonCharacters = 512;

    public string RuleId => InteropRuleIds.StructLayout;

    public InteropFinding? CreateFinding(AbiCompatibilityResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Compatibility == InteropCompatibility.Compatible)
        {
            return null;
        }

        var severity = result.Compatibility == InteropCompatibility.Error
            ? InteropFindingSeverity.Error
            : InteropFindingSeverity.Warning;
        var evidence = result.Evidence
            .Take(MaximumFindingEvidence)
            .ToArray();
        var evidenceWasTrimmed = result.Evidence.Count > evidence.Length;
        var reason = result.Differences.Count == 0
            ? "No complete compatibility proof is available."
            : Truncate(result.Differences[0], MaximumReasonCharacters);
        var issueCount = result.Differences.Count;
        var message = result.Compatibility == InteropCompatibility.Error
            ? $"Struct layout comparison found {issueCount} incompatible or unknown checks. {reason}"
            : $"Struct layout compatibility could not be proven across {issueCount} checks. {reason}";
        if (evidenceWasTrimmed)
        {
            message += $" Evidence was limited to {MaximumFindingEvidence} items.";
        }

        return new InteropFinding(
            RuleId,
            severity,
            message,
            result.ManagedSymbolCanonicalKey,
            result.NativeSymbolCanonicalKey,
            evidenceWasTrimmed
                ? EvidenceConfidence.Inferred
                : result.Confidence,
            evidence);
    }

    private static string Truncate(string value, int maximumCharacters) =>
        value.Length <= maximumCharacters
            ? value
            : value[..maximumCharacters] + "…";
}
