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
        var desc = renderer.CreateFullSurfaceDescription(ResourceUsage.Staging, BindFlags.None, CpuAccessFlags.Read, allowShared: true, Vortice.DXGI.Format.R8G8B8A8_UNorm);

        Assert.Equal(ResourceOptionFlags.None, desc.MiscFlags);
    }

    [Fact]
    public void CreateFullSurfaceDescription_ShouldUseBgra_WhenSharingIsRequested()
    {
        var renderer = new GpuInstancedMatrixRenderer();

        // D3DImage/D3D9Ex interop expects a BGRA-compatible shared surface.
        var desc = renderer.CreateFullSurfaceDescription(ResourceUsage.Default, BindFlags.RenderTarget | BindFlags.ShaderResource, CpuAccessFlags.None, allowShared: true, Vortice.DXGI.Format.B8G8R8A8_UNorm);

        Assert.Equal(Vortice.DXGI.Format.B8G8R8A8_UNorm, desc.Format);
        Assert.Equal(ResourceOptionFlags.Shared, desc.MiscFlags);
    }
}
