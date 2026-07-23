using DevBitsLab.Mcp.SourceGraph.Sdk;

// Assembly-level marker the plugin host uses to know this DLL hosts a tool plugin and to surface
// the prefix in `plugins info` output before any type is instantiated. The actual IMcpToolPlugin
// implementation re-declares the prefix; the attribute is the cheap pre-load signal.
[assembly: McpToolPlugin(Prefix = "sample")]
