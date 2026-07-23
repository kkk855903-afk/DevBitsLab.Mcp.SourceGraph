using System.IO;
using System.Reflection;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Reflection helpers for driving the Server's internal CLI subcommands from tests. The
/// CLI types (`CommandLine`, `ScopesCli`, `VocabularyCli`, …) are <c>internal</c> to the
/// Server assembly; <c>InternalsVisibleTo</c> exposes them to this test project but the
/// types themselves still don't satisfy <c>typeof(...)</c> from outside the project's
/// namespace tree without a fully-qualified path. This helper centralises the
/// `Type.GetType` + method dispatch + stdout capture so individual CLI tests stay tight.
///
/// <para><b>Threading:</b> <see cref="RunSubcommandAsync"/> redirects
/// <see cref="Console.Out"/> globally via <see cref="Console.SetOut"/>. Callers MUST be in
/// the <c>CliConsole</c> xUnit collection (see <c>OnboardingCliTests</c>,
/// <c>ScopesInfoCliTests</c>) so xUnit serialises every Console redirection across the
/// affected test classes. Without that, parallel test runs racing on
/// <c>Console.SetOut</c> can interleave or restore the wrong writer.</para>
/// </summary>
internal static class InternalCli
{
    private static readonly Assembly _serverAssembly =
        typeof(DevBitsLab.Mcp.SourceGraph.Server.Plugins.LanguageIndexerRegistry).Assembly;

    /// <summary>
    /// Parse <paramref name="args"/> via the internal <c>CommandLine.Parse</c> and dispatch
    /// the result through <paramref name="subcommandTypeName"/>'s
    /// <c>RunSubcommandAsync(CommandLine)</c> static method, capturing stdout. Returns the
    /// captured output and the subcommand's exit code.
    /// </summary>
    public static async Task<(string Stdout, int ExitCode)> RunSubcommandAsync(string subcommandTypeName, string[] args)
    {
        var cmdLineType = _serverAssembly.GetType("DevBitsLab.Mcp.SourceGraph.Server.Cli.CommandLine")
            ?? throw new InvalidOperationException("CommandLine type not found in Server assembly.");
        var subcommandType = _serverAssembly.GetType($"DevBitsLab.Mcp.SourceGraph.Server.Cli.{subcommandTypeName}")
            ?? throw new InvalidOperationException($"{subcommandTypeName} not found in Server assembly.");

        var parse = cmdLineType.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("CommandLine.Parse method not found.");
        var run = subcommandType.GetMethod("RunSubcommandAsync", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException($"{subcommandTypeName}.RunSubcommandAsync not found.");

        var parsed = parse.Invoke(null, new object[] { args })
            ?? throw new InvalidOperationException("CommandLine.Parse returned null.");

        using var sw = new StringWriter();
        var prev = Console.Out;
        Console.SetOut(sw);
        try
        {
            var task = (Task<int>)(run.Invoke(null, new[] { parsed })
                ?? throw new InvalidOperationException($"{subcommandTypeName}.RunSubcommandAsync returned null."));
            var exit = await task;
            return (sw.ToString(), exit);
        }
        finally
        {
            Console.SetOut(prev);
        }
    }
}
