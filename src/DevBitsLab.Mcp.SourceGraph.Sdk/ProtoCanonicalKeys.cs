using System;

namespace DevBitsLab.Mcp.SourceGraph.Sdk;

/// <summary>
/// Builds canonical keys from protobuf descriptor full names. A leading descriptor dot is
/// normalised away, so <c>.medical.v1.Scan</c> and <c>medical.v1.Scan</c> have the same identity.
/// These keys deliberately do not include the source path: protobuf full names are the contract
/// identity used by generated clients and servers.
/// </summary>
public static class ProtoCanonicalKeys
{
    /// <summary>Message declaration.</summary>
    public const string PrefixMessage = "M";

    /// <summary>RPC declaration.</summary>
    public const string PrefixRpc = "R";

    /// <summary>Message field declaration.</summary>
    public const string PrefixField = "F";

    /// <summary>Builds <c>proto:M:&lt;message-full-name&gt;</c>.</summary>
    public static string ForMessage(string messageFullName) =>
        $"proto:{PrefixMessage}:{NormalizeFullName(messageFullName, nameof(messageFullName))}";

    /// <summary>
    /// Builds <c>proto:R:&lt;service-full-name&gt;.&lt;rpc-name&gt;</c>.
    /// </summary>
    public static string ForRpc(string serviceFullName, string rpcName) =>
        $"proto:{PrefixRpc}:{NormalizeFullName(serviceFullName, nameof(serviceFullName))}.{NormalizeIdentifier(rpcName, nameof(rpcName))}";

    /// <summary>
    /// Builds <c>proto:F:&lt;message-full-name&gt;.&lt;field-name&gt;</c>.
    /// Field numbers remain occurrence metadata so a number change can be diagnosed without
    /// changing the declaration's name-based identity.
    /// </summary>
    public static string ForField(string messageFullName, string fieldName) =>
        $"proto:{PrefixField}:{NormalizeFullName(messageFullName, nameof(messageFullName))}.{NormalizeIdentifier(fieldName, nameof(fieldName))}";

    private static string NormalizeFullName(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                "Protobuf descriptor full name must be non-empty.",
                paramName);
        }

        var normalized = value.Trim();
        if (normalized.StartsWith(".", StringComparison.Ordinal))
        {
            normalized = normalized.Substring(1);
        }
        if (normalized.Length == 0)
        {
            throw new ArgumentException(
                "Protobuf descriptor full name must contain an identifier.",
                paramName);
        }

        var segments = normalized.Split('.');
        foreach (var segment in segments)
        {
            ValidateIdentifier(segment, paramName, "Protobuf descriptor full name");
        }
        return string.Join(".", segments);
    }

    private static string NormalizeIdentifier(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                "Protobuf identifier must be non-empty.",
                paramName);
        }
        var normalized = value.Trim();
        ValidateIdentifier(normalized, paramName, "Protobuf identifier");
        return normalized;
    }

    private static void ValidateIdentifier(string value, string paramName, string description)
    {
        if (value.Length == 0 || !IsIdentifierStart(value[0]))
        {
            throw new ArgumentException(
                $"{description} contains an invalid identifier segment '{value}'.",
                paramName);
        }
        for (var i = 1; i < value.Length; i++)
        {
            if (!IsIdentifierPart(value[i]))
            {
                throw new ArgumentException(
                    $"{description} contains an invalid identifier segment '{value}'.",
                    paramName);
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
}
