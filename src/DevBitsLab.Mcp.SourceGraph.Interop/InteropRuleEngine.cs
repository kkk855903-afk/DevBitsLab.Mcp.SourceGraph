using DevBitsLab.Mcp.SourceGraph.Core;

namespace DevBitsLab.Mcp.SourceGraph.Interop;

public static class InteropRuleIds
{
    public const string CallingConvention = "Interop001";
    public const string StructLayout = "Interop002";
    public const string ParameterTypeRisk = "Interop003";
    public const string CallbackGcRisk = "Interop004";
    public const string NativeException = "Interop005";
    public const string AllocatorMismatch = "Interop006";
}

public sealed record InteropBoundary(ManagedImport Managed, NativeExport Native)
{
    /// <summary>
    /// Managed callback invocations associated with this exact import/export boundary. Empty means
    /// no call-site lifetime fact is known.
    /// </summary>
    public IReadOnlyList<ManagedCallbackUsage> CallbackUsages { get; init; } = [];

    /// <summary>
    /// Managed releases of memory returned through this boundary. Empty means release behavior is
    /// unknown.
    /// </summary>
    public IReadOnlyList<ManagedReturnRelease> ReturnReleases { get; init; } = [];
}

public interface IInteropRule
{
    string RuleId { get; }

    IReadOnlyList<InteropFinding> Evaluate(InteropBoundary boundary);
}

/// <summary>
/// Deterministic rule dispatcher. Rules consume only normalized internal facts, never Roslyn,
/// Clang, PE, or protobuf objects.
/// </summary>
public sealed class InteropRuleEngine
{
    private readonly IReadOnlyList<IInteropRule> _rules;

    public InteropRuleEngine(IEnumerable<IInteropRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = rules.ToList();
        if (_rules.Select(rule => rule.RuleId).Distinct(StringComparer.Ordinal).Count()
            != _rules.Count)
        {
            throw new ArgumentException("Interop rule ids must be unique.", nameof(rules));
        }
    }

    public static InteropRuleEngine CreatePhase2() =>
        new(
            [
                new CallingConventionRule(),
                new ParameterTypeRiskRule(),
                new CallbackGcRiskRule(),
                new NativeExceptionRule(),
                new AllocatorMismatchRule(),
            ]);

    public IReadOnlyList<InteropFinding> Evaluate(InteropBoundary boundary)
    {
        ArgumentNullException.ThrowIfNull(boundary);
        return _rules
            .SelectMany(rule => rule.Evaluate(boundary))
            .OrderBy(finding => finding.RuleId, StringComparer.Ordinal)
            .ThenBy(finding => finding.Message, StringComparer.Ordinal)
            .ToList();
    }
}

public sealed class CallingConventionRule : IInteropRule
{
    public string RuleId => InteropRuleIds.CallingConvention;

    public IReadOnlyList<InteropFinding> Evaluate(InteropBoundary boundary)
    {
        ArgumentNullException.ThrowIfNull(boundary);
        var managed = boundary.Managed;
        var native = boundary.Native;
        var evidence = RuleEvidence.Combine(managed.Evidence, native.Evidence);

        if (!RuleEvidence.SameTarget(managed.Target, native.Target))
        {
            return
            [
                RuleEvidence.Finding(
                    RuleId,
                    InteropFindingSeverity.Warning,
                    "Calling convention cannot be compared because managed and native facts target different ABIs.",
                    managed,
                    native,
                    EvidenceConfidence.Inferred,
                    evidence),
            ];
        }

        var managedConvention = Normalize(managed.CallingConvention, managed.Target);
        var nativeConvention = Normalize(native.CallingConvention, native.Target);
        if (managedConvention == ConventionFamily.Unknown
            || nativeConvention == ConventionFamily.Unknown)
        {
            return
            [
                RuleEvidence.Finding(
                    RuleId,
                    InteropFindingSeverity.Warning,
                    "Calling convention is not explicit on both sides; compatibility is unknown.",
                    managed,
                    native,
                    EvidenceConfidence.Inferred,
                    evidence),
            ];
        }
        if (managedConvention == nativeConvention) return Array.Empty<InteropFinding>();

        return
        [
            RuleEvidence.Finding(
                RuleId,
                InteropFindingSeverity.Error,
                $"Calling convention mismatch: managed={managed.CallingConvention}, native={native.CallingConvention} for {managed.Target.RuntimeIdentifier}.",
                managed,
                native,
                RuleEvidence.Weakest(managed.Evidence.Confidence, native.Evidence.Confidence),
                evidence),
        ];
    }

    private static ConventionFamily Normalize(
        InteropCallingConvention convention,
        InteropTarget target)
    {
        if (target.Architecture is InteropArchitecture.X64 or InteropArchitecture.Arm64)
        {
            return convention switch
            {
                InteropCallingConvention.Cdecl
                    or InteropCallingConvention.StdCall
                    or InteropCallingConvention.ThisCall
                    or InteropCallingConvention.FastCall
                    or InteropCallingConvention.PlatformDefault => ConventionFamily.Platform,
                InteropCallingConvention.VectorCall => ConventionFamily.Vector,
                _ => ConventionFamily.Unknown,
            };
        }

        return convention switch
        {
            InteropCallingConvention.Cdecl => ConventionFamily.Cdecl,
            InteropCallingConvention.StdCall => ConventionFamily.StdCall,
            InteropCallingConvention.ThisCall => ConventionFamily.ThisCall,
            InteropCallingConvention.FastCall => ConventionFamily.FastCall,
            InteropCallingConvention.VectorCall => ConventionFamily.Vector,
            _ => ConventionFamily.Unknown,
        };
    }

    private enum ConventionFamily
    {
        Unknown,
        Platform,
        Cdecl,
        StdCall,
        ThisCall,
        FastCall,
        Vector,
    }
}

public sealed class ParameterTypeRiskRule : IInteropRule
{
    public string RuleId => InteropRuleIds.ParameterTypeRisk;

    public IReadOnlyList<InteropFinding> Evaluate(InteropBoundary boundary)
    {
        ArgumentNullException.ThrowIfNull(boundary);
        var managed = boundary.Managed;
        var native = boundary.Native;
        var findings = new List<InteropFinding>();
        var boundaryEvidence = RuleEvidence.Combine(managed.Evidence, native.Evidence);

        if (!RuleEvidence.SameTarget(managed.Target, native.Target))
        {
            findings.Add(RuleEvidence.Finding(
                RuleId,
                InteropFindingSeverity.Warning,
                "Parameter ABI cannot be compared because managed and native facts target different ABIs.",
                managed,
                native,
                EvidenceConfidence.Inferred,
                boundaryEvidence));
            return findings;
        }

        if (managed.Parameters.Count != native.Parameters.Count)
        {
            findings.Add(RuleEvidence.Finding(
                RuleId,
                InteropFindingSeverity.Error,
                $"Parameter count mismatch: managed={managed.Parameters.Count}, native={native.Parameters.Count}.",
                managed,
                native,
                RuleEvidence.Weakest(managed.Evidence.Confidence, native.Evidence.Confidence),
                boundaryEvidence));
        }

        CompareType(
            "return value",
            managed.ReturnType,
            native.ReturnType,
            managed,
            native,
            boundaryEvidence,
            findings);

        var sharedCount = Math.Min(managed.Parameters.Count, native.Parameters.Count);
        for (var index = 0; index < sharedCount; index++)
        {
            var managedParameter = managed.Parameters[index];
            var nativeParameter = native.Parameters[index];
            var evidence = RuleEvidence.Combine(
                managed.Evidence,
                native.Evidence,
                new Evidence(
                    managed.Evidence.ProducingFileId,
                    managedParameter.Location,
                    managed.Evidence.Confidence,
                    managed.Evidence.Producer),
                new Evidence(
                    native.Evidence.ProducingFileId,
                    nativeParameter.Location,
                    native.Evidence.Confidence,
                    native.Evidence.Producer));

            if (managedParameter.Direction != nativeParameter.Direction)
            {
                findings.Add(RuleEvidence.Finding(
                    RuleId,
                    InteropFindingSeverity.Error,
                    $"Parameter {index} direction mismatch: managed={managedParameter.Direction}, native={nativeParameter.Direction}.",
                    managed,
                    native,
                    RuleEvidence.Weakest(managed.Evidence.Confidence, native.Evidence.Confidence),
                    evidence));
            }
            CompareType(
                $"parameter {index} ({managedParameter.Name})",
                managedParameter.Type,
                nativeParameter.Type,
                managed,
                native,
                evidence,
                findings);
        }

        return findings;
    }

    private void CompareType(
        string subject,
        AbiTypeRef managedType,
        AbiTypeRef nativeType,
        ManagedImport managed,
        NativeExport native,
        IReadOnlyList<Evidence> evidence,
        ICollection<InteropFinding> findings)
    {
        if (managedType.PointerDepth != nativeType.PointerDepth)
        {
            findings.Add(RuleEvidence.Finding(
                RuleId,
                InteropFindingSeverity.Error,
                $"{subject} pointer depth mismatch: managed={managedType.PointerDepth}, native={nativeType.PointerDepth}.",
                managed,
                native,
                RuleEvidence.WeakestEvidence(evidence),
                evidence));
        }
        if (managedType.SizeBytes is not null
            && nativeType.SizeBytes is not null
            && managedType.SizeBytes != nativeType.SizeBytes)
        {
            findings.Add(RuleEvidence.Finding(
                RuleId,
                InteropFindingSeverity.Error,
                $"{subject} size mismatch: managed={managedType.SizeBytes} bytes, native={nativeType.SizeBytes} bytes.",
                managed,
                native,
                RuleEvidence.WeakestEvidence(evidence),
                evidence));
        }
        if (managedType.FixedArrayLength is not null
            && nativeType.FixedArrayLength is not null
            && managedType.FixedArrayLength != nativeType.FixedArrayLength)
        {
            findings.Add(RuleEvidence.Finding(
                RuleId,
                InteropFindingSeverity.Error,
                $"{subject} fixed-array length mismatch: managed={managedType.FixedArrayLength}, native={nativeType.FixedArrayLength}.",
                managed,
                native,
                RuleEvidence.WeakestEvidence(evidence),
                evidence));
        }
        if (managedType.Category is AbiTypeCategory.Opaque
            || nativeType.Category is AbiTypeCategory.Opaque)
        {
            findings.Add(RuleEvidence.Finding(
                RuleId,
                InteropFindingSeverity.Warning,
                $"{subject} contains an opaque/custom-marshaled type; ABI compatibility is unknown.",
                managed,
                native,
                EvidenceConfidence.Inferred,
                evidence));
        }
        else if (RequiresKnownSize(managedType.Category, nativeType.Category)
                 && (managedType.SizeBytes is null || nativeType.SizeBytes is null))
        {
            findings.Add(RuleEvidence.Finding(
                RuleId,
                InteropFindingSeverity.Warning,
                $"{subject} has an unknown managed or native size; ABI compatibility is unknown.",
                managed,
                native,
                EvidenceConfidence.Inferred,
                evidence));
        }
        if (managedType.IsSigned is not null
            && nativeType.IsSigned is not null
            && managedType.IsSigned != nativeType.IsSigned)
        {
            findings.Add(RuleEvidence.Finding(
                RuleId,
                InteropFindingSeverity.Warning,
                $"{subject} signedness differs between managed and native declarations.",
                managed,
                native,
                RuleEvidence.WeakestEvidence(evidence),
                evidence));
        }
        else if (managedType.Category == AbiTypeCategory.String
                 && nativeType.Category == AbiTypeCategory.String
                 && (string.IsNullOrWhiteSpace(managedType.StringEncoding)
                     || string.IsNullOrWhiteSpace(nativeType.StringEncoding)))
        {
            findings.Add(RuleEvidence.Finding(
                RuleId,
                InteropFindingSeverity.Warning,
                $"{subject} string encoding is not explicit on both sides; compatibility is unknown.",
                managed,
                native,
                EvidenceConfidence.Inferred,
                evidence));
        }
        if (!string.IsNullOrWhiteSpace(managedType.StringEncoding)
            && !string.IsNullOrWhiteSpace(nativeType.StringEncoding)
            && !string.Equals(
                managedType.StringEncoding,
                nativeType.StringEncoding,
                StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(RuleEvidence.Finding(
                RuleId,
                InteropFindingSeverity.Warning,
                $"{subject} string encoding mismatch: managed={managedType.StringEncoding}, native={nativeType.StringEncoding}.",
                managed,
                native,
                RuleEvidence.WeakestEvidence(evidence),
                evidence));
        }

        var comparableCategories =
            managedType.Category is not AbiTypeCategory.Opaque
            && nativeType.Category is not AbiTypeCategory.Opaque;
        if (comparableCategories
            && !CategoriesCompatible(managedType.Category, nativeType.Category))
        {
            findings.Add(RuleEvidence.Finding(
                RuleId,
                InteropFindingSeverity.Error,
                $"{subject} category mismatch: managed={managedType.Category}, native={nativeType.Category}.",
                managed,
                native,
                RuleEvidence.WeakestEvidence(evidence),
                evidence));
        }
    }

    private static bool CategoriesCompatible(AbiTypeCategory left, AbiTypeCategory right)
    {
        if (left == right) return true;
        return left is AbiTypeCategory.SignedInteger or AbiTypeCategory.UnsignedInteger
            && right is AbiTypeCategory.SignedInteger or AbiTypeCategory.UnsignedInteger;
    }

    private static bool RequiresKnownSize(AbiTypeCategory left, AbiTypeCategory right) =>
        left is AbiTypeCategory.Boolean
            or AbiTypeCategory.SignedInteger
            or AbiTypeCategory.UnsignedInteger
            or AbiTypeCategory.FloatingPoint
            or AbiTypeCategory.Enum
            or AbiTypeCategory.Record
            or AbiTypeCategory.Array
        || right is AbiTypeCategory.Boolean
            or AbiTypeCategory.SignedInteger
            or AbiTypeCategory.UnsignedInteger
            or AbiTypeCategory.FloatingPoint
            or AbiTypeCategory.Enum
            or AbiTypeCategory.Record
            or AbiTypeCategory.Array;
}

public sealed class CallbackGcRiskRule : IInteropRule
{
    public string RuleId => InteropRuleIds.CallbackGcRisk;

    public IReadOnlyList<InteropFinding> Evaluate(InteropBoundary boundary)
    {
        ArgumentNullException.ThrowIfNull(boundary);
        if (!RuleEvidence.SameTarget(boundary.Managed.Target, boundary.Native.Target))
        {
            return [];
        }

        var findings = new List<InteropFinding>();
        foreach (var retention in boundary.Native.RetainedCallbacks)
        {
            if (retention.ParameterPosition < 0
                || !boundary.Native.Parameters.Any(parameter =>
                    parameter.Position == retention.ParameterPosition)
                || !RuleEvidence.SameTarget(retention.Target, boundary.Native.Target))
            {
                continue;
            }

            foreach (var usage in boundary.CallbackUsages)
            {
                if (usage.Rooting != CallbackGcRooting.Unrooted
                    || usage.ParameterPosition != retention.ParameterPosition
                    || string.IsNullOrWhiteSpace(usage.CallerSymbolCanonicalKey)
                    || !boundary.Managed.Parameters.Any(parameter =>
                        parameter.Position == usage.ParameterPosition)
                    || !RuleEvidence.SameTarget(usage.Target, boundary.Managed.Target))
                {
                    continue;
                }

                var evidence = RuleEvidence.Combine(
                    boundary.Managed.Evidence,
                    boundary.Native.Evidence,
                    retention.Evidence,
                    usage.Evidence);
                findings.Add(RuleEvidence.Finding(
                    RuleId,
                    InteropFindingSeverity.Warning,
                    $"Callback parameter {usage.ParameterPosition} is retained by native code but managed caller '{usage.CallerSymbolCanonicalKey}' does not establish a GC root.",
                    boundary.Managed,
                    boundary.Native,
                    RuleEvidence.WeakestEvidence(evidence),
                    evidence,
                    usage.CallerSymbolCanonicalKey));
            }
        }

        return findings;
    }
}

public sealed class NativeExceptionRule : IInteropRule
{
    public string RuleId => InteropRuleIds.NativeException;

    public IReadOnlyList<InteropFinding> Evaluate(InteropBoundary boundary)
    {
        ArgumentNullException.ThrowIfNull(boundary);
        var escape = boundary.Native.ExceptionEscape;
        if (escape is null
            || !RuleEvidence.SameTarget(boundary.Managed.Target, boundary.Native.Target)
            || !RuleEvidence.SameTarget(escape.Target, boundary.Native.Target))
        {
            return [];
        }

        var evidence = RuleEvidence.Combine(
            boundary.Managed.Evidence,
            boundary.Native.Evidence,
            escape.Evidence);
        return
        [
            RuleEvidence.Finding(
                RuleId,
                InteropFindingSeverity.Error,
                "A native exception is proven able to escape across the C ABI boundary.",
                boundary.Managed,
                boundary.Native,
                RuleEvidence.WeakestEvidence(evidence),
                evidence),
        ];
    }
}

public sealed class AllocatorMismatchRule : IInteropRule
{
    public string RuleId => InteropRuleIds.AllocatorMismatch;

    public IReadOnlyList<InteropFinding> Evaluate(InteropBoundary boundary)
    {
        ArgumentNullException.ThrowIfNull(boundary);
        var allocation = boundary.Native.ReturnAllocation;
        if (allocation is null
            || allocation.AllocatorFamily == InteropAllocatorFamily.Unknown
            || !RuleEvidence.SameTarget(boundary.Managed.Target, boundary.Native.Target)
            || !RuleEvidence.SameTarget(allocation.Target, boundary.Native.Target))
        {
            return [];
        }

        var findings = new List<InteropFinding>();
        foreach (var release in boundary.ReturnReleases)
        {
            if (release.ReleaseFamily == InteropAllocatorFamily.Unknown
                || release.ReleaseFamily == allocation.AllocatorFamily
                || string.IsNullOrWhiteSpace(release.CallerSymbolCanonicalKey)
                || !RuleEvidence.SameTarget(release.Target, boundary.Managed.Target))
            {
                continue;
            }

            var evidence = RuleEvidence.Combine(
                boundary.Managed.Evidence,
                boundary.Native.Evidence,
                allocation.Evidence,
                release.Evidence);
            findings.Add(RuleEvidence.Finding(
                RuleId,
                InteropFindingSeverity.Warning,
                $"Native return memory uses {allocation.AllocatorFamily}, but managed caller '{release.CallerSymbolCanonicalKey}' releases it with {release.ReleaseFamily}.",
                boundary.Managed,
                boundary.Native,
                RuleEvidence.WeakestEvidence(evidence),
                evidence,
                release.CallerSymbolCanonicalKey));
        }

        return findings;
    }
}

internal static class RuleEvidence
{
    public static bool SameTarget(InteropTarget left, InteropTarget right) =>
        left.IsAbiEquivalentTo(right);

    public static EvidenceConfidence Weakest(
        EvidenceConfidence left,
        EvidenceConfidence right) =>
        (EvidenceConfidence)Math.Min((int)left, (int)right);

    public static EvidenceConfidence WeakestEvidence(IReadOnlyList<Evidence> evidence) =>
        evidence.Count == 0
            ? EvidenceConfidence.Inferred
            : evidence.Min(item => item.Confidence);

    public static IReadOnlyList<Evidence> Combine(params Evidence[] evidence) =>
        evidence.Distinct().ToList();

    public static InteropFinding Finding(
        string ruleId,
        InteropFindingSeverity severity,
        string message,
        ManagedImport managed,
        NativeExport native,
        EvidenceConfidence confidence,
        IReadOnlyList<Evidence> evidence,
        string? managedSymbolCanonicalKey = null) =>
        new(
            ruleId,
            severity,
            message,
            managedSymbolCanonicalKey ?? managed.SymbolCanonicalKey,
            native.SymbolCanonicalKey,
            confidence,
            evidence);
}
