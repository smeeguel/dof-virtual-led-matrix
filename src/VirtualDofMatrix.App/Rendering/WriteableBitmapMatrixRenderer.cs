using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Rendering;

public sealed class WriteableBitmapMatrixRenderer : IMatrixRenderer
{
    private readonly MatrixFrameRasterComposer _composer = new();
    private Image? _bitmapHost;
    private WriteableBitmap? _bitmap;

    public bool UsesImageHost => true;

    public void Initialize(Canvas primitiveCanvas, Image bitmapHost, MatrixConfig config)
    {
        _bitmapHost = bitmapHost ?? throw new ArgumentNullException(nameof(bitmapHost));
        primitiveCanvas.Children.Clear();
        primitiveCanvas.Width = 0;
        primitiveCanvas.Height = 0;
        _composer.Configure(config);

        var composed = _composer.Compose(new FramePresentation(Array.Empty<byte>(), 0, 0, 0, DateTimeOffset.UnixEpoch));
        _bitmap = new WriteableBitmap(composed.Width, composed.Height, 96, 96, PixelFormats.Bgra32, null);
        _bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, composed.Width, composed.Height), composed.Pixels, composed.Stride, 0);

        _bitmapHost.Source = _bitmap;
        _bitmapHost.Width = composed.Width;
        _bitmapHost.Height = composed.Height;
    }

    public void Render(FramePresentation framePresentation)
    {
        if (_bitmap is null || _bitmapHost is null)
        {
            throw new InvalidOperationException("Renderer must be initialized before rendering.");
        }

        var composed = _composer.Compose(framePresentation);
        if (_bitmap.PixelWidth != composed.Width || _bitmap.PixelHeight != composed.Height)
        {
            _bitmap = new WriteableBitmap(composed.Width, composed.Height, 96, 96, PixelFormats.Bgra32, null);
            _bitmapHost.Source = _bitmap;
            _bitmapHost.Width = composed.Width;
            _bitmapHost.Height = composed.Height;
        }

        _bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, composed.Width, composed.Height), composed.Pixels, composed.Stride, 0);
    }
}
