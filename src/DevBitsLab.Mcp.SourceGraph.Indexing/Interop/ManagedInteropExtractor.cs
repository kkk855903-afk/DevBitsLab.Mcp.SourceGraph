using System.Globalization;
using DevBitsLab.Mcp.SourceGraph.Core;
using Microsoft.CodeAnalysis;

namespace DevBitsLab.Mcp.SourceGraph.Indexing.Interop;

/// <summary>
/// Converts Roslyn P/Invoke declarations into the analyzer-neutral interop model. Roslyn symbols
/// never escape this adapter; downstream matching and rules consume only <see cref="ManagedImport"/>.
/// </summary>
internal static class ManagedInteropExtractor
{
    private const string DllImportAttribute =
        "System.Runtime.InteropServices.DllImportAttribute";
    private const string LibraryImportAttribute =
        "System.Runtime.InteropServices.LibraryImportAttribute";
    private const string MarshalAsAttribute =
        "System.Runtime.InteropServices.MarshalAsAttribute";
    private const string InAttribute =
        "System.Runtime.InteropServices.InAttribute";
    private const string OutAttribute =
        "System.Runtime.InteropServices.OutAttribute";
    private const string UnmanagedCallConvAttribute =
        "System.Runtime.InteropServices.UnmanagedCallConvAttribute";

    public static ManagedImport? TryExtract(
        IMethodSymbol method,
        InteropTarget target,
        long producingFileId)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentOutOfRangeException.ThrowIfLessThan(producingFileId, 1);

        var imports = method.GetAttributes()
            .Where(attribute =>
                IsAttribute(attribute, DllImportAttribute)
                || IsAttribute(attribute, LibraryImportAttribute))
            .ToArray();
        if (imports.Length != 1) return null;

        var import = imports[0];
        var libraryName = import.ConstructorArguments.FirstOrDefault().Value as string;
        if (string.IsNullOrWhiteSpace(libraryName)) return null;

        var symbolKey = SymbolMapping.CanonicalKey(method);
        if (symbolKey is null) return null;

        var kind = IsAttribute(import, DllImportAttribute)
            ? ManagedImportKind.DllImport
            : ManagedImportKind.LibraryImport;
        var declarationLocation =
            ToSourceLocation(import.ApplicationSyntaxReference?.GetSyntax().GetLocation())
            ?? ToSourceLocation(method.Locations.FirstOrDefault(location => location.IsInSource));
        if (declarationLocation is null) return null;

        var characterSet = kind == ManagedImportKind.DllImport
            ? ParseDllImportCharacterSet(import, target)
            : ParseLibraryImportCharacterSet(import);
        var callingConvention = kind == ManagedImportKind.DllImport
            ? ParseDllImportCallingConvention(import)
            : ParseLibraryImportCallingConvention(method);
        var fallbackLocation = declarationLocation;
        var parameters = method.Parameters
            .Select(parameter => ExtractParameter(
                parameter,
                target,
                characterSet,
                fallbackLocation))
            .ToArray();
        var returnType = MapType(
            method.ReturnType,
            FindMarshalInfo(method.GetReturnTypeAttributes()),
            characterSet,
            target);

        return new ManagedImport(
            symbolKey,
            kind,
            libraryName.Trim(),
            GetNamedString(import, "EntryPoint") ?? method.Name,
            callingConvention,
            returnType,
            parameters,
            characterSet,
            GetNamedBoolean(import, "SetLastError") ?? false,
            target,
            new Evidence(
                producingFileId,
                declarationLocation,
                EvidenceConfidence.Semantic,
                "roslyn-managed-interop"));
    }

    private static AbiParameter ExtractParameter(
        IParameterSymbol parameter,
        InteropTarget target,
        string? characterSet,
        SourceLocation fallbackLocation)
    {
        var attributes = parameter.GetAttributes();
        var type = MapType(
            parameter.Type,
            FindMarshalInfo(attributes),
            characterSet,
            target);
        if (parameter.RefKind != RefKind.None)
        {
            type = AddIndirection(
                type,
                target,
                isPointeeConst: parameter.RefKind == RefKind.In);
        }

        var hasIn = attributes.Any(attribute => IsAttribute(attribute, InAttribute));
        var hasOut = attributes.Any(attribute => IsAttribute(attribute, OutAttribute));
        var direction = (hasIn, hasOut, parameter.RefKind) switch
        {
            (true, true, _) => AbiParameterDirection.InOut,
            (true, false, _) => AbiParameterDirection.In,
            (false, true, _) => AbiParameterDirection.Out,
            (_, _, RefKind.Out) => AbiParameterDirection.Out,
            (_, _, RefKind.Ref) => AbiParameterDirection.InOut,
            (_, _, RefKind.In) => AbiParameterDirection.In,
            _ => AbiParameterDirection.In,
        };
        var location =
            ToSourceLocation(parameter.Locations.FirstOrDefault(item => item.IsInSource))
            ?? fallbackLocation;
        return new AbiParameter(
            parameter.Ordinal,
            parameter.Name,
            type,
            direction,
            location);
    }

    internal static AbiTypeRef MapType(
        ITypeSymbol type,
        MarshalInfo? marshal,
        string? characterSet,
        InteropTarget target)
    {
        if (marshal?.IsInvalid == true)
        {
            return new AbiTypeRef(
                DisplayName(type),
                AbiTypeCategory.Opaque);
        }
        var marshaled = MapExplicitMarshal(type, marshal, characterSet, target);
        if (marshaled is not null) return marshaled;
        if (marshal is not null)
        {
            return new AbiTypeRef(
                DisplayName(type),
                AbiTypeCategory.Opaque);
        }

        if (type is IPointerTypeSymbol pointer)
        {
            return AddIndirection(
                MapType(pointer.PointedAtType, null, characterSet, target),
                target,
                isPointeeConst: false);
        }
        if (type is IFunctionPointerTypeSymbol)
        {
            return new AbiTypeRef(
                DisplayName(type),
                AbiTypeCategory.FunctionPointer,
                pointerDepth: 1,
                sizeBytes: target.PointerSizeBytes);
        }
        if (type is IArrayTypeSymbol array)
        {
            var elementType = MapType(
                array.ElementType,
                marshal: null,
                characterSet,
                target);
            return new AbiTypeRef(
                DisplayName(array.ElementType) + "[]",
                AbiTypeCategory.Array,
                pointerDepth: 1,
                sizeBytes: target.PointerSizeBytes,
                elementType: elementType);
        }
        if (type.TypeKind == TypeKind.Delegate)
        {
            return new AbiTypeRef(
                DisplayName(type),
                AbiTypeCategory.FunctionPointer,
                pointerDepth: 1,
                sizeBytes: target.PointerSizeBytes);
        }
        if (type.TypeKind == TypeKind.Enum && type is INamedTypeSymbol enumType)
        {
            var underlying = enumType.EnumUnderlyingType is null
                ? null
                : MapType(enumType.EnumUnderlyingType, null, characterSet, target);
            return new AbiTypeRef(
                DisplayName(type),
                AbiTypeCategory.Enum,
                sizeBytes: underlying?.SizeBytes,
                alignmentBytes: underlying?.AlignmentBytes,
                isSigned: underlying?.IsSigned);
        }

        return type.SpecialType switch
        {
            SpecialType.System_Void => Scalar(type, AbiTypeCategory.Void),
            SpecialType.System_Boolean => Scalar(
                type,
                AbiTypeCategory.Boolean,
                size: 4,
                signed: false),
            SpecialType.System_SByte => Integer(type, size: 1, signed: true),
            SpecialType.System_Byte => Integer(type, size: 1, signed: false),
            SpecialType.System_Int16 => Integer(type, size: 2, signed: true),
            SpecialType.System_UInt16 => Integer(type, size: 2, signed: false),
            SpecialType.System_Int32 => Integer(type, size: 4, signed: true),
            SpecialType.System_UInt32 => Integer(type, size: 4, signed: false),
            SpecialType.System_Int64 => Integer(type, size: 8, signed: true),
            SpecialType.System_UInt64 => Integer(type, size: 8, signed: false),
            SpecialType.System_Single => Scalar(
                type,
                AbiTypeCategory.FloatingPoint,
                size: 4),
            SpecialType.System_Double => Scalar(
                type,
                AbiTypeCategory.FloatingPoint,
                size: 8),
            SpecialType.System_Char => new AbiTypeRef(
                DisplayName(type),
                AbiTypeCategory.UnsignedInteger,
                sizeBytes: 2,
                alignmentBytes: 2,
                isSigned: false,
                stringEncoding: "utf-16"),
            SpecialType.System_String => new AbiTypeRef(
                DisplayName(type),
                AbiTypeCategory.String,
                pointerDepth: 1,
                sizeBytes: target.PointerSizeBytes,
                stringEncoding: characterSet),
            SpecialType.System_IntPtr => new AbiTypeRef(
                DisplayName(type),
                AbiTypeCategory.Pointer,
                pointerDepth: 1,
                sizeBytes: target.PointerSizeBytes,
                isSigned: true),
            SpecialType.System_UIntPtr => new AbiTypeRef(
                DisplayName(type),
                AbiTypeCategory.Pointer,
                pointerDepth: 1,
                sizeBytes: target.PointerSizeBytes,
                isSigned: false),
            _ when type.TypeKind == TypeKind.Struct => new AbiTypeRef(
                DisplayName(type),
                AbiTypeCategory.Record),
            _ => new AbiTypeRef(
                DisplayName(type),
                AbiTypeCategory.Opaque),
        };
    }

    private static AbiTypeRef? MapExplicitMarshal(
        ITypeSymbol type,
        MarshalInfo? marshal,
        string? characterSet,
        InteropTarget target)
    {
        if (marshal is null) return null;
        var name = DisplayName(type);
        return marshal.UnmanagedType switch
        {
            "I1" => new AbiTypeRef(
                name,
                type.SpecialType == SpecialType.System_Boolean
                    ? AbiTypeCategory.Boolean
                    : AbiTypeCategory.SignedInteger,
                sizeBytes: 1,
                alignmentBytes: 1,
                isSigned: true),
            "U1" => new AbiTypeRef(
                name,
                type.SpecialType == SpecialType.System_Boolean
                    ? AbiTypeCategory.Boolean
                    : AbiTypeCategory.UnsignedInteger,
                sizeBytes: 1,
                alignmentBytes: 1,
                isSigned: false),
            "I2" or "VariantBool" => new AbiTypeRef(
                name,
                type.SpecialType == SpecialType.System_Boolean
                    ? AbiTypeCategory.Boolean
                    : AbiTypeCategory.SignedInteger,
                sizeBytes: 2,
                alignmentBytes: 2,
                isSigned: true),
            "U2" => Integer(name, size: 2, signed: false),
            "I4" or "Error" => Integer(name, size: 4, signed: true),
            "U4" => Integer(name, size: 4, signed: false),
            "Bool" => new AbiTypeRef(
                name,
                AbiTypeCategory.Boolean,
                sizeBytes: 4,
                alignmentBytes: 4,
                isSigned: false),
            "I8" => Integer(name, size: 8, signed: true),
            "U8" => Integer(name, size: 8, signed: false),
            "R4" => new AbiTypeRef(
                name,
                AbiTypeCategory.FloatingPoint,
                sizeBytes: 4,
                alignmentBytes: 4),
            "R8" => new AbiTypeRef(
                name,
                AbiTypeCategory.FloatingPoint,
                sizeBytes: 8,
                alignmentBytes: 8),
            "SysInt" => Integer(name, target.PointerSizeBytes, signed: true),
            "SysUInt" => Integer(name, target.PointerSizeBytes, signed: false),
            "LPStr" => StringPointer(name, target, "ansi"),
            "LPWStr" or "BStr" or "TBStr" => StringPointer(
                name,
                target,
                "utf-16"),
            "LPUTF8Str" => StringPointer(name, target, "utf-8"),
            "ByValTStr" => InlineString(
                name,
                marshal.SizeConst,
                characterSet),
            "LPArray" or "SafeArray" => new AbiTypeRef(
                name,
                AbiTypeCategory.Array,
                pointerDepth: 1,
                sizeBytes: target.PointerSizeBytes,
                fixedArrayLength: marshal.SizeConst),
            "ByValArray" => InlineArray(
                type,
                marshal,
                characterSet,
                target),
            "FunctionPtr" => new AbiTypeRef(
                name,
                AbiTypeCategory.FunctionPointer,
                pointerDepth: 1,
                sizeBytes: target.PointerSizeBytes),
            "LPStruct" => new AbiTypeRef(
                name,
                AbiTypeCategory.Record,
                pointerDepth: 1,
                sizeBytes: target.PointerSizeBytes),
            "CustomMarshaler" or "AsAny" or "Struct" or "IUnknown"
                or "IDispatch" or "Interface" => new AbiTypeRef(
                    name,
                    AbiTypeCategory.Opaque),
            _ => null,
        };
    }

    private static AbiTypeRef InlineArray(
        ITypeSymbol type,
        MarshalInfo marshal,
        string? characterSet,
        InteropTarget target)
    {
        var elementType = type is IArrayTypeSymbol array
            ? array.ElementType
            : null;
        var elementMarshal = marshal.ArraySubType is null
            ? null
            : new MarshalInfo(marshal.ArraySubType, null, null);
        var element = elementType is null
            ? null
            : MapType(elementType, elementMarshal, characterSet, target);
        int? size = element?.SizeBytes is not null && marshal.SizeConst is not null
            ? TryMultiply(element.SizeBytes.Value, marshal.SizeConst.Value)
            : null;
        return new AbiTypeRef(
            DisplayName(type),
            AbiTypeCategory.Array,
            sizeBytes: size,
            alignmentBytes: element?.AlignmentBytes,
            isSigned: element?.IsSigned,
            fixedArrayLength: marshal.SizeConst,
            elementType: element);
    }

    private static AbiTypeRef InlineString(
        string name,
        int? length,
        string? characterSet)
    {
        var bytesPerCharacter = string.Equals(
            characterSet,
            "utf-16",
            StringComparison.OrdinalIgnoreCase)
            ? 2
            : 1;
        return new AbiTypeRef(
            name,
            AbiTypeCategory.String,
            sizeBytes: length is null
                ? null
                : TryMultiply(length.Value, bytesPerCharacter),
            alignmentBytes: bytesPerCharacter,
            stringEncoding: characterSet,
            fixedArrayLength: length);
    }

    private static AbiTypeRef AddIndirection(
        AbiTypeRef type,
        InteropTarget target,
        bool? isPointeeConst) =>
        new(
            type.CanonicalName,
            type.Category,
            pointerDepth: type.PointerDepth + 1,
            sizeBytes: target.PointerSizeBytes,
            alignmentBytes: target.PointerSizeBytes,
            isSigned: type.IsSigned,
            stringEncoding: type.StringEncoding,
            fixedArrayLength: type.FixedArrayLength,
            pointeeType: type,
            elementType: type.ElementType,
            isPointeeConst: isPointeeConst);

    private static AbiTypeRef Scalar(
        ITypeSymbol type,
        AbiTypeCategory category,
        int? size = null,
        bool? signed = null) =>
        Scalar(DisplayName(type), category, size, signed);

    private static AbiTypeRef Scalar(
        string name,
        AbiTypeCategory category,
        int? size = null,
        bool? signed = null) =>
        new(
            name,
            category,
            sizeBytes: size,
            alignmentBytes: size,
            isSigned: signed);

    private static AbiTypeRef Integer(
        ITypeSymbol type,
        int size,
        bool signed) =>
        Integer(DisplayName(type), size, signed);

    private static AbiTypeRef Integer(
        string name,
        int size,
        bool signed) =>
        Scalar(
            name,
            signed
                ? AbiTypeCategory.SignedInteger
                : AbiTypeCategory.UnsignedInteger,
            size,
            signed);

    private static AbiTypeRef StringPointer(
        string name,
        InteropTarget target,
        string encoding) =>
        new(
            name,
            AbiTypeCategory.String,
            pointerDepth: 1,
            sizeBytes: target.PointerSizeBytes,
            alignmentBytes: target.PointerSizeBytes,
            stringEncoding: encoding);

    internal static MarshalInfo? FindMarshalInfo(
        IEnumerable<AttributeData> attributes)
    {
        var matches = attributes
            .Where(attribute => IsAttribute(attribute, MarshalAsAttribute))
            .ToArray();
        if (matches.Length == 0) return null;
        if (matches.Length != 1) return MarshalInfo.Invalid;

        var attribute = matches[0];
        var unmanagedType = attribute.ConstructorArguments.Length == 0
            ? null
            : GetEnumName(attribute.ConstructorArguments[0]);
        return unmanagedType is null
            ? MarshalInfo.Invalid
            : new MarshalInfo(
                unmanagedType,
                NormalizePositive(GetNamedInt32(attribute, "SizeConst")),
                GetNamedEnumName(attribute, "ArraySubType"));
    }

    private static InteropCallingConvention ParseDllImportCallingConvention(
        AttributeData attribute) =>
        GetNamedEnumName(attribute, "CallingConvention") switch
        {
            "Cdecl" => InteropCallingConvention.Cdecl,
            "StdCall" => InteropCallingConvention.StdCall,
            "ThisCall" => InteropCallingConvention.ThisCall,
            "FastCall" => InteropCallingConvention.FastCall,
            "Winapi" or null => InteropCallingConvention.PlatformDefault,
            _ => InteropCallingConvention.Unknown,
        };

    private static InteropCallingConvention ParseLibraryImportCallingConvention(
        IMethodSymbol method)
    {
        var attributes = method.GetAttributes()
            .Where(item => IsAttribute(item, UnmanagedCallConvAttribute))
            .ToArray();
        if (attributes.Length == 0) return InteropCallingConvention.PlatformDefault;
        if (attributes.Length != 1) return InteropCallingConvention.Unknown;
        var attribute = attributes[0];
        if (!TryGetNamed(attribute, "CallConvs", out var callConvs)
            || callConvs.Kind != TypedConstantKind.Array)
        {
            return InteropCallingConvention.Unknown;
        }

        var conventions = callConvs.Values
            .Select(item => (item.Value as ITypeSymbol)?.Name)
            .Select(name => name switch
            {
                "CallConvCdecl" => InteropCallingConvention.Cdecl,
                "CallConvStdcall" => InteropCallingConvention.StdCall,
                "CallConvThiscall" or "CallConvMemberFunction" =>
                    InteropCallingConvention.ThisCall,
                "CallConvFastcall" => InteropCallingConvention.FastCall,
                "CallConvVectorcall" => InteropCallingConvention.VectorCall,
                _ => InteropCallingConvention.Unknown,
            })
            .Where(value => value != InteropCallingConvention.Unknown)
            .Distinct()
            .ToArray();
        return conventions.Length == 1
            ? conventions[0]
            : InteropCallingConvention.Unknown;
    }

    private static string? ParseDllImportCharacterSet(
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

    private static string? ParseLibraryImportCharacterSet(
        AttributeData attribute) =>
        GetNamedEnumName(attribute, "StringMarshalling") switch
        {
            "Utf8" => "utf-8",
            "Utf16" => "utf-16",
            "Custom" => "custom",
            _ => null,
        };

    private static string DisplayName(ITypeSymbol type) =>
        type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            .Replace("global::", string.Empty, StringComparison.Ordinal);

    private static int? NormalizePositive(int? value) =>
        value is > 0 ? value : null;

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

    private static string? GetNamedString(
        AttributeData attribute,
        string name) =>
        TryGetNamed(attribute, name, out var value)
            ? value.Value as string
            : null;

    private static bool? GetNamedBoolean(
        AttributeData attribute,
        string name) =>
        TryGetNamed(attribute, name, out var value)
            && value.Value is bool result
                ? result
                : null;

    private static int? GetNamedInt32(
        AttributeData attribute,
        string name) =>
        TryGetNamed(attribute, name, out var value)
            && value.Value is int result
                ? result
                : null;

    private static string? GetNamedEnumName(
        AttributeData attribute,
        string name) =>
        TryGetNamed(attribute, name, out var value)
            ? GetEnumName(value)
            : null;

    private static bool TryGetNamed(
        AttributeData attribute,
        string name,
        out TypedConstant value)
    {
        foreach (var pair in attribute.NamedArguments)
        {
            if (!string.Equals(pair.Key, name, StringComparison.Ordinal)) continue;
            value = pair.Value;
            return true;
        }
        value = default;
        return false;
    }

    private static string? GetEnumName(TypedConstant constant)
    {
        if (constant.Type is not INamedTypeSymbol enumType
            || enumType.TypeKind != TypeKind.Enum
            || constant.Value is null)
        {
            return null;
        }

        var rawValue = Convert.ToInt64(
            constant.Value,
            CultureInfo.InvariantCulture);
        foreach (var field in enumType.GetMembers().OfType<IFieldSymbol>())
        {
            if (!field.HasConstantValue || field.ConstantValue is null) continue;
            if (Convert.ToInt64(
                    field.ConstantValue,
                    CultureInfo.InvariantCulture) == rawValue)
            {
                return field.Name;
            }
        }
        return null;
    }

    internal sealed record MarshalInfo(
        string UnmanagedType,
        int? SizeConst,
        string? ArraySubType,
        bool IsInvalid = false)
    {
        public static MarshalInfo Invalid { get; } =
            new(string.Empty, null, null, IsInvalid: true);
    }
}
