namespace DevBitsLab.Mcp.SourceGraph.Server.Tools;

/// <summary>
/// Declares the natural-language question this tool is meant to answer. Surfaced to the connected
/// model by appending a final <c>Use when: &lt;trigger&gt;</c> line to the tool's effective
/// description at registration time.
/// </summary>
/// <remarks>
/// The trigger SHOULD be the question phrase the user would speak, with surrounding quotes
/// included in the value, e.g. <c>[ToolTrigger("\"where is X defined?\"")]</c>. The host appends
/// the line verbatim, so multiple alternative phrasings can be combined into one trigger value
/// (`"where is X defined?" or "find the def of X"`).
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ToolTriggerAttribute : Attribute
{
    public string Trigger { get; }

    public ToolTriggerAttribute(string trigger)
    {
        Trigger = trigger;
    }
}
