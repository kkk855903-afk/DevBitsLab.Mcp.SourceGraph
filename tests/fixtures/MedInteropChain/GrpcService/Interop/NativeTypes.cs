using System.Runtime.InteropServices;

namespace MedInteropChain.GrpcService.Interop;

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NativeInput
{
    public int PatientAge;
    public double Scale;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NativeOutput
{
    public int Value;
}
