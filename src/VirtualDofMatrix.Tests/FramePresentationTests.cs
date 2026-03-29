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

        var snapshot = frameBuffer.MarkOutputAndCreatePresentation();

        frameBuffer.ApplySegment(0, new byte[] { 9, 9, 9, 9, 9, 9 }, ledCount: 2);

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, snapshot.RgbBytes.Take(6).ToArray());
    }

    [Fact]
    public void Presentation_ShouldCaptureLowestAndHighestWrittenLeds()
    {
        var frameBuffer = new FrameBuffer();
        frameBuffer.SetLedsPerChannel(1024);
        frameBuffer.ApplySegment(512, new byte[] { 10, 20, 30, 40, 50, 60 }, ledCount: 2);

        var snapshot = frameBuffer.MarkOutputAndCreatePresentation();

        Assert.Equal(512, snapshot.LowestLedWritten);
        Assert.Equal(514, snapshot.HighestLedWritten);
    }
}
