using System.Runtime.InteropServices;

namespace MedInteropChain.NegativeCases.Interop006;

internal static class AllocatorMismatchRisk
{
    [DllImport("medalgo", EntryPoint = "risk_allocate")]
    private static extern IntPtr Allocate();

    internal static void FreeWithWrongAllocator()
    {
        var pointer = Allocate();
        Marshal.FreeCoTaskMem(pointer);
    }
}
