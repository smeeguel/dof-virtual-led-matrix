using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Rendering;

public sealed class WpfPrimitiveMatrixRenderer : IMatrixRenderer
{
    private const int HardMinimumDotSpacing = 2;
    private readonly List<DotVisual> _dots = new();
    private readonly byte[] _colorLut = new byte[256];

    private Canvas? _targetCanvas;
    private MatrixConfig? _config;
    private Brush? _sharedBodyBrush;
    private Brush? _sharedCoreOpacityMask;
    private Brush? _sharedSpecularBrush;
    private float[] _mappedRgb = Array.Empty<float>();
    private float[] _workingRgb = Array.Empty<float>();
    private float[] _smoothedRgb = Array.Empty<float>();
    private float[] _thresholdRgb = Array.Empty<float>();
    private float[] _smallBlurRgb = Array.Empty<float>();
    private float[] _wideBlurRgb = Array.Empty<float>();
    private float[] _blurScratchRgb = Array.Empty<float>();
    private int _downsampleWidth;
    private int _downsampleHeight;
    private double _lutBrightness = double.NaN;
    private double _lutGamma = double.NaN;
    private bool _lutSoftKneeEnabled;
    private double _lutSoftKneeStart = double.NaN;
    private double _lutSoftKneeStrength = double.NaN;
    private ulong _lastFrameSignature;
    private ulong _skippedFrames;
    public bool UsesImageHost => false;
    public RendererTelemetry Telemetry { get; private set; } = RendererTelemetry.Empty;

    public void Initialize(Canvas primitiveCanvas, Image bitmapHost, MatrixConfig config)
    {
        _targetCanvas = primitiveCanvas ?? throw new ArgumentNullException(nameof(primitiveCanvas));
        bitmapHost.Source = null;
        _config = config ?? throw new ArgumentNullException(nameof(config));

        _dots.Clear();
        _targetCanvas.Children.Clear();
        _targetCanvas.Background = Brushes.Black;

        _sharedBodyBrush = CreateBodyBrush(_config.Visual);
        _sharedCoreOpacityMask = CreateCoreOpacityMask(_config.Visual.LensFalloff);
        _sharedSpecularBrush = CreateSpecularBrush();
        TryFreeze(_sharedBodyBrush);
        TryFreeze(_sharedCoreOpacityMask);
        TryFreeze(_sharedSpecularBrush);

        var dotSpacing = Math.Max(HardMinimumDotSpacing, _config.MinDotSpacing);
        var dotStride = _config.DotSize + dotSpacing;
        var width = _config.Width;
        var height = _config.Height;

        _targetCanvas.Width = (width * dotStride) + dotSpacing;
        _targetCanvas.Height = (height * dotStride) + dotSpacing;
        _targetCanvas.UseLayoutRounding = true;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var dot = CreateDotVisual();
                var left = dotSpacing + (x * dotStride);
                var top = dotSpacing + (y * dotStride);

                Canvas.SetLeft(dot.Body, left);
                Canvas.SetTop(dot.Body, top);
                Canvas.SetLeft(dot.Core, left);
                Canvas.SetTop(dot.Core, top);
                Canvas.SetLeft(dot.Specular, left);
                Canvas.SetTop(dot.Specular, top);

                _targetCanvas.Children.Add(dot.Body);
                _targetCanvas.Children.Add(dot.Core);
                _targetCanvas.Children.Add(dot.Specular);
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
        var dirtyRanges = framePresentation.DirtyLedRanges;
        var dirtyLedCount = 0;

        EnsureWorkingBuffers(matrixCapacity);
        var dirtyBounds = DirtyBounds.Empty;

        foreach (var range in dirtyRanges)
        {
            var rangeStart = Math.Clamp(range.StartLed, 0, ledCount);
            var rangeEnd = Math.Clamp(range.EndLedExclusive, 0, ledCount);
            if (rangeEnd <= rangeStart)
            {
                continue;
            }

            dirtyLedCount += rangeEnd - rangeStart;
            for (var logicalIndex = rangeStart; logicalIndex < rangeEnd; logicalIndex++)
            {
                var rgbOffset = logicalIndex * 3;
                var mapped = MatrixMapper.MapLinearIndex(logicalIndex, _config.Width, _config.Height, _config.Mapping);
                var shapeIndex = mapped.Y * _config.Width + mapped.X;

                if ((uint)shapeIndex >= (uint)_dots.Count)
                {
                    continue;
                }

                var colorOffset = shapeIndex * 3;
                _mappedRgb[colorOffset] = rgb[rgbOffset];
                _mappedRgb[colorOffset + 1] = rgb[rgbOffset + 1];
                _mappedRgb[colorOffset + 2] = rgb[rgbOffset + 2];
                dirtyBounds = dirtyBounds.Include(mapped.X, mapped.Y);
            }
        }

        if (dirtyLedCount == 0 || !dirtyBounds.HasValue)
        {
            _skippedFrames++;
            Telemetry = new RendererTelemetry(dirtyLedCount, _skippedFrames, ComputeDirtyAreaPercent(0, matrixCapacity));
            return;
        }

        var signature = ComputeFrameSignature(rgb, dirtyRanges, ledCount, framePresentation.OutputSequence);
        if (signature == _lastFrameSignature)
        {
            _skippedFrames++;
            Telemetry = new RendererTelemetry(dirtyLedCount, _skippedFrames, ComputeDirtyAreaPercent(dirtyBounds.Area, matrixCapacity));
            return;
        }

        _lastFrameSignature = signature;
        BuildColorLutIfNeeded(_config);
        ApplyColorTransforms(_config, dirtyBounds);
        ApplyBloomIfEnabled(matrixCapacity, dirtyBounds);

        for (var y = dirtyBounds.MinY; y <= dirtyBounds.MaxY; y++)
        {
            for (var x = dirtyBounds.MinX; x <= dirtyBounds.MaxX; x++)
            {
                var shapeIndex = (y * _config.Width) + x;
                var colorOffset = shapeIndex * 3;
                var r = ToByte(_workingRgb[colorOffset] / 255.0);
                var g = ToByte(_workingRgb[colorOffset + 1] / 255.0);
                var b = ToByte(_workingRgb[colorOffset + 2] / 255.0);

                var intensity = Math.Max(r, Math.Max(g, b)) / 255.0;
                var dot = _dots[shapeIndex];

                if (intensity > 0.0)
                {
                    dot.CoreBrush.Color = Color.FromRgb(r, g, b);
                    var rootIntensity = Math.Sqrt(intensity);
                    dot.Core.Opacity = Math.Clamp(0.2 + (rootIntensity * 0.72), 0.0, 1.0);
                    dot.Specular.Opacity = Math.Clamp((rootIntensity * 0.45) + 0.08, 0.0, 0.65);
                }
                else
                {
                    dot.Core.Opacity = 0.0;
                    dot.Specular.Opacity = 0.0;
                }
            }
        }

        Telemetry = new RendererTelemetry(dirtyLedCount, _skippedFrames, ComputeDirtyAreaPercent(dirtyBounds.Area, matrixCapacity));
    }

    private void ApplyColorTransforms(MatrixConfig config, DirtyBounds dirtyBounds)
    {
        var smoothing = config.TemporalSmoothing;
        var smoothingEnabled = smoothing.Enabled;
        var riseAlpha = Clamp01(smoothing.RiseAlpha);
        var fallAlpha = Clamp01(smoothing.FallAlpha);

        for (var y = dirtyBounds.MinY; y <= dirtyBounds.MaxY; y++)
        {
            for (var x = dirtyBounds.MinX; x <= dirtyBounds.MaxX; x++)
            {
                var offset = ((y * config.Width) + x) * 3;
                ApplyChannelTransform(offset, riseAlpha, fallAlpha, smoothingEnabled);
                ApplyChannelTransform(offset + 1, riseAlpha, fallAlpha, smoothingEnabled);
                ApplyChannelTransform(offset + 2, riseAlpha, fallAlpha, smoothingEnabled);
            }
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

    private void ApplyBloomIfEnabled(int matrixCapacity, DirtyBounds dirtyBounds)
    {
        if (_config is null)
        {
            return;
        }

        var bloomProfile = ResolveBloomProfile(_config.Bloom);
        if (!bloomProfile.Enabled)
        {
            return;
        }

        if (bloomProfile.SmallStrength <= 0.0 && bloomProfile.WideStrength <= 0.0)
        {
            return;
        }

        var bloomBounds = dirtyBounds.Inflate(bloomProfile.WideRadius * bloomProfile.ScaleDivisor, _config.Width, _config.Height);
        CopyRegion(_workingRgb, _thresholdRgb, _config.Width, bloomBounds);
        if (!ThresholdEmissive(_thresholdRgb, _config.Width, bloomBounds, bloomProfile.Threshold))
        {
            return;
        }

        Downsample(_thresholdRgb, _config.Width, _config.Height, bloomProfile.ScaleDivisor);
        Array.Copy(_smallBlurRgb, _wideBlurRgb, _smallBlurRgb.Length);

        BoxBlurRgbSeparable(_smallBlurRgb, _downsampleWidth, _downsampleHeight, bloomProfile.SmallRadius);
        BoxBlurRgbSeparable(_wideBlurRgb, _downsampleWidth, _downsampleHeight, bloomProfile.WideRadius);

        CompositeBloom(_workingRgb, _smallBlurRgb, _wideBlurRgb, _config.Width, _config.Height, _downsampleWidth, _downsampleHeight, bloomProfile, bloomBounds);
    }

    private static BloomProfile ResolveBloomProfile(BloomConfig bloom)
    {
        if (!bloom.Enabled)
        {
            return BloomProfile.Disabled;
        }

        var preset = bloom.QualityPreset.Trim().ToLowerInvariant();
        if (preset is "" or "off")
        {
            return BloomProfile.Disabled;
        }

        var profile = preset switch
        {
            "low" => new BloomProfile(true, 2, 1, 2, bloom.Threshold, bloom.SmallStrength, bloom.WideStrength),
            "medium" => new BloomProfile(true, 2, 2, 4, bloom.Threshold, bloom.SmallStrength, bloom.WideStrength),
            "high" => new BloomProfile(true, 1, 3, 6, bloom.Threshold, bloom.SmallStrength, bloom.WideStrength),
            _ => new BloomProfile(true, bloom.BufferScaleDivisor, bloom.SmallRadius, bloom.WideRadius, bloom.Threshold, bloom.SmallStrength, bloom.WideStrength),
        };

        return profile with
        {
            ScaleDivisor = Math.Clamp(profile.ScaleDivisor, 1, 4),
            SmallRadius = Math.Clamp(profile.SmallRadius, 1, 8),
            WideRadius = Math.Clamp(profile.WideRadius, Math.Max(1, profile.SmallRadius), 16),
            Threshold = Clamp01(profile.Threshold),
            SmallStrength = Math.Clamp(profile.SmallStrength, 0.0, 2.0),
            WideStrength = Math.Clamp(profile.WideStrength, 0.0, 2.0),
        };
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

    private static void CopyRegion(float[] source, float[] destination, int width, DirtyBounds bounds)
    {
        for (var y = bounds.MinY; y <= bounds.MaxY; y++)
        {
            var rowStart = ((y * width) + bounds.MinX) * 3;
            var length = bounds.Width * 3;
            Array.Copy(source, rowStart, destination, rowStart, length);
        }
    }

    private static bool ThresholdEmissive(float[] source, int width, DirtyBounds bounds, double threshold)
    {
        var cutoff = (float)(Math.Clamp(threshold, 0.0, 1.0) * 255.0);
        var anyActive = false;

        for (var y = bounds.MinY; y <= bounds.MaxY; y++)
        {
            for (var x = bounds.MinX; x <= bounds.MaxX; x++)
            {
                var offset = ((y * width) + x) * 3;
                var peak = Math.Max(source[offset], Math.Max(source[offset + 1], source[offset + 2]));
                if (peak < cutoff)
                {
                    source[offset] = 0f;
                    source[offset + 1] = 0f;
                    source[offset + 2] = 0f;
                }
                else
                {
                    anyActive = true;
                }
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
            float sumR = 0;
            float sumG = 0;
            float sumB = 0;
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
            float sumR = 0;
            float sumG = 0;
            float sumB = 0;
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

    private static void CompositeBloom(float[] target, float[] smallBlur, float[] wideBlur, int width, int height, int bloomWidth, int bloomHeight, BloomProfile profile, DirtyBounds bounds)
    {
        for (var y = bounds.MinY; y <= bounds.MaxY && y < height; y++)
        {
            for (var x = bounds.MinX; x <= bounds.MaxX && x < width; x++)
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

    private DotVisual CreateDotVisual()
    {
        if (_config is null || _sharedBodyBrush is null || _sharedCoreOpacityMask is null || _sharedSpecularBrush is null)
        {
            throw new InvalidOperationException("Renderer config unavailable.");
        }

        Shape body = CreateShape(_config.DotShape);
        Shape core = CreateShape(_config.DotShape);
        Shape specular = CreateShape(_config.DotShape);

        body.Width = _config.DotSize;
        body.Height = _config.DotSize;
        body.Stretch = Stretch.Fill;
        body.SnapsToDevicePixels = true;
        body.Fill = _sharedBodyBrush;

        core.Width = _config.DotSize;
        core.Height = _config.DotSize;
        core.Stretch = Stretch.Fill;
        core.SnapsToDevicePixels = true;
        core.Opacity = 0.0;
        var coreBrush = new SolidColorBrush(Colors.Black);
        core.Fill = coreBrush;
        core.OpacityMask = _sharedCoreOpacityMask;
        specular.Width = _config.DotSize;
        specular.Height = _config.DotSize;
        specular.Stretch = Stretch.Fill;
        specular.SnapsToDevicePixels = true;
        specular.Fill = _sharedSpecularBrush;
        specular.Opacity = 0.0;

        return new DotVisual(body, core, specular, coreBrush);
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

    private static Brush CreateCoreOpacityMask(double lensFalloff)
    {
        var falloff = Clamp01(lensFalloff);
        var mid = 0.78 - (0.24 * falloff);

        return new RadialGradientBrush
        {
            GradientOrigin = new System.Windows.Point(0.48, 0.46),
            Center = new System.Windows.Point(0.5, 0.5),
            RadiusX = 0.54,
            RadiusY = 0.54,
            GradientStops = new GradientStopCollection
            {
                new(Colors.White, 0.0),
                new(Colors.White, Math.Clamp(mid, 0.35, 0.85)),
                new(Colors.Transparent, 1.0),
            },
        };
    }

    private static Brush CreateSpecularBrush()
    {
        return new RadialGradientBrush
        {
            GradientOrigin = new System.Windows.Point(0.30, 0.24),
            Center = new System.Windows.Point(0.36, 0.3),
            RadiusX = 0.42,
            RadiusY = 0.42,
            GradientStops = new GradientStopCollection
            {
                new(Color.FromArgb(215, 255, 255, 255), 0.0),
                new(Color.FromArgb(120, 255, 255, 255), 0.30),
                new(Color.FromArgb(45, 255, 255, 255), 0.62),
                new(Colors.Transparent, 1.0),
            },
        };
    }

    private static void TryFreeze(Brush brush)
    {
        if (brush.CanFreeze)
        {
            brush.Freeze();
        }
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

    private static ulong ComputeFrameSignature(ReadOnlySpan<byte> rgb, IReadOnlyList<DirtyLedRange> dirtyRanges, int ledCount, ulong outputSequence)
    {
        var hash = 1469598103934665603UL ^ outputSequence;
        foreach (var range in dirtyRanges)
        {
            var start = Math.Clamp(range.StartLed, 0, ledCount);
            var end = Math.Clamp(range.EndLedExclusive, 0, ledCount);
            for (var led = start; led < end; led++)
            {
                var offset = led * 3;
                hash ^= rgb[offset];
                hash *= 1099511628211UL;
                hash ^= rgb[offset + 1];
                hash *= 1099511628211UL;
                hash ^= rgb[offset + 2];
                hash *= 1099511628211UL;
            }
        }

        return hash;
    }

    private static double ComputeDirtyAreaPercent(int dirtyArea, int matrixArea)
    {
        if (matrixArea <= 0 || dirtyArea <= 0)
        {
            return 0.0;
        }

        return Math.Clamp((double)dirtyArea / matrixArea * 100.0, 0.0, 100.0);
    }

    private sealed record DotVisual(Shape Body, Shape Core, Shape Specular, SolidColorBrush CoreBrush);

    private sealed record BloomProfile(
        bool Enabled,
        int ScaleDivisor,
        int SmallRadius,
        int WideRadius,
        double Threshold,
        double SmallStrength,
        double WideStrength)
    {
        public static BloomProfile Disabled => new(false, 1, 0, 0, 1.0, 0.0, 0.0);
    }

    private readonly record struct DirtyBounds(int MinX, int MinY, int MaxX, int MaxY, bool HasValue)
    {
        public static DirtyBounds Empty => new(0, 0, 0, 0, false);

        public int Width => HasValue ? (MaxX - MinX + 1) : 0;
        public int Height => HasValue ? (MaxY - MinY + 1) : 0;
        public int Area => Width * Height;

        public DirtyBounds Include(int x, int y)
        {
            if (!HasValue)
            {
                return new DirtyBounds(x, y, x, y, true);
            }

            return new DirtyBounds(Math.Min(MinX, x), Math.Min(MinY, y), Math.Max(MaxX, x), Math.Max(MaxY, y), true);
        }

        public DirtyBounds Inflate(int amount, int maxWidth, int maxHeight)
        {
            if (!HasValue || amount <= 0)
            {
                return this;
            }

            return new DirtyBounds(
                Math.Max(0, MinX - amount),
                Math.Max(0, MinY - amount),
                Math.Min(maxWidth - 1, MaxX + amount),
                Math.Min(maxHeight - 1, MaxY + amount),
                true);
        }
    }
}

public readonly record struct RendererTelemetry(int DirtyLedCount, ulong SkippedFrames, double DirtyAreaPercent)
{
    public static RendererTelemetry Empty => new(0, 0, 0.0);
}
