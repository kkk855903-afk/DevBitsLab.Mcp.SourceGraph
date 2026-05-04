using DevBitsLab.Mcp.SourceGraph.Sdk;

namespace DevBitsLab.Mcp.SourceGraph.Server.Plugins;

/// <summary>
/// In-memory representation of a single loaded plugin. Owned by the <see cref="PluginHost"/>;
/// reflected verbatim by the <c>plugins list</c> / <c>plugins info</c> CLI subcommands. Uses a
/// mutable record so the host can downgrade <see cref="Status"/> from <c>loaded</c> to
/// <c>failed</c> when the plugin throws during dispatch.
/// </summary>
public sealed class PluginRecord
{
    public PluginRecord(
        string identity,
        string? version,
        string sourcePath,
        bool isNuGet)
    {
        Identity = identity;
        Version = version;
        SourcePath = sourcePath;
        IsNuGet = isNuGet;
        Status = PluginStatus.Loaded;
    }

    /// <summary>NuGet package id or filename of the DLL drop. Stable across server restarts.</summary>
    public string Identity { get; }

    /// <summary>NuGet version string (only set for package plugins).</summary>
    public string? Version { get; }

    /// <summary>Absolute path to the loaded DLL (the resolved package DLL for NuGet plugins).</summary>
    public string SourcePath { get; }

    /// <summary>True for NuGet-restored plugins, false for direct DLL drops.</summary>
    public bool IsNuGet { get; }

    /// <summary>Current lifecycle status. Mutable; downgraded on dispatch failure.</summary>
    public PluginStatus Status { get; set; }

    /// <summary>Human-readable reason the plugin is in its current status. Surfaced via <c>plugins info</c>.</summary>
    public string? StatusMessage { get; set; }

    /// <summary>Tool prefix declared by the plugin (when it implements <see cref="IMcpToolPlugin"/>).</summary>
    public string? ToolPrefix { get; set; }

    /// <summary>The language indexer instances the plugin supplied.</summary>
    public IList<ILanguageIndexer> LanguageIndexers { get; } = new List<ILanguageIndexer>();

    /// <summary>The analyzers the plugin supplied.</summary>
    public IList<ICodeAnalyzer> Analyzers { get; } = new List<ICodeAnalyzer>();

    /// <summary>The tool plugin instances the plugin supplied.</summary>
    public IList<IMcpToolPlugin> ToolPlugins { get; } = new List<IMcpToolPlugin>();

    /// <summary>Final tool names registered (after prefixing). Used by <c>plugins info</c>.</summary>
    public IList<string> RegisteredToolNames { get; } = new List<string>();

    /// <summary>File extensions claimed by the plugin's language indexers.</summary>
    public IList<string> RegisteredFileExtensions { get; } = new List<string>();
}

/// <summary>Lifecycle states a plugin can be in. Surfaced via the CLI.</summary>
public enum PluginStatus
{
    /// <summary>Discovered, instantiated, and dispatched without error.</summary>
    Loaded,
    /// <summary>Loading or dispatch threw; the plugin is parked. <see cref="PluginRecord.StatusMessage"/> carries the exception text.</summary>
    Failed,
    /// <summary>Reserved for future use — operator explicitly disabled the plugin via config or CLI.</summary>
    Disabled,
}
