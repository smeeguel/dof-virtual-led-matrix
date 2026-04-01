using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Rendering.Vulkan;

public interface IVulkanInteropAdapter : IDisposable
{
    void Initialize(IntPtr hostHwnd, MatrixConfig config);

    void Resize(int width, int height);

    void UpdateShaderParameters(in VulkanShaderParameterBlock parameters);

    void UploadAndRender(ReadOnlySpan<GpuDotInstance> stagingInstances, int frameSlot);

    void NotifyDeviceLost();
}
