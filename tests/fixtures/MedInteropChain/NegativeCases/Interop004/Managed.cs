using System.Runtime.InteropServices;

namespace MedInteropChain.NegativeCases.Interop004;

internal static class CallbackGcRisk
{
    internal delegate void ResultCallback(int value);

    [DllImport("medalgo", EntryPoint = "risk_register_callback")]
    private static extern void RegisterCallback(ResultCallback callback);

    internal static void RegisterUnrootedCallback() =>
        RegisterCallback(value => Console.WriteLine(value));
}
