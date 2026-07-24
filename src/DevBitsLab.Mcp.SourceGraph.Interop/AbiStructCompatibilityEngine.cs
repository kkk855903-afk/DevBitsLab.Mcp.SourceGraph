using DevBitsLab.Mcp.SourceGraph.Core;

namespace DevBitsLab.Mcp.SourceGraph.Interop;

/// <summary>
/// Compares normalized managed/native record layouts without depending on Roslyn or Clang.
/// Missing facts remain warnings; only evidenced mismatches become errors.
/// </summary>
public sealed class AbiStructCompatibilityEngine
{
    private const int MaximumFieldsPerRecord = 4096;
    private const int MaximumNestedMappings = 4096;
    private const int MaximumComparedFields = 4096;
    private const int MaximumComparedTypes = 8192;
    private const int MaximumNestedDepth = 32;
    private const int MaximumChecks = 65_536;
    private const int MaximumResultEvidence = 4096;

    public AbiCompatibilityResult Compare(
        AbiRecordLayout managed,
        AbiRecordLayout native,
        IReadOnlyList<AbiRecordIdentityMapping>? nestedRecords = null)
    {
        ArgumentNullException.ThrowIfNull(managed);
        ArgumentNullException.ThrowIfNull(native);

        nestedRecords ??= [];
        var context = new ComparisonContext(managed, native);
        var recordEvidence = EvidenceFor(managed.Evidence, native.Evidence);
        if (!managed.Target.IsAbiEquivalentTo(native.Target))
        {
            context.Add(
                "$",
                AbiCompatibilityAspect.Target,
                InteropCompatibility.Warning,
                "Managed and native layouts target different ABIs; no layout comparison was performed.",
                recordEvidence,
                isUnknown: true);
            return context.Build();
        }

        context.Add(
            "$",
            AbiCompatibilityAspect.Target,
            InteropCompatibility.Compatible,
            $"Both layouts target {managed.Target.RuntimeIdentifier}/{managed.Target.CompilerAbi}.",
            recordEvidence);

        if (!CollectionsAreBounded(managed, native, nestedRecords, context))
        {
            return context.Build();
        }

        var mappings = new NestedMappingIndex(nestedRecords);
        CompareRecord(
            managed,
            native,
            "$",
            depth: 0,
            mappings,
            new HashSet<LayoutPairKey>(),
            context);
        return context.Build();
    }

    private static bool CollectionsAreBounded(
        AbiRecordLayout managed,
        AbiRecordLayout native,
        IReadOnlyList<AbiRecordIdentityMapping> nestedRecords,
        ComparisonContext context)
    {
        var evidence = EvidenceFor(managed.Evidence, native.Evidence);
        if (nestedRecords.Count > MaximumNestedMappings)
        {
            context.Add(
                "$",
                AbiCompatibilityAspect.CollectionLimit,
                InteropCompatibility.Warning,
                $"Nested record mappings exceed the {MaximumNestedMappings}-item comparison limit.",
                evidence,
                isUnknown: true);
            return false;
        }

        if (!RecordCollectionIsBounded(managed, "$.managed", evidence, context)
            || !RecordCollectionIsBounded(native, "$.native", evidence, context))
        {
            return false;
        }

        for (var index = 0; index < nestedRecords.Count; index++)
        {
            var mapping = nestedRecords[index];
            if (mapping is null)
            {
                context.Add(
                    "$",
                    AbiCompatibilityAspect.CollectionLimit,
                    InteropCompatibility.Warning,
                    "Nested record mappings contain a null item.",
                    evidence,
                    isUnknown: true);
                return false;
            }

            var mappingEvidence = EvidenceFor(
                mapping.ManagedLayout.Evidence,
                mapping.NativeLayout.Evidence);
            if (!RecordCollectionIsBounded(
                    mapping.ManagedLayout,
                    $"$.mapping[{index}].managed",
                    mappingEvidence,
                    context)
                || !RecordCollectionIsBounded(
                    mapping.NativeLayout,
                    $"$.mapping[{index}].native",
                    mappingEvidence,
                    context))
            {
                return false;
            }
        }

        return true;
    }

    private static bool RecordCollectionIsBounded(
        AbiRecordLayout record,
        string path,
        IReadOnlyList<Evidence> evidence,
        ComparisonContext context)
    {
        if (record.Fields is not null
            && record.Fields.Count <= MaximumFieldsPerRecord)
        {
            return true;
        }

        context.Add(
            path,
            AbiCompatibilityAspect.CollectionLimit,
            InteropCompatibility.Warning,
            $"A record field collection is null or exceeds the {MaximumFieldsPerRecord}-item comparison limit.",
            evidence,
            isUnknown: true);
        return false;
    }

    private static void CompareRecord(
        AbiRecordLayout managed,
        AbiRecordLayout native,
        string path,
        int depth,
        NestedMappingIndex mappings,
        HashSet<LayoutPairKey> activePairs,
        ComparisonContext context)
    {
        var recordEvidence = EvidenceFor(managed.Evidence, native.Evidence);
        if (depth > MaximumNestedDepth)
        {
            context.Add(
                path,
                AbiCompatibilityAspect.RecursionLimit,
                InteropCompatibility.Warning,
                $"Nested record comparison exceeds the {MaximumNestedDepth}-level recursion limit.",
                recordEvidence,
                isUnknown: true);
            return;
        }

        if (!managed.Target.IsAbiEquivalentTo(native.Target)
            || !managed.Target.IsAbiEquivalentTo(context.Target))
        {
            context.Add(
                path,
                AbiCompatibilityAspect.Target,
                InteropCompatibility.Warning,
                "A nested record does not target the exact root ABI; its layout was not compared.",
                recordEvidence,
                isUnknown: true);
            return;
        }

        var pairKey = new LayoutPairKey(
            managed.SymbolCanonicalKey,
            native.SymbolCanonicalKey);
        if (!activePairs.Add(pairKey))
        {
            context.Add(
                path,
                AbiCompatibilityAspect.Cycle,
                InteropCompatibility.Warning,
                "An inline nested-record cycle prevents a finite compatibility proof.",
                recordEvidence,
                isUnknown: true);
            return;
        }

        try
        {
            CompareRecordKind(managed, native, path, recordEvidence, context);
            CompareKnownDimension(
                path,
                AbiCompatibilityAspect.RecordSize,
                "record size",
                managed.SizeBytes,
                native.SizeBytes,
                recordEvidence,
                context);
            CompareKnownDimension(
                path,
                AbiCompatibilityAspect.RecordAlignment,
                "record alignment",
                managed.AlignmentBytes,
                native.AlignmentBytes,
                recordEvidence,
                context);
            CompareKnownDimension(
                path,
                AbiCompatibilityAspect.Pack,
                "effective pack",
                managed.Pack,
                native.Pack,
                recordEvidence,
                context);

            var managedFields = OrderFields(managed.Fields, out var managedOrderKnown);
            var nativeFields = OrderFields(native.Fields, out var nativeOrderKnown);
            CompareFieldCount(
                managedFields.Count,
                nativeFields.Count,
                path,
                recordEvidence,
                context);
            if (!managedOrderKnown || !nativeOrderKnown)
            {
                context.Add(
                    path,
                    AbiCompatibilityAspect.FieldOrder,
                    InteropCompatibility.Warning,
                    "One layout has duplicate or non-contiguous field-order facts; fields were not paired.",
                    recordEvidence,
                    isUnknown: true);
                return;
            }

            if (managedFields.Count == 0 && nativeFields.Count == 0)
            {
                context.Add(
                    path,
                    AbiCompatibilityAspect.FieldOrder,
                    InteropCompatibility.Compatible,
                    "Both layouts have an empty field order.",
                    recordEvidence);
            }
            else if (managedFields.Count != nativeFields.Count)
            {
                context.Add(
                    path,
                    AbiCompatibilityAspect.FieldOrder,
                    InteropCompatibility.Warning,
                    "Field order cannot be fully compared when field counts differ.",
                    recordEvidence,
                    isUnknown: true);
            }
            var sharedCount = Math.Min(managedFields.Count, nativeFields.Count);
            for (var index = 0; index < sharedCount; index++)
            {
                if (!context.TryCompareField(path))
                {
                    return;
                }

                CompareField(
                    managedFields[index],
                    nativeFields[index],
                    $"{path}.field[{index}]",
                    depth,
                    mappings,
                    activePairs,
                    context);
            }
        }
        finally
        {
            activePairs.Remove(pairKey);
        }
    }

    private static void CompareRecordKind(
        AbiRecordLayout managed,
        AbiRecordLayout native,
        string path,
        IReadOnlyList<Evidence> evidence,
        ComparisonContext context)
    {
        var rolesAreValid =
            managed.Kind is AbiRecordKind.Sequential or AbiRecordKind.Explicit
            && native.Kind == AbiRecordKind.Native;
        context.Add(
            path,
            AbiCompatibilityAspect.RecordKind,
            rolesAreValid
                ? InteropCompatibility.Compatible
                : InteropCompatibility.Warning,
            rolesAreValid
                ? $"Managed {managed.Kind} and native layout facts have valid comparison roles."
                : "The pair is not a managed Sequential/Explicit layout and a native layout; compatibility is unknown.",
            evidence,
            isUnknown: !rolesAreValid);
    }

    private static IReadOnlyList<AbiFieldLayout> OrderFields(
        IReadOnlyList<AbiFieldLayout> fields,
        out bool orderKnown)
    {
        var ordered = fields
            .OrderBy(field => field?.Order ?? int.MaxValue)
            .ThenBy(field => field?.Name, StringComparer.Ordinal)
            .ToArray();
        orderKnown = ordered.Length == fields.Count;
        for (var index = 0; index < ordered.Length && orderKnown; index++)
        {
            orderKnown = ordered[index] is not null
                && ordered[index].Order == index;
        }
        return ordered!;
    }

    private static void CompareFieldCount(
        int managedCount,
        int nativeCount,
        string path,
        IReadOnlyList<Evidence> evidence,
        ComparisonContext context)
    {
        context.Add(
            path,
            AbiCompatibilityAspect.FieldCount,
            managedCount == nativeCount
                ? InteropCompatibility.Compatible
                : InteropCompatibility.Error,
            managedCount == nativeCount
                ? $"Both layouts contain {managedCount} fields."
                : $"Field count mismatch: managed={managedCount}, native={nativeCount}.",
            evidence);
    }

    private static void CompareField(
        AbiFieldLayout managed,
        AbiFieldLayout native,
        string path,
        int recordDepth,
        NestedMappingIndex mappings,
        HashSet<LayoutPairKey> activePairs,
        ComparisonContext context)
    {
        var evidence = EvidenceFor(
            managed.Evidence,
            native.Evidence);
        context.Add(
            path,
            AbiCompatibilityAspect.FieldOrder,
            InteropCompatibility.Compatible,
            $"Both facts identify this field as ABI ordinal {managed.Order}.",
            evidence);
        CompareKnownDimension(
            path,
            AbiCompatibilityAspect.FieldOffset,
            "field offset",
            managed.OffsetBytes,
            native.OffsetBytes,
            evidence,
            context);
        CompareKnownDimension(
            path,
            AbiCompatibilityAspect.FieldSize,
            "field size",
            managed.SizeBytes,
            native.SizeBytes,
            evidence,
            context);
        CompareType(
            managed.Type,
            native.Type,
            path,
            typeDepth: 0,
            recordDepth,
            compareTypeSize: false,
            evidence,
            mappings,
            activePairs,
            context);
    }

    private static void CompareType(
        AbiTypeRef managed,
        AbiTypeRef native,
        string path,
        int typeDepth,
        int recordDepth,
        bool compareTypeSize,
        IReadOnlyList<Evidence> evidence,
        NestedMappingIndex mappings,
        HashSet<LayoutPairKey> activePairs,
        ComparisonContext context)
    {
        if (!context.TryCompareType(path))
        {
            return;
        }
        if (typeDepth > MaximumNestedDepth)
        {
            context.Add(
                path,
                AbiCompatibilityAspect.RecursionLimit,
                InteropCompatibility.Warning,
                $"ABI type comparison exceeds the {MaximumNestedDepth}-level recursion limit.",
                evidence,
                isUnknown: true);
            return;
        }

        var managedPointer = IsPointer(managed);
        var nativePointer = IsPointer(native);
        if (managedPointer || nativePointer)
        {
            if (managedPointer && nativePointer)
            {
                context.Add(
                    path,
                    AbiCompatibilityAspect.FieldCategory,
                    InteropCompatibility.Compatible,
                    "Both fields use an indirect pointer representation.",
                    evidence);
            }
            else
            {
                CompareCategory(managed, native, path, evidence, context);
            }
            ComparePointer(
                managed,
                native,
                managedPointer,
                nativePointer,
                path,
                evidence,
                context);
            return;
        }

        CompareCategory(managed, native, path, evidence, context);
        if (managed.Category == AbiTypeCategory.Boolean
            || native.Category == AbiTypeCategory.Boolean)
        {
            CompareKnownDimension(
                path,
                AbiCompatibilityAspect.BooleanSize,
                "boolean size",
                managed.SizeBytes,
                native.SizeBytes,
                evidence,
                context);
        }
        else if (compareTypeSize
                 && (managed.Category != AbiTypeCategory.Record
                     || native.Category != AbiTypeCategory.Record))
        {
            CompareKnownDimension(
                path,
                AbiCompatibilityAspect.FieldSize,
                "nested type size",
                managed.SizeBytes,
                native.SizeBytes,
                evidence,
                context);
        }

        CompareFixedArrayLength(managed, native, path, evidence, context);
        if (managed.Category == AbiTypeCategory.Array
            && native.Category == AbiTypeCategory.Array)
        {
            if (managed.ElementType is null || native.ElementType is null)
            {
                context.Add(
                    path,
                    AbiCompatibilityAspect.FieldCategory,
                    InteropCompatibility.Warning,
                    "An inline array element type is unknown on one or both sides.",
                    evidence,
                    isUnknown: true);
            }
            else
            {
                CompareType(
                    managed.ElementType,
                    native.ElementType,
                    $"{path}.element",
                    typeDepth + 1,
                    recordDepth,
                    compareTypeSize: true,
                    evidence,
                    mappings,
                    activePairs,
                    context);
            }
        }

        if (managed.Category == AbiTypeCategory.Record
            && native.Category == AbiTypeCategory.Record)
        {
            CompareNestedRecord(
                managed,
                native,
                path,
                recordDepth,
                evidence,
                mappings,
                activePairs,
                context);
        }
    }

    private static void CompareCategory(
        AbiTypeRef managed,
        AbiTypeRef native,
        string path,
        IReadOnlyList<Evidence> evidence,
        ComparisonContext context)
    {
        if (managed.Category == AbiTypeCategory.Opaque
            || native.Category == AbiTypeCategory.Opaque)
        {
            context.Add(
                path,
                AbiCompatibilityAspect.FieldCategory,
                InteropCompatibility.Warning,
                "An opaque field type prevents a layout compatibility proof.",
                evidence,
                isUnknown: true);
            return;
        }

        if (managed.Category == native.Category)
        {
            context.Add(
                path,
                AbiCompatibilityAspect.FieldCategory,
                InteropCompatibility.Compatible,
                $"Both fields use the {managed.Category} ABI category.",
                evidence);
            return;
        }

        var integerPair =
            managed.Category is AbiTypeCategory.SignedInteger
                or AbiTypeCategory.UnsignedInteger
            && native.Category is AbiTypeCategory.SignedInteger
                or AbiTypeCategory.UnsignedInteger;
        var representationOnlyPair =
            managed.Category is AbiTypeCategory.Boolean
                or AbiTypeCategory.SignedInteger
                or AbiTypeCategory.UnsignedInteger
            && native.Category is AbiTypeCategory.Boolean
                or AbiTypeCategory.SignedInteger
                or AbiTypeCategory.UnsignedInteger;
        context.Add(
            path,
            AbiCompatibilityAspect.FieldCategory,
            integerPair
                ? InteropCompatibility.Compatible
                : representationOnlyPair
                    ? InteropCompatibility.Warning
                    : InteropCompatibility.Error,
            integerPair
                ? "Signed and unsigned integer fields are layout-equivalent when their proven sizes match."
                : representationOnlyPair
                    ? "Field categories differ but may share a representation; semantic compatibility is unknown."
                    : $"Field category mismatch: managed={managed.Category}, native={native.Category}.",
            evidence,
            isUnknown: representationOnlyPair && !integerPair);
    }

    private static void ComparePointer(
        AbiTypeRef managed,
        AbiTypeRef native,
        bool managedPointer,
        bool nativePointer,
        string path,
        IReadOnlyList<Evidence> evidence,
        ComparisonContext context)
    {
        if (!managedPointer || !nativePointer)
        {
            context.Add(
                path,
                AbiCompatibilityAspect.PointerDepth,
                InteropCompatibility.Error,
                "Only one field is represented as a pointer.",
                evidence);
            return;
        }

        var depthsValid = managed.PointerDepth > 0
            && native.PointerDepth > 0;
        context.Add(
            path,
            AbiCompatibilityAspect.PointerDepth,
            !depthsValid
                ? InteropCompatibility.Warning
                : managed.PointerDepth == native.PointerDepth
                    ? InteropCompatibility.Compatible
                    : InteropCompatibility.Error,
            !depthsValid
                ? "A pointer category has no proven positive pointer depth."
                : managed.PointerDepth == native.PointerDepth
                    ? $"Both fields have pointer depth {managed.PointerDepth}."
                    : $"Pointer depth mismatch: managed={managed.PointerDepth}, native={native.PointerDepth}.",
            evidence,
            isUnknown: !depthsValid);

        if (managed.SizeBytes is null || native.SizeBytes is null)
        {
            context.Add(
                path,
                AbiCompatibilityAspect.PointerSize,
                InteropCompatibility.Warning,
                "Pointer size is unknown on one or both sides.",
                evidence,
                isUnknown: true);
            return;
        }

        var expected = context.Target.PointerSizeBytes;
        var sizesMatch = managed.SizeBytes == native.SizeBytes
            && managed.SizeBytes == expected;
        context.Add(
            path,
            AbiCompatibilityAspect.PointerSize,
            sizesMatch
                ? InteropCompatibility.Compatible
                : InteropCompatibility.Error,
            sizesMatch
                ? $"Both pointer fields use the target's {expected}-byte pointer size."
                : $"Pointer size mismatch: managed={managed.SizeBytes}, native={native.SizeBytes}, target={expected}.",
            evidence);
    }

    private static bool IsPointer(AbiTypeRef type) =>
        type.PointerDepth > 0
        || type.Category is AbiTypeCategory.Pointer
            or AbiTypeCategory.FunctionPointer;

    private static void CompareFixedArrayLength(
        AbiTypeRef managed,
        AbiTypeRef native,
        string path,
        IReadOnlyList<Evidence> evidence,
        ComparisonContext context)
    {
        var needsLength =
            managed.FixedArrayLength is not null
            || native.FixedArrayLength is not null
            || managed.Category == AbiTypeCategory.Array
                && managed.PointerDepth == 0
            || native.Category == AbiTypeCategory.Array
                && native.PointerDepth == 0;
        if (!needsLength)
        {
            return;
        }

        CompareKnownDimension(
            path,
            AbiCompatibilityAspect.FixedArrayLength,
            "fixed array length",
            managed.FixedArrayLength,
            native.FixedArrayLength,
            evidence,
            context);
    }

    private static void CompareNestedRecord(
        AbiTypeRef managed,
        AbiTypeRef native,
        string path,
        int recordDepth,
        IReadOnlyList<Evidence> fieldEvidence,
        NestedMappingIndex mappings,
        HashSet<LayoutPairKey> activePairs,
        ComparisonContext context)
    {
        var resolution = mappings.Resolve(
            managed.CanonicalName,
            native.CanonicalName);
        if (resolution.Kind != NestedMappingResolutionKind.Exact)
        {
            var compatibility =
                resolution.Kind == NestedMappingResolutionKind.Mismatch
                    ? InteropCompatibility.Error
                    : InteropCompatibility.Warning;
            var reason = resolution.Kind switch
            {
                NestedMappingResolutionKind.Mismatch =>
                    "The exact managed/native nested-record identities conflict with the supplied one-to-one mapping.",
                NestedMappingResolutionKind.Ambiguous =>
                    "Nested-record identity mapping is duplicate or one-to-many.",
                _ =>
                    "No exact managed/native nested-record identity mapping is available.",
            };
            context.Add(
                path,
                AbiCompatibilityAspect.NestedRecordIdentity,
                compatibility,
                reason,
                fieldEvidence,
                isUnknown: compatibility == InteropCompatibility.Warning);
            return;
        }

        var mapping = resolution.Mapping!;
        var nestedEvidence = EvidenceFor(
            fieldEvidence,
            mapping.ManagedLayout.Evidence,
            mapping.NativeLayout.Evidence);
        context.Add(
            path,
            AbiCompatibilityAspect.NestedRecordIdentity,
            InteropCompatibility.Compatible,
            "The nested record identities have one exact explicit mapping.",
            nestedEvidence);

        var start = context.CheckCount;
        CompareRecord(
            mapping.ManagedLayout,
            mapping.NativeLayout,
            $"{path}.record",
            recordDepth + 1,
            mappings,
            activePairs,
            context);
        var nestedCompatibility = context.CompatibilitySince(start);
        context.Add(
            path,
            AbiCompatibilityAspect.NestedRecordLayout,
            nestedCompatibility,
            nestedCompatibility switch
            {
                InteropCompatibility.Compatible =>
                    "The exactly mapped nested record layout is compatible.",
                InteropCompatibility.Error =>
                    "The exactly mapped nested record layout contains a proven mismatch.",
                _ =>
                    "The exactly mapped nested record layout could not be proven compatible.",
            },
            nestedEvidence,
            isUnknown: nestedCompatibility == InteropCompatibility.Warning);
    }

    private static void CompareKnownDimension(
        string path,
        AbiCompatibilityAspect aspect,
        string label,
        int? managed,
        int? native,
        IReadOnlyList<Evidence> evidence,
        ComparisonContext context)
    {
        if (managed is null || native is null)
        {
            context.Add(
                path,
                aspect,
                InteropCompatibility.Warning,
                $"{label} is unknown on one or both sides.",
                evidence,
                isUnknown: true);
            return;
        }

        context.Add(
            path,
            aspect,
            managed == native
                ? InteropCompatibility.Compatible
                : InteropCompatibility.Error,
            managed == native
                ? $"Both layouts use {label} {managed}."
                : $"{label} mismatch: managed={managed}, native={native}.",
            evidence);
    }

    private static IReadOnlyList<Evidence> EvidenceFor(params Evidence[] evidence) =>
        OrderEvidence(evidence);

    private static IReadOnlyList<Evidence> EvidenceFor(
        IReadOnlyList<Evidence> existing,
        params Evidence[] evidence) =>
        OrderEvidence(existing.Concat(evidence));

    private static IReadOnlyList<Evidence> OrderEvidence(
        IEnumerable<Evidence> evidence) =>
        evidence
            .Where(item => item is not null)
            .Distinct()
            .OrderBy(item => item.Location.FilePath, StringComparer.Ordinal)
            .ThenBy(item => item.Location.StartLine)
            .ThenBy(item => item.Location.StartColumn)
            .ThenBy(item => item.Location.EndLine)
            .ThenBy(item => item.Location.EndColumn)
            .ThenBy(item => item.ProducingFileId)
            .ThenBy(item => item.Producer, StringComparer.Ordinal)
            .ToArray();

    private readonly record struct LayoutPairKey(
        string ManagedSymbolCanonicalKey,
        string NativeSymbolCanonicalKey);

    private readonly record struct TypeNamePair(
        string ManagedTypeCanonicalName,
        string NativeTypeCanonicalName);

    private enum NestedMappingResolutionKind
    {
        Missing,
        Exact,
        Mismatch,
        Ambiguous,
    }

    private sealed record NestedMappingResolution(
        NestedMappingResolutionKind Kind,
        AbiRecordIdentityMapping? Mapping = null);

    private sealed class NestedMappingIndex
    {
        private readonly Dictionary<TypeNamePair, List<AbiRecordIdentityMapping>> _exact = [];
        private readonly Dictionary<string, HashSet<string>> _managedToNative =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _nativeToManaged =
            new(StringComparer.Ordinal);

        public NestedMappingIndex(
            IReadOnlyList<AbiRecordIdentityMapping> mappings)
        {
            foreach (var mapping in mappings
                         .OrderBy(
                             item => item.ManagedTypeCanonicalName,
                             StringComparer.Ordinal)
                         .ThenBy(
                             item => item.NativeTypeCanonicalName,
                             StringComparer.Ordinal)
                         .ThenBy(
                             item => item.ManagedLayout.SymbolCanonicalKey,
                             StringComparer.Ordinal)
                         .ThenBy(
                             item => item.NativeLayout.SymbolCanonicalKey,
                             StringComparer.Ordinal))
            {
                var pair = new TypeNamePair(
                    mapping.ManagedTypeCanonicalName,
                    mapping.NativeTypeCanonicalName);
                if (!_exact.TryGetValue(pair, out var exact))
                {
                    exact = [];
                    _exact.Add(pair, exact);
                }
                exact.Add(mapping);
                AddIdentity(
                    _managedToNative,
                    mapping.ManagedTypeCanonicalName,
                    mapping.NativeTypeCanonicalName);
                AddIdentity(
                    _nativeToManaged,
                    mapping.NativeTypeCanonicalName,
                    mapping.ManagedTypeCanonicalName);
            }
        }

        public NestedMappingResolution Resolve(
            string managedTypeCanonicalName,
            string nativeTypeCanonicalName)
        {
            var pair = new TypeNamePair(
                managedTypeCanonicalName,
                nativeTypeCanonicalName);
            if (_exact.TryGetValue(pair, out var exact))
            {
                if (exact.Count == 1
                    && _managedToNative[managedTypeCanonicalName].Count == 1
                    && _nativeToManaged[nativeTypeCanonicalName].Count == 1)
                {
                    return new NestedMappingResolution(
                        NestedMappingResolutionKind.Exact,
                        exact[0]);
                }
                return new NestedMappingResolution(
                    NestedMappingResolutionKind.Ambiguous);
            }

            return _managedToNative.ContainsKey(managedTypeCanonicalName)
                || _nativeToManaged.ContainsKey(nativeTypeCanonicalName)
                    ? new NestedMappingResolution(
                        NestedMappingResolutionKind.Mismatch)
                    : new NestedMappingResolution(
                        NestedMappingResolutionKind.Missing);
        }

        private static void AddIdentity(
            Dictionary<string, HashSet<string>> index,
            string key,
            string value)
        {
            if (!index.TryGetValue(key, out var values))
            {
                values = new HashSet<string>(StringComparer.Ordinal);
                index.Add(key, values);
            }
            values.Add(value);
        }
    }

    private sealed class ComparisonContext
    {
        private readonly AbiRecordLayout _managed;
        private readonly AbiRecordLayout _native;
        private readonly List<AbiCompatibilityCheck> _checks = [];
        private int _comparedFields;
        private int _comparedTypes;
        private bool _comparisonLimitReported;
        private bool _checkLimitReported;

        public ComparisonContext(
            AbiRecordLayout managed,
            AbiRecordLayout native)
        {
            _managed = managed;
            _native = native;
            Target = managed.Target;
        }

        public InteropTarget Target { get; }
        public int CheckCount => _checks.Count;

        public bool TryCompareField(string path)
        {
            if (_comparedFields++ < MaximumComparedFields)
            {
                return true;
            }

            AddComparisonLimit(
                path,
                $"Field comparisons exceed the {MaximumComparedFields}-item limit.");
            return false;
        }

        public bool TryCompareType(string path)
        {
            if (_comparedTypes++ < MaximumComparedTypes)
            {
                return true;
            }

            AddComparisonLimit(
                path,
                $"Type comparisons exceed the {MaximumComparedTypes}-item limit.");
            return false;
        }

        public void Add(
            string path,
            AbiCompatibilityAspect aspect,
            InteropCompatibility compatibility,
            string reason,
            IReadOnlyList<Evidence> evidence,
            bool isUnknown = false)
        {
            if (_checks.Count >= MaximumChecks - 1)
            {
                if (!_checkLimitReported)
                {
                    _checkLimitReported = true;
                    AddInternal(
                        "$",
                        AbiCompatibilityAspect.CollectionLimit,
                        InteropCompatibility.Warning,
                        $"Compatibility checks exceed the {MaximumChecks}-item result limit.",
                        EvidenceFor(_managed.Evidence, _native.Evidence),
                        EvidenceConfidence.Inferred);
                }
                return;
            }

            AddInternal(
                path,
                aspect,
                compatibility,
                reason,
                evidence,
                isUnknown
                    ? EvidenceConfidence.Inferred
                    : WeakestEvidence(evidence));
        }

        public InteropCompatibility CompatibilitySince(int start)
        {
            var checks = _checks.Skip(start);
            return checks.Any(check =>
                    check.Compatibility == InteropCompatibility.Error)
                ? InteropCompatibility.Error
                : checks.Any(check =>
                    check.Compatibility is InteropCompatibility.Warning
                        or InteropCompatibility.Unknown)
                    ? InteropCompatibility.Warning
                    : InteropCompatibility.Compatible;
        }

        public AbiCompatibilityResult Build()
        {
            var orderedEvidence = OrderEvidence(
                _checks.SelectMany(check => check.Evidence));
            if (orderedEvidence.Count > MaximumResultEvidence)
            {
                Add(
                    "$",
                    AbiCompatibilityAspect.CollectionLimit,
                    InteropCompatibility.Warning,
                    $"Aggregate evidence exceeds the {MaximumResultEvidence}-item result limit; per-check evidence remains available.",
                    EvidenceFor(_managed.Evidence, _native.Evidence),
                    isUnknown: true);
                orderedEvidence = OrderEvidence(
                    _checks.SelectMany(check => check.Evidence));
            }

            var compatibility = CompatibilitySince(0);
            var materialChecks = _checks.ToArray();
            var materialDifferences = materialChecks
                .Where(check =>
                    check.Compatibility != InteropCompatibility.Compatible)
                .Select(check => $"{check.Path}: {check.Reason}")
                .ToArray();
            var confidenceChecks = materialChecks
                .Where(check =>
                    check.Compatibility != InteropCompatibility.Compatible)
                .DefaultIfEmpty(materialChecks.First());
            var confidence = confidenceChecks.Min(check => check.Confidence);
            return new AbiCompatibilityResult(
                _managed.SymbolCanonicalKey,
                _native.SymbolCanonicalKey,
                compatibility,
                materialDifferences,
                confidence,
                orderedEvidence.Take(MaximumResultEvidence).ToArray())
            {
                Checks = materialChecks,
            };
        }

        private void AddComparisonLimit(string path, string reason)
        {
            if (_comparisonLimitReported)
            {
                return;
            }

            _comparisonLimitReported = true;
            Add(
                path,
                AbiCompatibilityAspect.CollectionLimit,
                InteropCompatibility.Warning,
                reason,
                EvidenceFor(_managed.Evidence, _native.Evidence),
                isUnknown: true);
        }

        private void AddInternal(
            string path,
            AbiCompatibilityAspect aspect,
            InteropCompatibility compatibility,
            string reason,
            IReadOnlyList<Evidence> evidence,
            EvidenceConfidence confidence) =>
            _checks.Add(new AbiCompatibilityCheck(
                path,
                aspect,
                compatibility,
                reason,
                confidence,
                evidence));

        private static EvidenceConfidence WeakestEvidence(
            IReadOnlyList<Evidence> evidence) =>
            evidence.Count == 0
                ? EvidenceConfidence.Inferred
                : evidence.Min(item => item.Confidence);
    }
}
