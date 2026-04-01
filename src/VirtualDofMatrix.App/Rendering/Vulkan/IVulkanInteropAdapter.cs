using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Rendering.Vulkan;

public interface IVulkanInteropAdapter : IDisposable
{
    void Initialize(IntPtr hostHwnd, MatrixConfig config);

    void RecreateSwapchain(int width, int height, string presentMode);

    void UpdateShaderParameters(in VulkanShaderParameterBlock parameters);

    void UploadAndRender(ReadOnlySpan<GpuDotInstance> stagingInstances, int frameSlot);

    void NotifyDeviceLost();
}
