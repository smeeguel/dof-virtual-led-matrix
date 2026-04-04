using System.Threading;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Rendering;

public sealed class CpuMatrixRenderer : IMatrixRenderer
{
    private readonly MatrixFrameRasterComposer _composer = new();
    private readonly object _frameGate = new();
    private readonly SemaphoreSlim _composeMutex = new(1, 1);
    private readonly SemaphoreSlim _composeSignal = new(0, 1);
    private readonly CancellationTokenSource _composeCts = new();
    private readonly Task _composeLoopTask;

    private Image? _bitmapHost;
    private MatrixConfig? _config;
    private WriteableBitmap? _bitmap;
    private FramePresentation _latestFrame = new(Array.Empty<byte>(), 0, 0, 0, DateTimeOffset.UnixEpoch);
    private int _composeRequested;
    private volatile bool _isDisposed;

    public string BackendName => "cpu";

    public bool UsesImageHost => true;

    public CpuMatrixRenderer()
    {
        // We keep a dedicated worker loop alive so expensive composition never blocks the UI thread.
        _composeLoopTask = Task.Run(ComposeLoopAsync);
    }

    public void Initialize(MatrixRendererSurface renderSurface, int width, int height, DotStyleConfig dotStyleConfig)
    {
        _composeMutex.Wait();
        try
        {
            _bitmap = null;
            _bitmapHost = renderSurface.BitmapHost ?? throw new ArgumentNullException(nameof(renderSurface.BitmapHost));
            _bitmapHost.Source = null;
            renderSurface.PrimitiveCanvas.Children.Clear();
            renderSurface.PrimitiveCanvas.Width = 0;
            renderSurface.PrimitiveCanvas.Height = 0;

            _config = BuildConfig(width, height, dotStyleConfig);
            _composer.Configure(_config);

            var composed = _composer.Compose(new FramePresentation(Array.Empty<byte>(), 0, 0, 0, DateTimeOffset.UnixEpoch));
            _bitmap = new WriteableBitmap(composed.Width, composed.Height, 96, 96, PixelFormats.Bgra32, null);
            _bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, composed.Width, composed.Height), composed.Pixels, composed.Stride, 0);
            _bitmapHost.Source = _bitmap;
            _bitmapHost.Stretch = Stretch.Fill;
        }
        finally
        {
            _composeMutex.Release();
        }
    }

    public void UpdateFrame(FramePresentation presentation)
    {
        lock (_frameGate)
        {
            _latestFrame = presentation;
        }
    }

    public void Resize(double viewportWidth, double viewportHeight)
    {
    }

    public void Render()
    {
        if (_isDisposed || _bitmapHost is null || _config is null)
        {
            return;
        }

        // We only allow one pending compose ticket so frame bursts collapse to latest-frame-only behavior.
        if (Interlocked.Exchange(ref _composeRequested, 1) == 0)
        {
            _composeSignal.Release();
        }
    }

    public void Clear()
    {
        if (_config is null)
        {
            return;
        }

        _composeMutex.Wait();
        try
        {
            _composer.Reset();
        }
        finally
        {
            _composeMutex.Release();
        }

        var ledCount = Math.Max(1, _config.Width * _config.Height);
        var clear = new FramePresentation(new byte[ledCount * 3], 0, ledCount, 0, DateTimeOffset.UtcNow);
        UpdateFrame(clear);
        Render();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _composeCts.Cancel();

        try
        {
            _composeLoopTask.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // Shutdown can race a pending wait; that is expected when the app is closing.
        }

        _composeSignal.Dispose();
        _composeMutex.Dispose();
        _composeCts.Dispose();
    }

    private async Task ComposeLoopAsync()
    {
        while (!_composeCts.IsCancellationRequested)
        {
            try
            {
                await _composeSignal.WaitAsync(_composeCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            Interlocked.Exchange(ref _composeRequested, 0);

            Image? bitmapHost;
            MatrixConfig? config;
            FramePresentation frame;
            lock (_frameGate)
            {
                frame = _latestFrame;
                bitmapHost = _bitmapHost;
                config = _config;
            }

            if (bitmapHost is null || config is null || _isDisposed)
            {
                continue;
            }

            (int Width, int Height, int Stride, byte[] Pixels, IReadOnlyList<DirtyRect> DirtyRects, bool UseFullFrameWrite) composed;
            await _composeMutex.WaitAsync(_composeCts.Token).ConfigureAwait(false);
            try
            {
                composed = _composer.Compose(frame);
            }
            finally
            {
                _composeMutex.Release();
            }

            try
            {
                await bitmapHost.Dispatcher.InvokeAsync(
                    () => ApplyComposedFrame(bitmapHost, composed),
                    DispatcherPriority.Render,
                    _composeCts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void ApplyComposedFrame(Image bitmapHost, (int Width, int Height, int Stride, byte[] Pixels, IReadOnlyList<DirtyRect> DirtyRects, bool UseFullFrameWrite) composed)
    {
        if (_isDisposed)
        {
            return;
        }

        var needsFullFrameWrite = composed.UseFullFrameWrite;
        if (_bitmap is null || _bitmap.PixelWidth != composed.Width || _bitmap.PixelHeight != composed.Height)
        {
            _bitmap = new WriteableBitmap(composed.Width, composed.Height, 96, 96, PixelFormats.Bgra32, null);
            bitmapHost.Source = _bitmap;
            // Fresh bitmaps have no valid backing pixels yet, so we always prime them with a full upload once.
            needsFullFrameWrite = true;
        }

        if (needsFullFrameWrite)
        {
            _bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, composed.Width, composed.Height), composed.Pixels, composed.Stride, 0);
            return;
        }

        if (composed.DirtyRects.Count == 0)
        {
            return;
        }

        // Dirty rows come from the composer and let us avoid uploading untouched portions of the backbuffer.
        foreach (var dirtyRect in composed.DirtyRects)
        {
            var rect = new System.Windows.Int32Rect(dirtyRect.X, dirtyRect.Y, dirtyRect.Width, dirtyRect.Height);
            var sourceOffset = (dirtyRect.Y * composed.Stride) + (dirtyRect.X * 4);
            _bitmap.WritePixels(rect, composed.Pixels, composed.Stride, sourceOffset);
        }
    }

    private static MatrixConfig BuildConfig(int width, int height, DotStyleConfig dotStyleConfig)
    {
        return new MatrixConfig
        {
            Width = width,
            Height = height,
            DotShape = dotStyleConfig.DotShape,
            DotSize = dotStyleConfig.DotSize,
            Mapping = dotStyleConfig.Mapping,
            MinDotSpacing = dotStyleConfig.DotSpacing,
            Brightness = dotStyleConfig.Brightness,
            Gamma = dotStyleConfig.Gamma,
            Visual = dotStyleConfig.Visual,
            ToneMapping = dotStyleConfig.ToneMapping,
            TemporalSmoothing = dotStyleConfig.TemporalSmoothing,
            Bloom = dotStyleConfig.Bloom,
        };
    }
}
