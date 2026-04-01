using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Rendering;

internal sealed class MatrixFrameRasterComposer
{
    private const int HardMinimumDotSpacing = 2;
    private readonly byte[] _colorLut = new byte[256];
    private float[] _mappedRgb = Array.Empty<float>();
    private float[] _workingRgb = Array.Empty<float>();
    private float[] _smoothedRgb = Array.Empty<float>();
    private float[] _thresholdRgb = Array.Empty<float>();
    private float[] _smallBlurRgb = Array.Empty<float>();
    private float[] _wideBlurRgb = Array.Empty<float>();
    private float[] _blurScratchRgb = Array.Empty<float>();
    private int _downsampleWidth;
    private int _downsampleHeight;
    private MatrixConfig? _config;
    private DotKernel? _kernel;
    private int _surfaceWidth;
    private int _surfaceHeight;
    private int _stride;
    private byte[] _surfaceBgra = Array.Empty<byte>();
    private int _dotSpacing;
    private int _dotStride;
    private double _lutBrightness = double.NaN;
    private double _lutGamma = double.NaN;
    private bool _lutSoftKneeEnabled;
    private double _lutSoftKneeStart = double.NaN;
    private double _lutSoftKneeStrength = double.NaN;

    public void Configure(MatrixConfig config)
    {
        _config = config;
        _dotSpacing = Math.Max(HardMinimumDotSpacing, config.MinDotSpacing);
        _dotStride = config.DotSize + _dotSpacing;
        _surfaceWidth = (config.Width * _dotStride) + _dotSpacing;
        _surfaceHeight = (config.Height * _dotStride) + _dotSpacing;
        _stride = _surfaceWidth * 4;
        _surfaceBgra = new byte[_stride * _surfaceHeight];
        _kernel = DotKernel.Create(config.DotSize, config.DotShape, config.Visual);
    }

    public (int Width, int Height, int Stride, byte[] Pixels) Compose(FramePresentation framePresentation)
    {
        if (_config is null || _kernel is null)
        {
            throw new InvalidOperationException("Composer must be configured before composition.");
        }

        var matrixCapacity = _config.Width * _config.Height;
        EnsureWorkingBuffers(matrixCapacity);
        Array.Clear(_mappedRgb, 0, _mappedRgb.Length);

        var rgb = framePresentation.RgbMemory.Span;
        var requestedLedCount = Math.Max(framePresentation.HighestLedWritten, framePresentation.LedsPerChannel);
        var ledCount = Math.Min(Math.Min(requestedLedCount, rgb.Length / 3), matrixCapacity);

        for (var logicalIndex = 0; logicalIndex < ledCount; logicalIndex++)
        {
            var rgbOffset = logicalIndex * 3;
            var mapped = MatrixMapper.MapLinearIndex(logicalIndex, _config.Width, _config.Height, _config.Mapping);
            var mappedOffset = ((mapped.Y * _config.Width) + mapped.X) * 3;
            _mappedRgb[mappedOffset] = rgb[rgbOffset];
            _mappedRgb[mappedOffset + 1] = rgb[rgbOffset + 1];
            _mappedRgb[mappedOffset + 2] = rgb[rgbOffset + 2];
        }

        BuildColorLutIfNeeded(_config);
        ApplyColorTransforms(_config, matrixCapacity);
        ApplyBloomIfEnabled(matrixCapacity);

        Array.Clear(_surfaceBgra, 0, _surfaceBgra.Length);
        for (var y = 0; y < _config.Height; y++)
        {
            for (var x = 0; x < _config.Width; x++)
            {
                var matrixIndex = (y * _config.Width) + x;
                var colorOffset = matrixIndex * 3;
                var r = ToByte(_workingRgb[colorOffset] / 255.0);
                var g = ToByte(_workingRgb[colorOffset + 1] / 255.0);
                var b = ToByte(_workingRgb[colorOffset + 2] / 255.0);
                var intensity = Math.Max(r, Math.Max(g, b)) / 255.0;

                var dstX = _dotSpacing + (x * _dotStride);
                var dstY = _dotSpacing + (y * _dotStride);
                RasterDot(dstX, dstY, r, g, b, intensity, _config.Visual);
            }
        }

        return (_surfaceWidth, _surfaceHeight, _stride, _surfaceBgra);
    }

    private void RasterDot(int originX, int originY, byte r, byte g, byte b, double intensity, MatrixVisualConfig visual)
    {
        if (_kernel is null)
        {
            return;
        }

        if (visual.FlatShading)
        {
            RasterFlatDot(originX, originY, r, g, b);
            return;
        }

        var rootIntensity = Math.Sqrt(Math.Clamp(intensity, 0.0, 1.0));
        var bodyContribution = (float)Math.Clamp(visual.BodyContribution, 0.0, 4.0);
        var coreContribution = (float)Math.Clamp(visual.CoreContribution, 0.0, 4.0);
        var specContribution = (float)Math.Clamp(visual.SpecularContribution, 0.0, 4.0);
        var specularMax = Math.Clamp(visual.SpecularMax, 0.0, 1.0);
        var coreOpacity = intensity > 0.0
            ? Math.Clamp(visual.CoreBase + (rootIntensity * visual.CoreIntensityScale), 0.0, 1.0)
            : 0.0;
        var specOpacity = intensity > 0.0
            ? Math.Clamp(visual.SpecularBase + (rootIntensity * visual.SpecularIntensityScale), 0.0, specularMax)
            : 0.0;

        var offR = visual.OffStateTintR;
        var offG = visual.OffStateTintG;
        var offB = visual.OffStateTintB;

        for (var ky = 0; ky < _kernel.Size; ky++)
        {
            var py = originY + ky;
            if ((uint)py >= (uint)_surfaceHeight)
            {
                continue;
            }

            for (var kx = 0; kx < _kernel.Size; kx++)
            {
                var px = originX + kx;
                if ((uint)px >= (uint)_surfaceWidth)
                {
                    continue;
                }

                var kernelIndex = (ky * _kernel.Size) + kx;
                var body = _kernel.Body[kernelIndex] * bodyContribution;
                var core = _kernel.Core[kernelIndex] * coreOpacity * coreContribution;
                var spec = _kernel.Specular[kernelIndex] * specOpacity * specContribution;

                var dst = (py * _stride) + (px * 4);
                var outR = (offR * body) + (r * core) + (255.0 * spec);
                var outG = (offG * body) + (g * core) + (255.0 * spec);
                var outB = (offB * body) + (b * core) + (255.0 * spec);

                _surfaceBgra[dst] = (byte)Math.Clamp(outB, 0.0, 255.0);
                _surfaceBgra[dst + 1] = (byte)Math.Clamp(outG, 0.0, 255.0);
                _surfaceBgra[dst + 2] = (byte)Math.Clamp(outR, 0.0, 255.0);
                _surfaceBgra[dst + 3] = 255;
            }
        }
    }

    private void RasterFlatDot(int originX, int originY, byte r, byte g, byte b)
    {
        if (_kernel is null)
        {
            return;
        }

        for (var ky = 0; ky < _kernel.Size; ky++)
        {
            var py = originY + ky;
            if ((uint)py >= (uint)_surfaceHeight)
            {
                continue;
            }

            for (var kx = 0; kx < _kernel.Size; kx++)
            {
                var px = originX + kx;
                if ((uint)px >= (uint)_surfaceWidth)
                {
                    continue;
                }

                var kernelIndex = (ky * _kernel.Size) + kx;
                if (_kernel.Body[kernelIndex] <= 0.0)
                {
                    continue;
                }

                var dst = (py * _stride) + (px * 4);
                _surfaceBgra[dst] = b;
                _surfaceBgra[dst + 1] = g;
                _surfaceBgra[dst + 2] = r;
                _surfaceBgra[dst + 3] = 255;
            }
        }
    }

    private void ApplyColorTransforms(MatrixConfig config, int matrixCapacity)
    {
        var smoothing = config.TemporalSmoothing;
        var smoothingEnabled = smoothing.Enabled;
        var riseAlpha = Clamp01(smoothing.RiseAlpha);
        var fallAlpha = Clamp01(smoothing.FallAlpha);
        for (var i = 0; i < matrixCapacity; i++)
        {
            var offset = i * 3;
            ApplyChannelTransform(offset, riseAlpha, fallAlpha, smoothingEnabled);
            ApplyChannelTransform(offset + 1, riseAlpha, fallAlpha, smoothingEnabled);
            ApplyChannelTransform(offset + 2, riseAlpha, fallAlpha, smoothingEnabled);
        }
    }

    private void ApplyChannelTransform(int channelOffset, double riseAlpha, double fallAlpha, bool smoothingEnabled)
    {
        var target = _colorLut[(byte)_mappedRgb[channelOffset]];
        if (!smoothingEnabled)
        {
            _smoothedRgb[channelOffset] = target;
            _workingRgb[channelOffset] = target;
            return;
        }

        var current = _smoothedRgb[channelOffset];
        var delta = target - current;
        var alpha = delta >= 0 ? riseAlpha : fallAlpha;
        var next = current + ((float)alpha * delta);
        _smoothedRgb[channelOffset] = next;
        _workingRgb[channelOffset] = next;
    }

    private void BuildColorLutIfNeeded(MatrixConfig config)
    {
        var softKnee = config.ToneMapping;
        if (_lutBrightness.Equals(config.Brightness) &&
            _lutGamma.Equals(config.Gamma) &&
            _lutSoftKneeEnabled == softKnee.Enabled &&
            _lutSoftKneeStart.Equals(softKnee.KneeStart) &&
            _lutSoftKneeStrength.Equals(softKnee.Strength))
        {
            return;
        }

        _lutBrightness = config.Brightness;
        _lutGamma = config.Gamma;
        _lutSoftKneeEnabled = softKnee.Enabled;
        _lutSoftKneeStart = softKnee.KneeStart;
        _lutSoftKneeStrength = softKnee.Strength;
        for (var channel = 0; channel < 256; channel++)
        {
            _colorLut[channel] = ApplyToneMap((byte)channel, config.Brightness, config.Gamma, softKnee);
        }
    }

    private void EnsureWorkingBuffers(int matrixCapacity)
    {
        var channelCapacity = matrixCapacity * 3;
        if (_workingRgb.Length != channelCapacity)
        {
            _mappedRgb = new float[channelCapacity];
            _workingRgb = new float[channelCapacity];
            _smoothedRgb = new float[channelCapacity];
            _thresholdRgb = new float[channelCapacity];
        }
    }

    private void ApplyBloomIfEnabled(int matrixCapacity)
    {
        if (_config is null)
        {
            return;
        }

        var bloomProfile = ResolveBloomProfile(_config.Bloom);
        if (!bloomProfile.Enabled || (bloomProfile.SmallStrength <= 0.0 && bloomProfile.WideStrength <= 0.0))
        {
            return;
        }

        Array.Copy(_workingRgb, _thresholdRgb, matrixCapacity * 3);
        if (!ThresholdEmissive(_thresholdRgb, matrixCapacity, bloomProfile.Threshold))
        {
            return;
        }

        Downsample(_thresholdRgb, _config.Width, _config.Height, bloomProfile.ScaleDivisor);
        Array.Copy(_smallBlurRgb, _wideBlurRgb, _smallBlurRgb.Length);
        BoxBlurRgbSeparable(_smallBlurRgb, _downsampleWidth, _downsampleHeight, bloomProfile.SmallRadius);
        BoxBlurRgbSeparable(_wideBlurRgb, _downsampleWidth, _downsampleHeight, bloomProfile.WideRadius);
        CompositeBloom(_workingRgb, _smallBlurRgb, _wideBlurRgb, _config.Width, _config.Height, _downsampleWidth, _downsampleHeight, bloomProfile);
    }

    private static BloomProfile ResolveBloomProfile(BloomConfig bloom)
    {
        if (!bloom.Enabled)
        {
            return BloomProfile.Disabled;
        }

        var preset = bloom.QualityPreset.Trim().ToLowerInvariant();
        var profile = preset switch
        {
            "low" => new BloomProfile(true, 2, 1, 2, bloom.Threshold, bloom.SmallStrength, bloom.WideStrength),
            "medium" => new BloomProfile(true, 2, 2, 4, bloom.Threshold, bloom.SmallStrength, bloom.WideStrength),
            "high" => new BloomProfile(true, 1, 3, 6, bloom.Threshold, bloom.SmallStrength, bloom.WideStrength),
            "off" or "" => BloomProfile.Disabled,
            _ => new BloomProfile(true, bloom.BufferScaleDivisor, bloom.SmallRadius, bloom.WideRadius, bloom.Threshold, bloom.SmallStrength, bloom.WideStrength),
        };

        return profile with
        {
            ScaleDivisor = Math.Clamp(profile.ScaleDivisor, 1, 4),
            SmallRadius = Math.Clamp(profile.SmallRadius, 1, 8),
            WideRadius = Math.Clamp(profile.WideRadius, Math.Max(1, profile.SmallRadius), 16),
            Threshold = Clamp01(profile.Threshold),
        };
    }

    private void Downsample(float[] source, int width, int height, int scaleDivisor)
    {
        _downsampleWidth = Math.Max(1, width / scaleDivisor);
        _downsampleHeight = Math.Max(1, height / scaleDivisor);
        var downsamplePixels = _downsampleWidth * _downsampleHeight * 3;
        if (_smallBlurRgb.Length != downsamplePixels)
        {
            _smallBlurRgb = new float[downsamplePixels];
            _wideBlurRgb = new float[downsamplePixels];
            _blurScratchRgb = new float[downsamplePixels];
        }

        Array.Clear(_smallBlurRgb, 0, _smallBlurRgb.Length);
        for (var y = 0; y < _downsampleHeight; y++)
        {
            for (var x = 0; x < _downsampleWidth; x++)
            {
                var srcX = Math.Min(width - 1, x * scaleDivisor);
                var srcY = Math.Min(height - 1, y * scaleDivisor);
                var srcOffset = ((srcY * width) + srcX) * 3;
                var dstOffset = ((y * _downsampleWidth) + x) * 3;
                _smallBlurRgb[dstOffset] = source[srcOffset];
                _smallBlurRgb[dstOffset + 1] = source[srcOffset + 1];
                _smallBlurRgb[dstOffset + 2] = source[srcOffset + 2];
            }
        }
    }

    private static bool ThresholdEmissive(float[] source, int matrixCapacity, double threshold)
    {
        var cutoff = (float)(Math.Clamp(threshold, 0.0, 1.0) * 255.0);
        var anyActive = false;
        for (var i = 0; i < matrixCapacity; i++)
        {
            var offset = i * 3;
            var peak = Math.Max(source[offset], Math.Max(source[offset + 1], source[offset + 2]));
            if (peak < cutoff)
            {
                source[offset] = source[offset + 1] = source[offset + 2] = 0f;
            }
            else
            {
                anyActive = true;
            }
        }

        return anyActive;
    }

    private void BoxBlurRgbSeparable(float[] rgb, int width, int height, int radius)
    {
        if (radius <= 0)
        {
            return;
        }

        if (_blurScratchRgb.Length != rgb.Length)
        {
            _blurScratchRgb = new float[rgb.Length];
        }

        HorizontalBlurRgb(rgb, _blurScratchRgb, width, height, radius);
        VerticalBlurRgb(_blurScratchRgb, rgb, width, height, radius);
    }

    private static void HorizontalBlurRgb(float[] source, float[] destination, int width, int height, int radius)
    {
        for (var y = 0; y < height; y++)
        {
            float sumR = 0, sumG = 0, sumB = 0;
            var samples = 0;
            for (var sx = 0; sx <= Math.Min(width - 1, radius); sx++)
            {
                var sampleOffset = ((y * width) + sx) * 3;
                sumR += source[sampleOffset];
                sumG += source[sampleOffset + 1];
                sumB += source[sampleOffset + 2];
                samples++;
            }

            for (var x = 0; x < width; x++)
            {
                var dstOffset = ((y * width) + x) * 3;
                destination[dstOffset] = sumR / Math.Max(1, samples);
                destination[dstOffset + 1] = sumG / Math.Max(1, samples);
                destination[dstOffset + 2] = sumB / Math.Max(1, samples);

                var removeX = x - radius;
                if (removeX >= 0)
                {
                    var removeOffset = ((y * width) + removeX) * 3;
                    sumR -= source[removeOffset];
                    sumG -= source[removeOffset + 1];
                    sumB -= source[removeOffset + 2];
                    samples--;
                }

                var addX = x + radius + 1;
                if (addX < width)
                {
                    var addOffset = ((y * width) + addX) * 3;
                    sumR += source[addOffset];
                    sumG += source[addOffset + 1];
                    sumB += source[addOffset + 2];
                    samples++;
                }
            }
        }
    }

    private static void VerticalBlurRgb(float[] source, float[] destination, int width, int height, int radius)
    {
        for (var x = 0; x < width; x++)
        {
            float sumR = 0, sumG = 0, sumB = 0;
            var samples = 0;
            for (var sy = 0; sy <= Math.Min(height - 1, radius); sy++)
            {
                var sampleOffset = ((sy * width) + x) * 3;
                sumR += source[sampleOffset];
                sumG += source[sampleOffset + 1];
                sumB += source[sampleOffset + 2];
                samples++;
            }

            for (var y = 0; y < height; y++)
            {
                var dstOffset = ((y * width) + x) * 3;
                destination[dstOffset] = sumR / Math.Max(1, samples);
                destination[dstOffset + 1] = sumG / Math.Max(1, samples);
                destination[dstOffset + 2] = sumB / Math.Max(1, samples);

                var removeY = y - radius;
                if (removeY >= 0)
                {
                    var removeOffset = ((removeY * width) + x) * 3;
                    sumR -= source[removeOffset];
                    sumG -= source[removeOffset + 1];
                    sumB -= source[removeOffset + 2];
                    samples--;
                }

                var addY = y + radius + 1;
                if (addY < height)
                {
                    var addOffset = ((addY * width) + x) * 3;
                    sumR += source[addOffset];
                    sumG += source[addOffset + 1];
                    sumB += source[addOffset + 2];
                    samples++;
                }
            }
        }
    }

    private static void CompositeBloom(float[] target, float[] smallBlur, float[] wideBlur, int width, int height, int bloomWidth, int bloomHeight, BloomProfile profile)
    {
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var targetOffset = ((y * width) + x) * 3;
                var bloomX = Math.Min(bloomWidth - 1, x / profile.ScaleDivisor);
                var bloomY = Math.Min(bloomHeight - 1, y / profile.ScaleDivisor);
                var bloomOffset = ((bloomY * bloomWidth) + bloomX) * 3;
                target[targetOffset] = Math.Clamp(target[targetOffset] + (smallBlur[bloomOffset] * (float)profile.SmallStrength) + (wideBlur[bloomOffset] * (float)profile.WideStrength), 0f, 255f);
                target[targetOffset + 1] = Math.Clamp(target[targetOffset + 1] + (smallBlur[bloomOffset + 1] * (float)profile.SmallStrength) + (wideBlur[bloomOffset + 1] * (float)profile.WideStrength), 0f, 255f);
                target[targetOffset + 2] = Math.Clamp(target[targetOffset + 2] + (smallBlur[bloomOffset + 2] * (float)profile.SmallStrength) + (wideBlur[bloomOffset + 2] * (float)profile.WideStrength), 0f, 255f);
            }
        }
    }

    private static byte ApplyToneMap(byte channel, double brightness, double gamma, ToneMappingConfig toneMapping)
    {
        var normalized = channel / 255.0;
        var adjusted = Math.Pow(Math.Clamp(normalized, 0.0, 1.0), Math.Clamp(gamma, 0.1, 5.0));
        var scaled = adjusted * Math.Clamp(brightness, 0.0, 4.0);
        if (toneMapping.Enabled)
        {
            var kneeStart = Math.Clamp(toneMapping.KneeStart, 0.5, 0.99);
            var strength = Math.Clamp(toneMapping.Strength, 0.0, 8.0);
            if (scaled > kneeStart)
            {
                var excess = scaled - kneeStart;
                scaled = kneeStart + (excess / (1.0 + (strength * excess)));
            }
        }

        return ToByte(Math.Clamp(scaled, 0.0, 1.0));
    }

    private static byte ToByte(double value) => (byte)Math.Round(Math.Clamp(value, 0.0, 1.0) * 255.0);
    private static double Clamp01(double value) => Math.Clamp(value, 0.0, 1.0);

    private sealed record BloomProfile(bool Enabled, int ScaleDivisor, int SmallRadius, int WideRadius, double Threshold, double SmallStrength, double WideStrength)
    {
        public static BloomProfile Disabled => new(false, 1, 0, 0, 1.0, 0.0, 0.0);
    }

    private sealed class DotKernel
    {
        public required int Size { get; init; }
        public required double[] Body { get; init; }
        public required double[] Core { get; init; }
        public required double[] Specular { get; init; }

        public static DotKernel Create(int dotSize, string shape, MatrixVisualConfig visual)
        {
            var size = Math.Max(1, dotSize);
            var body = new double[size * size];
            var core = new double[size * size];
            var specular = new double[size * size];
            var lensFalloff = Clamp01(visual.LensFalloff);
            var specHotspot = Clamp01(visual.SpecularHotspot);
            var rim = Clamp01(visual.RimHighlight);
            var offAlpha = Clamp01(visual.OffStateAlpha);

            var center = (size - 1) * 0.5;
            var radius = Math.Max(0.5, size * 0.5);
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var dx = (x - center) / radius;
                    var dy = (y - center) / radius;
                    var radial = Math.Sqrt((dx * dx) + (dy * dy));
                    var mask = shape.Equals("square", StringComparison.OrdinalIgnoreCase) || radial <= 1.0 ? 1.0 : 0.0;
                    if (mask <= 0.0)
                    {
                        continue;
                    }

                    var idx = (y * size) + x;
                    var edge = Math.Clamp(1.0 - radial, 0.0, 1.0);
                    body[idx] = offAlpha * (0.25 + (0.55 * Math.Pow(edge, 0.5 + lensFalloff))) + (rim * 0.08 * (1.0 - edge));
                    core[idx] = Math.Pow(edge, 1.1 + (lensFalloff * 1.6));

                    var hx = (x / (double)Math.Max(1, size - 1)) - 0.30;
                    var hy = (y / (double)Math.Max(1, size - 1)) - 0.24;
                    var hotspotDist2 = (hx * hx) + (hy * hy);
                    specular[idx] = Math.Exp(-hotspotDist2 / Math.Max(0.01, 0.02 + (0.12 * specHotspot))) * (0.35 + (0.55 * specHotspot));
                }
            }

            return new DotKernel
            {
                Size = size,
                Body = body,
                Core = core,
                Specular = specular,
            };
        }
    }
}
