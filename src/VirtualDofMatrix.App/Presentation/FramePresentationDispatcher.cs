using System.Windows.Threading;
using VirtualDofMatrix.App.Serial;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Presentation;

public sealed class FramePresentationDispatcher : IDisposable
{
    private readonly object _pendingSync = new();
    private readonly Dispatcher _dispatcher;
    private SerialEmulatorHost? _host;
    private FramePresentation? _latestPendingFrame;
    private int _uiCallbackQueued;

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
        lock (_pendingSync)
        {
            _latestPendingFrame = frame;
        }

        if (Interlocked.CompareExchange(ref _uiCallbackQueued, 1, 0) != 0)
        {
            return;
        }

        _dispatcher.BeginInvoke(ProcessPendingFramesOnUiThread);
    }

    private void ProcessPendingFramesOnUiThread()
    {
        while (true)
        {
            FramePresentation? frame;
            lock (_pendingSync)
            {
                frame = _latestPendingFrame;
                _latestPendingFrame = null;
            }

            if (frame is null)
            {
                break;
            }

            FramePresentedOnUiThread?.Invoke(this, frame);
        }

        Interlocked.Exchange(ref _uiCallbackQueued, 0);

        lock (_pendingSync)
        {
            if (_latestPendingFrame is null)
            {
                return;
            }
        }

        if (Interlocked.CompareExchange(ref _uiCallbackQueued, 1, 0) == 0)
        {
            _dispatcher.BeginInvoke(ProcessPendingFramesOnUiThread);
        }
    }

    public void Dispose()
    {
        if (_host is not null)
        {
            _host.FramePresented -= OnFramePresentedFromHost;
            _host = null;
        }
    }
}
