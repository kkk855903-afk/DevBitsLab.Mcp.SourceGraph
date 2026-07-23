using System.Runtime.InteropServices;

namespace MedInteropChain.NegativeCases.Interop001;

internal static class CallingConventionRisk
{
    [DllImport("medalgo", EntryPoint = "risk_callconv", CallingConvention = CallingConvention.StdCall)]
    internal static extern int WrongCallingConvention(int value);
}
