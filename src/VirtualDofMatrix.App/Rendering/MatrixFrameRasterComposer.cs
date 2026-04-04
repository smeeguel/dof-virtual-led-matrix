using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Rendering;

internal sealed class MatrixFrameRasterComposer
{
    private const int HardMinimumDotSpacing = 2;
    private const float TemporalSmoothingOffSnapThreshold = 1.0f;
    private readonly byte[] _colorLut = new byte[256];
    private float[] _mappedRgb = Array.Empty<float>();
    private float[] _workingRgb = Array.Empty<float>();
    private float[] _smoothedRgb = Array.Empty<float>();
    private float[] _screenBloomSourceRgb = Array.Empty<float>();
    private float[] _screenBloomNearRgb = Array.Empty<float>();
    private float[] _screenBloomFarRgb = Array.Empty<float>();
    private float[] _screenBloomScratchRgb = Array.Empty<float>();
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
    private int _rasterEmissiveMinX;
    private int _rasterEmissiveMinY;
    private int _rasterEmissiveMaxX;
    private int _rasterEmissiveMaxY;

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
        Array.Clear(_surfaceBgra, 0, _surfaceBgra.Length);
        EnsureOpaqueBackground(_surfaceBgra);
        ResetRasterEmissiveBounds();
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
        ApplyBloomIfEnabled();

        return (_surfaceWidth, _surfaceHeight, _stride, _surfaceBgra);
    }

    public void Reset()
    {
        Array.Clear(_mappedRgb, 0, _mappedRgb.Length);
        Array.Clear(_workingRgb, 0, _workingRgb.Length);
        Array.Clear(_smoothedRgb, 0, _smoothedRgb.Length);
        Array.Clear(_screenBloomSourceRgb, 0, _screenBloomSourceRgb.Length);
        Array.Clear(_screenBloomNearRgb, 0, _screenBloomNearRgb.Length);
        Array.Clear(_screenBloomFarRgb, 0, _screenBloomFarRgb.Length);
        Array.Clear(_screenBloomScratchRgb, 0, _screenBloomScratchRgb.Length);
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
        var coreOpacity = intensity > 0.0 ? Math.Clamp(0.35 + (rootIntensity * 0.65), 0.0, 1.0) : 0.0;
        var specOpacity = intensity > 0.0 ? Math.Clamp((rootIntensity * 0.45) + 0.08, 0.0, 0.65) : 0.0;

        var offR = visual.OffStateTintR;
        var offG = visual.OffStateTintG;
        var offB = visual.OffStateTintB;
        var hasOffState = visual.OffStateAlpha > 0.0001 && (offR > 0 || offG > 0 || offB > 0);
        if (!hasOffState && intensity <= 0.0)
        {
            return;
        }

        // We track a conservative raster ROI so bloom can skip untouched screen space.
        if (r > 0 || g > 0 || b > 0)
        {
            MarkRasterEmissiveBounds(originX, originY, _kernel.Size, _kernel.Size);
        }

        var litFactor = Math.Clamp(intensity, 0.0, 1.0);
        var offBlend = 1.0 - (litFactor * litFactor);

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
                var body = _kernel.Body[kernelIndex];
                var core = _kernel.Core[kernelIndex] * coreOpacity;
                var spec = _kernel.Specular[kernelIndex] * specOpacity;

                var dst = (py * _stride) + (px * 4);
                var outR = (offR * body * offBlend) + (r * core) + (255.0 * spec);
                var outG = (offG * body * offBlend) + (g * core) + (255.0 * spec);
                var outB = (offB * body * offBlend) + (b * core) + (255.0 * spec);

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

        // Flat shading writes a hard lit footprint, so capture its raster bounds up front.
        if (r > 0 || g > 0 || b > 0)
        {
            MarkRasterEmissiveBounds(originX, originY, _kernel.Size, _kernel.Size);
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
        if (target == byte.MaxValue)
        {
            _smoothedRgb[channelOffset] = byte.MaxValue;
            _workingRgb[channelOffset] = byte.MaxValue;
            return;
        }

        var delta = target - current;
        var alpha = delta >= 0 ? riseAlpha : fallAlpha;
        var next = current + ((float)alpha * delta);
        if (target == 0 && next <= TemporalSmoothingOffSnapThreshold)
        {
            next = 0f;
        }

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
        }
    }

    private void ApplyBloomIfEnabled()
    {
        if (_config is null)
        {
            return;
        }

        var bloomProfile = BloomProfileResolver.Resolve(_config.Bloom);
        // No point touching bloom buffers if both contribution lanes are effectively muted.
        if (!bloomProfile.Enabled || (bloomProfile.NearStrength <= 0.0 && bloomProfile.FarStrength <= 0.0))
        {
            return;
        }
        // If every logical LED is dark, skip bloom entirely so "off bulb" styling never glows.
        if (!HasAnyLitLed(_workingRgb))
        {
            return;
        }

        // We extract emissive energy from final rendered pixels so glow follows real on-screen proximity.
        if (!TryGetRasterEmissiveBounds(out var emissiveMinX, out var emissiveMinY, out var emissiveMaxX, out var emissiveMaxY))
        {
            return;
        }

        if (!DownsampleEmissive(_surfaceBgra, _surfaceWidth, _surfaceHeight, bloomProfile, emissiveMinX, emissiveMinY, emissiveMaxX, emissiveMaxY, out var minBloomX, out var minBloomY, out var maxBloomX, out var maxBloomY))
        {
            return;
        }

        var effectiveNearRadius = GetEffectiveBloomRadius(bloomProfile.NearRadius, bloomProfile.ScaleDivisor, _config.DotSize);
        var effectiveFarRadius = GetEffectiveBloomRadius(bloomProfile.FarRadius, bloomProfile.ScaleDivisor, _config.DotSize);
        var compositeRadius = Math.Max(effectiveNearRadius, effectiveFarRadius) + 1;
        ExpandDownsampleRoi(minBloomX, minBloomY, maxBloomX, maxBloomY, compositeRadius, _downsampleWidth, _downsampleHeight, out var compositeMinX, out var compositeMinY, out var compositeMaxX, out var compositeMaxY);
        // We always clear the full composite ROI so stale lane values can't leak as edge tint lines.
        ClearBloomRoi(_screenBloomNearRgb, _downsampleWidth, compositeMinX, compositeMinY, compositeMaxX, compositeMaxY);
        ClearBloomRoi(_screenBloomFarRgb, _downsampleWidth, compositeMinX, compositeMinY, compositeMaxX, compositeMaxY);
        // We expand blur input bounds by per-lane radius so edge taps still see neighbors outside the emissive core.
        ExpandDownsampleRoi(minBloomX, minBloomY, maxBloomX, maxBloomY, effectiveNearRadius + 1, _downsampleWidth, _downsampleHeight, out var nearMinX, out var nearMinY, out var nearMaxX, out var nearMaxY);
        ExpandDownsampleRoi(minBloomX, minBloomY, maxBloomX, maxBloomY, effectiveFarRadius + 1, _downsampleWidth, _downsampleHeight, out var farMinX, out var farMinY, out var farMaxX, out var farMaxY);
        // Keep source immutable per frame; each lane writes into its own destination buffer.
        // This avoids near/far aliasing bugs where one blur pass accidentally feeds the other lane.
        BloomBlurStrategy.BlurFromImmutableSource(_screenBloomSourceRgb, _screenBloomNearRgb, _screenBloomScratchRgb, _downsampleWidth, _downsampleHeight, effectiveNearRadius, nearMinX, nearMinY, nearMaxX, nearMaxY);
        BloomBlurStrategy.BlurFromImmutableSource(_screenBloomSourceRgb, _screenBloomFarRgb, _screenBloomScratchRgb, _downsampleWidth, _downsampleHeight, effectiveFarRadius, farMinX, farMinY, farMaxX, farMaxY);
        var effectiveNearStrength = (float)bloomProfile.NearStrength;
        var effectiveFarStrength = (float)bloomProfile.FarStrength;
        CompositeBloom(_surfaceBgra, _surfaceWidth, _surfaceHeight, _screenBloomNearRgb, _screenBloomFarRgb, _downsampleWidth, _downsampleHeight, compositeMinX, compositeMinY, compositeMaxX, compositeMaxY, effectiveNearStrength, effectiveFarStrength, bloomProfile);
    }

    private bool DownsampleEmissive(byte[] bgra, int width, int height, BloomProfile profile, int emissiveMinX, int emissiveMinY, int emissiveMaxX, int emissiveMaxY, out int minBloomX, out int minBloomY, out int maxBloomX, out int maxBloomY)
    {
        var scaleDivisor = profile.ScaleDivisor;
        _downsampleWidth = Math.Max(1, width / scaleDivisor);
        _downsampleHeight = Math.Max(1, height / scaleDivisor);
        var downsamplePixels = _downsampleWidth * _downsampleHeight * 3;
        if (_screenBloomSourceRgb.Length != downsamplePixels)
        {
            _screenBloomSourceRgb = new float[downsamplePixels];
            _screenBloomNearRgb = new float[downsamplePixels];
            _screenBloomFarRgb = new float[downsamplePixels];
            _screenBloomScratchRgb = new float[downsamplePixels];
        }

        var emissiveMinTileX = Math.Clamp(emissiveMinX / scaleDivisor, 0, _downsampleWidth - 1);
        var emissiveMinTileY = Math.Clamp(emissiveMinY / scaleDivisor, 0, _downsampleHeight - 1);
        var emissiveMaxTileX = Math.Clamp(emissiveMaxX / scaleDivisor, 0, _downsampleWidth - 1);
        var emissiveMaxTileY = Math.Clamp(emissiveMaxY / scaleDivisor, 0, _downsampleHeight - 1);
        // Separable blur can read up to 2*radius beyond the emissive core, plus one extra tile for bilinear upsample continuity.
        var maxBlurRadius = Math.Max(GetEffectiveBloomRadius(profile.NearRadius, profile.ScaleDivisor, _config?.DotSize ?? 1), GetEffectiveBloomRadius(profile.FarRadius, profile.ScaleDivisor, _config?.DotSize ?? 1));
        var blurPadding = (maxBlurRadius * 2) + 1;
        var processMinX = Math.Max(0, emissiveMinTileX - blurPadding);
        var processMinY = Math.Max(0, emissiveMinTileY - blurPadding);
        var processMaxX = Math.Min(_downsampleWidth - 1, emissiveMaxTileX + blurPadding);
        var processMaxY = Math.Min(_downsampleHeight - 1, emissiveMaxTileY + blurPadding);

        var any = false;
        minBloomX = _downsampleWidth;
        minBloomY = _downsampleHeight;
        maxBloomX = -1;
        maxBloomY = -1;
        for (var y = processMinY; y <= processMaxY; y++)
        {
            for (var x = processMinX; x <= processMaxX; x++)
            {
                var dstOffset = ((y * _downsampleWidth) + x) * 3;
                _screenBloomSourceRgb[dstOffset] = 0f;
                _screenBloomSourceRgb[dstOffset + 1] = 0f;
                _screenBloomSourceRgb[dstOffset + 2] = 0f;
                var srcStartX = x * scaleDivisor;
                var srcStartY = y * scaleDivisor;
                var srcEndX = Math.Min(width, srcStartX + scaleDivisor);
                var srcEndY = Math.Min(height, srcStartY + scaleDivisor);

                float sumR = 0f, sumG = 0f, sumB = 0f;
                var samples = 0;
                for (var py = srcStartY; py < srcEndY; py++)
                {
                    for (var px = srcStartX; px < srcEndX; px++)
                    {
                        var srcOffset = (py * width + px) * 4;
                        var b = bgra[srcOffset];
                        var g = bgra[srcOffset + 1];
                        var r = bgra[srcOffset + 2];
                        // Soft-knee thresholding gives us a gentle ramp into bloom instead of a hard cutoff.
                        var emissive = EmissiveWeight(r, g, b, profile.Threshold, profile.SoftKnee);
                        if (emissive <= 0f)
                        {
                            continue;
                        }

                        sumR += r * emissive;
                        sumG += g * emissive;
                        sumB += b * emissive;
                        samples++;
                    }
                }

                if (samples <= 0)
                {
                    continue;
                }

                var inv = 1f / samples;
                _screenBloomSourceRgb[dstOffset] = sumR * inv;
                _screenBloomSourceRgb[dstOffset + 1] = sumG * inv;
                _screenBloomSourceRgb[dstOffset + 2] = sumB * inv;
                any = true;
                minBloomX = Math.Min(minBloomX, x);
                minBloomY = Math.Min(minBloomY, y);
                maxBloomX = Math.Max(maxBloomX, x);
                maxBloomY = Math.Max(maxBloomY, y);
            }
        }
        return any;
    }

    private static void CompositeBloom(byte[] target, int width, int height, float[] nearBlur, float[] farBlur, int bloomWidth, int bloomHeight, int minBloomX, int minBloomY, int maxBloomX, int maxBloomY, float effectiveNearStrength, float effectiveFarStrength, BloomProfile profile)
    {
        var nearStrength = effectiveNearStrength;
        var farStrength = effectiveFarStrength;
        // Composite only in the expanded ROI that already accounts for effective blur radius and bilinear fringe.
        var startX = Math.Max(0, minBloomX * profile.ScaleDivisor);
        var startY = Math.Max(0, minBloomY * profile.ScaleDivisor);
        var endX = Math.Min(width - 1, ((maxBloomX + 1) * profile.ScaleDivisor) - 1);
        var endY = Math.Min(height - 1, ((maxBloomY + 1) * profile.ScaleDivisor) - 1);

        for (var y = startY; y <= endY; y++)
        {
            for (var x = startX; x <= endX; x++)
            {
                // Bilinear fetch keeps bloom smooth when upsampling from the downsampled buffers.
                var bloomU = ((x + 0.5f) / profile.ScaleDivisor) - 0.5f;
                var bloomV = ((y + 0.5f) / profile.ScaleDivisor) - 0.5f;
                var nearR = SampleBilinear(nearBlur, bloomWidth, bloomHeight, bloomU, bloomV, 0);
                var nearG = SampleBilinear(nearBlur, bloomWidth, bloomHeight, bloomU, bloomV, 1);
                var nearB = SampleBilinear(nearBlur, bloomWidth, bloomHeight, bloomU, bloomV, 2);
                var farR = SampleBilinear(farBlur, bloomWidth, bloomHeight, bloomU, bloomV, 0);
                var farG = SampleBilinear(farBlur, bloomWidth, bloomHeight, bloomU, bloomV, 1);
                var farB = SampleBilinear(farBlur, bloomWidth, bloomHeight, bloomU, bloomV, 2);

                var targetOffset = ((y * width) + x) * 4;
                target[targetOffset + 2] = (byte)Math.Clamp(target[targetOffset + 2] + (nearR * nearStrength) + (farR * farStrength), 0f, 255f);
                target[targetOffset + 1] = (byte)Math.Clamp(target[targetOffset + 1] + (nearG * nearStrength) + (farG * farStrength), 0f, 255f);
                target[targetOffset] = (byte)Math.Clamp(target[targetOffset] + (nearB * nearStrength) + (farB * farStrength), 0f, 255f);
            }
        }
    }

    private static float EmissiveWeight(float r, float g, float b, double threshold, double softKnee)
    {
        // We key bloom off peak channel brightness so saturated colors (e.g. pure green) bloom like white highlights.
        var peak = Math.Max(r, Math.Max(g, b)) / 255f;
        if (softKnee <= 0.0001)
        {
            return peak >= threshold ? 1f : 0f;
        }

        var knee = Math.Max(0.0001f, (float)softKnee);
        var t = Math.Clamp((peak - (float)threshold) / knee, 0f, 1f);
        return t * t * (3f - (2f * t));
    }

    private static bool HasAnyLitLed(float[] rgb)
    {
        for (var i = 0; i < rgb.Length; i += 3)
        {
            if (rgb[i] > 0.5f || rgb[i + 1] > 0.5f || rgb[i + 2] > 0.5f)
            {
                return true;
            }
        }

        return false;
    }

    private static int GetEffectiveBloomRadius(int configuredRadius, int scaleDivisor, int dotSize)
    {
        // Radius is interpreted as pure spill distance in screen pixels (mapped to bloom space), not dot-size inflated.
        _ = dotSize;
        _ = scaleDivisor;
        return Math.Max(0, configuredRadius);
    }

    private static void EnsureOpaqueBackground(byte[] bgra)
    {
        // We keep the whole surface opaque black so bloom in "empty" spacing pixels remains visible.
        for (var i = 3; i < bgra.Length; i += 4)
        {
            bgra[i] = 255;
        }
    }

    private static float SampleBilinear(float[] source, int width, int height, float x, float y, int channel)
    {
        var x0 = Math.Clamp((int)Math.Floor(x), 0, width - 1);
        var y0 = Math.Clamp((int)Math.Floor(y), 0, height - 1);
        var x1 = Math.Min(width - 1, x0 + 1);
        var y1 = Math.Min(height - 1, y0 + 1);
        var tx = x - x0;
        var ty = y - y0;

        var a = source[((y0 * width + x0) * 3) + channel];
        var b = source[((y0 * width + x1) * 3) + channel];
        var c = source[((y1 * width + x0) * 3) + channel];
        var d = source[((y1 * width + x1) * 3) + channel];

        var ab = a + ((b - a) * tx);
        var cd = c + ((d - c) * tx);
        return ab + ((cd - ab) * ty);
    }

    private static byte ApplyToneMap(byte channel, double brightness, double gamma, ToneMappingConfig toneMapping)
    {
        var normalized = channel / 255.0;
        var adjusted = Math.Pow(Math.Clamp(normalized, 0.0, 1.0), Math.Clamp(gamma, 0.1, 5.0));
        var scaled = adjusted * Math.Clamp(brightness, 0.0, 1.0);
        if (toneMapping.Enabled && scaled > 1.0)
        {
            var kneeStart = Math.Clamp(toneMapping.KneeStart, 1.0, 2.0);
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

    private void ResetRasterEmissiveBounds()
    {
        _rasterEmissiveMinX = _surfaceWidth;
        _rasterEmissiveMinY = _surfaceHeight;
        _rasterEmissiveMaxX = -1;
        _rasterEmissiveMaxY = -1;
    }

    private void MarkRasterEmissiveBounds(int originX, int originY, int width, int height)
    {
        var minX = Math.Clamp(originX, 0, _surfaceWidth - 1);
        var minY = Math.Clamp(originY, 0, _surfaceHeight - 1);
        var maxX = Math.Clamp(originX + width - 1, 0, _surfaceWidth - 1);
        var maxY = Math.Clamp(originY + height - 1, 0, _surfaceHeight - 1);
        if (minX > maxX || minY > maxY)
        {
            return;
        }

        _rasterEmissiveMinX = Math.Min(_rasterEmissiveMinX, minX);
        _rasterEmissiveMinY = Math.Min(_rasterEmissiveMinY, minY);
        _rasterEmissiveMaxX = Math.Max(_rasterEmissiveMaxX, maxX);
        _rasterEmissiveMaxY = Math.Max(_rasterEmissiveMaxY, maxY);
    }

    private bool TryGetRasterEmissiveBounds(out int minX, out int minY, out int maxX, out int maxY)
    {
        minX = _rasterEmissiveMinX;
        minY = _rasterEmissiveMinY;
        maxX = _rasterEmissiveMaxX;
        maxY = _rasterEmissiveMaxY;
        return maxX >= minX && maxY >= minY;
    }

    private static void ExpandDownsampleRoi(int minX, int minY, int maxX, int maxY, int expandBy, int width, int height, out int expandedMinX, out int expandedMinY, out int expandedMaxX, out int expandedMaxY)
    {
        expandedMinX = Math.Max(0, minX - expandBy);
        expandedMinY = Math.Max(0, minY - expandBy);
        expandedMaxX = Math.Min(width - 1, maxX + expandBy);
        expandedMaxY = Math.Min(height - 1, maxY + expandBy);
    }

    private static void ClearBloomRoi(float[] rgb, int width, int minX, int minY, int maxX, int maxY)
    {
        for (var y = minY; y <= maxY; y++)
        {
            var offset = ((y * width) + minX) * 3;
            var length = ((maxX - minX) + 1) * 3;
            Array.Clear(rgb, offset, length);
        }
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
            var fullRadius = Clamp01(visual.FullBrightnessRadiusMinPct);

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
                    var normalizedRadial = radial <= fullRadius
                        ? 0.0
                        : (radial - fullRadius) / Math.Max(0.0001, 1.0 - fullRadius);
                    var edge = Math.Clamp(1.0 - normalizedRadial, 0.0, 1.0);
                    body[idx] = offAlpha * ((0.25 + (0.55 * Math.Pow(edge, 0.5 + lensFalloff))) + (rim * 0.08 * (1.0 - edge)));
                    core[idx] = Math.Pow(edge, 1.1 + (lensFalloff * 1.6));

                    var hx = (x / (double)Math.Max(1, size - 1)) - 0.50;
                    var hy = (y / (double)Math.Max(1, size - 1)) - 0.35;
                    var hotspotDist2 = (hx * hx) + (hy * hy);
                    specular[idx] = Math.Exp(-hotspotDist2 / Math.Max(0.01, 0.02 + (0.12 * specHotspot))) * (0.35 + (0.55 * specHotspot));
                }
            }

            NormalizeMask(body);
            NormalizeMask(core);
            NormalizeMask(specular);

            return new DotKernel
            {
                Size = size,
                Body = body,
                Core = core,
                Specular = specular,
            };
        }

        private static void NormalizeMask(double[] mask)
        {
            var max = 0.0;
            for (var i = 0; i < mask.Length; i++)
            {
                if (mask[i] > max)
                {
                    max = mask[i];
                }
            }

            if (max <= 0.0 || max >= 1.0)
            {
                return;
            }

            for (var i = 0; i < mask.Length; i++)
            {
                if (mask[i] > 0.0)
                {
                    mask[i] /= max;
                }
            }
        }
    }
}
