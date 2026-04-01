namespace VirtualDofMatrix.App.Rendering.Vulkan;

internal static class VulkanCapabilityProbe
{
    public static bool TryProbe(out string reason)
    {
        if (!OperatingSystem.IsWindows())
        {
            reason = "Vulkan renderer is only available on Windows hosts.";
            return false;
        }

        // Current repository includes Vulkan plumbing and shader assets,
        // but does not yet ship a concrete native Vulkan adapter.
        reason = "Vulkan backend is not installed in this build; falling back to primitive renderer.";
        return false;
    }
}
