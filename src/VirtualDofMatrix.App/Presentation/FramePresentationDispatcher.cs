using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Threading;
using VirtualDofMatrix.App.Serial;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Presentation;

public sealed class FramePresentationDispatcher : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly long _minFrameIntervalTicks;
    private readonly bool _logFrameStats;
    private readonly bool _latestOnlyPolicy;
    private SerialEmulatorHost? _host;
    private FramePresentation? _latestFrame;
    private long _producedFrames;
    private long _consumedFrames;
    private long _renderedFrames;
    private long _droppedFrames;
    private long _lastRenderTick;
    private long _lastStatsTick;
    private bool _isRenderingHooked;

    public FramePresentationDispatcher(Dispatcher dispatcher, AppConfig config)
    {
        _dispatcher = dispatcher;
        var maxRenderFps = Math.Clamp(config.Settings.MaxRenderFps, 1, 240);
        _minFrameIntervalTicks = Stopwatch.Frequency / maxRenderFps;
        _logFrameStats = config.Debug.LogFrames;
        _latestOnlyPolicy = string.Equals(config.Settings.FrameDropPolicy, "latestOnly", StringComparison.OrdinalIgnoreCase);
        _lastStatsTick = _clock.ElapsedTicks;
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
        HookRenderLoopIfNeeded();
    }

    private void OnFramePresentedFromHost(object? sender, FramePresentation frame)
    {
        Interlocked.Increment(ref _producedFrames);
        if (_latestOnlyPolicy)
        {
            var dropped = Interlocked.Exchange(ref _latestFrame, frame);
            if (dropped is not null)
            {
                Interlocked.Increment(ref _droppedFrames);
                dropped.Dispose();
            }
        }
        else
        {
            // Current scheduler only supports latest-only semantics.
            // Unknown policy values fall back to latest-only for safety.
            var dropped = Interlocked.Exchange(ref _latestFrame, frame);
            if (dropped is not null)
            {
                Interlocked.Increment(ref _droppedFrames);
                dropped.Dispose();
            }
        }
    }

    private void HookRenderLoopIfNeeded()
    {
        if (_isRenderingHooked)
        {
            return;
        }

        CompositionTarget.Rendering += OnRendering;
        _isRenderingHooked = true;
    }

    private void UnhookRenderLoopIfNeeded()
    {
        if (!_isRenderingHooked)
        {
            return;
        }

        CompositionTarget.Rendering -= OnRendering;
        _isRenderingHooked = false;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => OnRendering(sender, e)));
            return;
        }

        var nowTicks = _clock.ElapsedTicks;
        if (_minFrameIntervalTicks > 0 && (nowTicks - _lastRenderTick) < _minFrameIntervalTicks)
        {
            LogStatsIfNeeded(nowTicks);
            return;
        }

        var frame = Interlocked.Exchange(ref _latestFrame, null);
        if (frame is not null)
        {
            Interlocked.Increment(ref _consumedFrames);
            _lastRenderTick = nowTicks;
            var handler = FramePresentedOnUiThread;
            if (handler is not null)
            {
                handler(this, frame);
                Interlocked.Increment(ref _renderedFrames);
            }
            else
            {
                frame.Dispose();
            }
        }

        LogStatsIfNeeded(nowTicks);
    }

    private void LogStatsIfNeeded(long nowTicks)
    {
        if (!_logFrameStats)
        {
            return;
        }

        var elapsedTicks = nowTicks - _lastStatsTick;
        if (elapsedTicks < Stopwatch.Frequency)
        {
            return;
        }

        var produced = Interlocked.Exchange(ref _producedFrames, 0);
        var consumed = Interlocked.Exchange(ref _consumedFrames, 0);
        var rendered = Interlocked.Exchange(ref _renderedFrames, 0);
        var dropped = Interlocked.Exchange(ref _droppedFrames, 0);
        var elapsedSeconds = elapsedTicks / (double)Stopwatch.Frequency;
        _lastStatsTick = nowTicks;

        Console.WriteLine(
            $"[{DateTimeOffset.UtcNow:O}] Frame scheduler stats: producer={produced / elapsedSeconds:0.0} fps, consumed={consumed / elapsedSeconds:0.0} fps, rendered={rendered / elapsedSeconds:0.0} fps, dropped={dropped / elapsedSeconds:0.0} fps.");
    }

    public void Dispose()
    {
        UnhookRenderLoopIfNeeded();

        if (_host is not null)
        {
            _host.FramePresented -= OnFramePresentedFromHost;
            _host = null;
        }

        var pending = Interlocked.Exchange(ref _latestFrame, null);
        pending?.Dispose();
    }
}
