using System.Text.Json.Nodes;

namespace DevBitsLab.Mcp.SourceGraph.Server.Cli.ClientConfigWriters;

/// <summary>
/// Writer for GitHub Copilot's <c>.vscode/mcp.json</c> (project). Distinct from the other JSON
/// writers in two ways: top-level key is <c>servers</c> (not <c>mcpServers</c>), and each server
/// entry carries an explicit <c>type: "stdio"</c> field. User-scope writes target the user's
/// VS Code settings under <c>chat.mcp.servers</c>; we surface only the project-scope path
/// here and leave user-scope settings.json edits to a follow-up (the user-scope flag is
/// accepted by the CLI but treated as a print-only-with-instructions degraded path in v1).
/// </summary>
internal sealed class CopilotWriter : JsonMcpServerWriter
{
    public override ClientId ClientId => ClientId.Copilot;

    public override string? DefaultProjectPath(string root) => Path.Join(root, ".vscode", "mcp.json");

    public override string? DefaultUserPath() => null;

    protected override string TopLevelKey => "servers";

    protected override JsonObject BuildServerEntry(WriterContext ctx)
    {
        var (command, args) = WriterCommandLine.Build(ctx);
        var argsArray = new JsonArray();
        foreach (var a in args) argsArray.Add(a);
        // VS Code's MCP schema requires `type: "stdio"` on every server entry. Insertion order
        // matters for diff legibility — type goes first.
        return new JsonObject
        {
            ["type"] = "stdio",
            ["command"] = command,
            ["args"] = argsArray,
        };
    }
}
