using System.Reflection;
using System.Runtime.InteropServices;

namespace DevBitsLab.Mcp.SourceGraph.Server.Cli;

/// <summary>
/// Renders the identity of the executable that is actually running. Package builds can override
/// <c>Version</c> at pack time (for example a local patch build), so assembly metadata is the
/// authoritative source rather than a duplicated constant.
/// </summary>
internal static class VersionInfo
{
    public static string EffectiveVersion
    {
        get
        {
            var assembly = typeof(VersionInfo).Assembly;
            var informationalVersion = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            return string.IsNullOrWhiteSpace(informationalVersion)
                ? assembly.GetName().Version?.ToString() ?? "unknown"
                : informationalVersion;
        }
    }

    public static string Render()
    {
        var assembly = typeof(VersionInfo).Assembly;
        var assemblyVersion = assembly.GetName().Version?.ToString()
            ?? "unknown";

        return string.Join(
            Environment.NewLine,
            $"sourcegraph-mcp {EffectiveVersion}",
            $"assembly: {assemblyVersion}",
            $"runtime: {RuntimeInformation.FrameworkDescription}",
            $"os: {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture})");
    }
}
