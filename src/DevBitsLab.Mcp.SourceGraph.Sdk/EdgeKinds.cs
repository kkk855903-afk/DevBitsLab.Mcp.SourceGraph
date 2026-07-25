namespace DevBitsLab.Mcp.SourceGraph.Sdk;

/// <summary>
/// Well-known edge-kind identifiers shared by built-in and third-party language analyzers.
/// Plugins MAY emit additional kebab-case identifiers — the host stores them as TEXT and does not
/// reject unknown kebab-case kinds. Use
/// <see cref="DevBitsLab.Mcp.SourceGraph.Sdk.Validation.KebabCaseValidator"/> to validate
/// plugin-supplied kinds.
/// </summary>
public static class EdgeKinds
{
    public const string Calls = "calls";
    public const string Inherits = "inherits";
    public const string Implements = "implements";
    public const string UsesType = "uses-type";
    public const string OverridesMember = "overrides-member";
    public const string ImplementsMember = "implements-member";
    public const string Instantiates = "instantiates";
    public const string Throws = "throws";

    /// <summary>A symbol contains a non-call reference to another symbol.</summary>
    public const string References = "references";

    /// <summary>A source operation reads the target symbol.</summary>
    public const string Reads = "reads";

    /// <summary>A source operation writes the target symbol.</summary>
    public const string Writes = "writes";

    /// <summary>A declarative UI value binds to the target symbol.</summary>
    public const string BindsTo = "binds-to";

    /// <summary>A declarative UI event is handled by the target symbol.</summary>
    public const string HandlesEvent = "handles-event";

    /// <summary>A source member subscribes a handler to the target event.</summary>
    public const string SubscribesEvent = "subscribes-event";

    /// <summary>A source member removes a handler from the target event.</summary>
    public const string UnsubscribesEvent = "unsubscribes-event";

    /// <summary>A source member raises the target event.</summary>
    public const string RaisesEvent = "raises-event";

    /// <summary>An ICommand property dispatches execution to a source method.</summary>
    public const string CommandExecutes = "command-executes";

    /// <summary>A managed gRPC client invocation targets a protobuf RPC declaration.</summary>
    public const string GrpcCalls = "grpc-calls";

    /// <summary>A server handler implements a protobuf RPC declaration.</summary>
    public const string ImplementsRpc = "implements-rpc";

    /// <summary>A protobuf RPC dispatches execution to a managed server handler.</summary>
    public const string RpcDispatchesTo = "rpc-dispatches-to";

    /// <summary>A managed P/Invoke declaration maps to a native ABI export.</summary>
    public const string PInvokeMapsTo = "pinvoke-maps-to";

    /// <summary>A managed interop struct maps to a native struct declaration.</summary>
    public const string StructMapsTo = "struct-maps-to";

    /// <summary>Test method exercises a piece of production code.</summary>
    public const string Tests = "tests";
}
