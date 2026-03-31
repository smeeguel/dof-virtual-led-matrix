using System.Windows.Controls;
using System.Windows.Media.Imaging;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Rendering;

/// <summary>
/// Direct3D-style renderer that keeps per-dot instance buffers (position/color/intensity)
/// and shades each dot through a bulb model (body/core/spec) in a shader-like fragment pass.
///
/// Note: this implementation intentionally stays managed/WPF-only so the app remains portable
/// in developer environments where native D3D interop is unavailable.
/// </summary>
public sealed class Direct3DMatrixRenderer : IMatrixRenderer
{
    private const int HardMinimumDotSpacing = 2;

    private readonly byte[] _colorLut = new byte[256];
    private DotInstance[] _instances = Array.Empty<DotInstance>();
    private byte[] _pixels = Array.Empty<byte>();

    private MatrixConfig? _config;
    private Image? _bitmapHost;
    private WriteableBitmap? _bitmap;
    private int _surfaceWidth;
    private int _surfaceHeight;
    private int _stride;

    private double _lutBrightness = double.NaN;
    private double _lutGamma = double.NaN;

    public bool UsesImageHost => true;

    public void Initialize(Canvas primitiveCanvas, Image bitmapHost, MatrixConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _bitmapHost = bitmapHost ?? throw new ArgumentNullException(nameof(bitmapHost));

        primitiveCanvas.Children.Clear();
        primitiveCanvas.Width = 0;
        primitiveCanvas.Height = 0;

        var dotSpacing = Math.Max(HardMinimumDotSpacing, _config.MinDotSpacing);
        var dotStride = _config.DotSize + dotSpacing;

        _surfaceWidth = (_config.Width * dotStride) + dotSpacing;
        _surfaceHeight = (_config.Height * dotStride) + dotSpacing;
        _stride = _surfaceWidth * 4;

        _pixels = new byte[_surfaceWidth * _surfaceHeight * 4];
        _instances = new DotInstance[_config.Width * _config.Height];

        var dotRadius = _config.DotSize * 0.5f;
        var index = 0;
        for (var y = 0; y < _config.Height; y++)
        {
            for (var x = 0; x < _config.Width; x++)
            {
                var left = dotSpacing + (x * dotStride);
                var top = dotSpacing + (y * dotStride);
                _instances[index++] = new DotInstance(
                    left + dotRadius,
                    top + dotRadius,
                    dotRadius,
                    0,
                    0,
                    0,
                    0f);
            }
        }

        _bitmap = new WriteableBitmap(_surfaceWidth, _surfaceHeight, 96, 96, PixelFormats.Bgra32, null);
        _bitmapHost.Source = _bitmap;
        _bitmapHost.Width = _surfaceWidth;
        _bitmapHost.Height = _surfaceHeight;
    }

    public void Render(FramePresentation framePresentation)
    {
        if (_config is null || _bitmap is null)
        {
            throw new InvalidOperationException("Renderer must be initialized before rendering.");
        }

        BuildColorLutIfNeeded(_config);
        PopulateInstanceBuffer(framePresentation, _config);
        ShadeInstanceBuffer(_config);

        _bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, _surfaceWidth, _surfaceHeight), _pixels, _stride, 0);
    }

    private void PopulateInstanceBuffer(FramePresentation framePresentation, MatrixConfig config)
    {
        Array.Clear(_pixels, 0, _pixels.Length);

        var rgb = framePresentation.RgbMemory.Span;
        var matrixCapacity = config.Width * config.Height;
        var requestedLedCount = Math.Max(framePresentation.HighestLedWritten, framePresentation.LedsPerChannel);
        var ledCount = Math.Min(Math.Min(requestedLedCount, rgb.Length / 3), matrixCapacity);

        for (var logicalIndex = 0; logicalIndex < ledCount; logicalIndex++)
        {
            var rgbOffset = logicalIndex * 3;
            var mapped = MatrixMapper.MapLinearIndex(logicalIndex, config.Width, config.Height, config.Mapping);
            var instanceIndex = (mapped.Y * config.Width) + mapped.X;
            if ((uint)instanceIndex >= (uint)_instances.Length)
            {
                continue;
            }

            var r = _colorLut[rgb[rgbOffset]];
            var g = _colorLut[rgb[rgbOffset + 1]];
            var b = _colorLut[rgb[rgbOffset + 2]];
            var intensity = Math.Max(r, Math.Max(g, b)) / 255f;

            _instances[instanceIndex] = _instances[instanceIndex] with
            {
                R = r,
                G = g,
                B = b,
                Intensity = intensity,
            };
        }
    }

    private void ShadeInstanceBuffer(MatrixConfig config)
    {
        foreach (var dot in _instances)
        {
            if (dot.Intensity <= 0f)
            {
                continue;
            }

            var minX = Math.Max(0, (int)Math.Floor(dot.CenterX - dot.Radius));
            var maxX = Math.Min(_surfaceWidth - 1, (int)Math.Ceiling(dot.CenterX + dot.Radius));
            var minY = Math.Max(0, (int)Math.Floor(dot.CenterY - dot.Radius));
            var maxY = Math.Min(_surfaceHeight - 1, (int)Math.Ceiling(dot.CenterY + dot.Radius));

            for (var y = minY; y <= maxY; y++)
            {
                for (var x = minX; x <= maxX; x++)
                {
                    ShadePixel(config, dot, x, y);
                }
            }
        }
    }

    private void ShadePixel(MatrixConfig config, DotInstance dot, int x, int y)
    {
        var dx = (float)((x + 0.5f) - dot.CenterX);
        var dy = (float)((y + 0.5f) - dot.CenterY);
        var distance = MathF.Sqrt((dx * dx) + (dy * dy));
        var normalized = distance / MathF.Max(0.0001f, dot.Radius);

        if (normalized > 1f)
        {
            return;
        }

        // Shader-like bulb model (body/core/spec) approximating a fragment shader.
        var body = MathF.Pow(1f - normalized, 1.2f);
        var core = MathF.Pow(MathF.Max(0f, 1f - (normalized * (float)(1.0 + config.Visual.LensFalloff))), 2.8f);
        var spec = MathF.Pow(MathF.Max(0f, 1f - (normalized / MathF.Max(0.05f, (float)config.Visual.SpecularHotspot))), 8.0f)
            * (float)Math.Clamp(config.Visual.RimHighlight + 0.4, 0.0, 1.5);

        var bulb = Math.Clamp((body * 0.55f) + (core * 0.75f) + (spec * 0.45f), 0f, 1.8f);
        var alpha = Math.Clamp(dot.Intensity * bulb, 0f, 1f);
        if (alpha <= 0f)
        {
            return;
        }

        var index = ((y * _surfaceWidth) + x) * 4;
        _pixels[index] = Blend(_pixels[index], dot.B, alpha);
        _pixels[index + 1] = Blend(_pixels[index + 1], dot.G, alpha);
        _pixels[index + 2] = Blend(_pixels[index + 2], dot.R, alpha);
        _pixels[index + 3] = (byte)Math.Clamp(Math.Max(_pixels[index + 3], alpha * 255f), 0f, 255f);
    }

    private static byte Blend(byte existing, byte incoming, float alpha)
    {
        var mixed = existing + ((incoming - existing) * alpha);
        return (byte)Math.Clamp(mixed, 0f, 255f);
    }

    private void BuildColorLutIfNeeded(MatrixConfig config)
    {
        if (_lutBrightness.Equals(config.Brightness) && _lutGamma.Equals(config.Gamma))
        {
            return;
        }

        _lutBrightness = config.Brightness;
        _lutGamma = config.Gamma;

        for (var channel = 0; channel < 256; channel++)
        {
            var normalized = channel / 255.0;
            var gammaCorrected = Math.Pow(normalized, 1.0 / Math.Max(0.01, config.Gamma));
            var scaled = gammaCorrected * config.Brightness;
            _colorLut[channel] = (byte)Math.Clamp((int)Math.Round(scaled * 255.0), 0, 255);
        }
    }

    private readonly record struct DotInstance(
        float CenterX,
        float CenterY,
        float Radius,
        byte R,
        byte G,
        byte B,
        float Intensity);
}
