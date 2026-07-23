using System.Runtime.InteropServices;

namespace MedInteropChain.GrpcService.Interop;

internal static partial class NativeMethods
{
    [DllImport(
        "medalgo",
        EntryPoint = "medalgo_calculate",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    internal static extern int Calculate(in NativeInput input, out NativeOutput output);
}
