using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Rendering;

public sealed class WpfPrimitiveMatrixRenderer : IMatrixRenderer
{
    private readonly List<DotVisual> _dots = new();

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
                var dot = CreateDotVisual();
                var left = _config.DotSpacing + (x * dotStride);
                var top = _config.DotSpacing + (y * dotStride);

                Canvas.SetLeft(dot.Body, left);
                Canvas.SetTop(dot.Body, top);
                Canvas.SetLeft(dot.Core, left);
                Canvas.SetTop(dot.Core, top);

                _targetCanvas.Children.Add(dot.Body);
                _targetCanvas.Children.Add(dot.Core);
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
        var requestedLedCount = Math.Max(framePresentation.HighestLedWritten, framePresentation.LedsPerChannel);
        var ledCount = Math.Min(Math.Min(requestedLedCount, rgb.Length / 3), matrixCapacity);

        var touchedShapes = new bool[_dots.Count];

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

            var isOn = r > 0 || g > 0 || b > 0;
            var dot = _dots[shapeIndex];
            dot.Core.Opacity = isOn ? 1.0 : 0.0;

            if (isOn)
            {
                dot.Core.Fill = CreateCoreBrush(Color.FromRgb(r, g, b), _config.Visual.LensFalloff);
            }

            touchedShapes[shapeIndex] = true;
        }

        for (var shapeIndex = 0; shapeIndex < _dots.Count; shapeIndex++)
        {
            if (!touchedShapes[shapeIndex])
            {
                _dots[shapeIndex].Core.Opacity = 0.0;
            }
        }
    }

    private DotVisual CreateDotVisual()
    {
        if (_config is null)
        {
            throw new InvalidOperationException("Renderer config unavailable.");
        }

        Shape body = CreateShape(_config.DotShape);
        Shape core = CreateShape(_config.DotShape);

        body.Width = _config.DotSize;
        body.Height = _config.DotSize;
        body.Stretch = Stretch.Fill;
        body.SnapsToDevicePixels = true;
        body.Fill = CreateBodyBrush(_config.Visual);

        core.Width = _config.DotSize;
        core.Height = _config.DotSize;
        core.Stretch = Stretch.Fill;
        core.SnapsToDevicePixels = true;
        core.Opacity = 0.0;
        core.Fill = Brushes.Black;

        return new DotVisual(body, core);
    }

    private static Shape CreateShape(string dotShape)
    {
        return dotShape.Equals("square", StringComparison.OrdinalIgnoreCase)
            ? new Rectangle()
            : new Ellipse();
    }

    private static Brush CreateBodyBrush(MatrixVisualConfig visual)
    {
        var offColor = Color.FromArgb(ToByte(visual.OffStateAlpha), visual.OffStateTintR, visual.OffStateTintG, visual.OffStateTintB);
        var lensFalloff = Clamp01(visual.LensFalloff);
        var specular = Clamp01(visual.SpecularHotspot);
        var rim = Clamp01(visual.RimHighlight);

        var centerColor = Lerp(offColor, Colors.White, 0.20 * specular);
        var midColor = Lerp(offColor, Colors.Black, 0.20 + (0.35 * lensFalloff));
        var rimColor = Color.FromArgb(ToByte(0.05 + (0.30 * rim)), 255, 255, 255);

        return new RadialGradientBrush
        {
            GradientOrigin = new System.Windows.Point(0.38, 0.34),
            Center = new System.Windows.Point(0.5, 0.5),
            RadiusX = 0.6,
            RadiusY = 0.6,
            GradientStops = new GradientStopCollection
            {
                new(centerColor, 0.0),
                new(midColor, 0.58),
                new(rimColor, 0.94),
                new(Lerp(rimColor, Colors.Black, 0.6), 1.0),
            },
        };
    }

    private static Brush CreateCoreBrush(Color emissiveColor, double lensFalloff)
    {
        var falloff = Clamp01(lensFalloff);
        var coreCenter = Lerp(emissiveColor, Colors.White, 0.22);
        var edge = Lerp(emissiveColor, Colors.Black, 0.30 + (0.35 * falloff));

        return new RadialGradientBrush
        {
            GradientOrigin = new System.Windows.Point(0.48, 0.46),
            Center = new System.Windows.Point(0.5, 0.5),
            RadiusX = 0.52,
            RadiusY = 0.52,
            GradientStops = new GradientStopCollection
            {
                new(coreCenter, 0.0),
                new(emissiveColor, 0.40),
                new(edge, 1.0),
            },
        };
    }

    private static Color Lerp(Color a, Color b, double t)
    {
        var factor = Clamp01(t);
        return Color.FromArgb(
            (byte)(a.A + ((b.A - a.A) * factor)),
            (byte)(a.R + ((b.R - a.R) * factor)),
            (byte)(a.G + ((b.G - a.G) * factor)),
            (byte)(a.B + ((b.B - a.B) * factor)));
    }

    private static double Clamp01(double value) => Math.Clamp(value, 0.0, 1.0);

    private static byte ToByte(double value) => (byte)Math.Round(Math.Clamp(value, 0.0, 1.0) * 255.0);

    private static byte ApplyBrightnessAndGamma(byte channel, double brightness, double gamma)
    {
        var normalized = channel / 255.0;
        var adjusted = Math.Pow(Math.Clamp(normalized, 0.0, 1.0), gamma);
        var scaled = adjusted * Math.Clamp(brightness, 0.0, 4.0);
        var final = (int)Math.Round(Math.Clamp(scaled, 0.0, 1.0) * 255.0);
        return (byte)final;
    }

    private sealed record DotVisual(Shape Body, Shape Core);
}
