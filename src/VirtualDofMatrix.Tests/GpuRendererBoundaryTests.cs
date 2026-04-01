using VirtualDofMatrix.Core;
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
}
