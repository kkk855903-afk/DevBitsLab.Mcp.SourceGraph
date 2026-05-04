using System.Reflection;
using System.Runtime.Loader;

namespace DevBitsLab.Mcp.SourceGraph.Server.Plugins;

/// <summary>
/// Per-plugin <see cref="AssemblyLoadContext"/> that isolates the plugin's private deps but shares
/// a documented set of well-known assemblies with the host. The shared list MUST include every
/// type the host hands to the plugin and every type the plugin hands back, otherwise the host
/// would fail with <c>InvalidCastException</c> when the same type loaded into different ALCs is
/// treated as distinct.
///
/// <para>
/// Shared assemblies (loaded from the host context):
/// <list type="bullet">
///   <item><c>DevBitsLab.Mcp.SourceGraph.Sdk</c> — the public contracts; without this the
///         <see cref="DevBitsLab.Mcp.SourceGraph.Sdk.ILanguageIndexer"/> the plugin implements
///         would not be the same type the host registers against.</item>
///   <item><c>Microsoft.CodeAnalysis.*</c> — Roslyn types are heavy and one process must not load
///         them twice. Plugins that need Roslyn pick it up from the host's already-loaded copy.</item>
///   <item><c>Microsoft.Extensions.*</c> — logging / DI abstractions used in plugin signatures.</item>
///   <item><c>System.*</c> / <c>netstandard</c> — always shared by ALC convention.</item>
/// </list>
/// Anything else the plugin depends on is loaded from the plugin's directory (private).
/// </para>
/// </summary>
internal sealed class PluginAssemblyLoadContext : AssemblyLoadContext
{
    private static readonly string[] _sharedAssemblyPrefixes =
    {
        "DevBitsLab.Mcp.SourceGraph.Sdk",
        "Microsoft.CodeAnalysis",
        "Microsoft.Extensions.",
        "Microsoft.Build.",
        "System.",
        "netstandard",
        "mscorlib",
    };

    private readonly AssemblyDependencyResolver _resolver;

    public PluginAssemblyLoadContext(string pluginPath, string name)
        : base(name, isCollectible: false)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    /// <summary>
    /// Resolves an assembly request. Names matching a shared prefix are deferred to the default
    /// (host) load context — the host's already-loaded copy is reused. Everything else falls back
    /// to <see cref="AssemblyDependencyResolver"/>, which picks up the plugin's private deps from
    /// its <c>.deps.json</c>; if that fails, returning <c>null</c> lets the runtime apply its own
    /// fallback chain.
    /// </summary>
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is { } n)
        {
            foreach (var shared in _sharedAssemblyPrefixes)
            {
                if (n.StartsWith(shared, StringComparison.Ordinal))
                {
                    // Defer to the default (host) ALC. Returning null falls through to the parent
                    // resolution chain, which is exactly what we want here.
                    return null;
                }
            }
            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            if (path is not null) return LoadFromAssemblyPath(path);
        }
        return null;
    }

    /// <summary>Resolve an unmanaged DLL the plugin's deps.json references (e.g., a native vec0 pair).</summary>
    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is not null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
}
