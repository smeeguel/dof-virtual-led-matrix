using System.Runtime.InteropServices;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Rendering.Vulkan;

[StructLayout(LayoutKind.Sequential)]
public struct VulkanShaderParameterBlock
{
    public float OffTintR;
    public float OffTintG;
    public float OffTintB;
    public float BodyContribution;
    public float CoreContribution;
    public float SpecularContribution;
    public float CoreBase;
    public float CoreIntensityScale;
    public float SpecularBase;
    public float SpecularIntensityScale;
    public float SpecularMax;

    public static VulkanShaderParameterBlock FromVisual(MatrixVisualConfig visual)
    {
        return new VulkanShaderParameterBlock
        {
            OffTintR = visual.OffStateTintR / 255f,
            OffTintG = visual.OffStateTintG / 255f,
            OffTintB = visual.OffStateTintB / 255f,
            BodyContribution = (float)Math.Clamp(visual.BodyContribution, 0.0, 4.0),
            CoreContribution = (float)Math.Clamp(visual.CoreContribution, 0.0, 4.0),
            SpecularContribution = (float)Math.Clamp(visual.SpecularContribution, 0.0, 4.0),
            CoreBase = (float)visual.CoreBase,
            CoreIntensityScale = (float)visual.CoreIntensityScale,
            SpecularBase = (float)visual.SpecularBase,
            SpecularIntensityScale = (float)visual.SpecularIntensityScale,
            SpecularMax = (float)Math.Clamp(visual.SpecularMax, 0.0, 1.0),
        };
    }
}
