namespace DevBitsLab.Mcp.SourceGraph.Server.Cli.ClientConfigWriters;

/// <summary>
/// Writer for Claude Code's <c>.mcp.json</c> (project) or <c>~/.claude/.mcp.json</c> (user).
/// Schema is the canonical <c>mcpServers.&lt;name&gt;</c> shape with <c>command</c> + <c>args</c>.
/// </summary>
internal sealed class ClaudeCodeWriter : JsonMcpServerWriter
{
    public override ClientId ClientId => ClientId.ClaudeCode;

    public override string? DefaultProjectPath(string root) => Path.Join(root, ".mcp.json");

    public override string? DefaultUserPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolderOption.DoNotVerify);
        return string.IsNullOrEmpty(home) ? null : Path.Join(home, ".claude", ".mcp.json");
    }

    protected override string TopLevelKey => "mcpServers";
}
