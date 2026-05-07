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
        => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args).Any() ? 0 : 1;
}
