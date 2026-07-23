using System.Runtime.InteropServices;

namespace MedInteropChain.NegativeCases.Interop003;

internal static class ParameterTypeRisk
{
    [DllImport("medalgo", EntryPoint = "risk_parameter", CharSet = CharSet.Ansi)]
    internal static extern int RiskyParameter(long value, string text);
}
