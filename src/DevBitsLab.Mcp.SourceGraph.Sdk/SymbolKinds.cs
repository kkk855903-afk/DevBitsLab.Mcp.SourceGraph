namespace DevBitsLab.Mcp.SourceGraph.Sdk;

/// <summary>
/// Well-known symbol-kind identifiers shared by built-in and third-party language analyzers.
/// Plugins MAY emit additional kebab-case identifiers — the host stores them as TEXT and does not
/// reject unknown kebab-case kinds. Use
/// <see cref="DevBitsLab.Mcp.SourceGraph.Sdk.Validation.KebabCaseValidator"/> to validate
/// plugin-supplied kinds.
/// </summary>
public static class SymbolKinds
{
    public const string Other = "other";
    public const string Namespace = "namespace";
    public const string Class = "class";
    public const string Interface = "interface";
    public const string Struct = "struct";
    public const string Enum = "enum";
    public const string Delegate = "delegate";
    public const string Method = "method";
    public const string Constructor = "constructor";
    public const string Property = "property";
    public const string Field = "field";
    public const string Event = "event";
    public const string EnumMember = "enum-member";
    public const string Operator = "operator";
    public const string Record = "record";
    /// <summary>Method-local variable. Not emitted by the built-in indexer today.</summary>
    public const string Local = "local";
    /// <summary>Method/constructor parameter. Not emitted by the built-in indexer today.</summary>
    public const string Parameter = "parameter";
    /// <summary>Generic type parameter. Not emitted by the built-in indexer today.</summary>
    public const string TypeParameter = "type-parameter";

    /// <summary>C or C++ free function (including a non-exported translation-unit function).</summary>
    public const string Function = "function";

    /// <summary>C/C++ typedef or using-alias declaration.</summary>
    public const string TypeAlias = "type-alias";

    /// <summary>Native ABI entry point exported from a library.</summary>
    public const string NativeExport = "native-export";

    /// <summary>RPC method declared by a protobuf service.</summary>
    public const string Rpc = "rpc";

    /// <summary>Message type declared in a protobuf schema.</summary>
    public const string Message = "message";

    /// <summary>Numbered field declared by a protobuf message.</summary>
    public const string ProtoField = "proto-field";
}
