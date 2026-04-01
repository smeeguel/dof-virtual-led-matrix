using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Rendering.Vulkan;

/// <summary>
/// Placeholder Vulkan adapter used until a concrete Silk.NET/VulkanSharp backend is added.
/// Keeps renderer plumbing testable and allows incremental rollout of native interop.
/// </summary>
public sealed class NullVulkanInteropAdapter : IVulkanInteropAdapter
{
    public void Initialize(IntPtr hostHwnd, MatrixConfig config)
    {
        // Intentionally no-op.
    }

    public void Resize(int width, int height)
    {
        // Intentionally no-op.
    }

    public void RenderFrame(FramePresentation framePresentation)
    {
        // Intentionally no-op.
    }

    public void NotifyDeviceLost()
    {
        // Intentionally no-op.
    }

    public void Dispose()
    {
        // Intentionally no-op.
    }
}
