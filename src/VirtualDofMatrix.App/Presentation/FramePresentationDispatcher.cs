using System.Windows.Threading;
using VirtualDofMatrix.App.Serial;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Presentation;

public sealed class FramePresentationDispatcher : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private SerialEmulatorHost? _host;
    private FramePresentation? _latestFrame;
    private int _dispatchQueued;

    public FramePresentationDispatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public event EventHandler<FramePresentation>? FramePresentedOnUiThread;

    public void Attach(SerialEmulatorHost host)
    {
        if (_host is not null)
        {
            _host.FramePresented -= OnFramePresentedFromHost;
        }

        _host = host;
        _host.FramePresented += OnFramePresentedFromHost;
    }

    private void OnFramePresentedFromHost(object? sender, FramePresentation frame)
    {
        Interlocked.Exchange(ref _latestFrame, frame);

        if (Interlocked.CompareExchange(ref _dispatchQueued, 1, 0) == 0)
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(DispatchLatestFrame));
        }
    }

    private void DispatchLatestFrame()
    {
        var frame = Interlocked.Exchange(ref _latestFrame, null);
        if (frame is not null)
        {
            FramePresentedOnUiThread?.Invoke(this, frame);
        }

        Interlocked.Exchange(ref _dispatchQueued, 0);

        if (Volatile.Read(ref _latestFrame) is not null &&
            Interlocked.CompareExchange(ref _dispatchQueued, 1, 0) == 0)
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(DispatchLatestFrame));
        }
    }

    public void Dispose()
    {
        if (_host is not null)
        {
            _host.FramePresented -= OnFramePresentedFromHost;
            _host = null;
        }

        Interlocked.Exchange(ref _latestFrame, null);
        Interlocked.Exchange(ref _dispatchQueued, 0);
    }
}
