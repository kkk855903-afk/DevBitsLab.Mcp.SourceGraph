namespace DevBitsLab.Mcp.SourceGraph.Server.Cli.ClientConfigWriters;

/// <summary>
/// Writer for Cursor's <c>.cursor/mcp.json</c> (project) or <c>~/.cursor/mcp.json</c> (user).
/// Cursor follows the same <c>mcpServers</c> shape as Claude Code; only the file path differs.
/// </summary>
internal sealed class CursorWriter : JsonMcpServerWriter
{
    public override ClientId ClientId => ClientId.Cursor;

    public override string? DefaultProjectPath(string root) => Path.Join(root, ".cursor", "mcp.json");

    public override string? DefaultUserPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolderOption.DoNotVerify);
        return string.IsNullOrEmpty(home) ? null : Path.Join(home, ".cursor", "mcp.json");
    }

    protected override string TopLevelKey => "mcpServers";
}
