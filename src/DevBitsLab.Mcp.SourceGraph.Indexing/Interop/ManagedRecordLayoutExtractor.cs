using System.Globalization;
using DevBitsLab.Mcp.SourceGraph.Core;
using Microsoft.CodeAnalysis;

namespace DevBitsLab.Mcp.SourceGraph.Indexing.Interop;

/// <summary>
/// Computes a target-specific managed struct layout from Roslyn symbols and explicit interop
/// attributes. Any unknown field size or invalid layout input propagates to unknown offsets/size.
/// </summary>
internal static class ManagedRecordLayoutExtractor
{
    private const string StructLayoutAttribute =
        "System.Runtime.InteropServices.StructLayoutAttribute";
    private const string FieldOffsetAttribute =
        "System.Runtime.InteropServices.FieldOffsetAttribute";

    public static AbiRecordLayout? TryExtract(
        INamedTypeSymbol type,
        InteropTarget target,
        long producingFileId)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentOutOfRangeException.ThrowIfLessThan(producingFileId, 1);
        if (type.TypeKind != TypeKind.Struct) return null;

        var layoutAttributes = type.GetAttributes()
            .Where(attribute => IsAttribute(attribute, StructLayoutAttribute))
            .ToArray();
        if (layoutAttributes.Length > 1) return null;
        var layoutAttribute = layoutAttributes.SingleOrDefault();
        var kindName = layoutAttribute is null
            ? "Sequential"
            : layoutAttribute.ConstructorArguments.Length == 0
                ? null
                : GetEnumName(layoutAttribute.ConstructorArguments[0]);
        var kind = kindName switch
        {
            "Sequential" => AbiRecordKind.Sequential,
            "Explicit" => AbiRecordKind.Explicit,
            _ => (AbiRecordKind?)null,
        };
        if (kind is null) return null;

        var typeKey = SymbolMapping.CanonicalKey(type);
        var declarationLocation =
            ToSourceLocation(layoutAttribute?.ApplicationSyntaxReference?.GetSyntax().GetLocation())
            ?? ToSourceLocation(type.Locations.FirstOrDefault(location => location.IsInSource));
        if (typeKey is null || declarationLocation is null) return null;

        var declaredPack = layoutAttribute is null
            ? null
            : GetNamedInt32(layoutAttribute, "Pack");
        var effectivePack = declaredPack switch
        {
            null or 0 => target.DefaultPack,
            > 0 and <= 128 when IsPowerOfTwo(declaredPack.Value) =>
                declaredPack.Value,
            _ => (int?)null,
        };
        var declaredSize = layoutAttribute is null
            ? null
            : NormalizePositive(GetNamedInt32(layoutAttribute, "Size"));
        var characterSet = layoutAttribute is null
            ? null
            : ParseCharacterSet(layoutAttribute, target);
        var evidence = new Evidence(
            producingFileId,
            declarationLocation,
            EvidenceConfidence.Semantic,
            "roslyn-managed-layout");

        var fieldFacts = type.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(field =>
                !field.IsStatic
                && !field.IsConst
                && !field.IsImplicitlyDeclared)
            .OrderBy(FieldPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(FieldPosition)
            .Select((field, order) => ExtractField(
                field,
                order,
                target,
                characterSet,
                producingFileId,
                declarationLocation))
            .ToArray();
        var hasMultipleDeclarations = type.DeclaringSyntaxReferences.Length > 1;
        var computed = kind == AbiRecordKind.Explicit
            ? ComputeExplicit(fieldFacts)
            : ComputeSequential(
                fieldFacts,
                hasMultipleDeclarations ? null : effectivePack);

        return new AbiRecordLayout(
            typeKey,
            kind.Value,
            declaredSize ?? computed.Size,
            computed.Alignment,
            effectivePack,
            computed.Fields,
            target,
            evidence);
    }

    private static FieldFact ExtractField(
        IFieldSymbol field,
        int order,
        InteropTarget target,
        string? characterSet,
        long producingFileId,
        SourceLocation fallbackLocation)
    {
        var type = field.IsFixedSizeBuffer && field.FixedSize > 0
            ? MapFixedBuffer(field, target, characterSet)
            : ManagedInteropExtractor.MapType(
                field.Type,
                ManagedInteropExtractor.FindMarshalInfo(field.GetAttributes()),
                characterSet,
                target);
        var location =
            ToSourceLocation(field.Locations.FirstOrDefault(item => item.IsInSource))
            ?? fallbackLocation;
        var evidence = new Evidence(
            producingFileId,
            location,
            EvidenceConfidence.Semantic,
            "roslyn-managed-layout");
        var offsets = field.GetAttributes()
            .Where(attribute => IsAttribute(attribute, FieldOffsetAttribute))
            .ToArray();
        var explicitOffset = offsets.Length == 1
            && offsets[0].ConstructorArguments.Length == 1
            && offsets[0].ConstructorArguments[0].Value is int value
            && value >= 0
                ? value
                : (int?)null;
        return new FieldFact(
            order,
            field.Name,
            type,
            explicitOffset,
            evidence);
    }

    private static AbiTypeRef MapFixedBuffer(
        IFieldSymbol field,
        InteropTarget target,
        string? characterSet)
    {
        var elementType = field.Type is IPointerTypeSymbol pointer
            ? pointer.PointedAtType
            : field.Type;
        var element = ManagedInteropExtractor.MapType(
            elementType,
            marshal: null,
            characterSet,
            target);
        int? size = element.SizeBytes is null
            ? null
            : TryMultiply(element.SizeBytes.Value, field.FixedSize);
        return new AbiTypeRef(
            element.CanonicalName + "[]",
            AbiTypeCategory.Array,
            sizeBytes: size,
            alignmentBytes: element.AlignmentBytes,
            isSigned: element.IsSigned,
            stringEncoding: element.StringEncoding,
            fixedArrayLength: field.FixedSize);
    }

    private static LayoutComputation ComputeSequential(
        IReadOnlyList<FieldFact> fields,
        int? pack)
    {
        var rows = new List<AbiFieldLayout>(fields.Count);
        var cursor = 0;
        var maxAlignment = 1;
        var layoutKnown = pack is not null;
        foreach (var field in fields)
        {
            var naturalAlignment = field.Type.AlignmentBytes
                ?? field.Type.SizeBytes;
            int? alignment = naturalAlignment is null || pack is null
                ? null
                : Math.Min(naturalAlignment.Value, pack.Value);
            int? offset = null;
            if (layoutKnown
                && alignment is > 0
                && field.Type.SizeBytes is not null
                && TryAlign(cursor, alignment.Value, out var aligned)
                && TryAdd(aligned, field.Type.SizeBytes.Value, out var next))
            {
                offset = aligned;
                cursor = next;
                maxAlignment = Math.Max(maxAlignment, alignment.Value);
            }
            else
            {
                layoutKnown = false;
            }
            rows.Add(ToLayout(field, offset));
        }

        int? size;
        if (!layoutKnown)
        {
            size = null;
        }
        else if (fields.Count == 0)
        {
            size = 1;
        }
        else
        {
            size = TryAlign(cursor, maxAlignment, out var aligned)
                ? aligned
                : null;
        }
        return new LayoutComputation(rows, size, layoutKnown ? maxAlignment : null);
    }

    private static LayoutComputation ComputeExplicit(
        IReadOnlyList<FieldFact> fields)
    {
        var rows = fields
            .Select(field => ToLayout(field, field.ExplicitOffset))
            .ToArray();
        var sizeKnown = true;
        var maxEnd = 0;
        var maxAlignment = 1;
        foreach (var field in fields)
        {
            if (field.ExplicitOffset is null
                || field.Type.SizeBytes is null
                || !TryAdd(
                    field.ExplicitOffset.Value,
                    field.Type.SizeBytes.Value,
                    out var end))
            {
                sizeKnown = false;
                continue;
            }
            maxEnd = Math.Max(maxEnd, end);
            maxAlignment = Math.Max(
                maxAlignment,
                field.Type.AlignmentBytes ?? 1);
        }
        return new LayoutComputation(
            rows,
            sizeKnown ? Math.Max(1, maxEnd) : null,
            sizeKnown ? maxAlignment : null);
    }

    private static AbiFieldLayout ToLayout(
        FieldFact field,
        int? offset) =>
        new(
            field.Order,
            field.Name,
            field.Type,
            offset,
            field.Type.SizeBytes,
            field.Evidence);

    private static string FieldPath(IFieldSymbol field) =>
        field.Locations.FirstOrDefault(location => location.IsInSource)
            ?.SourceTree?.FilePath
        ?? string.Empty;

    private static int FieldPosition(IFieldSymbol field) =>
        field.Locations.FirstOrDefault(location => location.IsInSource)
            ?.SourceSpan.Start
        ?? int.MaxValue;

    private static string? ParseCharacterSet(
        AttributeData attribute,
        InteropTarget target) =>
        GetNamedEnumName(attribute, "CharSet") switch
        {
            "Ansi" => "ansi",
            "Unicode" => "utf-16",
            "Auto" when target.RuntimeIdentifier.StartsWith(
                "win-",
                StringComparison.OrdinalIgnoreCase) => "utf-16",
            "Auto" => "auto",
            _ => null,
        };

    private static SourceLocation? ToSourceLocation(Location? location)
    {
        if (location is null || !location.IsInSource) return null;
        var span = location.GetLineSpan();
        return new SourceLocation(
            span.Path,
            span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1,
            span.EndLinePosition.Line + 1,
            span.EndLinePosition.Character + 1);
    }

    private static bool IsAttribute(AttributeData attribute, string metadataName) =>
        string.Equals(
            attribute.AttributeClass?.ToDisplayString(),
            metadataName,
            StringComparison.Ordinal);

    private static int? GetNamedInt32(
        AttributeData attribute,
        string name)
    {
        foreach (var pair in attribute.NamedArguments)
        {
            if (string.Equals(pair.Key, name, StringComparison.Ordinal)
                && pair.Value.Value is int value)
            {
                return value;
            }
        }
        return null;
    }

    private static string? GetNamedEnumName(
        AttributeData attribute,
        string name)
    {
        foreach (var pair in attribute.NamedArguments)
        {
            if (string.Equals(pair.Key, name, StringComparison.Ordinal))
            {
                return GetEnumName(pair.Value);
            }
        }
        return null;
    }

    private static string? GetEnumName(TypedConstant constant)
    {
        if (constant.Type is not INamedTypeSymbol enumType
            || enumType.TypeKind != TypeKind.Enum
            || constant.Value is null)
        {
            return null;
        }
        var value = Convert.ToInt64(
            constant.Value,
            CultureInfo.InvariantCulture);
        return enumType.GetMembers()
            .OfType<IFieldSymbol>()
            .FirstOrDefault(field =>
                field.HasConstantValue
                && field.ConstantValue is not null
                && Convert.ToInt64(
                    field.ConstantValue,
                    CultureInfo.InvariantCulture) == value)
            ?.Name;
    }

    private static int? NormalizePositive(int? value) =>
        value is > 0 ? value : null;

    private static bool IsPowerOfTwo(int value) =>
        (value & (value - 1)) == 0;

    private static int? TryMultiply(int left, int right)
    {
        try
        {
            return checked(left * right);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static bool TryAdd(int left, int right, out int result)
    {
        try
        {
            result = checked(left + right);
            return true;
        }
        catch (OverflowException)
        {
            result = 0;
            return false;
        }
    }

    private static bool TryAlign(int value, int alignment, out int result)
    {
        try
        {
            result = checked((value + alignment - 1) / alignment * alignment);
            return true;
        }
        catch (OverflowException)
        {
            result = 0;
            return false;
        }
    }

    private sealed record FieldFact(
        int Order,
        string Name,
        AbiTypeRef Type,
        int? ExplicitOffset,
        Evidence Evidence);

    private sealed record LayoutComputation(
        IReadOnlyList<AbiFieldLayout> Fields,
        int? Size,
        int? Alignment);
}
