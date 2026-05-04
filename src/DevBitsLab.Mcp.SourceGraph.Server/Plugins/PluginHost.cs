using System.Diagnostics;
using System.Reflection;
using System.Text;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevBitsLab.Mcp.SourceGraph.Server.Plugins;

/// <summary>
/// Discovers, isolates, and instantiates plugins declared in <c>.sourcegraph.json</c>'s
/// <c>plugins[]</c> array. Each plugin gets its own <see cref="PluginAssemblyLoadContext"/> so a
/// crashing plugin's deps don't pollute the host. Per-plugin try/catch wraps every interaction
/// with the SDK contracts: a throwing plugin is marked <see cref="PluginStatus.Failed"/> and the
/// rest of the host keeps running. The built-in <c>RoslynLanguageIndexer</c> is registered
/// outside this host (in <c>Program.cs</c>) so single-solution users with no <c>plugins[]</c>
/// entry behave exactly like v0.5.0.
/// </summary>
public sealed class PluginHost
{
    private readonly string _repoRoot;
    private readonly IReadOnlyList<PluginRef> _refs;
    private readonly ILogger<PluginHost> _logger;
    private readonly List<PluginRecord> _plugins = new();

    public PluginHost(string repoRoot, IReadOnlyList<PluginRef> refs, ILogger<PluginHost>? logger = null)
    {
        _repoRoot = repoRoot;
        _refs = refs;
        _logger = logger ?? NullLogger<PluginHost>.Instance;
    }

    /// <summary>The plugin records, in declaration order. Mutated in place as plugins fail.</summary>
    public IReadOnlyList<PluginRecord> Plugins => _plugins;

    /// <summary>
    /// Discover and instantiate every declared plugin. Each step is wrapped in try/catch so a
    /// malformed entry, restore failure, or constructor throw never aborts the whole pass.
    /// Returns synchronously after every plugin has been processed (load is the slow path; the
    /// async surface is preserved for a future when restoration goes through an HTTP-driven NuGet
    /// client).
    /// </summary>
    public async Task LoadAllAsync(CancellationToken ct = default)
    {
        if (_refs.Count == 0)
        {
            _logger.LogDebug("No plugins declared in .sourcegraph.json; plugin host is idle");
            return;
        }
        foreach (var pref in _refs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await LoadPluginAsync(pref, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Plugin `{Identity}` failed during discovery", pref.Identity);
                _plugins.Add(new PluginRecord(pref.Identity, pref.Version, pref.Path ?? "?", pref.IsNuGet)
                {
                    Status = PluginStatus.Failed,
                    StatusMessage = ex.Message,
                });
            }
        }
    }

    private async Task LoadPluginAsync(PluginRef pref, CancellationToken ct)
    {
        string assemblyPath;
        if (pref.IsPath)
        {
            // Path resolution: relative paths resolve against the repo root so users can write
            // ".sourcegraph/plugins/Foo.dll" without remembering the absolute path.
            assemblyPath = Path.IsPathRooted(pref.Path!)
                ? pref.Path!
                : Path.GetFullPath(Path.Combine(_repoRoot, pref.Path!));
            if (!File.Exists(assemblyPath))
            {
                throw new FileNotFoundException($"Plugin DLL not found: {assemblyPath}");
            }
        }
        else
        {
            // NuGet restore path. We synthesise a tiny csproj that references the requested
            // package and version, run `dotnet restore` against it, and pick up the package's
            // primary assembly from the restored output. This is intentionally rudimentary in
            // v1 — sufficient for the supported case where the package's main DLL matches its id.
            assemblyPath = await RestoreNuGetPluginAsync(pref, ct).ConfigureAwait(false);
        }

        var record = new PluginRecord(pref.Identity, pref.Version, assemblyPath, pref.IsNuGet);
        _plugins.Add(record);

        try
        {
            var alc = new PluginAssemblyLoadContext(assemblyPath, $"plugin:{pref.Identity}");
            var asm = alc.LoadFromAssemblyPath(assemblyPath);

            // Pre-read the optional [McpToolPlugin(prefix)] assembly attribute so `plugins info`
            // can render the prefix even if the IMcpToolPlugin instantiation fails later.
            var pluginAttr = asm.GetCustomAttribute<McpToolPluginAttribute>();
            if (pluginAttr is not null)
            {
                record.ToolPrefix = pluginAttr.Prefix;
            }

            // Walk every public type in the plugin assembly and try to instantiate the three
            // contract interfaces. We instantiate using the parameterless constructor in v1; a
            // future iteration might thread an IServiceProvider in. Each instantiation is in a
            // try/catch so a single throwing constructor doesn't kill the whole record.
            foreach (var type in SafeGetTypes(asm))
            {
                if (type.IsAbstract || type.IsInterface) continue;
                if (typeof(ILanguageIndexer).IsAssignableFrom(type))
                {
                    if (TryActivate<ILanguageIndexer>(type, record, "ILanguageIndexer") is { } li)
                    {
                        record.LanguageIndexers.Add(li);
                        foreach (var ext in li.FileExtensions ?? Array.Empty<string>())
                        {
                            record.RegisteredFileExtensions.Add(ext);
                        }
                    }
                }
                if (typeof(ICodeAnalyzer).IsAssignableFrom(type))
                {
                    if (TryActivate<ICodeAnalyzer>(type, record, "ICodeAnalyzer") is { } ca)
                    {
                        record.Analyzers.Add(ca);
                    }
                }
                if (typeof(IMcpToolPlugin).IsAssignableFrom(type))
                {
                    if (TryActivate<IMcpToolPlugin>(type, record, "IMcpToolPlugin") is { } tp)
                    {
                        record.ToolPlugins.Add(tp);
                        if (string.IsNullOrEmpty(record.ToolPrefix))
                        {
                            record.ToolPrefix = tp.Prefix;
                        }
                    }
                }
            }

            if (record.LanguageIndexers.Count == 0 && record.Analyzers.Count == 0 && record.ToolPlugins.Count == 0)
            {
                record.Status = PluginStatus.Failed;
                record.StatusMessage = "Plugin assembly contained no ILanguageIndexer / ICodeAnalyzer / IMcpToolPlugin implementations.";
                _logger.LogWarning("Plugin `{Identity}` loaded but exposed no contract implementations", pref.Identity);
                return;
            }

            _logger.LogInformation(
                "Plugin `{Identity}` loaded: {Indexers} indexer(s), {Analyzers} analyzer(s), {ToolPlugins} tool plugin(s)",
                pref.Identity,
                record.LanguageIndexers.Count,
                record.Analyzers.Count,
                record.ToolPlugins.Count);
        }
        catch (Exception ex)
        {
            record.Status = PluginStatus.Failed;
            record.StatusMessage = ex.Message;
            _logger.LogError(ex, "Plugin `{Identity}` failed during instantiation", pref.Identity);
        }
    }

    /// <summary>
    /// Try to activate <paramref name="type"/> as <typeparamref name="T"/>. Returns null on any
    /// throw and downgrades <paramref name="record"/>'s status accordingly. The contract type
    /// label is included in the failure message so <c>plugins info</c> can show which interface
    /// failed to instantiate.
    /// </summary>
    private T? TryActivate<T>(Type type, PluginRecord record, string contractLabel) where T : class
    {
        try
        {
            var instance = Activator.CreateInstance(type) as T;
            if (instance is null)
            {
                record.Status = PluginStatus.Failed;
                record.StatusMessage = $"Activator.CreateInstance({type.FullName}) returned null for contract {contractLabel}";
                return null;
            }
            return instance;
        }
        catch (Exception ex)
        {
            record.Status = PluginStatus.Failed;
            record.StatusMessage = $"Activator failure for {type.FullName} as {contractLabel}: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// <see cref="Assembly.GetTypes"/> can throw <see cref="ReflectionTypeLoadException"/> when a
    /// dep is missing; that's expected for plugins compiled against a different SDK version
    /// pinned in central pkg management. Surface every type we managed to load and ignore the
    /// rest.
    /// </summary>
    private static IEnumerable<Type> SafeGetTypes(Assembly asm)
    {
        try { return asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null).Cast<Type>();
        }
    }

    /// <summary>
    /// Synthesise a tiny csproj that references the plugin package, run <c>dotnet restore</c>,
    /// and return the restored DLL's absolute path. Cached at
    /// <c>&lt;repo&gt;/.sourcegraph/plugins/restore/</c> so subsequent boots reuse the same
    /// nuget cache. NB: deliberately rudimentary — we reuse the host's <c>dotnet</c> binary, the
    /// host's NuGet config, and the host's TFM. A v2 might lift restore into a managed NuGet
    /// client to avoid depending on the SDK being on PATH.
    /// </summary>
    private async Task<string> RestoreNuGetPluginAsync(PluginRef pref, CancellationToken ct)
    {
        var restoreDir = Path.Combine(_repoRoot, ".sourcegraph", "plugins", "restore");
        Directory.CreateDirectory(restoreDir);
        var pluginsCsproj = Path.Combine(restoreDir, "plugins.csproj");

        // Single project file restoring every NuGet plugin in one shot; rewriting it is cheap
        // and keeps the on-disk state aligned with the current `.sourcegraph.json`.
        var sb = new StringBuilder();
        sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
        sb.AppendLine("  <PropertyGroup>");
        sb.AppendLine("    <TargetFramework>net10.0</TargetFramework>");
        sb.AppendLine("    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>");
        sb.AppendLine("    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>");
        sb.AppendLine("  </PropertyGroup>");
        sb.AppendLine("  <ItemGroup>");
        foreach (var p in _refs.Where(r => r.IsNuGet))
        {
            sb.AppendLine($"    <PackageReference Include=\"{p.Package}\" Version=\"{p.Version}\" />");
        }
        sb.AppendLine("  </ItemGroup>");
        sb.AppendLine("</Project>");
        await File.WriteAllTextAsync(pluginsCsproj, sb.ToString(), ct).ConfigureAwait(false);

        // Run `dotnet restore`. We don't fail-fast here when the restore process is missing —
        // the surrounding try/catch will mark the plugin failed.
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            ArgumentList = { "restore", pluginsCsproj, "--verbosity", "minimal" },
            WorkingDirectory = restoreDir,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start `dotnet restore` for plugin pack");
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        if (proc.ExitCode != 0)
        {
            var stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException($"`dotnet restore` failed for `{pref.Package}` v{pref.Version}: {stderr}");
        }

        // Pick up the restored package's primary assembly from the user-cache nuget store.
        // We ask `dotnet build /t:_FindPluginAssemblies` indirectly: the simplest portable thing
        // is to look in the global packages folder. Mirrors the path NuGet creates by default.
        var nugetGlobal = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        var pkgDir = Path.Combine(nugetGlobal, pref.Package!.ToLowerInvariant(), pref.Version!);
        if (!Directory.Exists(pkgDir))
        {
            throw new DirectoryNotFoundException($"Restored package directory missing: {pkgDir}");
        }
        // Pick the first net10.0 / netstandard2.1 / netstandard2.0 dll matching the package id.
        var libDir = Path.Combine(pkgDir, "lib");
        if (!Directory.Exists(libDir)) throw new DirectoryNotFoundException($"`lib/` missing under {pkgDir}");
        var preferredTfms = new[] { "net10.0", "net9.0", "net8.0", "netstandard2.1", "netstandard2.0" };
        foreach (var tfm in preferredTfms)
        {
            var candidate = Path.Combine(libDir, tfm, pref.Package + ".dll");
            if (File.Exists(candidate)) return candidate;
        }
        // Fallback: any DLL named after the package id.
        var any = Directory.EnumerateFiles(libDir, pref.Package + ".dll", SearchOption.AllDirectories).FirstOrDefault();
        if (any is not null) return any;

        throw new FileNotFoundException($"Could not locate `{pref.Package}.dll` under {libDir}");
    }
}
