using System.Runtime.InteropServices;

namespace MedInteropChain.NegativeCases.Interop002;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct WrongLayout
{
    public byte Enabled;
    public int Count;
}
