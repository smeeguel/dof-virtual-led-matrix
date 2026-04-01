using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Rendering;

public sealed class CpuMatrixRenderer : IMatrixRenderer
{
    private readonly MatrixFrameRasterComposer _composer = new();
    private readonly object _gate = new();
    private Image? _bitmapHost;
    private MatrixConfig? _config;
    private WriteableBitmap? _bitmap;
    private Rgb24[] _latestFrame = Array.Empty<Rgb24>();

    public string BackendName => "cpu";

    public bool UsesImageHost => true;

    public void Initialize(MatrixRendererSurface renderSurface, int width, int height, DotStyleConfig dotStyleConfig)
    {
        _bitmapHost = renderSurface.BitmapHost ?? throw new ArgumentNullException(nameof(renderSurface.BitmapHost));
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

    public void UpdateFrame(ReadOnlySpan<Rgb24> logicalFrame)
    {
        lock (_gate)
        {
            _latestFrame = logicalFrame.ToArray();
        }
    }

    public void Resize(double viewportWidth, double viewportHeight)
    {
    }

    public void Render()
    {
        if (_bitmap is null || _bitmapHost is null || _config is null)
        {
            return;
        }

        Rgb24[] frame;
        lock (_gate)
        {
            frame = _latestFrame;
        }

        var payload = new byte[frame.Length * 3];
        for (var i = 0; i < frame.Length; i++)
        {
            payload[i * 3] = frame[i].R;
            payload[(i * 3) + 1] = frame[i].G;
            payload[(i * 3) + 2] = frame[i].B;
        }

        var composed = _composer.Compose(new FramePresentation(payload, frame.Length, frame.Length, 0, DateTimeOffset.UtcNow));
        if (_bitmap.PixelWidth != composed.Width || _bitmap.PixelHeight != composed.Height)
        {
            _bitmap = new WriteableBitmap(composed.Width, composed.Height, 96, 96, PixelFormats.Bgra32, null);
            _bitmapHost.Source = _bitmap;
        }

        _bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, composed.Width, composed.Height), composed.Pixels, composed.Stride, 0);
    }

    public void Dispose()
    {
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
