using System.Runtime.InteropServices;
using System.Windows.Controls;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Rendering;

public sealed class VulkanMatrixRenderer : IMatrixRenderer
{
    private readonly WpfPrimitiveMatrixRenderer _fallbackRenderer = new();

    public bool UsesImageHost => _fallbackRenderer.UsesImageHost;

    public static bool TryCreate(out VulkanMatrixRenderer renderer, out string failureReason)
    {
        renderer = new VulkanMatrixRenderer();

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            failureReason = "Vulkan renderer is only supported on Windows builds.";
            return false;
        }

        if (!NativeLibrary.TryLoad("vulkan-1", out var handle))
        {
            failureReason = "Vulkan loader (vulkan-1.dll) was not found on this machine.";
            return false;
        }

        NativeLibrary.Free(handle);
        failureReason = string.Empty;
        return true;
    }

    public void Initialize(Canvas primitiveCanvas, Image bitmapHost, MatrixConfig config)
    {
        _fallbackRenderer.Initialize(primitiveCanvas, bitmapHost, config);
    }

    public void Render(FramePresentation framePresentation)
    {
        _fallbackRenderer.Render(framePresentation);
    }
}
