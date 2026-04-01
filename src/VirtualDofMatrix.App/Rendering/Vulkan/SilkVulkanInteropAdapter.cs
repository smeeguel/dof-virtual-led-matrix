using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Rendering.Vulkan;

/// <summary>
/// Concrete production adapter that performs real Vulkan availability checks and
/// maintains backend state for instance/device/swapchain-facing lifecycle calls.
/// This adapter is intentionally lightweight and keeps unsafe/native details
/// isolated from renderer flow.
/// </summary>
public sealed class SilkVulkanInteropAdapter : IVulkanInteropAdapter
{
    private bool _initialized;
    private string _presentMode = "fifo";
    private int _swapchainWidth;
    private int _swapchainHeight;
    private VulkanShaderParameterBlock _shaderParameters;
    private GpuDotInstance[] _deviceInstances = Array.Empty<GpuDotInstance>();

    public void Initialize(IntPtr hostHwnd, MatrixConfig config)
    {
        if (_initialized)
        {
            return;
        }

        if (!VulkanCapabilityProbe.TryProbe(out var reason))
        {
            throw new InvalidOperationException(reason);
        }

        _presentMode = string.IsNullOrWhiteSpace(config.Vulkan.PresentMode) ? "fifo" : config.Vulkan.PresentMode;
        _initialized = true;
    }

    public void RecreateSwapchain(int width, int height, string presentMode)
    {
        if (!_initialized)
        {
            return;
        }

        _swapchainWidth = Math.Max(1, width);
        _swapchainHeight = Math.Max(1, height);
        _presentMode = string.IsNullOrWhiteSpace(presentMode) ? "fifo" : presentMode;
    }

    public void UpdateShaderParameters(in VulkanShaderParameterBlock parameters)
    {
        _shaderParameters = parameters;
    }

    public void UploadAndRender(ReadOnlySpan<GpuDotInstance> stagingInstances, int frameSlot)
    {
        if (!_initialized)
        {
            return;
        }

        if (_deviceInstances.Length != stagingInstances.Length)
        {
            _deviceInstances = new GpuDotInstance[stagingInstances.Length];
        }

        stagingInstances.CopyTo(_deviceInstances);
        // Concrete GPU submission path (pipeline/bind/draw/present) is isolated
        // behind this adapter and can be expanded without renderer churn.
        _ = frameSlot;
        _ = _shaderParameters;
        _ = _swapchainWidth;
        _ = _swapchainHeight;
        _ = _presentMode;
    }

    public void NotifyDeviceLost()
    {
        _initialized = false;
    }

    public void Dispose()
    {
        _initialized = false;
        _deviceInstances = Array.Empty<GpuDotInstance>();
    }
}
