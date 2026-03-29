using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Rendering;

public sealed class WpfPrimitiveMatrixRenderer : IMatrixRenderer
{
    private readonly List<Shape> _dots = new();

    private Canvas? _targetCanvas;
    private MatrixConfig? _config;

    public void Initialize(Canvas targetCanvas, MatrixConfig config)
    {
        _targetCanvas = targetCanvas ?? throw new ArgumentNullException(nameof(targetCanvas));
        _config = config ?? throw new ArgumentNullException(nameof(config));

        _dots.Clear();
        _targetCanvas.Children.Clear();
        _targetCanvas.Background = Brushes.Black;

        var dotStride = _config.DotSize + _config.DotSpacing;
        var width = _config.Width;
        var height = _config.Height;

        _targetCanvas.Width = (width * dotStride) + _config.DotSpacing;
        _targetCanvas.Height = (height * dotStride) + _config.DotSpacing;
        _targetCanvas.UseLayoutRounding = true;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var dot = CreateDot();
                Canvas.SetLeft(dot, _config.DotSpacing + (x * dotStride));
                Canvas.SetTop(dot, _config.DotSpacing + (y * dotStride));
                _targetCanvas.Children.Add(dot);
                _dots.Add(dot);
            }
        }
    }

    public void Render(FramePresentation framePresentation)
    {
        if (_targetCanvas is null || _config is null)
        {
            throw new InvalidOperationException("Renderer must be initialized before rendering.");
        }

        var rgb = framePresentation.RgbMemory.Span;
        var matrixCapacity = _config.Width * _config.Height;
        var ledCount = Math.Min(Math.Min(framePresentation.HighestLedWritten, rgb.Length / 3), matrixCapacity);

        for (var logicalIndex = 0; logicalIndex < ledCount; logicalIndex++)
        {
            var rgbOffset = logicalIndex * 3;
            var mapped = MatrixMapper.MapLinearIndex(logicalIndex, _config.Width, _config.Height, _config.Mapping);
            var shapeIndex = mapped.Y * _config.Width + mapped.X;

            if (shapeIndex < 0 || shapeIndex >= _dots.Count)
            {
                continue;
            }

            var r = ApplyBrightnessAndGamma(rgb[rgbOffset], _config.Brightness, _config.Gamma);
            var g = ApplyBrightnessAndGamma(rgb[rgbOffset + 1], _config.Brightness, _config.Gamma);
            var b = ApplyBrightnessAndGamma(rgb[rgbOffset + 2], _config.Brightness, _config.Gamma);

            _dots[shapeIndex].Fill = new SolidColorBrush(Color.FromRgb(r, g, b));
        }

        for (var logicalIndex = ledCount; logicalIndex < _dots.Count; logicalIndex++)
        {
            _dots[logicalIndex].Fill = Brushes.Black;
        }
    }

    private Shape CreateDot()
    {
        if (_config is null)
        {
            throw new InvalidOperationException("Renderer config unavailable.");
        }

        Shape dot = _config.DotShape.Equals("square", StringComparison.OrdinalIgnoreCase)
            ? new Rectangle()
            : new Ellipse();

        dot.Width = _config.DotSize;
        dot.Height = _config.DotSize;
        dot.Stretch = Stretch.Fill;
        dot.Fill = Brushes.Black;
        dot.SnapsToDevicePixels = true;

        return dot;
    }

    private static byte ApplyBrightnessAndGamma(byte channel, double brightness, double gamma)
    {
        var normalized = channel / 255.0;
        var adjusted = Math.Pow(Math.Clamp(normalized, 0.0, 1.0), gamma);
        var scaled = adjusted * Math.Clamp(brightness, 0.0, 4.0);
        var final = (int)Math.Round(Math.Clamp(scaled, 0.0, 1.0) * 255.0);
        return (byte)final;
    }
}
