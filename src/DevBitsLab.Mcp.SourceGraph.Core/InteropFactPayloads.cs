using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevBitsLab.Mcp.SourceGraph.Core;

/// <summary>
/// Stable annotation flavors used to persist normalized interop facts.
/// </summary>
public static class InteropAnnotationFlavors
{
    public const string ManagedImport = "interop-managed-import";
    public const string NativeExport = "interop-native-export";
    public const string AbiRecord = "interop-abi-record";
}

/// <summary>
/// Serializes normalized interop facts into a versioned, analyzer-neutral annotation payload.
/// SQLite ownership is intentionally not part of the wire format: every decoded
/// <see cref="Evidence"/> receives the annotation owner's file id supplied by the caller.
/// </summary>
public static class InteropFactPayloadCodec
{
    public const int CurrentVersion = 1;
    public const int MaximumPayloadBytes = 256 * 1024;

    private const int MaximumJsonDepth = 64;
    private const int MaximumTypeDepth = 32;
    private const int MaximumCollectionItems = 4096;
    private const int MaximumMetadataEntries = 256;
    private const int MaximumStringCharacters = 32 * 1024;
    private const int MaximumPointerDepth = 32;

    private const string ManagedImportKindToken = "managed_import";
    private const string NativeExportKindToken = "native_export";
    private const string AbiRecordKindToken = "abi_record";

    private static readonly UTF8Encoding _strictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = MaximumJsonDepth,
        WriteIndented = false,
    };

    public static string EncodeManagedImport(ManagedImport import)
    {
        ArgumentNullException.ThrowIfNull(import);
        var payload = ToPayload(import);
        Validate(payload, ownerFileId: 1);
        return Serialize(payload);
    }

    public static ManagedImport DecodeManagedImport(
        string json,
        long ownerFileId)
    {
        var payload = Deserialize<ManagedImportPayload>(json);
        return Validate(payload, ownerFileId);
    }

    public static string EncodeNativeExport(NativeExport export)
    {
        ArgumentNullException.ThrowIfNull(export);
        var payload = ToPayload(export);
        Validate(payload, ownerFileId: 1);
        return Serialize(payload);
    }

    public static NativeExport DecodeNativeExport(
        string json,
        long ownerFileId)
    {
        var payload = Deserialize<NativeExportPayload>(json);
        return Validate(payload, ownerFileId);
    }

    public static string EncodeAbiRecord(AbiRecordLayout record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var payload = ToPayload(record);
        Validate(payload, ownerFileId: 1);
        return Serialize(payload);
    }

    public static AbiRecordLayout DecodeAbiRecord(
        string json,
        long ownerFileId)
    {
        var payload = Deserialize<AbiRecordPayload>(json);
        return Validate(payload, ownerFileId);
    }

    private static string Serialize<T>(T payload)
    {
        byte[] bytes;
        try
        {
            bytes = JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw Invalid("The interop payload could not be encoded.", ex);
        }

        if (bytes.Length > MaximumPayloadBytes)
        {
            throw Invalid(
                $"The interop payload exceeds the {MaximumPayloadBytes}-byte limit.");
        }

        return _strictUtf8.GetString(bytes);
    }

    private static T Deserialize<T>(string json)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(json);

        int byteCount;
        try
        {
            byteCount = _strictUtf8.GetByteCount(json);
        }
        catch (EncoderFallbackException ex)
        {
            throw Invalid("The interop payload is not valid UTF-8 text.", ex);
        }

        if (byteCount > MaximumPayloadBytes)
        {
            throw Invalid(
                $"The interop payload exceeds the {MaximumPayloadBytes}-byte limit.");
        }

        byte[] bytes;
        try
        {
            bytes = _strictUtf8.GetBytes(json);
        }
        catch (EncoderFallbackException ex)
        {
            throw Invalid("The interop payload is not valid UTF-8 text.", ex);
        }

        try
        {
            EnsureUniquePropertyNames(bytes);
            return JsonSerializer.Deserialize<T>(bytes, _jsonOptions)
                ?? throw Invalid("The interop payload must be a JSON object.");
        }
        catch (InteropFactPayloadException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw Invalid("The interop payload is malformed.", ex);
        }
    }

    private static void EnsureUniquePropertyNames(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(
            json,
            new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = MaximumJsonDepth,
            });
        var objectProperties = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    objectProperties.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;

                case JsonTokenType.PropertyName:
                    if (objectProperties.Count == 0
                        || !objectProperties.Peek().Add(reader.GetString()!))
                    {
                        throw Invalid("The interop payload contains a duplicate property.");
                    }
                    break;

                case JsonTokenType.EndObject:
                    if (objectProperties.Count == 0)
                    {
                        throw Invalid("The interop payload has an invalid object boundary.");
                    }
                    objectProperties.Pop();
                    break;
            }
        }

        if (objectProperties.Count != 0)
        {
            throw Invalid("The interop payload has an unterminated object.");
        }
    }

    private static ManagedImportPayload ToPayload(ManagedImport import) =>
        new()
        {
            Version = CurrentVersion,
            Kind = ManagedImportKindToken,
            SymbolCanonicalKey = import.SymbolCanonicalKey,
            ImportKind = ToToken(import.ImportKind),
            LibraryName = import.LibraryName,
            EntryPoint = import.EntryPoint,
            CallingConvention = ToToken(import.CallingConvention),
            ReturnType = ToPayload(import.ReturnType, depth: 0),
            Parameters = ToPayload(import.Parameters),
            CharacterSet = import.CharacterSet,
            SetLastError = import.SetLastError,
            ExactSpelling = import.ExactSpelling,
            Target = ToPayload(import.Target),
            Evidence = ToPayload(import.Evidence),
        };

    private static NativeExportPayload ToPayload(NativeExport export) =>
        new()
        {
            Version = CurrentVersion,
            Kind = NativeExportKindToken,
            SymbolCanonicalKey = export.SymbolCanonicalKey,
            ExportName = export.ExportName,
            CallingConvention = ToToken(export.CallingConvention),
            ReturnType = ToPayload(export.ReturnType, depth: 0),
            Parameters = ToPayload(export.Parameters),
            HasCLinkage = export.HasCLinkage,
            IsBinaryVerified = export.IsBinaryVerified,
            LibraryName = export.LibraryName,
            ModuleIdentitySource = ToToken(export.ModuleIdentitySource),
            RetainedCallbacks = RequireCollection(
                    export.RetainedCallbacks,
                    "native_export.retained_callbacks")
                .Select(ToPayload)
                .ToList(),
            ExceptionEscape = export.ExceptionEscape is null
                ? null
                : ToPayload(export.ExceptionEscape),
            ReturnAllocation = export.ReturnAllocation is null
                ? null
                : ToPayload(export.ReturnAllocation),
            Target = ToPayload(export.Target),
            Evidence = ToPayload(export.Evidence),
        };

    private static AbiRecordPayload ToPayload(AbiRecordLayout record) =>
        new()
        {
            Version = CurrentVersion,
            Kind = AbiRecordKindToken,
            SymbolCanonicalKey = record.SymbolCanonicalKey,
            RecordKind = ToToken(record.Kind),
            SizeBytes = record.SizeBytes,
            AlignmentBytes = record.AlignmentBytes,
            Pack = record.Pack,
            Fields = RequireCollection(record.Fields, "abi_record.fields")
                .Select(ToPayload)
                .ToList(),
            Target = ToPayload(record.Target),
            Evidence = ToPayload(record.Evidence),
        };

    private static TypePayload ToPayload(AbiTypeRef type, int depth)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (depth > MaximumTypeDepth)
        {
            throw Invalid(
                $"An ABI type exceeds the {MaximumTypeDepth}-level recursion limit.");
        }

        return new TypePayload
        {
            CanonicalName = type.CanonicalName,
            Category = ToToken(type.Category),
            PointerDepth = type.PointerDepth,
            SizeBytes = type.SizeBytes,
            AlignmentBytes = type.AlignmentBytes,
            IsSigned = type.IsSigned,
            StringEncoding = type.StringEncoding,
            FixedArrayLength = type.FixedArrayLength,
            PointeeType = type.PointeeType is null
                ? null
                : ToPayload(type.PointeeType, depth + 1),
            ElementType = type.ElementType is null
                ? null
                : ToPayload(type.ElementType, depth + 1),
            IsPointeeConst = type.IsPointeeConst,
        };
    }

    private static List<ParameterPayload> ToPayload(
        IReadOnlyList<AbiParameter> parameters) =>
        RequireCollection(parameters, "parameters")
            .Select(parameter =>
            {
                ArgumentNullException.ThrowIfNull(parameter);
                return new ParameterPayload
                {
                    Position = parameter.Position,
                    Name = parameter.Name,
                    Type = ToPayload(parameter.Type, depth: 0),
                    Direction = ToToken(parameter.Direction),
                    Location = ToPayload(parameter.Location),
                };
            })
            .ToList();

    private static FieldPayload ToPayload(AbiFieldLayout field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return new FieldPayload
        {
            Order = field.Order,
            Name = field.Name,
            Type = ToPayload(field.Type, depth: 0),
            OffsetBytes = field.OffsetBytes,
            SizeBytes = field.SizeBytes,
            Evidence = ToPayload(field.Evidence),
        };
    }

    private static TargetPayload ToPayload(InteropTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new TargetPayload
        {
            RuntimeIdentifier = target.RuntimeIdentifier,
            Architecture = ToToken(target.Architecture),
            CompilerAbi = ToToken(target.CompilerAbi),
            PointerSizeBytes = target.PointerSizeBytes,
            DefaultPack = target.DefaultPack,
        };
    }

    private static LocationPayload ToPayload(SourceLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return new LocationPayload
        {
            FilePath = location.FilePath,
            StartLine = location.StartLine,
            StartColumn = location.StartColumn,
            EndLine = location.EndLine,
            EndColumn = location.EndColumn,
        };
    }

    private static EvidencePayload ToPayload(Evidence evidence)
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

    private static CallbackRetentionPayload ToPayload(
        NativeCallbackRetention retention)
    {
        ArgumentNullException.ThrowIfNull(retention);
        return new CallbackRetentionPayload
        {
            ParameterPosition = retention.ParameterPosition,
            Target = ToPayload(retention.Target),
            Evidence = ToPayload(retention.Evidence),
        };
    }

    private static ExceptionEscapePayload ToPayload(
        NativeExceptionEscape escape)
    {
        ArgumentNullException.ThrowIfNull(escape);
        return new ExceptionEscapePayload
        {
            Target = ToPayload(escape.Target),
            Evidence = ToPayload(escape.Evidence),
        };
    }

    private static ReturnAllocationPayload ToPayload(
        NativeReturnAllocation allocation)
    {
        ArgumentNullException.ThrowIfNull(allocation);
        return new ReturnAllocationPayload
        {
            AllocatorFamily = ToToken(allocation.AllocatorFamily),
            Target = ToPayload(allocation.Target),
            Evidence = ToPayload(allocation.Evidence),
        };
    }

    private static SortedDictionary<string, string>? SortMetadata(
        IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null) return null;
        if (metadata.Count > MaximumMetadataEntries)
        {
            throw Invalid(
                $"Evidence metadata exceeds the {MaximumMetadataEntries}-entry limit.");
        }

        var sorted = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in metadata)
        {
            RequireString(item.Key, "evidence.metadata key", allowWhiteSpace: true);
            RequireString(item.Value, "evidence.metadata value", allowWhiteSpace: true);
            sorted.Add(item.Key, item.Value);
        }
        return sorted;
    }

    private static ManagedImport Validate(
        ManagedImportPayload payload,
        long ownerFileId)
    {
        ValidateOwnerFileId(ownerFileId);
        ValidateHeader(payload.Version, payload.Kind, ManagedImportKindToken);
        return new ManagedImport(
            RequireString(
                payload.SymbolCanonicalKey,
                "managed_import.symbol_canonical_key"),
            ParseManagedImportKind(payload.ImportKind),
            RequireString(payload.LibraryName, "managed_import.library_name"),
            RequireString(payload.EntryPoint, "managed_import.entry_point"),
            ParseCallingConvention(payload.CallingConvention),
            FromPayload(
                RequireObject(payload.ReturnType, "managed_import.return_type"),
                depth: 0),
            FromPayload(
                RequireCollection(payload.Parameters, "managed_import.parameters")),
            OptionalString(payload.CharacterSet, "managed_import.character_set"),
            payload.SetLastError,
            FromPayload(RequireObject(payload.Target, "managed_import.target")),
            FromPayload(
                RequireObject(payload.Evidence, "managed_import.evidence"),
                ownerFileId))
        {
            ExactSpelling = payload.ExactSpelling,
        };
    }

    private static NativeExport Validate(
        NativeExportPayload payload,
        long ownerFileId)
    {
        ValidateOwnerFileId(ownerFileId);
        ValidateHeader(payload.Version, payload.Kind, NativeExportKindToken);
        var parameters = FromPayload(
            RequireCollection(payload.Parameters, "native_export.parameters"));
        var retainedCallbacks = RequireCollection(
                payload.RetainedCallbacks,
                "native_export.retained_callbacks")
            .Select((retention, index) =>
            {
                var value = FromPayload(
                    RequireObject(
                        retention,
                        $"native_export.retained_callbacks[{index}]"),
                    ownerFileId);
                if (value.ParameterPosition >= parameters.Count)
                {
                    throw Invalid(
                        $"native_export.retained_callbacks[{index}].parameter_position "
                        + "does not identify a declared parameter.");
                }
                return value;
            })
            .ToArray();

        return new NativeExport(
            RequireString(
                payload.SymbolCanonicalKey,
                "native_export.symbol_canonical_key"),
            RequireString(payload.ExportName, "native_export.export_name"),
            ParseCallingConvention(payload.CallingConvention),
            FromPayload(
                RequireObject(payload.ReturnType, "native_export.return_type"),
                depth: 0),
            parameters,
            payload.HasCLinkage,
            payload.IsBinaryVerified,
            FromPayload(RequireObject(payload.Target, "native_export.target")),
            FromPayload(
                RequireObject(payload.Evidence, "native_export.evidence"),
                ownerFileId))
        {
            LibraryName = OptionalString(
                payload.LibraryName,
                "native_export.library_name"),
            ModuleIdentitySource = ParseModuleIdentitySource(
                payload.ModuleIdentitySource),
            RetainedCallbacks = retainedCallbacks,
            ExceptionEscape = payload.ExceptionEscape is null
                ? null
                : FromPayload(payload.ExceptionEscape, ownerFileId),
            ReturnAllocation = payload.ReturnAllocation is null
                ? null
                : FromPayload(payload.ReturnAllocation, ownerFileId),
        };
    }

    private static AbiRecordLayout Validate(
        AbiRecordPayload payload,
        long ownerFileId)
    {
        ValidateOwnerFileId(ownerFileId);
        ValidateHeader(payload.Version, payload.Kind, AbiRecordKindToken);
        ValidatePositiveNullable(payload.SizeBytes, "abi_record.size_bytes");
        ValidatePositiveNullable(
            payload.AlignmentBytes,
            "abi_record.alignment_bytes");
        ValidatePack(payload.Pack, "abi_record.pack");

        var fields = RequireCollection(payload.Fields, "abi_record.fields")
            .Select((field, index) =>
            {
                var value = FromPayload(
                    RequireObject(field, $"abi_record.fields[{index}]"),
                    ownerFileId);
                if (value.Order != index)
                {
                    throw Invalid(
                        $"abi_record.fields[{index}].order must equal {index}.");
                }
                return value;
            })
            .ToArray();

        return new AbiRecordLayout(
            RequireString(
                payload.SymbolCanonicalKey,
                "abi_record.symbol_canonical_key"),
            ParseRecordKind(payload.RecordKind),
            payload.SizeBytes,
            payload.AlignmentBytes,
            payload.Pack,
            fields,
            FromPayload(RequireObject(payload.Target, "abi_record.target")),
            FromPayload(
                RequireObject(payload.Evidence, "abi_record.evidence"),
                ownerFileId));
    }

    private static AbiTypeRef FromPayload(TypePayload payload, int depth)
    {
        if (depth > MaximumTypeDepth)
        {
            throw Invalid(
                $"An ABI type exceeds the {MaximumTypeDepth}-level recursion limit.");
        }
        if (payload.PointerDepth is < 0 or > MaximumPointerDepth)
        {
            throw Invalid(
                $"type.pointer_depth must be between 0 and {MaximumPointerDepth}.");
        }
        ValidatePositiveNullable(payload.SizeBytes, "type.size_bytes");
        ValidatePositiveNullable(payload.AlignmentBytes, "type.alignment_bytes");
        ValidatePositiveNullable(
            payload.FixedArrayLength,
            "type.fixed_array_length");

        try
        {
            return new AbiTypeRef(
                RequireString(payload.CanonicalName, "type.canonical_name"),
                ParseTypeCategory(payload.Category),
                payload.PointerDepth,
                payload.SizeBytes,
                payload.AlignmentBytes,
                payload.IsSigned,
                OptionalString(
                    payload.StringEncoding,
                    "type.string_encoding"),
                payload.FixedArrayLength,
                payload.PointeeType is null
                    ? null
                    : FromPayload(payload.PointeeType, depth + 1),
                payload.ElementType is null
                    ? null
                    : FromPayload(payload.ElementType, depth + 1),
                payload.IsPointeeConst);
        }
        catch (ArgumentException ex)
        {
            throw Invalid("The ABI type contains invalid dimensions.", ex);
        }
    }

    private static IReadOnlyList<AbiParameter> FromPayload(
        IReadOnlyList<ParameterPayload> payloads)
    {
        var result = new AbiParameter[payloads.Count];
        for (var index = 0; index < payloads.Count; index++)
        {
            var payload = RequireObject(payloads[index], $"parameters[{index}]");
            if (payload.Position != index)
            {
                throw Invalid($"parameters[{index}].position must equal {index}.");
            }
            result[index] = new AbiParameter(
                payload.Position,
                RequireString(payload.Name, $"parameters[{index}].name"),
                FromPayload(
                    RequireObject(payload.Type, $"parameters[{index}].type"),
                    depth: 0),
                ParseParameterDirection(payload.Direction),
                FromPayload(
                    RequireObject(
                        payload.Location,
                        $"parameters[{index}].location")));
        }
        return result;
    }

    private static AbiFieldLayout FromPayload(
        FieldPayload payload,
        long ownerFileId)
    {
        if (payload.Order < 0)
        {
            throw Invalid("field.order must be non-negative.");
        }
        ValidateNonNegativeNullable(payload.OffsetBytes, "field.offset_bytes");
        ValidatePositiveNullable(payload.SizeBytes, "field.size_bytes");
        return new AbiFieldLayout(
            payload.Order,
            RequireString(payload.Name, "field.name"),
            FromPayload(RequireObject(payload.Type, "field.type"), depth: 0),
            payload.OffsetBytes,
            payload.SizeBytes,
            FromPayload(
                RequireObject(payload.Evidence, "field.evidence"),
                ownerFileId));
    }

    private static InteropTarget FromPayload(TargetPayload payload)
    {
        var architecture = ParseArchitecture(payload.Architecture);
        var pointerSize = architecture == InteropArchitecture.X86 ? 4 : 8;
        if (payload.PointerSizeBytes != pointerSize)
        {
            throw Invalid(
                $"target.pointer_size_bytes must be {pointerSize} for "
                + $"architecture `{payload.Architecture}`.");
        }
        ValidatePack(payload.DefaultPack, "target.default_pack", allowNull: false);

        try
        {
            return new InteropTarget(
                RequireString(
                    payload.RuntimeIdentifier,
                    "target.runtime_identifier"),
                architecture,
                ParseCompilerAbi(payload.CompilerAbi),
                payload.PointerSizeBytes,
                payload.DefaultPack);
        }
        catch (ArgumentException ex)
        {
            throw Invalid("The interop target is invalid.", ex);
        }
    }

    private static SourceLocation FromPayload(LocationPayload payload)
    {
        if (payload.StartLine <= 0
            || payload.StartColumn <= 0
            || payload.EndLine <= 0
            || payload.EndColumn <= 0)
        {
            throw Invalid("Source locations must use positive 1-based coordinates.");
        }
        if (payload.EndLine < payload.StartLine
            || (payload.EndLine == payload.StartLine
                && payload.EndColumn < payload.StartColumn))
        {
            throw Invalid("A source location end must not precede its start.");
        }

        return new SourceLocation(
            RequireString(payload.FilePath, "location.file_path"),
            payload.StartLine,
            payload.StartColumn,
            payload.EndLine,
            payload.EndColumn);
    }

    private static Evidence FromPayload(
        EvidencePayload payload,
        long ownerFileId) =>
        new(
            ownerFileId,
            FromPayload(RequireObject(payload.Location, "evidence.location")),
            ParseEvidenceConfidence(payload.Confidence),
            RequireString(payload.Producer, "evidence.producer"),
            ValidateMetadata(payload.Metadata));

    private static NativeCallbackRetention FromPayload(
        CallbackRetentionPayload payload,
        long ownerFileId)
    {
        if (payload.ParameterPosition < 0)
        {
            throw Invalid(
                "callback_retention.parameter_position must be non-negative.");
        }
        return new NativeCallbackRetention(
            payload.ParameterPosition,
            FromPayload(
                RequireObject(payload.Target, "callback_retention.target")),
            FromPayload(
                RequireObject(
                    payload.Evidence,
                    "callback_retention.evidence"),
                ownerFileId));
    }

    private static NativeExceptionEscape FromPayload(
        ExceptionEscapePayload payload,
        long ownerFileId) =>
        new(
            FromPayload(
                RequireObject(payload.Target, "exception_escape.target")),
            FromPayload(
                RequireObject(payload.Evidence, "exception_escape.evidence"),
                ownerFileId));

    private static NativeReturnAllocation FromPayload(
        ReturnAllocationPayload payload,
        long ownerFileId) =>
        new(
            ParseAllocatorFamily(payload.AllocatorFamily),
            FromPayload(
                RequireObject(payload.Target, "return_allocation.target")),
            FromPayload(
                RequireObject(payload.Evidence, "return_allocation.evidence"),
                ownerFileId));

    private static IReadOnlyDictionary<string, string>? ValidateMetadata(
        SortedDictionary<string, string>? metadata)
    {
        if (metadata is null) return null;
        if (metadata.Count > MaximumMetadataEntries)
        {
            throw Invalid(
                $"Evidence metadata exceeds the {MaximumMetadataEntries}-entry limit.");
        }

        var sorted = new Dictionary<string, string>(
            metadata.Count,
            StringComparer.Ordinal);
        foreach (var item in metadata.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var key = RequireString(
                item.Key,
                "evidence.metadata key",
                allowWhiteSpace: true);
            var value = RequireString(
                item.Value,
                "evidence.metadata value",
                allowWhiteSpace: true);
            sorted.Add(key, value);
        }
        return sorted;
    }

    private static void ValidateHeader(
        int version,
        string? kind,
        string expectedKind)
    {
        if (version != CurrentVersion)
        {
            throw Invalid($"Unsupported interop payload version `{version}`.");
        }
        if (!string.Equals(kind, expectedKind, StringComparison.Ordinal))
        {
            throw Invalid(
                $"Expected interop payload kind `{expectedKind}`, found `{kind}`.");
        }
    }

    private static void ValidateOwnerFileId(long ownerFileId)
    {
        if (ownerFileId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ownerFileId),
                ownerFileId,
                "The annotation owner file id must be positive.");
        }
    }

    private static IReadOnlyList<T> RequireCollection<T>(
        IReadOnlyList<T>? values,
        string path)
    {
        if (values is null)
        {
            throw Invalid($"{path} is required.");
        }
        if (values.Count > MaximumCollectionItems)
        {
            throw Invalid(
                $"{path} exceeds the {MaximumCollectionItems}-item limit.");
        }
        return values;
    }

    private static T RequireObject<T>(T? value, string path)
        where T : class =>
        value ?? throw Invalid($"{path} is required.");

    private static string RequireString(
        string? value,
        string path,
        bool allowWhiteSpace = false)
    {
        if (value is null
            || (!allowWhiteSpace && string.IsNullOrWhiteSpace(value)))
        {
            throw Invalid($"{path} must be a non-empty string.");
        }
        if (value.Length > MaximumStringCharacters)
        {
            throw Invalid(
                $"{path} exceeds the {MaximumStringCharacters}-character limit.");
        }
        return value;
    }

    private static string? OptionalString(string? value, string path)
    {
        if (value is null) return null;
        return RequireString(value, path);
    }

    private static void ValidatePositiveNullable(int? value, string path)
    {
        if (value is <= 0)
        {
            throw Invalid($"{path} must be positive when present.");
        }
    }

    private static void ValidateNonNegativeNullable(int? value, string path)
    {
        if (value is < 0)
        {
            throw Invalid($"{path} must be non-negative when present.");
        }
    }

    private static void ValidatePack(
        int? value,
        string path,
        bool allowNull = true)
    {
        if (value is null)
        {
            if (!allowNull) throw Invalid($"{path} is required.");
            return;
        }
        if (value is < 1 or > 128 || (value.Value & (value.Value - 1)) != 0)
        {
            throw Invalid(
                $"{path} must be a power of two between 1 and 128.");
        }
    }

    private static string ToToken(InteropArchitecture value) => value switch
    {
        InteropArchitecture.X86 => "x86",
        InteropArchitecture.X64 => "x64",
        InteropArchitecture.Arm64 => "arm64",
        _ => throw Invalid($"Unknown interop architecture `{value}`."),
    };

    private static InteropArchitecture ParseArchitecture(string? value) =>
        value switch
        {
            "x86" => InteropArchitecture.X86,
            "x64" => InteropArchitecture.X64,
            "arm64" => InteropArchitecture.Arm64,
            _ => throw Invalid($"Unknown interop architecture `{value}`."),
        };

    private static string ToToken(InteropCompilerAbi value) => value switch
    {
        InteropCompilerAbi.Msvc => "msvc",
        InteropCompilerAbi.Itanium => "itanium",
        _ => throw Invalid($"Unknown compiler ABI `{value}`."),
    };

    private static InteropCompilerAbi ParseCompilerAbi(string? value) =>
        value switch
        {
            "msvc" => InteropCompilerAbi.Msvc,
            "itanium" => InteropCompilerAbi.Itanium,
            _ => throw Invalid($"Unknown compiler ABI `{value}`."),
        };

    private static string ToToken(InteropCallingConvention value) => value switch
    {
        InteropCallingConvention.Unknown => "unknown",
        InteropCallingConvention.PlatformDefault => "platform_default",
        InteropCallingConvention.Cdecl => "cdecl",
        InteropCallingConvention.StdCall => "std_call",
        InteropCallingConvention.ThisCall => "this_call",
        InteropCallingConvention.FastCall => "fast_call",
        InteropCallingConvention.VectorCall => "vector_call",
        _ => throw Invalid($"Unknown calling convention `{value}`."),
    };

    private static InteropCallingConvention ParseCallingConvention(
        string? value) =>
        value switch
        {
            "unknown" => InteropCallingConvention.Unknown,
            "platform_default" => InteropCallingConvention.PlatformDefault,
            "cdecl" => InteropCallingConvention.Cdecl,
            "std_call" => InteropCallingConvention.StdCall,
            "this_call" => InteropCallingConvention.ThisCall,
            "fast_call" => InteropCallingConvention.FastCall,
            "vector_call" => InteropCallingConvention.VectorCall,
            _ => throw Invalid($"Unknown calling convention `{value}`."),
        };

    private static string ToToken(ManagedImportKind value) => value switch
    {
        ManagedImportKind.DllImport => "dll_import",
        ManagedImportKind.LibraryImport => "library_import",
        _ => throw Invalid($"Unknown managed import kind `{value}`."),
    };

    private static ManagedImportKind ParseManagedImportKind(string? value) =>
        value switch
        {
            "dll_import" => ManagedImportKind.DllImport,
            "library_import" => ManagedImportKind.LibraryImport,
            _ => throw Invalid($"Unknown managed import kind `{value}`."),
        };

    private static string ToToken(AbiTypeCategory value) => value switch
    {
        AbiTypeCategory.Void => "void",
        AbiTypeCategory.Boolean => "boolean",
        AbiTypeCategory.SignedInteger => "signed_integer",
        AbiTypeCategory.UnsignedInteger => "unsigned_integer",
        AbiTypeCategory.FloatingPoint => "floating_point",
        AbiTypeCategory.Enum => "enum",
        AbiTypeCategory.Record => "record",
        AbiTypeCategory.Pointer => "pointer",
        AbiTypeCategory.FunctionPointer => "function_pointer",
        AbiTypeCategory.String => "string",
        AbiTypeCategory.Array => "array",
        AbiTypeCategory.Opaque => "opaque",
        _ => throw Invalid($"Unknown ABI type category `{value}`."),
    };

    private static AbiTypeCategory ParseTypeCategory(string? value) =>
        value switch
        {
            "void" => AbiTypeCategory.Void,
            "boolean" => AbiTypeCategory.Boolean,
            "signed_integer" => AbiTypeCategory.SignedInteger,
            "unsigned_integer" => AbiTypeCategory.UnsignedInteger,
            "floating_point" => AbiTypeCategory.FloatingPoint,
            "enum" => AbiTypeCategory.Enum,
            "record" => AbiTypeCategory.Record,
            "pointer" => AbiTypeCategory.Pointer,
            "function_pointer" => AbiTypeCategory.FunctionPointer,
            "string" => AbiTypeCategory.String,
            "array" => AbiTypeCategory.Array,
            "opaque" => AbiTypeCategory.Opaque,
            _ => throw Invalid($"Unknown ABI type category `{value}`."),
        };

    private static string ToToken(AbiParameterDirection value) => value switch
    {
        AbiParameterDirection.Unknown => "unknown",
        AbiParameterDirection.In => "in",
        AbiParameterDirection.Out => "out",
        AbiParameterDirection.InOut => "in_out",
        _ => throw Invalid($"Unknown ABI parameter direction `{value}`."),
    };

    private static AbiParameterDirection ParseParameterDirection(string? value) =>
        value switch
        {
            "unknown" => AbiParameterDirection.Unknown,
            "in" => AbiParameterDirection.In,
            "out" => AbiParameterDirection.Out,
            "in_out" => AbiParameterDirection.InOut,
            _ => throw Invalid($"Unknown ABI parameter direction `{value}`."),
        };

    private static string ToToken(AbiRecordKind value) => value switch
    {
        AbiRecordKind.Sequential => "sequential",
        AbiRecordKind.Explicit => "explicit",
        AbiRecordKind.Native => "native",
        _ => throw Invalid($"Unknown ABI record kind `{value}`."),
    };

    private static AbiRecordKind ParseRecordKind(string? value) =>
        value switch
        {
            "sequential" => AbiRecordKind.Sequential,
            "explicit" => AbiRecordKind.Explicit,
            "native" => AbiRecordKind.Native,
            _ => throw Invalid($"Unknown ABI record kind `{value}`."),
        };

    private static string ToToken(NativeModuleIdentitySource value) => value switch
    {
        NativeModuleIdentitySource.Unknown => "unknown",
        NativeModuleIdentitySource.Configuration => "configuration",
        NativeModuleIdentitySource.Binary => "binary",
        _ => throw Invalid($"Unknown native module identity source `{value}`."),
    };

    private static NativeModuleIdentitySource ParseModuleIdentitySource(
        string? value) =>
        value switch
        {
            "unknown" => NativeModuleIdentitySource.Unknown,
            "configuration" => NativeModuleIdentitySource.Configuration,
            "binary" => NativeModuleIdentitySource.Binary,
            _ => throw Invalid(
                $"Unknown native module identity source `{value}`."),
        };

    private static string ToToken(EvidenceConfidence value) => value switch
    {
        EvidenceConfidence.Inferred => "inferred",
        EvidenceConfidence.Semantic => "semantic",
        EvidenceConfidence.Exact => "exact",
        _ => throw Invalid($"Unknown evidence confidence `{value}`."),
    };

    private static EvidenceConfidence ParseEvidenceConfidence(string? value) =>
        value switch
        {
            "inferred" => EvidenceConfidence.Inferred,
            "semantic" => EvidenceConfidence.Semantic,
            "exact" => EvidenceConfidence.Exact,
            _ => throw Invalid($"Unknown evidence confidence `{value}`."),
        };

    private static string ToToken(InteropAllocatorFamily value) => value switch
    {
        InteropAllocatorFamily.Unknown => "unknown",
        InteropAllocatorFamily.CrtHeap => "crt_heap",
        InteropAllocatorFamily.CppNew => "cpp_new",
        InteropAllocatorFamily.CppNewArray => "cpp_new_array",
        InteropAllocatorFamily.CoTaskMem => "co_task_mem",
        InteropAllocatorFamily.HGlobal => "hglobal",
        _ => throw Invalid($"Unknown allocator family `{value}`."),
    };

    private static InteropAllocatorFamily ParseAllocatorFamily(string? value) =>
        value switch
        {
            "unknown" => InteropAllocatorFamily.Unknown,
            "crt_heap" => InteropAllocatorFamily.CrtHeap,
            "cpp_new" => InteropAllocatorFamily.CppNew,
            "cpp_new_array" => InteropAllocatorFamily.CppNewArray,
            "co_task_mem" => InteropAllocatorFamily.CoTaskMem,
            "hglobal" => InteropAllocatorFamily.HGlobal,
            _ => throw Invalid($"Unknown allocator family `{value}`."),
        };

    private static InteropFactPayloadException Invalid(
        string message,
        Exception? innerException = null) =>
        new(message, innerException);

    private sealed class ManagedImportPayload
    {
        [JsonPropertyOrder(0)] public required int Version { get; init; }
        [JsonPropertyOrder(1)] public required string? Kind { get; init; }
        [JsonPropertyOrder(2)] public required string? SymbolCanonicalKey { get; init; }
        [JsonPropertyOrder(3)] public required string? ImportKind { get; init; }
        [JsonPropertyOrder(4)] public required string? LibraryName { get; init; }
        [JsonPropertyOrder(5)] public required string? EntryPoint { get; init; }
        [JsonPropertyOrder(6)] public required string? CallingConvention { get; init; }
        [JsonPropertyOrder(7)] public required TypePayload? ReturnType { get; init; }
        [JsonPropertyOrder(8)] public required List<ParameterPayload>? Parameters { get; init; }
        [JsonPropertyOrder(9)] public required string? CharacterSet { get; init; }
        [JsonPropertyOrder(10)] public required bool SetLastError { get; init; }
        [JsonPropertyOrder(11)] public required bool? ExactSpelling { get; init; }
        [JsonPropertyOrder(12)] public required TargetPayload? Target { get; init; }
        [JsonPropertyOrder(13)] public required EvidencePayload? Evidence { get; init; }
    }

    private sealed class NativeExportPayload
    {
        [JsonPropertyOrder(0)] public required int Version { get; init; }
        [JsonPropertyOrder(1)] public required string? Kind { get; init; }
        [JsonPropertyOrder(2)] public required string? SymbolCanonicalKey { get; init; }
        [JsonPropertyOrder(3)] public required string? ExportName { get; init; }
        [JsonPropertyOrder(4)] public required string? CallingConvention { get; init; }
        [JsonPropertyOrder(5)] public required TypePayload? ReturnType { get; init; }
        [JsonPropertyOrder(6)] public required List<ParameterPayload>? Parameters { get; init; }
        [JsonPropertyOrder(7)] public required bool HasCLinkage { get; init; }
        [JsonPropertyOrder(8)] public required bool IsBinaryVerified { get; init; }
        [JsonPropertyOrder(9)] public required string? LibraryName { get; init; }
        [JsonPropertyOrder(10)] public required string? ModuleIdentitySource { get; init; }
        [JsonPropertyOrder(11)] public required List<CallbackRetentionPayload>? RetainedCallbacks { get; init; }
        [JsonPropertyOrder(12)] public required ExceptionEscapePayload? ExceptionEscape { get; init; }
        [JsonPropertyOrder(13)] public required ReturnAllocationPayload? ReturnAllocation { get; init; }
        [JsonPropertyOrder(14)] public required TargetPayload? Target { get; init; }
        [JsonPropertyOrder(15)] public required EvidencePayload? Evidence { get; init; }
    }

    private sealed class AbiRecordPayload
    {
        [JsonPropertyOrder(0)] public required int Version { get; init; }
        [JsonPropertyOrder(1)] public required string? Kind { get; init; }
        [JsonPropertyOrder(2)] public required string? SymbolCanonicalKey { get; init; }
        [JsonPropertyOrder(3)] public required string? RecordKind { get; init; }
        [JsonPropertyOrder(4)] public required int? SizeBytes { get; init; }
        [JsonPropertyOrder(5)] public required int? AlignmentBytes { get; init; }
        [JsonPropertyOrder(6)] public required int? Pack { get; init; }
        [JsonPropertyOrder(7)] public required List<FieldPayload>? Fields { get; init; }
        [JsonPropertyOrder(8)] public required TargetPayload? Target { get; init; }
        [JsonPropertyOrder(9)] public required EvidencePayload? Evidence { get; init; }
    }

    private sealed class TypePayload
    {
        [JsonPropertyOrder(0)] public required string? CanonicalName { get; init; }
        [JsonPropertyOrder(1)] public required string? Category { get; init; }
        [JsonPropertyOrder(2)] public required int PointerDepth { get; init; }
        [JsonPropertyOrder(3)] public required int? SizeBytes { get; init; }
        [JsonPropertyOrder(4)] public required int? AlignmentBytes { get; init; }
        [JsonPropertyOrder(5)] public required bool? IsSigned { get; init; }
        [JsonPropertyOrder(6)] public required string? StringEncoding { get; init; }
        [JsonPropertyOrder(7)] public required int? FixedArrayLength { get; init; }
        [JsonPropertyOrder(8)] public required TypePayload? PointeeType { get; init; }
        [JsonPropertyOrder(9)] public required TypePayload? ElementType { get; init; }
        [JsonPropertyOrder(10)] public required bool? IsPointeeConst { get; init; }
    }

    private sealed class ParameterPayload
    {
        [JsonPropertyOrder(0)] public required int Position { get; init; }
        [JsonPropertyOrder(1)] public required string? Name { get; init; }
        [JsonPropertyOrder(2)] public required TypePayload? Type { get; init; }
        [JsonPropertyOrder(3)] public required string? Direction { get; init; }
        [JsonPropertyOrder(4)] public required LocationPayload? Location { get; init; }
    }

    private sealed class FieldPayload
    {
        [JsonPropertyOrder(0)] public required int Order { get; init; }
        [JsonPropertyOrder(1)] public required string? Name { get; init; }
        [JsonPropertyOrder(2)] public required TypePayload? Type { get; init; }
        [JsonPropertyOrder(3)] public required int? OffsetBytes { get; init; }
        [JsonPropertyOrder(4)] public required int? SizeBytes { get; init; }
        [JsonPropertyOrder(5)] public required EvidencePayload? Evidence { get; init; }
    }

    private sealed class TargetPayload
    {
        [JsonPropertyOrder(0)] public required string? RuntimeIdentifier { get; init; }
        [JsonPropertyOrder(1)] public required string? Architecture { get; init; }
        [JsonPropertyOrder(2)] public required string? CompilerAbi { get; init; }
        [JsonPropertyOrder(3)] public required int PointerSizeBytes { get; init; }
        [JsonPropertyOrder(4)] public required int DefaultPack { get; init; }
    }

    private sealed class LocationPayload
    {
        [JsonPropertyOrder(0)] public required string? FilePath { get; init; }
        [JsonPropertyOrder(1)] public required int StartLine { get; init; }
        [JsonPropertyOrder(2)] public required int StartColumn { get; init; }
        [JsonPropertyOrder(3)] public required int EndLine { get; init; }
        [JsonPropertyOrder(4)] public required int EndColumn { get; init; }
    }

    private sealed class EvidencePayload
    {
        [JsonPropertyOrder(0)] public required LocationPayload? Location { get; init; }
        [JsonPropertyOrder(1)] public required string? Confidence { get; init; }
        [JsonPropertyOrder(2)] public required string? Producer { get; init; }
        [JsonPropertyOrder(3)] public required SortedDictionary<string, string>? Metadata { get; init; }
    }

    private sealed class CallbackRetentionPayload
    {
        [JsonPropertyOrder(0)] public required int ParameterPosition { get; init; }
        [JsonPropertyOrder(1)] public required TargetPayload? Target { get; init; }
        [JsonPropertyOrder(2)] public required EvidencePayload? Evidence { get; init; }
    }

    private sealed class ExceptionEscapePayload
    {
        [JsonPropertyOrder(0)] public required TargetPayload? Target { get; init; }
        [JsonPropertyOrder(1)] public required EvidencePayload? Evidence { get; init; }
    }

    private sealed class ReturnAllocationPayload
    {
        [JsonPropertyOrder(0)] public required string? AllocatorFamily { get; init; }
        [JsonPropertyOrder(1)] public required TargetPayload? Target { get; init; }
        [JsonPropertyOrder(2)] public required EvidencePayload? Evidence { get; init; }
    }
}

/// <summary>
/// Signals that a persisted interop annotation payload does not satisfy the current strict
/// versioned contract.
/// </summary>
public sealed class InteropFactPayloadException : FormatException
{
    public InteropFactPayloadException(
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
