using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace DevBitsLab.Mcp.SourceGraph.Server.Plugins;

/// <summary>
/// Helper used by Program.cs to construct a logger before the host is built. The DI-managed
/// <see cref="ILoggerFactory"/> isn't available until <c>HostBuilder.Build()</c>, but the plugin
/// host needs a logger during the bootstrap window where plugins are discovered. This factory is
/// used exactly during that window and disposed when the throwaway loggers go out of scope.
///
/// <para>
/// CRITICAL: log every level to stderr, never stdout. The MCP server speaks JSON-RPC over stdout;
/// any unstructured log line that lands there will corrupt the wire protocol. <see cref="ConsoleLoggerOptions.LogToStandardErrorThreshold"/>
/// is set to <c>Trace</c> so absolutely everything (Trace through Critical) is redirected to
/// stderr, mirroring the way the DI-managed `serve` logger is configured in Program.cs.
/// </para>
/// </summary>
internal static class StartupLogging
{
    public static ILogger<T> CreateLogger<T>()
    {
        var factory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Information);
            b.AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss ";
            });
            // Redirect every log level (Trace through Critical) to stderr so plugin-host startup
            // logs do not corrupt the JSON-RPC stdout channel the MCP transport speaks on.
            b.Services.Configure<ConsoleLoggerOptions>(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        });
        return factory.CreateLogger<T>();
    }
}
