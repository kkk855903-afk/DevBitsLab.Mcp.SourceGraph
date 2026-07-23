using DevBitsLab.Mcp.SourceGraph.Server.Plugins;
using DevBitsLab.Mcp.SourceGraph.Storage;

namespace DevBitsLab.Mcp.SourceGraph.Server.Cli;

/// <summary>
/// CLI surface for the v0.6 extensibility architecture: <c>plugins list</c> and
/// <c>plugins info &lt;name&gt;</c>. Both subcommands instantiate a transient
/// <see cref="PluginHost"/> against the configured <c>.sourcegraph.json</c>, run discovery, then
/// render the resulting <see cref="PluginRecord"/> list. There's no caching: each invocation
/// re-discovers, which keeps the output current after a config edit.
/// </summary>
internal static class PluginsCli
{
    public static async Task<int> RunSubcommandAsync(CommandLine cli)
    {
        var sub = cli.Positional.Count > 0 ? cli.Positional[0] : "list";
        return sub switch
        {
            "list" => await RunListAsync(cli).ConfigureAwait(false),
            "info" => await RunInfoAsync(cli).ConfigureAwait(false),
            _ => Unknown(sub),
        };
    }

    private static async Task<PluginHost> DiscoverAsync(CommandLine cli)
    {
        var repoRoot = cli.ResolvedRepoRoot();
        ScopeConfig config;
        try
        {
            config = ScopeConfigLoader.Load(repoRoot);
        }
        catch (ScopeConfigException)
        {
            // For introspection commands a malformed config still shouldn't crash the CLI; show
            // an empty plugin list. The serve path validates and exits non-zero on bad config.
            config = new ScopeConfig(Array.Empty<DevBitsLab.Mcp.SourceGraph.Core.Scope>(), null, Array.Empty<PluginRef>());
        }
        var host = new PluginHost(repoRoot, config.Plugins);
        await host.LoadAllAsync().ConfigureAwait(false);
        return host;
    }

    private static async Task<int> RunListAsync(CommandLine cli)
    {
        var host = await DiscoverAsync(cli).ConfigureAwait(false);
        if (host.Plugins.Count == 0)
        {
            Console.WriteLine("No plugins declared in .sourcegraph.json.");
            return 0;
        }
        Console.WriteLine($"{"id",-40} {"version",-12} {"status",-9} {"contracts",-30} path");
        foreach (var p in host.Plugins)
        {
            var contracts = string.Join(",",
                new[]
                {
                    p.LanguageIndexers.Count > 0 ? $"lang({p.LanguageIndexers.Count})" : null,
                    p.Analyzers.Count > 0 ? $"analyzer({p.Analyzers.Count})" : null,
                    p.ToolPlugins.Count > 0 ? $"tools({p.ToolPlugins.Count})" : null,
                }.Where(x => x is not null));
            Console.WriteLine($"{p.Identity,-40} {p.Version ?? "-",-12} {p.Status.ToString().ToLowerInvariant(),-9} {contracts,-30} {p.SourcePath}");
        }
        return 0;
    }

    private static async Task<int> RunInfoAsync(CommandLine cli)
    {
        if (cli.Positional.Count < 2)
        {
            await Console.Error.WriteLineAsync("usage: sourcegraph-mcp plugins info <name>").ConfigureAwait(false);
            return 2;
        }
        var name = cli.Positional[1];
        var host = await DiscoverAsync(cli).ConfigureAwait(false);
        var p = host.Plugins.FirstOrDefault(x => string.Equals(x.Identity, name, StringComparison.OrdinalIgnoreCase));
        if (p is null)
        {
            await Console.Error.WriteLineAsync($"plugin `{name}` is not declared in .sourcegraph.json").ConfigureAwait(false);
            return 1;
        }
        Console.WriteLine($"id:       {p.Identity}");
        Console.WriteLine($"version:  {p.Version ?? "-"}");
        Console.WriteLine($"status:   {p.Status.ToString().ToLowerInvariant()}");
        if (!string.IsNullOrEmpty(p.StatusMessage)) Console.WriteLine($"  -> {p.StatusMessage}");
        Console.WriteLine($"source:   {p.SourcePath}");
        Console.WriteLine($"is-nuget: {p.IsNuGet}");
        Console.WriteLine($"prefix:   {p.ToolPrefix ?? "-"}");
        Console.WriteLine($"language indexers ({p.LanguageIndexers.Count}):");
        foreach (var li in p.LanguageIndexers)
        {
            Console.WriteLine($"  - {li.GetType().FullName} : extensions = [{string.Join(", ", li.FileExtensions)}]");
        }
        Console.WriteLine($"analyzers ({p.Analyzers.Count}):");
        foreach (var a in p.Analyzers)
        {
            Console.WriteLine($"  - {a.Name} ({a.GetType().FullName})");
        }
        Console.WriteLine($"tool plugins ({p.ToolPlugins.Count}):");
        foreach (var tp in p.ToolPlugins)
        {
            Console.WriteLine($"  - {tp.GetType().FullName} : prefix = `{tp.Prefix}`");
        }
        if (p.RegisteredToolNames.Count > 0)
        {
            Console.WriteLine($"registered tools ({p.RegisteredToolNames.Count}):");
            foreach (var t in p.RegisteredToolNames) Console.WriteLine($"  - {t}");
        }
        return 0;
    }

    private static int Unknown(string sub)
    {
        Console.Error.WriteLine($"Unknown plugins subcommand: {sub}");
        Console.Error.WriteLine("usage: sourcegraph-mcp plugins [list|info <name>]");
        return 2;
    }
}
