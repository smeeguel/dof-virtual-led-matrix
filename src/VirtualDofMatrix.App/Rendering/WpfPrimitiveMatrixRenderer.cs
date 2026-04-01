using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Rendering;

public sealed class WpfPrimitiveMatrixRenderer : IMatrixRenderer
{
    private readonly List<Shape> _dots = new();
    private readonly object _gate = new();
    private Canvas? _canvas;
    private int _width;
    private int _height;
    private int _dotStride;
    private int[] _logicalToRaster = Array.Empty<int>();
    private FramePresentation _latestFrame = new(Array.Empty<byte>(), 0, 0, 0, DateTimeOffset.UnixEpoch);
    private DotStyleConfig _style = new(
        "circle",
        "TopDownAlternateRightLeft",
        1,
        2,
        1.0,
        1.0,
        new MatrixVisualConfig(),
        new ToneMappingConfig(),
        new TemporalSmoothingConfig(),
        new BloomConfig(),
        false,
        0.0);

    public string BackendName => "cpu-primitive";

    public bool UsesImageHost => false;

    public void Initialize(MatrixRendererSurface renderSurface, int width, int height, DotStyleConfig dotStyleConfig)
    {
        _canvas = renderSurface.PrimitiveCanvas;
        renderSurface.BitmapHost.Source = null;
        _width = width;
        _height = height;
        _style = dotStyleConfig;
        _logicalToRaster = MatrixFrameIndexMap.BuildLogicalToRasterMap(width, height, dotStyleConfig.Mapping);

        _canvas.Children.Clear();
        _dots.Clear();
        _dotStride = Math.Max(2, dotStyleConfig.DotSpacing + 3);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                Shape shape = dotStyleConfig.DotShape.Equals("square", StringComparison.OrdinalIgnoreCase)
                    ? new Rectangle()
                    : new Ellipse();
                shape.Width = _dotStride - 1;
                shape.Height = _dotStride - 1;
                shape.Fill = Brushes.Black;
                Canvas.SetLeft(shape, x * _dotStride);
                Canvas.SetTop(shape, y * _dotStride);
                _canvas.Children.Add(shape);
                _dots.Add(shape);
            }
        }

        _canvas.Width = width * _dotStride;
        _canvas.Height = height * _dotStride;
    }

    public void UpdateFrame(FramePresentation presentation)
    {
        lock (_gate)
        {
            _latestFrame = presentation;
        }
    }

    public void Resize(double viewportWidth, double viewportHeight)
    {
    }

    public void Render()
    {
        if (_canvas is null)
        {
            return;
        }

        FramePresentation frame;
        lock (_gate)
        {
            frame = _latestFrame;
        }

        var rgb = frame.RgbMemory.Span;
        var count = Math.Min(rgb.Length / 3, _logicalToRaster.Length);
        for (var logical = 0; logical < count; logical++)
        {
            var rasterIndex = _logicalToRaster[logical];
            if ((uint)rasterIndex >= (uint)_dots.Count)
            {
                continue;
            }

            var offset = logical * 3;
            _dots[rasterIndex].Fill = new SolidColorBrush(Color.FromRgb(rgb[offset], rgb[offset + 1], rgb[offset + 2]));
        }
    }

    public void Dispose()
    {
    }
}
