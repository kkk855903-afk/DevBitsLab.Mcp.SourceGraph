using System.Linq;
using BenchmarkDotNet.Running;

namespace DevBitsLab.Mcp.SourceGraph.Benchmarks;

/// <summary>
/// Run with: <c>dotnet run -c Release --project bench/DevBitsLab.Mcp.SourceGraph.Benchmarks</c>.
/// Filter with <c>-- --filter '*Search*'</c> to run a subset.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length > 0
            && string.Equals(args[0], "--same-process-query", StringComparison.Ordinal))
        {
            return SameProcessQueryProbe.RunAsync(args[1..]).GetAwaiter().GetResult();
        }
        return BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args).Any() ? 0 : 1;
    }
}
