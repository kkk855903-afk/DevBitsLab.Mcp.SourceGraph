using System.Text.Json.Serialization;

namespace DevBitsLab.Mcp.SourceGraph.Core;

/// <summary>
/// Versioned payloads for caller-owned managed interop data-flow facts. These are separate from
/// the import declaration payload because a call site can live in another physical file and must
/// follow that file's incremental lifecycle.
/// </summary>
public static partial class InteropFactPayloadCodec
{
    private const string ManagedCallbackUsageKindToken =
        "managed_callback_usage";
    private const string ManagedReturnReleaseKindToken =
        "managed_return_release";

    public static string EncodeManagedCallbackUsage(
        ManagedCallbackUsageProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        var payload = ToPayload(projection);
        Validate(payload, ownerFileId: 1);
        return Serialize(payload);
    }

    public static ManagedCallbackUsageProjection DecodeManagedCallbackUsage(
        string json,
        long ownerFileId) =>
        Validate(
            Deserialize<ManagedCallbackUsagePayload>(json),
            ownerFileId);

    public static string EncodeManagedReturnRelease(
        ManagedReturnReleaseProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        var payload = ToPayload(projection);
        Validate(payload, ownerFileId: 1);
        return Serialize(payload);
    }

    public static ManagedReturnReleaseProjection DecodeManagedReturnRelease(
        string json,
        long ownerFileId) =>
        Validate(
            Deserialize<ManagedReturnReleasePayload>(json),
            ownerFileId);

    private static ManagedCallbackUsagePayload ToPayload(
        ManagedCallbackUsageProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection.Usage);
        return new ManagedCallbackUsagePayload
        {
            Version = CurrentVersion,
            Kind = ManagedCallbackUsageKindToken,
            ManagedImportSymbolCanonicalKey =
                projection.ManagedImportSymbolCanonicalKey,
            ParameterPosition = projection.Usage.ParameterPosition,
            CallerSymbolCanonicalKey =
                projection.Usage.CallerSymbolCanonicalKey,
            Rooting = ToToken(projection.Usage.Rooting),
            Target = ToPayload(projection.Usage.Target),
            Evidence = ToPayload(projection.Usage.Evidence),
        };
    }

    private static ManagedReturnReleasePayload ToPayload(
        ManagedReturnReleaseProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection.Release);
        return new ManagedReturnReleasePayload
        {
            Version = CurrentVersion,
            Kind = ManagedReturnReleaseKindToken,
            ManagedImportSymbolCanonicalKey =
                projection.ManagedImportSymbolCanonicalKey,
            CallerSymbolCanonicalKey =
                projection.Release.CallerSymbolCanonicalKey,
            ReleaseFamily = ToToken(projection.Release.ReleaseFamily),
            Target = ToPayload(projection.Release.Target),
            Evidence = ToPayload(projection.Release.Evidence),
        };
    }

    private static ManagedCallbackUsageProjection Validate(
        ManagedCallbackUsagePayload payload,
        long ownerFileId)
    {
        ValidateOwnerFileId(ownerFileId);
        ValidateHeader(
            payload.Version,
            payload.Kind,
            ManagedCallbackUsageKindToken);
        if (payload.ParameterPosition < 0)
        {
            throw Invalid(
                "managed_callback_usage.parameter_position must be non-negative.");
        }

        var rooting = ParseCallbackRooting(payload.Rooting);
        if (rooting == CallbackGcRooting.Unknown)
        {
            throw Invalid(
                "managed_callback_usage.rooting must contain a proven state.");
        }
        return new ManagedCallbackUsageProjection(
            RequireString(
                payload.ManagedImportSymbolCanonicalKey,
                "managed_callback_usage.managed_import_symbol_canonical_key"),
            new ManagedCallbackUsage(
                payload.ParameterPosition,
                RequireString(
                    payload.CallerSymbolCanonicalKey,
                    "managed_callback_usage.caller_symbol_canonical_key"),
                rooting,
                FromPayload(
                    RequireObject(
                        payload.Target,
                        "managed_callback_usage.target")),
                FromPayload(
                    RequireObject(
                        payload.Evidence,
                        "managed_callback_usage.evidence"),
                    ownerFileId)));
    }

    private static ManagedReturnReleaseProjection Validate(
        ManagedReturnReleasePayload payload,
        long ownerFileId)
    {
        ValidateOwnerFileId(ownerFileId);
        ValidateHeader(
            payload.Version,
            payload.Kind,
            ManagedReturnReleaseKindToken);
        var family = ParseAllocatorFamily(payload.ReleaseFamily);
        if (family == InteropAllocatorFamily.Unknown)
        {
            throw Invalid(
                "managed_return_release.release_family must be proven.");
        }
        return new ManagedReturnReleaseProjection(
            RequireString(
                payload.ManagedImportSymbolCanonicalKey,
                "managed_return_release.managed_import_symbol_canonical_key"),
            new ManagedReturnRelease(
                RequireString(
                    payload.CallerSymbolCanonicalKey,
                    "managed_return_release.caller_symbol_canonical_key"),
                family,
                FromPayload(
                    RequireObject(
                        payload.Target,
                        "managed_return_release.target")),
                FromPayload(
                    RequireObject(
                        payload.Evidence,
                        "managed_return_release.evidence"),
                    ownerFileId)));
    }

    private static string ToToken(CallbackGcRooting value) => value switch
    {
        CallbackGcRooting.Unknown => "unknown",
        CallbackGcRooting.Rooted => "rooted",
        CallbackGcRooting.Unrooted => "unrooted",
        _ => throw Invalid($"Unknown callback rooting `{value}`."),
    };

    private static CallbackGcRooting ParseCallbackRooting(string? value) =>
        value switch
        {
            "unknown" => CallbackGcRooting.Unknown,
            "rooted" => CallbackGcRooting.Rooted,
            "unrooted" => CallbackGcRooting.Unrooted,
            _ => throw Invalid($"Unknown callback rooting `{value}`."),
        };

    private sealed class ManagedCallbackUsagePayload
    {
        [JsonPropertyOrder(0)] public required int Version { get; init; }
        [JsonPropertyOrder(1)] public required string? Kind { get; init; }
        [JsonPropertyOrder(2)]
        public required string? ManagedImportSymbolCanonicalKey { get; init; }
        [JsonPropertyOrder(3)]
        public required int ParameterPosition { get; init; }
        [JsonPropertyOrder(4)]
        public required string? CallerSymbolCanonicalKey { get; init; }
        [JsonPropertyOrder(5)] public required string? Rooting { get; init; }
        [JsonPropertyOrder(6)] public required TargetPayload? Target { get; init; }
        [JsonPropertyOrder(7)] public required EvidencePayload? Evidence { get; init; }
    }

    private sealed class ManagedReturnReleasePayload
    {
        [JsonPropertyOrder(0)] public required int Version { get; init; }
        [JsonPropertyOrder(1)] public required string? Kind { get; init; }
        [JsonPropertyOrder(2)]
        public required string? ManagedImportSymbolCanonicalKey { get; init; }
        [JsonPropertyOrder(3)]
        public required string? CallerSymbolCanonicalKey { get; init; }
        [JsonPropertyOrder(4)]
        public required string? ReleaseFamily { get; init; }
        [JsonPropertyOrder(5)] public required TargetPayload? Target { get; init; }
        [JsonPropertyOrder(6)] public required EvidencePayload? Evidence { get; init; }
    }
}
