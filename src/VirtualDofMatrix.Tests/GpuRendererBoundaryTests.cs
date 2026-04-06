using VirtualDofMatrix.Core;
using VirtualDofMatrix.App.Rendering;
using Xunit;

namespace VirtualDofMatrix.Tests;

public sealed class GpuRendererBoundaryTests
{
    [Fact]
    public void LogicalToRasterMap_ShouldSupportRowColumnAndSerpentine()
    {
        var rowMajor = MatrixFrameIndexMap.BuildLogicalToRasterMap(4, 2, "RowMajor");
        var columnMajor = MatrixFrameIndexMap.BuildLogicalToRasterMap(4, 2, "ColumnMajor");
        var serpentine = MatrixFrameIndexMap.BuildLogicalToRasterMap(4, 2, "TopDownAlternateRightLeft");

        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6, 7 }, rowMajor);
        Assert.Equal(new[] { 0, 4, 1, 5, 2, 6, 3, 7 }, columnMajor);
        Assert.Equal(new[] { 3, 7, 6, 2, 1, 5, 4, 0 }, serpentine);
    }

    [Fact]
    public void BuildBgraFrame_ShouldProduceExpectedPayloadSize_For256Leds()
    {
        var leds = new Rgb24[256];
        for (var i = 0; i < leds.Length; i++)
        {
            leds[i] = new Rgb24((byte)i, (byte)(255 - i), 42);
        }

        var map = MatrixFrameIndexMap.BuildLogicalToRasterMap(32, 8, "RowMajor");
        var bgra = GpuFrameUpload.BuildBgraFrame(leds, map, 32, 8);

        Assert.Equal(32 * 8 * 4, bgra.Length);
        Assert.Equal((byte)42, bgra[0]);
        Assert.Equal((byte)255, bgra[1]);
        Assert.Equal((byte)0, bgra[2]);
        Assert.Equal((byte)255, bgra[3]);
    }

    [Theory]
    [InlineData(32, 8)]
    [InlineData(128, 32)]
    [InlineData(128, 64)]
    public void BuildBgraFrame_ShouldHandleBoundaryMatrixSizes_WithoutOutOfRangeWrites(int width, int height)
    {
        var ledCount = checked(width * height);
        var leds = new Rgb24[ledCount];
        for (var i = 0; i < ledCount; i++)
        {
            leds[i] = new Rgb24((byte)(i % 256), (byte)((i * 3) % 256), (byte)((i * 7) % 256));
        }

        var map = MatrixFrameIndexMap.BuildLogicalToRasterMap(width, height, "TopDownAlternateRightLeft");
        // Conversational note: add a deliberate invalid index to prove out-of-range map values are skipped safely.
        if (map.Length > 0)
        {
            map[0] = ledCount + 10;
        }

        var bgra = GpuFrameUpload.BuildBgraFrame(leds, map, width, height);
        Assert.Equal(checked(width * height * 4), bgra.Length);
    }

    [Fact]
    public void BloomRoiMath_ShouldExpandAndClampBoundsWithoutClippingEdges()
    {
        var emissiveSurface = new GpuInstancedMatrixRenderer.IntRect(30, 6, 6, 4);
        var bloomBounds = GpuInstancedMatrixRenderer.BloomRoiMath.SurfaceToBloomBounds(emissiveSurface, scaleDivisor: 2, bloomWidth: 32, bloomHeight: 16);
        var expandedNear = GpuInstancedMatrixRenderer.BloomRoiMath.Expand(bloomBounds, padding: 3, maxWidth: 32, maxHeight: 16);
        var expandedFar = GpuInstancedMatrixRenderer.BloomRoiMath.Expand(bloomBounds, padding: 7, maxWidth: 32, maxHeight: 16);
        var union = GpuInstancedMatrixRenderer.BloomRoiMath.Union(expandedNear, expandedFar);
        var compositeSurface = GpuInstancedMatrixRenderer.BloomRoiMath.BloomToSurfaceBounds(union, scaleDivisor: 2, surfaceWidth: 64, surfaceHeight: 32);

        Assert.Equal(new GpuInstancedMatrixRenderer.IntRect(15, 3, 3, 2), bloomBounds);
        Assert.Equal(new GpuInstancedMatrixRenderer.IntRect(12, 0, 9, 8), expandedNear);
        Assert.Equal(new GpuInstancedMatrixRenderer.IntRect(8, 0, 13, 12), expandedFar);
        Assert.Equal(new GpuInstancedMatrixRenderer.IntRect(16, 0, 26, 24), compositeSurface);
    }

    [Fact]
    public void BloomRoiMath_ShouldReturnEmptyWhenInputBoundsAreEmpty()
    {
        var empty = GpuInstancedMatrixRenderer.IntRect.Empty;

        Assert.True(GpuInstancedMatrixRenderer.BloomRoiMath.SurfaceToBloomBounds(empty, 4, 64, 16).IsEmpty);
        Assert.True(GpuInstancedMatrixRenderer.BloomRoiMath.Expand(empty, 8, 64, 16).IsEmpty);
        Assert.True(GpuInstancedMatrixRenderer.BloomRoiMath.BloomToSurfaceBounds(empty, 4, 256, 64).IsEmpty);
    }
}
