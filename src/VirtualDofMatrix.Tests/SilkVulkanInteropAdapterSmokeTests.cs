using VirtualDofMatrix.App.Rendering.Vulkan;
using VirtualDofMatrix.Core;
using Xunit;

namespace VirtualDofMatrix.Tests;

public sealed class SilkVulkanInteropAdapterSmokeTests
{
    [Fact]
    public void AdapterLifecycle_ShouldNotThrow_WhenVulkanProbeSucceeds()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (!VulkanCapabilityProbe.TryProbe(out _))
        {
            return;
        }

        var adapter = new SilkVulkanInteropAdapter();
        adapter.Initialize(IntPtr.Zero, new MatrixConfig());
        adapter.RecreateSwapchain(1280, 320, "fifo");
        adapter.UpdateShaderParameters(VulkanShaderParameterBlock.FromVisual(new MatrixVisualConfig()));
        adapter.UploadAndRender(new GpuDotInstance[1], frameSlot: 0);
        adapter.NotifyDeviceLost();
        adapter.Dispose();
    }
}
