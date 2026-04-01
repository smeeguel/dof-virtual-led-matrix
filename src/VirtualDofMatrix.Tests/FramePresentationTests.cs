using VirtualDofMatrix.Core;
using Xunit;

namespace VirtualDofMatrix.Tests;

public sealed class FramePresentationTests
{
    [Fact]
    public void PresentationSnapshot_ShouldRemainStableAfterSubsequentWrites()
    {
        var frameBuffer = new FrameBuffer();
        frameBuffer.SetLedsPerChannel(2);
        frameBuffer.ApplySegment(0, new byte[] { 1, 2, 3, 4, 5, 6 }, ledCount: 2);

        using var snapshot = frameBuffer.MarkOutputAndCreatePresentation();

        frameBuffer.ApplySegment(0, new byte[] { 9, 9, 9, 9, 9, 9 }, ledCount: 2);

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, snapshot.RgbBytes.Slice(0, 6).ToArray());
    }

    [Fact]
    public void PooledPresentation_DisposeShouldReleaseBufferAndClearMemoryView()
    {
        var frameBuffer = new FrameBuffer();
        frameBuffer.SetLedsPerChannel(2);
        frameBuffer.ApplySegment(0, new byte[] { 1, 2, 3, 4, 5, 6 }, ledCount: 2);

        var snapshot = frameBuffer.MarkOutputAndCreatePresentation();
        Assert.Equal(6, snapshot.RgbBytes.Length);

        snapshot.Dispose();

        Assert.Equal(0, snapshot.RgbBytes.Length);
    }
}
