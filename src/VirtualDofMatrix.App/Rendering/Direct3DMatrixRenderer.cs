using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Rendering;

/// <summary>
/// Direct3D-style renderer that keeps per-dot instance buffers (position/color/intensity)
/// and shades each dot through a shader-style bulb model (body/core/spec).
/// The bulb math is baked into a reusable kernel so frame rendering stays responsive.
/// </summary>
public sealed class Direct3DMatrixRenderer : IMatrixRenderer
{
    private const int HardMinimumDotSpacing = 2;

    private readonly byte[] _colorLut = new byte[256];
    private DotInstance[] _instances = Array.Empty<DotInstance>();
    private KernelSample[] _kernel = Array.Empty<KernelSample>();
    private byte[] _pixels = Array.Empty<byte>();

    private MatrixConfig? _config;
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

        var index = 0;
        for (var y = 0; y < _config.Height; y++)
        {
            for (var x = 0; x < _config.Width; x++)
            {
                var left = dotSpacing + (x * dotStride);
                var top = dotSpacing + (y * dotStride);
                _instances[index++] = new DotInstance(left, top, 0, 0, 0, 0f);
            }
        }

        BuildKernel(_config);

        _bitmap = new WriteableBitmap(_surfaceWidth, _surfaceHeight, 96, 96, PixelFormats.Bgra32, null);
        bitmapHost.Source = _bitmap;
        bitmapHost.Width = _surfaceWidth;
        bitmapHost.Height = _surfaceHeight;
    }

    public void Render(FramePresentation framePresentation)
    {
        if (_config is null || _bitmap is null)
        {
            throw new InvalidOperationException("Renderer must be initialized before rendering.");
        }

        BuildColorLutIfNeeded(_config);
        PopulateInstanceBuffer(framePresentation, _config);
        RasterizeInstances(_config);

        _bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, _surfaceWidth, _surfaceHeight), _pixels, _stride, 0);
    }

    private void PopulateInstanceBuffer(FramePresentation framePresentation, MatrixConfig config)
    {
        // Clear per-frame target values for every dot so old intensity does not persist.
        for (var i = 0; i < _instances.Length; i++)
        {
            _instances[i] = _instances[i] with { R = 0, G = 0, B = 0, Intensity = 0f };
        }

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

    private void RasterizeInstances(MatrixConfig config)
    {
        Array.Clear(_pixels, 0, _pixels.Length);

        var offR = config.Visual.OffStateTintR;
        var offG = config.Visual.OffStateTintG;
        var offB = config.Visual.OffStateTintB;
        var offAlphaBase = (float)Math.Clamp(config.Visual.OffStateAlpha, 0.0, 1.0);

        foreach (var dot in _instances)
        {
            var intensity = dot.Intensity;
            foreach (var sample in _kernel)
            {
                var px = dot.Left + sample.OffsetX;
                var py = dot.Top + sample.OffsetY;
                if ((uint)px >= (uint)_surfaceWidth || (uint)py >= (uint)_surfaceHeight)
                {
                    continue;
                }

                var offAlpha = offAlphaBase * (0.40f + (0.60f * sample.BodyAlpha));
                var litAlpha = sample.BodyAlpha * (0.35f + (1.10f * intensity));
                var alpha = Math.Clamp(offAlpha + litAlpha, 0f, 1f);
                if (alpha <= 0f)
                {
                    continue;
                }

                var colorBoost = 0.90f + (0.80f * sample.BodyAlpha);
                var targetR = (byte)Math.Clamp(Math.Max(offR, dot.R * colorBoost), 0f, 255f);
                var targetG = (byte)Math.Clamp(Math.Max(offG, dot.G * colorBoost), 0f, 255f);
                var targetB = (byte)Math.Clamp(Math.Max(offB, dot.B * colorBoost), 0f, 255f);

                var specularStrength = sample.SpecAlpha * (0.25f + (0.75f * intensity));
                if (specularStrength > 0f)
                {
                    targetR = Blend(targetR, 255, specularStrength * 0.65f);
                    targetG = Blend(targetG, 255, specularStrength * 0.65f);
                    targetB = Blend(targetB, 255, specularStrength * 0.70f);
                }

                var index = ((py * _surfaceWidth) + px) * 4;
                _pixels[index] = Blend(_pixels[index], targetB, alpha);
                _pixels[index + 1] = Blend(_pixels[index + 1], targetG, alpha);
                _pixels[index + 2] = Blend(_pixels[index + 2], targetR, alpha);
                _pixels[index + 3] = (byte)Math.Clamp(Math.Max(_pixels[index + 3], alpha * 255f), 0f, 255f);
            }
        }
    }

    private void BuildKernel(MatrixConfig config)
    {
        var kernelSize = Math.Max(1, config.DotSize);
        var center = (kernelSize - 1) * 0.5f;
        var radius = Math.Max(0.5f, kernelSize * 0.5f);

        var samples = new List<KernelSample>(kernelSize * kernelSize);
        for (var y = 0; y < kernelSize; y++)
        {
            for (var x = 0; x < kernelSize; x++)
            {
                var dx = (x - center) / radius;
                var dy = (y - center) / radius;
                var dist = MathF.Sqrt((dx * dx) + (dy * dy));
                if (dist > 1f)
                {
                    continue;
                }

                var hotspotCoverage = Math.Clamp((float)config.Visual.HotspotCoverage, 0.05f, 0.98f);
                var edgeBlend = dist <= hotspotCoverage
                    ? 1f
                    : MathF.Pow(MathF.Max(0f, (1f - dist) / MathF.Max(0.001f, 1f - hotspotCoverage)), 0.65f);

                var body = edgeBlend;
                var core = MathF.Pow(MathF.Max(0f, 1f - (dist * (float)(1.0 + config.Visual.LensFalloff))), 1.6f);

                // Specular lobe is offset toward top-left to mimic a bulb highlight.
                var specDx = dx + 0.22f;
                var specDy = dy + 0.22f;
                var specDist = MathF.Sqrt((specDx * specDx) + (specDy * specDy));
                var specBase = MathF.Max(0f, 1f - (specDist / MathF.Max(0.08f, (float)config.Visual.SpecularHotspot)));
                var spec = MathF.Pow(specBase, 2.4f) * (float)Math.Clamp(config.Visual.RimHighlight + 0.45, 0.0, 1.8);

                var bulb = Math.Clamp((body * 0.90f) + (core * 0.24f), 0f, 1.4f);
                if (bulb <= 0f && spec <= 0f)
                {
                    continue;
                }

                samples.Add(new KernelSample(x, y, Math.Clamp(bulb, 0f, 1f), Math.Clamp(spec, 0f, 1f)));
            }
        }

        _kernel = samples.ToArray();
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

    private readonly record struct DotInstance(int Left, int Top, byte R, byte G, byte B, float Intensity);

    private readonly record struct KernelSample(int OffsetX, int OffsetY, float BodyAlpha, float SpecAlpha);
}
