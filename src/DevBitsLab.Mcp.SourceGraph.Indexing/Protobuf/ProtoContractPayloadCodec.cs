using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevBitsLab.Mcp.SourceGraph.Sdk;

namespace DevBitsLab.Mcp.SourceGraph.Indexing.Protobuf;

/// <summary>Stable annotation flavor used for protobuf source contract facts.</summary>
public static class ProtoContractAnnotations
{
    public const string Flavor = "proto-contract";
}

/// <summary>Discriminates the protobuf declaration variant carried by a contract fact.</summary>
public enum ProtoContractKind
{
    Message,
    Field,
    Rpc,
}

/// <summary>Indicates whether import-dependent compiler information was fully available.</summary>
public enum ProtoContractStatus
{
    Complete,
    Partial,
}

/// <summary>Source-level protobuf field cardinality, retained independently of CLR defaults.</summary>
public enum ProtoFieldCardinality
{
    Singular,
    Optional,
    Repeated,
    Required,
}

/// <summary>Message-specific contract fields; present only for <see cref="ProtoContractKind.Message"/> facts.</summary>
public sealed record ProtoMessageContract(
    string? ParentFullName,
    int NestingDepth);

/// <summary>Field-specific contract fields; present only for <see cref="ProtoContractKind.Field"/> facts.</summary>
public sealed record ProtoFieldContract(
    string ContainingMessageFullName,
    string Type,
    int Number,
    ProtoFieldCardinality Cardinality,
    string? OneofName);

/// <summary>RPC-specific contract fields; present only for <see cref="ProtoContractKind.Rpc"/> facts.</summary>
public sealed record ProtoRpcContract(
    string ServiceFullName,
    string InputType,
    string OutputType,
    bool ClientStreaming,
    bool ServerStreaming);

/// <summary>
/// Analyzer-neutral protobuf declaration fact persisted through an annotation. The official
/// compiler resolves imports inside a privacy-filtered staging tree before reflection data is
/// projected, so <see cref="ProtoContractStatus.Complete"/> may legitimately carry a non-zero
/// <see cref="ProtoContractFact.ImportCount"/>.
/// </summary>
public sealed record ProtoContractFact(
    ProtoContractKind Kind,
    string SymbolCanonicalKey,
    string Package,
    string FullName,
    ProtoContractStatus Status,
    IReadOnlyList<string> IncompleteReasons,
    int ImportCount,
    ProtoMessageContract? Message,
    ProtoFieldContract? Field,
    ProtoRpcContract? Rpc);

/// <summary>
/// Strict, deterministic versioned JSON codec for <see cref="ProtoContractFact"/> annotations.
/// Unknown properties, duplicate properties, unknown enum tokens, and inconsistent variants are
/// rejected so future readers cannot silently reinterpret an older contract.
/// </summary>
public static class ProtoContractPayloadCodec
{
    public const int CurrentVersion = 1;
    public const int MaximumPayloadBytes = 64 * 1024;
    public const string ImportsNotResolvedReason = "imports-not-resolved";

    private const int MaximumJsonDepth = 16;
    private const int MaximumStringCharacters = 4096;
    private const int MaximumReasons = 16;

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

    public static string Encode(ProtoContractFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        Validate(fact);
        var payload = new ContractPayload
        {
            Version = CurrentVersion,
            Kind = ToToken(fact.Kind),
            SymbolCanonicalKey = fact.SymbolCanonicalKey,
            Package = fact.Package,
            FullName = fact.FullName,
            Status = ToToken(fact.Status),
            IncompleteReasons = fact.IncompleteReasons.ToList(),
            ImportCount = fact.ImportCount,
            Message = fact.Message is null
                ? null
                : new MessagePayload
                {
                    ParentFullName = fact.Message.ParentFullName,
                    NestingDepth = fact.Message.NestingDepth,
                },
            Field = fact.Field is null
                ? null
                : new FieldPayload
                {
                    ContainingMessageFullName =
                        fact.Field.ContainingMessageFullName,
                    Type = fact.Field.Type,
                    Number = fact.Field.Number,
                    Cardinality = ToToken(fact.Field.Cardinality),
                    OneofName = fact.Field.OneofName,
                },
            Rpc = fact.Rpc is null
                ? null
                : new RpcPayload
                {
                    ServiceFullName = fact.Rpc.ServiceFullName,
                    InputType = fact.Rpc.InputType,
                    OutputType = fact.Rpc.OutputType,
                    ClientStreaming = fact.Rpc.ClientStreaming,
                    ServerStreaming = fact.Rpc.ServerStreaming,
                },
        };

        byte[] bytes;
        try
        {
            bytes = JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw Invalid("The protobuf contract payload could not be encoded.", ex);
        }
        EnsurePayloadSize(bytes.Length);
        return _strictUtf8.GetString(bytes);
    }

    public static ProtoContractFact Decode(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        byte[] bytes;
        try
        {
            bytes = _strictUtf8.GetBytes(json);
        }
        catch (EncoderFallbackException ex)
        {
            throw Invalid("The protobuf contract payload is not valid UTF-8 text.", ex);
        }
        EnsurePayloadSize(bytes.Length);
        EnsureUniquePropertyNames(bytes);

        ContractPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<ContractPayload>(
                    bytes,
                    _jsonOptions)
                ?? throw Invalid(
                    "The protobuf contract payload must be a JSON object.");
        }
        catch (ProtoContractPayloadException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw Invalid("The protobuf contract payload is malformed.", ex);
        }

        if (payload.Version != CurrentVersion)
        {
            throw Invalid(
                $"Unsupported protobuf contract payload version `{payload.Version}`.");
        }

        var fact = new ProtoContractFact(
            ParseKind(payload.Kind),
            RequireString(
                payload.SymbolCanonicalKey,
                "symbol_canonical_key"),
            RequireString(payload.Package, "package", allowEmpty: true),
            RequireString(payload.FullName, "full_name"),
            ParseStatus(payload.Status),
            RequireReasons(payload.IncompleteReasons),
            payload.ImportCount,
            payload.Message is null
                ? null
                : new ProtoMessageContract(
                    OptionalString(
                        payload.Message.ParentFullName,
                        "message.parent_full_name"),
                    payload.Message.NestingDepth),
            payload.Field is null
                ? null
                : new ProtoFieldContract(
                    RequireString(
                        payload.Field.ContainingMessageFullName,
                        "field.containing_message_full_name"),
                    RequireString(payload.Field.Type, "field.type"),
                    payload.Field.Number,
                    ParseCardinality(payload.Field.Cardinality),
                    OptionalString(
                        payload.Field.OneofName,
                        "field.oneof_name")),
            payload.Rpc is null
                ? null
                : new ProtoRpcContract(
                    RequireString(
                        payload.Rpc.ServiceFullName,
                        "rpc.service_full_name"),
                    RequireString(payload.Rpc.InputType, "rpc.input_type"),
                    RequireString(payload.Rpc.OutputType, "rpc.output_type"),
                    payload.Rpc.ClientStreaming,
                    payload.Rpc.ServerStreaming));
        Validate(fact);
        return fact;
    }

    private static void Validate(ProtoContractFact fact)
    {
        RequireString(fact.SymbolCanonicalKey, "symbol_canonical_key");
        RequireString(fact.Package, "package", allowEmpty: true);
        ValidateDottedIdentifier(
            fact.Package,
            "package",
            allowEmpty: true);
        RequireString(fact.FullName, "full_name");
        var reasons = RequireReasons(fact.IncompleteReasons);
        if (fact.ImportCount < 0)
        {
            throw Invalid("import_count must be non-negative.");
        }
        if (fact.Status == ProtoContractStatus.Complete && reasons.Count != 0)
        {
            throw Invalid(
                "A complete protobuf contract payload cannot have incomplete reasons.");
        }
        if (fact.Status == ProtoContractStatus.Partial && reasons.Count == 0)
        {
            throw Invalid(
                "A partial protobuf contract payload requires an incomplete reason.");
        }
        if (!reasons.SequenceEqual(
                reasons
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal),
                StringComparer.Ordinal))
        {
            throw Invalid(
                "incomplete_reasons must be unique and ordinally sorted.");
        }

        switch (fact.Kind)
        {
            case ProtoContractKind.Message:
                if (fact.Message is null || fact.Field is not null || fact.Rpc is not null)
                {
                    throw Invalid(
                        "A message payload requires only the message variant.");
                }
                if (fact.Message.NestingDepth < 0)
                {
                    throw Invalid(
                        "message.nesting_depth must be non-negative.");
                }
                var parentFullName = OptionalString(
                    fact.Message.ParentFullName,
                    "message.parent_full_name");
                RequireCanonicalKey(
                    ProtoCanonicalKeys.ForMessage(fact.FullName),
                    fact.SymbolCanonicalKey);
                var relativeMessageName = RequirePackageMember(
                    fact.Package,
                    fact.FullName,
                    "message full_name");
                var expectedNestingDepth =
                    CountSegments(relativeMessageName) - 1;
                if (fact.Message.NestingDepth != expectedNestingDepth)
                {
                    throw Invalid(
                        "message.nesting_depth does not match full_name.");
                }
                if (expectedNestingDepth == 0)
                {
                    if (parentFullName is not null)
                    {
                        throw Invalid(
                            "A top-level message cannot have parent_full_name.");
                    }
                }
                else
                {
                    if (parentFullName is null)
                    {
                        throw Invalid(
                            "A nested message requires parent_full_name.");
                    }
                    RequirePackageMember(
                        fact.Package,
                        parentFullName,
                        "message.parent_full_name");
                    var finalSeparator =
                        fact.FullName.LastIndexOf('.');
                    var expectedParent =
                        fact.FullName[..finalSeparator];
                    if (!string.Equals(
                            parentFullName,
                            expectedParent,
                            StringComparison.Ordinal))
                    {
                        throw Invalid(
                            "message.parent_full_name must be the direct "
                            + "parent of full_name.");
                    }
                }
                break;

            case ProtoContractKind.Field:
                if (fact.Field is null || fact.Message is not null || fact.Rpc is not null)
                {
                    throw Invalid(
                        "A field payload requires only the field variant.");
                }
                RequireString(
                    fact.Field.ContainingMessageFullName,
                    "field.containing_message_full_name");
                RequirePackageMember(
                    fact.Package,
                    fact.Field.ContainingMessageFullName,
                    "field.containing_message_full_name");
                RequireString(fact.Field.Type, "field.type");
                OptionalString(fact.Field.OneofName, "field.oneof_name");
                ValidateFieldNumber(fact.Field.Number);
                var fieldPrefix =
                    fact.Field.ContainingMessageFullName + ".";
                if (!fact.FullName.StartsWith(
                        fieldPrefix,
                        StringComparison.Ordinal)
                    || fact.FullName.Length == fieldPrefix.Length)
                {
                    throw Invalid(
                        "A field full_name must be below its containing message.");
                }
                var fieldName = fact.FullName[fieldPrefix.Length..];
                if (fieldName.Contains('.'))
                {
                    throw Invalid(
                        "A field full_name must end with one identifier.");
                }
                RequireCanonicalKey(
                    ProtoCanonicalKeys.ForField(
                        fact.Field.ContainingMessageFullName,
                        fieldName),
                    fact.SymbolCanonicalKey);
                break;

            case ProtoContractKind.Rpc:
                if (fact.Rpc is null || fact.Message is not null || fact.Field is not null)
                {
                    throw Invalid(
                        "An RPC payload requires only the rpc variant.");
                }
                RequireString(
                    fact.Rpc.ServiceFullName,
                    "rpc.service_full_name");
                var relativeServiceName = RequirePackageMember(
                    fact.Package,
                    fact.Rpc.ServiceFullName,
                    "rpc.service_full_name");
                if (CountSegments(relativeServiceName) != 1)
                {
                    throw Invalid(
                        "rpc.service_full_name must identify a top-level "
                        + "service in the package.");
                }
                RequireString(fact.Rpc.InputType, "rpc.input_type");
                RequireString(fact.Rpc.OutputType, "rpc.output_type");
                var rpcPrefix = fact.Rpc.ServiceFullName + ".";
                if (!fact.FullName.StartsWith(
                        rpcPrefix,
                        StringComparison.Ordinal)
                    || fact.FullName.Length == rpcPrefix.Length)
                {
                    throw Invalid(
                        "An RPC full_name must be below its service.");
                }
                var rpcName = fact.FullName[rpcPrefix.Length..];
                if (rpcName.Contains('.'))
                {
                    throw Invalid(
                        "An RPC full_name must end with one identifier.");
                }
                RequireCanonicalKey(
                    ProtoCanonicalKeys.ForRpc(
                        fact.Rpc.ServiceFullName,
                        rpcName),
                    fact.SymbolCanonicalKey);
                break;

            default:
                throw Invalid($"Unknown protobuf contract kind `{fact.Kind}`.");
        }
    }

    private static void ValidateFieldNumber(int number)
    {
        if (number is < 1 or > 536_870_911
            || number is >= 19_000 and <= 19_999)
        {
            throw Invalid($"Invalid protobuf field number `{number}`.");
        }
    }

    private static void RequireCanonicalKey(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw Invalid(
                $"Canonical key `{actual}` does not match `{expected}`.");
        }
    }

    private static IReadOnlyList<string> RequireReasons(
        IReadOnlyList<string>? reasons)
    {
        if (reasons is null)
        {
            throw Invalid("incomplete_reasons is required.");
        }
        if (reasons.Count > MaximumReasons)
        {
            throw Invalid(
                $"incomplete_reasons exceeds the {MaximumReasons}-item limit.");
        }
        foreach (var reason in reasons)
        {
            RequireString(reason, "incomplete_reasons item");
        }
        return reasons;
    }

    private static string RequireString(
        string? value,
        string path,
        bool allowEmpty = false)
    {
        if (value is null
            || (!allowEmpty && string.IsNullOrWhiteSpace(value))
            || (allowEmpty
                && value.Length > 0
                && string.IsNullOrWhiteSpace(value)))
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

    private static string RequirePackageMember(
        string package,
        string fullName,
        string path)
    {
        ValidateDottedIdentifier(fullName, path, allowEmpty: false);
        if (package.Length == 0)
        {
            return fullName;
        }
        var prefix = package + ".";
        if (!fullName.StartsWith(prefix, StringComparison.Ordinal)
            || fullName.Length == prefix.Length)
        {
            throw Invalid($"{path} must belong to package `{package}`.");
        }
        return fullName[prefix.Length..];
    }

    private static int CountSegments(string dottedName) =>
        dottedName.Count(character => character == '.') + 1;

    private static void ValidateDottedIdentifier(
        string value,
        string path,
        bool allowEmpty)
    {
        if (value.Length == 0)
        {
            if (allowEmpty) return;
            throw Invalid($"{path} must contain an identifier.");
        }
        foreach (var segment in value.Split('.'))
        {
            if (segment.Length == 0 || !IsIdentifierStart(segment[0]))
            {
                throw Invalid(
                    $"{path} contains an invalid identifier segment.");
            }
            for (var index = 1; index < segment.Length; index++)
            {
                if (!IsIdentifierPart(segment[index]))
                {
                    throw Invalid(
                        $"{path} contains an invalid identifier segment.");
                }
            }
        }
    }

    private static bool IsIdentifierStart(char value) =>
        value == '_'
        || value is >= 'A' and <= 'Z'
        || value is >= 'a' and <= 'z';

    private static bool IsIdentifierPart(char value) =>
        IsIdentifierStart(value)
        || value is >= '0' and <= '9';

    private static string? OptionalString(string? value, string path)
    {
        if (value is null) return null;
        return RequireString(value, path);
    }

    private static string ToToken(ProtoContractKind value) => value switch
    {
        ProtoContractKind.Message => "message",
        ProtoContractKind.Field => "field",
        ProtoContractKind.Rpc => "rpc",
        _ => throw Invalid($"Unknown protobuf contract kind `{value}`."),
    };

    private static ProtoContractKind ParseKind(string? value) => value switch
    {
        "message" => ProtoContractKind.Message,
        "field" => ProtoContractKind.Field,
        "rpc" => ProtoContractKind.Rpc,
        _ => throw Invalid($"Unknown protobuf contract kind `{value}`."),
    };

    private static string ToToken(ProtoContractStatus value) => value switch
    {
        ProtoContractStatus.Complete => "complete",
        ProtoContractStatus.Partial => "partial",
        _ => throw Invalid($"Unknown protobuf contract status `{value}`."),
    };

    private static ProtoContractStatus ParseStatus(string? value) => value switch
    {
        "complete" => ProtoContractStatus.Complete,
        "partial" => ProtoContractStatus.Partial,
        _ => throw Invalid($"Unknown protobuf contract status `{value}`."),
    };

    private static string ToToken(ProtoFieldCardinality value) => value switch
    {
        ProtoFieldCardinality.Singular => "singular",
        ProtoFieldCardinality.Optional => "optional",
        ProtoFieldCardinality.Repeated => "repeated",
        ProtoFieldCardinality.Required => "required",
        _ => throw Invalid($"Unknown protobuf field cardinality `{value}`."),
    };

    private static ProtoFieldCardinality ParseCardinality(
        string? value) => value switch
        {
            "singular" => ProtoFieldCardinality.Singular,
            "optional" => ProtoFieldCardinality.Optional,
            "repeated" => ProtoFieldCardinality.Repeated,
            "required" => ProtoFieldCardinality.Required,
            _ => throw Invalid(
                $"Unknown protobuf field cardinality `{value}`."),
        };

    private static void EnsurePayloadSize(int byteCount)
    {
        if (byteCount > MaximumPayloadBytes)
        {
            throw Invalid(
                $"The protobuf contract payload exceeds the "
                + $"{MaximumPayloadBytes}-byte limit.");
        }
    }

    private static void EnsureUniquePropertyNames(ReadOnlySpan<byte> json)
    {
        try
        {
            var reader = new Utf8JsonReader(
                json,
                new JsonReaderOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = MaximumJsonDepth,
                });
            var objects = new Stack<HashSet<string>>();
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        objects.Push(new HashSet<string>(StringComparer.Ordinal));
                        break;

                    case JsonTokenType.PropertyName:
                        if (objects.Count == 0
                            || !objects.Peek().Add(reader.GetString()!))
                        {
                            throw Invalid(
                                "The protobuf contract payload contains a "
                                + "duplicate property.");
                        }
                        break;

                    case JsonTokenType.EndObject:
                        if (objects.Count == 0)
                        {
                            throw Invalid(
                                "The protobuf contract payload has an invalid "
                                + "object boundary.");
                        }
                        objects.Pop();
                        break;
                }
            }
            if (objects.Count != 0)
            {
                throw Invalid(
                    "The protobuf contract payload has an unterminated object.");
            }
        }
        catch (ProtoContractPayloadException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw Invalid("The protobuf contract payload is malformed.", ex);
        }
    }

    private static ProtoContractPayloadException Invalid(
        string message,
        Exception? innerException = null) =>
        new(message, innerException);

    private sealed class ContractPayload
    {
        [JsonPropertyOrder(0)] public required int Version { get; init; }
        [JsonPropertyOrder(1)] public required string? Kind { get; init; }
        [JsonPropertyOrder(2)] public required string? SymbolCanonicalKey { get; init; }
        [JsonPropertyOrder(3)] public required string? Package { get; init; }
        [JsonPropertyOrder(4)] public required string? FullName { get; init; }
        [JsonPropertyOrder(5)] public required string? Status { get; init; }
        [JsonPropertyOrder(6)] public required List<string>? IncompleteReasons { get; init; }
        [JsonPropertyOrder(7)] public required int ImportCount { get; init; }
        [JsonPropertyOrder(8)] public required MessagePayload? Message { get; init; }
        [JsonPropertyOrder(9)] public required FieldPayload? Field { get; init; }
        [JsonPropertyOrder(10)] public required RpcPayload? Rpc { get; init; }
    }

    private sealed class MessagePayload
    {
        [JsonPropertyOrder(0)] public required string? ParentFullName { get; init; }
        [JsonPropertyOrder(1)] public required int NestingDepth { get; init; }
    }

    private sealed class FieldPayload
    {
        [JsonPropertyOrder(0)] public required string? ContainingMessageFullName { get; init; }
        [JsonPropertyOrder(1)] public required string? Type { get; init; }
        [JsonPropertyOrder(2)] public required int Number { get; init; }
        [JsonPropertyOrder(3)] public required string? Cardinality { get; init; }
        [JsonPropertyOrder(4)] public required string? OneofName { get; init; }
    }

    private sealed class RpcPayload
    {
        [JsonPropertyOrder(0)] public required string? ServiceFullName { get; init; }
        [JsonPropertyOrder(1)] public required string? InputType { get; init; }
        [JsonPropertyOrder(2)] public required string? OutputType { get; init; }
        [JsonPropertyOrder(3)] public required bool ClientStreaming { get; init; }
        [JsonPropertyOrder(4)] public required bool ServerStreaming { get; init; }
    }
}

public sealed class ProtoContractPayloadException : FormatException
{
    public ProtoContractPayloadException(
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
