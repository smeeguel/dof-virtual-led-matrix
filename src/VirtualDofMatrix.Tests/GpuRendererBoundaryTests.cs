using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VirtualDofMatrix.App.Rendering;
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
    public void GpuFallbackPresent_ShouldWritePixelsWithoutFrameTextureStaging()
    {
        var frameTextureField = typeof(GpuInstancedMatrixRenderer).GetField("_frameTexture", BindingFlags.Instance | BindingFlags.NonPublic);
        var frameSrvField = typeof(GpuInstancedMatrixRenderer).GetField("_frameSrv", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Null(frameTextureField);
        Assert.Null(frameSrvField);

        RunInSta(() =>
        {
            var presenter = typeof(GpuInstancedMatrixRenderer).GetMethod("PresentFallbackBitmap", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(presenter);

            var width = 2;
            var height = 1;
            var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            // Conversational note: pixel order is BGRA to mirror the renderer's fallback path contract.
            var bgra = new byte[] { 1, 2, 3, 255, 4, 5, 6, 255 };

            presenter!.Invoke(null, new object[] { bitmap, bgra, width, height });

            var copy = new byte[bgra.Length];
            bitmap.CopyPixels(copy, width * 4, 0);
            Assert.Equal(bgra, copy);
        });
    }

    private static void RunInSta(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (captured is not null)
        {
            throw new TargetInvocationException(captured);
        }
    }
}
