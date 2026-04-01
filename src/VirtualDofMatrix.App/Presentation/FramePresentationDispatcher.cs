using System.Windows.Threading;
using VirtualDofMatrix.App.Serial;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Presentation;

public sealed class FramePresentationDispatcher : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly object _gate = new();

    private SerialEmulatorHost? _host;
    private FramePresentation? _latestFrame;
    private bool _uiDispatchPending;

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
        var shouldSchedule = false;
        lock (_gate)
        {
            _latestFrame = frame;
            if (!_uiDispatchPending)
            {
                _uiDispatchPending = true;
                shouldSchedule = true;
            }
        }

        if (shouldSchedule)
        {
            _dispatcher.BeginInvoke(ProcessPendingFrameOnUiThread, DispatcherPriority.Send);
        }
    }

    private void ProcessPendingFrameOnUiThread()
    {
        while (true)
        {
            FramePresentation? frame;
            lock (_gate)
            {
                frame = _latestFrame;
                _latestFrame = null;
                if (frame is null)
                {
                    _uiDispatchPending = false;
                    return;
                }
            }

            FramePresentedOnUiThread?.Invoke(this, frame);
        }
    }

    public void Dispose()
    {
        if (_host is not null)
        {
            _host.FramePresented -= OnFramePresentedFromHost;
            _host = null;
        }

        lock (_gate)
        {
            _latestFrame = null;
            _uiDispatchPending = false;
        }
    }
}
