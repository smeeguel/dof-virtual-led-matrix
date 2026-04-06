using System.Threading;
using System.Windows.Threading;
using VirtualDofMatrix.Core;
using VirtualDofMatrix.Core.Toys;

namespace VirtualDofMatrix.App.Presentation;

// Overview: viewer adapter coalesces toy frames and pushes the newest one to the main window on the UI thread.
public sealed class ViewerOutputAdapter : IOutputAdapter
{
    private readonly Dispatcher _dispatcher;
    private readonly MainWindow _window;
    private readonly object _gate = new();
    private ToyFrame? _latestFrame;
    private int _dispatchScheduled;

    public ViewerOutputAdapter(Dispatcher dispatcher, MainWindow window)
    {
        _dispatcher = dispatcher;
        _window = window;
    }

    public string Name => "viewer";

    public void Write(ToyFrame frame, RoutingFrameContext context)
    {
        lock (_gate)
        {
            _latestFrame = frame;
        }

        if (Interlocked.Exchange(ref _dispatchScheduled, 1) == 0)
        {
            _dispatcher.BeginInvoke(DispatchLatest);
        }
    }

    private void DispatchLatest()
    {
        ToyFrame? frame;
        lock (_gate)
        {
            frame = _latestFrame;
            _latestFrame = null;
        }

        if (frame is not null)
        {
            var bytes = frame.PayloadBytes ?? FrameBytesFromPixels(frame.Pixels ?? Array.Empty<Rgb24>());
            var presentation = new FramePresentation(
                bytes,
                HighestLedWritten: bytes.Length / 3,
                LedsPerChannel: Math.Max(0, frame.Width * frame.Height),
                OutputSequence: frame.FrameId,
                PresentedAtUtc: frame.TimestampUtc);
            _window.ApplyPresentation(presentation);
        }

        Interlocked.Exchange(ref _dispatchScheduled, 0);

        var needsReschedule = false;
        lock (_gate)
        {
            needsReschedule = _latestFrame is not null;
        }

        if (needsReschedule && Interlocked.Exchange(ref _dispatchScheduled, 1) == 0)
        {
            _dispatcher.BeginInvoke(DispatchLatest);
        }
    }

    private static byte[] FrameBytesFromPixels(IReadOnlyList<Rgb24> pixels)
    {
        var bytes = new byte[pixels.Count * 3];
        for (var i = 0; i < pixels.Count; i++)
        {
            var offset = i * 3;
            bytes[offset] = pixels[i].R;
            bytes[offset + 1] = pixels[i].G;
            bytes[offset + 2] = pixels[i].B;
        }

        return bytes;
    }
}
