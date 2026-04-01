using VirtualDofMatrix.App.Rendering.Vulkan;
using VirtualDofMatrix.Core;
using Xunit;

namespace VirtualDofMatrix.Tests;

public sealed class VulkanFrameUploadPipelineTests
{
    [Fact]
    public void Prepare_ShouldMapLinearFrameToPackedInstances()
    {
        var pipeline = new VulkanFrameUploadPipeline();
        pipeline.Configure(new MatrixConfig
        {
            Width = 2,
            Height = 2,
            Mapping = "RowMajor",
        });

        var frame = new FramePresentation(
            RgbBytes:
            [
                10, 20, 30,
                40, 50, 60,
                70, 80, 90,
                100, 110, 120,
            ],
            HighestLedWritten: 4,
            LedsPerChannel: 4,
            OutputSequence: 1,
            PresentedAtUtc: DateTimeOffset.UtcNow);

        var prepared = pipeline.Prepare(frame);
        var instances = prepared.StagingInstances.Span;

        Assert.Equal(4, instances.Length);
        Assert.Equal(0, prepared.FrameSlot);

        Assert.Equal((ushort)0, instances[0].X);
        Assert.Equal((ushort)0, instances[0].Y);
        Assert.Equal((byte)30, instances[0].Intensity);

        Assert.Equal((ushort)1, instances[1].X);
        Assert.Equal((ushort)0, instances[1].Y);
        Assert.Equal((byte)60, instances[1].Intensity);

        Assert.Equal((ushort)0, instances[2].X);
        Assert.Equal((ushort)1, instances[2].Y);
        Assert.Equal((byte)90, instances[2].Intensity);

        Assert.Equal((ushort)1, instances[3].X);
        Assert.Equal((ushort)1, instances[3].Y);
        Assert.Equal((byte)120, instances[3].Intensity);
    }

    [Fact]
    public void Prepare_ShouldAlternateUploadSlots_ForRingBuffering()
    {
        var pipeline = new VulkanFrameUploadPipeline();
        pipeline.Configure(new MatrixConfig
        {
            Width = 1,
            Height = 1,
            Mapping = "RowMajor",
        });

        var frame = new FramePresentation([1, 2, 3], 1, 1, 1, DateTimeOffset.UtcNow);
        var first = pipeline.Prepare(frame);
        var second = pipeline.Prepare(frame);
        var third = pipeline.Prepare(frame);

        Assert.Equal(0, first.FrameSlot);
        Assert.Equal(1, second.FrameSlot);
        Assert.Equal(0, third.FrameSlot);
    }
}
