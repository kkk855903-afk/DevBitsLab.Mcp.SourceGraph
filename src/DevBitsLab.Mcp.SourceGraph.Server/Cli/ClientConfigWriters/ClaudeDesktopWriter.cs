namespace DevBitsLab.Mcp.SourceGraph.Server.Cli.ClientConfigWriters;

/// <summary>
/// Writer for Claude Desktop's <c>claude_desktop_config.json</c>. Claude Desktop has no project-
/// scope concept — the only target path is the platform-specific user path. Wiring this client
/// requires the explicit <c>--claude-desktop</c> flag at the CLI level (project-scoped defaults
/// rule from design.md Decision 2).
/// </summary>
internal sealed class ClaudeDesktopWriter : JsonMcpServerWriter
{
    public override ClientId ClientId => ClientId.ClaudeDesktop;

    public override string? DefaultProjectPath(string root) => null;

    public override string? DefaultUserPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolderOption.DoNotVerify);
        return string.IsNullOrEmpty(home) ? null : OnboardingDetector.ClaudeDesktopUserPath(home);
    }

    protected override string TopLevelKey => "mcpServers";
}
