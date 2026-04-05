using Vortice.Direct3D11;
using VirtualDofMatrix.App.Rendering;
using Xunit;

namespace VirtualDofMatrix.Tests;

public sealed class GpuTextureDescriptorTests
{
    [Fact]
    public void CreateFullSurfaceDescription_ShouldDisableSharedMiscFlags_ForStagingReadback()
    {
        var renderer = new GpuInstancedMatrixRenderer();

        // This specifically guards the startup regression where a staging texture accidentally inherited Shared flags.
        var desc = renderer.CreateFullSurfaceDescription(ResourceUsage.Staging, BindFlags.None, CpuAccessFlags.Read, allowShared: true);

        Assert.Equal(ResourceOptionFlags.None, desc.MiscFlags);
    }
}
