using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Rendering.Vulkan;

/// <summary>
/// Placeholder Vulkan adapter used until a concrete Silk.NET/VulkanSharp backend is added.
/// Keeps renderer plumbing testable and allows incremental rollout of native interop.
/// </summary>
public sealed class NullVulkanInteropAdapter : IVulkanInteropAdapter
{
    private readonly GpuDotInstance[][] _deviceRing =
    [
        Array.Empty<GpuDotInstance>(),
        Array.Empty<GpuDotInstance>(),
    ];

    public void Initialize(IntPtr hostHwnd, MatrixConfig config)
    {
        // Intentionally no-op.
    }

    public void RecreateSwapchain(int width, int height, string presentMode)
    {
        // Intentionally no-op.
    }

    public void UpdateShaderParameters(in VulkanShaderParameterBlock parameters)
    {
        // Intentionally no-op. Real adapter uploads to push constants/uniforms.
    }

    public void UploadAndRender(ReadOnlySpan<GpuDotInstance> stagingInstances, int frameSlot)
    {
        var slot = Math.Abs(frameSlot) % _deviceRing.Length;
        if (_deviceRing[slot].Length != stagingInstances.Length)
        {
            _deviceRing[slot] = new GpuDotInstance[stagingInstances.Length];
        }

        stagingInstances.CopyTo(_deviceRing[slot]);
        // Intentionally no-op presentation. This placeholder still models
        // staging->device buffer copy and double-buffered upload slots.
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
