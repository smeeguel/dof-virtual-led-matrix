using VirtualDofMatrix.Core;
using VirtualDofMatrix.App.Presentation;
using System.Reflection;
using System.Windows.Threading;
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
    public void DispatcherBurst_ShouldCoalesceCallbacksAndPresentNewestFrame()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        using var presentationDispatcher = new FramePresentationDispatcher(dispatcher);
        var callbackCount = 0;
        FramePresentation? lastFrame = null;
        presentationDispatcher.FramePresentedOnUiThread += (_, frame) =>
        {
            callbackCount++;
            lastFrame = frame;
        };

        for (ulong i = 1; i <= 100; i++)
        {
            InvokePrivateFramePresented(presentationDispatcher, CreateFrame(i));
        }

        PumpDispatcherUntil(
            () => callbackCount >= 1,
            TimeSpan.FromSeconds(1));

        Assert.Equal(1, callbackCount);
        Assert.NotNull(lastFrame);
        Assert.Equal((ulong)100, lastFrame!.OutputSequence);
    }

    private static FramePresentation CreateFrame(ulong outputSequence)
    {
        return new FramePresentation(
            RgbBytes: new byte[] { 1, 2, 3 },
            HighestLedWritten: 1,
            LedsPerChannel: 1,
            OutputSequence: outputSequence,
            PresentedAtUtc: DateTimeOffset.UtcNow);
    }

    private static void InvokePrivateFramePresented(FramePresentationDispatcher dispatcher, FramePresentation frame)
    {
        var method = typeof(FramePresentationDispatcher).GetMethod(
            "OnFramePresentedFromHost",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);
        method!.Invoke(dispatcher, new object?[] { null, frame });
    }

    private static void PumpDispatcherUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Timed out waiting for dispatcher condition.");
            }

            var frame = new DispatcherFrame();
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }
}
