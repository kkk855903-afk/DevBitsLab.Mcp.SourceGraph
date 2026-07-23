using System.Runtime.InteropServices;

namespace MedInteropChain.NegativeCases.Interop005;

internal static class NativeExceptionRisk
{
    [DllImport("medalgo", EntryPoint = "risk_throws")]
    internal static extern int InvokeThrowingExport();
}
