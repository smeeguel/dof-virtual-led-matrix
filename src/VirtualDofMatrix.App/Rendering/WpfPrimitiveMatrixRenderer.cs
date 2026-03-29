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

            UpdateDotVisual(_dots[shapeIndex], Color.FromRgb(r, g, b));
        }

        for (var logicalIndex = ledCount; logicalIndex < _dots.Count; logicalIndex++)
        {
            UpdateDotVisual(_dots[logicalIndex], Colors.Black);
        }
    }

    private DotVisual CreateDotVisual()
    {
        if (_config is null)
        {
            throw new InvalidOperationException("Renderer config unavailable.");
        }

        Shape body = _config.DotShape.Equals("square", StringComparison.OrdinalIgnoreCase)
            ? new Rectangle()
            : new Ellipse();

        Shape core = _config.DotShape.Equals("square", StringComparison.OrdinalIgnoreCase)
            ? new Rectangle()
            : new Ellipse();

        body.Width = _config.DotSize;
        body.Height = _config.DotSize;
        body.Stretch = Stretch.Fill;
        body.SnapsToDevicePixels = true;

        core.Width = _config.DotSize;
        core.Height = _config.DotSize;
        core.Stretch = Stretch.Fill;
        core.SnapsToDevicePixels = true;

        var dot = new DotVisual(body, core);
        UpdateDotVisual(dot, Colors.Black);

        return dot;
    }

    private void UpdateDotVisual(DotVisual dot, Color emissiveColor)
    {
        if (_config is null)
        {
            return;
        }

        var visual = _config.Visual;
        var isOn = emissiveColor.R > 0 || emissiveColor.G > 0 || emissiveColor.B > 0;

        var offColor = Color.FromArgb(ToByte(visual.OffStateAlpha), visual.OffStateTintR, visual.OffStateTintG, visual.OffStateTintB);
        var rimOpacity = Clamp01(visual.RimHighlight);
        var rimColor = Color.FromArgb(ToByte(0.12 + (rimOpacity * 0.28)), 255, 255, 255);

        dot.Body.Fill = CreateBodyBrush(offColor, rimColor, visual.LensFalloff, visual.SpecularHotspot);

        dot.Core.Fill = CreateCoreBrush(emissiveColor, visual.LensFalloff);
        dot.Core.Opacity = isOn ? 1.0 : 0.0;
    }

    private static Brush CreateBodyBrush(Color offColor, Color rimColor, double lensFalloff, double specularHotspot)
    {
        var adjustedFalloff = Clamp01(lensFalloff);
        var hotspot = Clamp01(specularHotspot);

        var centerMix = 0.35 + (0.35 * (1.0 - adjustedFalloff));
        var centerColor = Lerp(offColor, Colors.White, centerMix * hotspot * 0.8);
        var midColor = Lerp(offColor, Colors.Black, adjustedFalloff * 0.3);

        return new RadialGradientBrush
        {
            GradientOrigin = new System.Windows.Point(0.38, 0.34),
            Center = new System.Windows.Point(0.5, 0.5),
            RadiusX = 0.6,
            RadiusY = 0.6,
            GradientStops = new GradientStopCollection
            {
                new(centerColor, 0.0),
                new(midColor, 0.55),
                new(rimColor, 0.95),
                new(Lerp(rimColor, Colors.Black, 0.6), 1.0),
            },
        };
    }

    private static Brush CreateCoreBrush(Color emissiveColor, double lensFalloff)
    {
        var adjustedFalloff = Clamp01(lensFalloff);
        var softCore = Lerp(emissiveColor, Colors.White, 0.22);
        var edge = Lerp(emissiveColor, Colors.Black, 0.45 + (adjustedFalloff * 0.2));

        return new RadialGradientBrush
        {
            GradientOrigin = new System.Windows.Point(0.48, 0.46),
            Center = new System.Windows.Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
            GradientStops = new GradientStopCollection
            {
                new(softCore, 0.0),
                new(emissiveColor, 0.35),
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
