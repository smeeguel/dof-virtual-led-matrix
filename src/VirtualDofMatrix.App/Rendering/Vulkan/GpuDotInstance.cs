using System.Runtime.InteropServices;

namespace VirtualDofMatrix.App.Rendering.Vulkan;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct GpuDotInstance
{
    public ushort X;
    public ushort Y;
    public byte R;
    public byte G;
    public byte B;
    public byte Intensity;
    public byte Flags;
}
