using System.Threading;
using System.Windows.Threading;
using VirtualDofMatrix.App.Transport;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Presentation;

public sealed class FramePresentationDispatcher : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly object _gate = new();
    private FrameTransportHost? _host;
    private FramePresentation _latestFrame = new(Array.Empty<byte>(), 0, 0, 0, DateTimeOffset.UnixEpoch);
    private bool _hasFrame;
    private int _uiDispatchScheduled;

    public FramePresentationDispatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public event EventHandler<FramePresentation>? FramePresentedOnUiThread;

    public void Attach(FrameTransportHost host)
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
        lock (_gate)
        {
            _latestFrame = frame;
            _hasFrame = true;
        }

        if (Interlocked.Exchange(ref _uiDispatchScheduled, 1) == 0)
        {
            _dispatcher.BeginInvoke(DispatchLatestFrameToUi);
        }
    }

    private void DispatchLatestFrameToUi()
    {
        FramePresentation frame;
        lock (_gate)
        {
            if (!_hasFrame)
            {
                Interlocked.Exchange(ref _uiDispatchScheduled, 0);
                return;
            }

            frame = _latestFrame;
            _hasFrame = false;
        }

        FramePresentedOnUiThread?.Invoke(this, frame);

        Interlocked.Exchange(ref _uiDispatchScheduled, 0);
        var shouldReschedule = false;
        lock (_gate)
        {
            shouldReschedule = _hasFrame;
        }

        if (shouldReschedule && Interlocked.Exchange(ref _uiDispatchScheduled, 1) == 0)
        {
            _dispatcher.BeginInvoke(DispatchLatestFrameToUi);
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
