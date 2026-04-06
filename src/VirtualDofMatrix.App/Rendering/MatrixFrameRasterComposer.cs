using System.Diagnostics;
using VirtualDofMatrix.App.Logging;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Rendering;

// Overview: raster composer converts RGB frame payloads into a BGRA surface and tracks dirty regions for efficient uploads.
internal sealed class MatrixFrameRasterComposer
{
    private const int HardMinimumDotSpacing = 2;
    private const float TemporalSmoothingOffSnapThreshold = 4.0f;
    private const int DirtyRectComplexityThreshold = 128;
    private const double DirtyCoverageFullUploadThreshold = 0.65;
    private static readonly TimeSpan SessionGapResetThreshold = TimeSpan.FromMilliseconds(750);
    private readonly byte[] _colorLut = new byte[256];
    private readonly RgbPlaneBuffer _mappedRgb = new();
    private readonly RgbPlaneBuffer _workingRgb = new();
    private readonly RgbPlaneBuffer _previousWorkingRgb = new();
    private readonly RgbPlaneBuffer _smoothedRgb = new();
    private readonly RgbPlaneBuffer _screenBloomSourceRgb = new();
    private readonly RgbPlaneBuffer _screenBloomNearRgb = new();
    private readonly RgbPlaneBuffer _screenBloomFarRgb = new();
    private readonly RgbPlaneBuffer _screenBloomScratchRgb = new();
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
    private int _rasterDirtyMinX;
    private int _rasterDirtyMinY;
    private int _rasterDirtyMaxX;
    private int _rasterDirtyMaxY;
    private byte[] _previousSurfaceBgra = Array.Empty<byte>();
    private bool[] _changedMatrixCells = Array.Empty<bool>();
    private readonly List<DirtyRect> _dirtyRectScratch = new();
    private int[] _logicalToRasterMap = Array.Empty<int>();
    private DateTimeOffset _lastPresentedAt = DateTimeOffset.UnixEpoch;
    private bool _forceFullFrameWrite = true;
    private const float BloomLaneStrengthEpsilon = 0.0001f;
    private ulong _bloomNearBlurPassCount;
    private ulong _bloomFarBlurPassCount;
    private ulong _bloomNearCompositePassCount;
    private ulong _bloomFarCompositePassCount;
    private long _bloomNearBlurTicks;
    private long _bloomFarBlurTicks;
    private long _bloomNearCompositeTicks;
    private long _bloomFarCompositeTicks;

    public void Configure(MatrixConfig config)
    {
        _config = config;
        _dotSpacing = Math.Max(HardMinimumDotSpacing, config.MinDotSpacing);
        _dotStride = config.DotSize + _dotSpacing;
        _surfaceWidth = (config.Width * _dotStride) + _dotSpacing;
        _surfaceHeight = (config.Height * _dotStride) + _dotSpacing;
        _stride = _surfaceWidth * 4;
        _surfaceBgra = new byte[_stride * _surfaceHeight];
        _previousSurfaceBgra = new byte[_surfaceBgra.Length];
        _forceFullFrameWrite = true;
        _kernel = DotKernel.Create(config.DotSize, config.DotShape, config.Visual);
        // Precompute logical->raster lookups once per configuration so Compose stays allocation-free and branch-light.
        _logicalToRasterMap = MatrixFrameIndexMap.BuildLogicalToRasterMap(config.Width, config.Height, config.Mapping);
    }

    public (int Width, int Height, int Stride, byte[] Pixels, IReadOnlyList<DirtyRect> DirtyRects, bool UseFullFrameWrite) Compose(FramePresentation framePresentation)
    {
        if (_config is null || _kernel is null)
        {
            throw new InvalidOperationException("Composer must be configured before composition.");
        }

        // Tables can stop/restart without recreating the renderer; reset temporal state across big gaps/session flips.
        TryResetForSessionBoundary(framePresentation.PresentedAtUtc);

        var matrixCapacity = _config.Width * _config.Height;
        EnsureWorkingBuffers(matrixCapacity);
        _mappedRgb.Clear();

        var rgb = framePresentation.RgbMemory.Span;
        var requestedLedCount = Math.Max(framePresentation.HighestLedWritten, framePresentation.LedsPerChannel);
        var ledCount = Math.Min(Math.Min(requestedLedCount, rgb.Length / 3), matrixCapacity);

        for (var logicalIndex = 0; logicalIndex < ledCount; logicalIndex++)
        {
            var rgbOffset = logicalIndex * 3;
            var mappedIndex = _logicalToRasterMap[logicalIndex];
            _mappedRgb.R[mappedIndex] = rgb[rgbOffset];
            _mappedRgb.G[mappedIndex] = rgb[rgbOffset + 1];
            _mappedRgb.B[mappedIndex] = rgb[rgbOffset + 2];
        }

        BuildColorLutIfNeeded(_config);
        ApplyColorTransforms(_config, matrixCapacity);
        EnsureChangedMatrixBuffer(matrixCapacity);
        Array.Clear(_changedMatrixCells, 0, matrixCapacity);
        ResetRasterEmissiveBounds();
        ResetRasterDirtyBounds();

        var hasChangedCells = false;
        for (var matrixIndex = 0; matrixIndex < matrixCapacity; matrixIndex++)
        {
            if (!HasMatrixCellChanged(matrixIndex))
            {
                continue;
            }

            _changedMatrixCells[matrixIndex] = true;
            hasChangedCells = true;
        }

        var fullFrameRaster = _forceFullFrameWrite;
        // Conversational note: unchanged frames are common in idle tables, so we skip all raster work when nothing moved.
        if (!fullFrameRaster && !hasChangedCells)
        {
            CopyCurrentWorkingToPrevious(matrixCapacity);
            return (_surfaceWidth, _surfaceHeight, _stride, _surfaceBgra, Array.Empty<DirtyRect>(), false);
        }

        if (fullFrameRaster)
        {
            Array.Clear(_surfaceBgra, 0, _surfaceBgra.Length);
            EnsureOpaqueBackground(_surfaceBgra);
        }
        else
        {
            MarkChangedCellsDirtyBoundsWithBloomMargin();
            if (TryGetRasterDirtyBounds(out var dirtyMinX, out var dirtyMinY, out var dirtyMaxX, out var dirtyMaxY))
            {
                ClearSurfaceRegion(_surfaceBgra, dirtyMinX, dirtyMinY, dirtyMaxX, dirtyMaxY);
            }
        }

        for (var y = 0; y < _config.Height; y++)
        {
            for (var x = 0; x < _config.Width; x++)
            {
                var matrixIndex = (y * _config.Width) + x;
                var r = ToByte(_workingRgb.R[matrixIndex] / 255.0);
                var g = ToByte(_workingRgb.G[matrixIndex] / 255.0);
                var b = ToByte(_workingRgb.B[matrixIndex] / 255.0);
                var intensity = Math.Max(r, Math.Max(g, b)) / 255.0;
                if (fullFrameRaster || MatrixCellIntersectsDirtyBounds(x, y))
                {
                    var dstX = _dotSpacing + (x * _dotStride);
                    var dstY = _dotSpacing + (y * _dotStride);
                    RasterDot(dstX, dstY, r, g, b, intensity, _config.Visual);
                }
            }
        }
        ApplyBloomIfEnabled();

        IReadOnlyList<DirtyRect> dirtyRects = Array.Empty<DirtyRect>();
        var fullFrameWrite = _forceFullFrameWrite || !TryBuildDirtyRects(out dirtyRects);
#if DEBUG
        if (!fullFrameWrite && DebugHasUncoveredPixelDiffs(dirtyRects))
        {
            // Debug guardrail: if ROI strips miss anything (for example bloom spill), safely fall back.
            dirtyRects = Array.Empty<DirtyRect>();
            fullFrameWrite = true;
        }
#endif
        _forceFullFrameWrite = false;
        Buffer.BlockCopy(_surfaceBgra, 0, _previousSurfaceBgra, 0, _surfaceBgra.Length);
        CopyCurrentWorkingToPrevious(matrixCapacity);
        return (_surfaceWidth, _surfaceHeight, _stride, _surfaceBgra, dirtyRects, fullFrameWrite);
    }

    public void Reset()
    {
        _mappedRgb.Clear();
        _workingRgb.Clear();
        _smoothedRgb.Clear();
        _previousWorkingRgb.Clear();
        _screenBloomSourceRgb.Clear();
        _screenBloomNearRgb.Clear();
        _screenBloomFarRgb.Clear();
        _screenBloomScratchRgb.Clear();
        if (_previousSurfaceBgra.Length > 0)
        {
            Array.Clear(_previousSurfaceBgra, 0, _previousSurfaceBgra.Length);
        }
        _dirtyRectScratch.Clear();
        _lastPresentedAt = DateTimeOffset.UnixEpoch;
        _forceFullFrameWrite = true;
    }

    private void TryResetForSessionBoundary(DateTimeOffset presentedAt)
    {
        if (_lastPresentedAt == DateTimeOffset.UnixEpoch)
        {
            _lastPresentedAt = presentedAt;
            return;
        }

        var movedBackward = presentedAt < _lastPresentedAt;
        var gapExceeded = presentedAt - _lastPresentedAt > SessionGapResetThreshold;
        _lastPresentedAt = presentedAt;
        if (!movedBackward && !gapExceeded)
        {
            return;
        }

        // Reset only temporal/raster history so new sessions start clean without "stuck dim" carry-over.
        _mappedRgb.Clear();
        _workingRgb.Clear();
        _smoothedRgb.Clear();
        _previousWorkingRgb.Clear();
        _dirtyRectScratch.Clear();
        if (_previousSurfaceBgra.Length > 0)
        {
            Array.Clear(_previousSurfaceBgra, 0, _previousSurfaceBgra.Length);
        }

        _forceFullFrameWrite = true;
    }

    private bool TryBuildDirtyRects(out IReadOnlyList<DirtyRect> dirtyRects)
    {
        _dirtyRectScratch.Clear();
        if (_previousSurfaceBgra.Length != _surfaceBgra.Length)
        {
            dirtyRects = Array.Empty<DirtyRect>();
            return false;
        }

        if (!TryGetRasterDirtyBounds(out _, out var dirtyMinY, out _, out var dirtyMaxY))
        {
            dirtyRects = Array.Empty<DirtyRect>();
            return true;
        }

        if (!TryGetRasterDirtyBounds(out var dirtyMinX, out _, out var dirtyMaxX, out _))
        {
            dirtyRects = Array.Empty<DirtyRect>();
            return true;
        }

        var totalPixels = _surfaceWidth * _surfaceHeight;
        var dirtyWidth = (dirtyMaxX - dirtyMinX) + 1;
        var dirtyHeight = (dirtyMaxY - dirtyMinY) + 1;
        var dirtyPixels = dirtyWidth * dirtyHeight;

        if (dirtyWidth <= 0 || dirtyHeight <= 0)
        {
            dirtyRects = Array.Empty<DirtyRect>();
            return true;
        }

        _dirtyRectScratch.Add(new DirtyRect(dirtyMinX, dirtyMinY, dirtyWidth, dirtyHeight));
        if (dirtyPixels >= (int)(totalPixels * DirtyCoverageFullUploadThreshold))
        {
            dirtyRects = Array.Empty<DirtyRect>();
            return false;
        }

        dirtyRects = _dirtyRectScratch.ToArray();
        return true;
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
            ApplyChannelTransform(_mappedRgb.R, _smoothedRgb.R, _workingRgb.R, i, riseAlpha, fallAlpha, smoothingEnabled);
            ApplyChannelTransform(_mappedRgb.G, _smoothedRgb.G, _workingRgb.G, i, riseAlpha, fallAlpha, smoothingEnabled);
            ApplyChannelTransform(_mappedRgb.B, _smoothedRgb.B, _workingRgb.B, i, riseAlpha, fallAlpha, smoothingEnabled);
        }
    }

    private void ApplyChannelTransform(float[] mappedChannel, float[] smoothedChannel, float[] workingChannel, int pixelIndex, double riseAlpha, double fallAlpha, bool smoothingEnabled)
    {
        var target = _colorLut[(byte)mappedChannel[pixelIndex]];
        if (!smoothingEnabled)
        {
            smoothedChannel[pixelIndex] = target;
            workingChannel[pixelIndex] = target;
            return;
        }

        var current = smoothedChannel[pixelIndex];
        if (target == byte.MaxValue)
        {
            smoothedChannel[pixelIndex] = byte.MaxValue;
            workingChannel[pixelIndex] = byte.MaxValue;
            return;
        }

        var delta = target - current;
        var alpha = delta >= 0 ? riseAlpha : fallAlpha;
        var next = current + ((float)alpha * delta);
        if (target == 0 && next <= TemporalSmoothingOffSnapThreshold)
        {
            next = 0f;
        }

        smoothedChannel[pixelIndex] = next;
        workingChannel[pixelIndex] = next;
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
        if (_workingRgb.Length != matrixCapacity)
        {
            _mappedRgb.EnsureSize(matrixCapacity);
            _workingRgb.EnsureSize(matrixCapacity);
            _previousWorkingRgb.EnsureSize(matrixCapacity);
            _smoothedRgb.EnsureSize(matrixCapacity);
        }
    }

    private void EnsureChangedMatrixBuffer(int matrixCapacity)
    {
        if (_changedMatrixCells.Length != matrixCapacity)
        {
            _changedMatrixCells = new bool[matrixCapacity];
        }
    }

    private bool HasMatrixCellChanged(int matrixIndex)
    {
        // Any per-channel delta means we touched this dot footprint this frame.
        const float epsilon = 0.5f;
        return Math.Abs(_workingRgb.R[matrixIndex] - _previousWorkingRgb.R[matrixIndex]) >= epsilon ||
               Math.Abs(_workingRgb.G[matrixIndex] - _previousWorkingRgb.G[matrixIndex]) >= epsilon ||
               Math.Abs(_workingRgb.B[matrixIndex] - _previousWorkingRgb.B[matrixIndex]) >= epsilon;
    }

    private void CopyCurrentWorkingToPrevious(int matrixCapacity)
    {
        Array.Copy(_workingRgb.R, _previousWorkingRgb.R, matrixCapacity);
        Array.Copy(_workingRgb.G, _previousWorkingRgb.G, matrixCapacity);
        Array.Copy(_workingRgb.B, _previousWorkingRgb.B, matrixCapacity);
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
        // Conversational note: lane activity is keyed off effective radius + non-trivial strength so no-op lanes skip all work.
        var nearActive = effectiveNearRadius > 0 && bloomProfile.NearStrength > BloomLaneStrengthEpsilon;
        var farActive = effectiveFarRadius > 0 && bloomProfile.FarStrength > BloomLaneStrengthEpsilon;
        if (!nearActive && !farActive)
        {
            return;
        }

        var compositeRadius = Math.Max(effectiveNearRadius, effectiveFarRadius) + 1;
        ExpandDownsampleRoi(minBloomX, minBloomY, maxBloomX, maxBloomY, compositeRadius, _downsampleWidth, _downsampleHeight, out var compositeMinX, out var compositeMinY, out var compositeMaxX, out var compositeMaxY);
        // We always clear the full composite ROI so stale lane values can't leak as edge tint lines.
        if (nearActive)
        {
            ClearBloomRoi(_screenBloomNearRgb, _downsampleWidth, compositeMinX, compositeMinY, compositeMaxX, compositeMaxY);
        }

        if (farActive)
        {
            ClearBloomRoi(_screenBloomFarRgb, _downsampleWidth, compositeMinX, compositeMinY, compositeMaxX, compositeMaxY);
        }

        // We expand blur input bounds by per-lane radius so edge taps still see neighbors outside the emissive core.
        // Keep source immutable per frame; each lane writes into its own destination buffer.
        // This avoids near/far aliasing bugs where one blur pass accidentally feeds the other lane.
        if (nearActive)
        {
            ExpandDownsampleRoi(minBloomX, minBloomY, maxBloomX, maxBloomY, effectiveNearRadius + 1, _downsampleWidth, _downsampleHeight, out var nearMinX, out var nearMinY, out var nearMaxX, out var nearMaxY);
            var nearBlurStart = Stopwatch.GetTimestamp();
            BloomBlurStrategy.BlurFromImmutableSource(_screenBloomSourceRgb, _screenBloomNearRgb, _screenBloomScratchRgb, _downsampleWidth, _downsampleHeight, effectiveNearRadius, nearMinX, nearMinY, nearMaxX, nearMaxY);
            _bloomNearBlurTicks += Stopwatch.GetTimestamp() - nearBlurStart;
            _bloomNearBlurPassCount++;
        }

        if (farActive)
        {
            ExpandDownsampleRoi(minBloomX, minBloomY, maxBloomX, maxBloomY, effectiveFarRadius + 1, _downsampleWidth, _downsampleHeight, out var farMinX, out var farMinY, out var farMaxX, out var farMaxY);
            var farBlurStart = Stopwatch.GetTimestamp();
            BloomBlurStrategy.BlurFromImmutableSource(_screenBloomSourceRgb, _screenBloomFarRgb, _screenBloomScratchRgb, _downsampleWidth, _downsampleHeight, effectiveFarRadius, farMinX, farMinY, farMaxX, farMaxY);
            _bloomFarBlurTicks += Stopwatch.GetTimestamp() - farBlurStart;
            _bloomFarBlurPassCount++;
        }

        var effectiveNearStrength = (float)bloomProfile.NearStrength;
        var effectiveFarStrength = (float)bloomProfile.FarStrength;
        var compositeStart = Stopwatch.GetTimestamp();
        if (nearActive && farActive)
        {
            CompositeBloom(_surfaceBgra, _surfaceWidth, _surfaceHeight, _screenBloomNearRgb, _screenBloomFarRgb, _downsampleWidth, _downsampleHeight, compositeMinX, compositeMinY, compositeMaxX, compositeMaxY, effectiveNearStrength, effectiveFarStrength, bloomProfile);
            _bloomNearCompositePassCount++;
            _bloomFarCompositePassCount++;
        }
        else if (nearActive)
        {
            CompositeBloomSingleLane(_surfaceBgra, _surfaceWidth, _surfaceHeight, _screenBloomNearRgb, _downsampleWidth, _downsampleHeight, compositeMinX, compositeMinY, compositeMaxX, compositeMaxY, effectiveNearStrength, bloomProfile);
            _bloomNearCompositePassCount++;
        }
        else
        {
            CompositeBloomSingleLane(_surfaceBgra, _surfaceWidth, _surfaceHeight, _screenBloomFarRgb, _downsampleWidth, _downsampleHeight, compositeMinX, compositeMinY, compositeMaxX, compositeMaxY, effectiveFarStrength, bloomProfile);
            _bloomFarCompositePassCount++;
        }

        var compositeTicks = Stopwatch.GetTimestamp() - compositeStart;
        if (nearActive)
        {
            _bloomNearCompositeTicks += compositeTicks;
        }

        if (farActive)
        {
            _bloomFarCompositeTicks += compositeTicks;
        }

        LogBloomLaneCountersIfNeeded();
    }

    private void LogBloomLaneCountersIfNeeded()
    {
        if (ShouldLogBloomCounter(_bloomNearBlurPassCount))
        {
            var avgNearBlurMs = ToMilliseconds(_bloomNearBlurTicks, _bloomNearBlurPassCount);
            var avgNearCompositeMs = ToMilliseconds(_bloomNearCompositeTicks, _bloomNearCompositePassCount);
            AppLogger.Info($"[composer] bloom near lane blurPasses={_bloomNearBlurPassCount} compositePasses={_bloomNearCompositePassCount} avgBlurMs={avgNearBlurMs:F4} avgCompositeMs={avgNearCompositeMs:F4}");
        }

        if (ShouldLogBloomCounter(_bloomFarBlurPassCount))
        {
            var avgFarBlurMs = ToMilliseconds(_bloomFarBlurTicks, _bloomFarBlurPassCount);
            var avgFarCompositeMs = ToMilliseconds(_bloomFarCompositeTicks, _bloomFarCompositePassCount);
            AppLogger.Info($"[composer] bloom far lane blurPasses={_bloomFarBlurPassCount} compositePasses={_bloomFarCompositePassCount} avgBlurMs={avgFarBlurMs:F4} avgCompositeMs={avgFarCompositeMs:F4}");
        }
    }

    private static bool ShouldLogBloomCounter(ulong count) => count > 0 && (count <= 3 || (count & (count - 1)) == 0);

    private static double ToMilliseconds(long ticks, ulong count)
    {
        if (count == 0 || ticks <= 0)
        {
            return 0;
        }

        return (ticks * 1000.0 / Stopwatch.Frequency) / count;
    }

    private bool DownsampleEmissive(byte[] bgra, int width, int height, BloomProfile profile, int emissiveMinX, int emissiveMinY, int emissiveMaxX, int emissiveMaxY, out int minBloomX, out int minBloomY, out int maxBloomX, out int maxBloomY)
    {
        var scaleDivisor = profile.ScaleDivisor;
        _downsampleWidth = Math.Max(1, width / scaleDivisor);
        _downsampleHeight = Math.Max(1, height / scaleDivisor);
        var downsamplePixels = _downsampleWidth * _downsampleHeight;
        if (_screenBloomSourceRgb.Length != downsamplePixels)
        {
            _screenBloomSourceRgb.EnsureSize(downsamplePixels);
            _screenBloomNearRgb.EnsureSize(downsamplePixels);
            _screenBloomFarRgb.EnsureSize(downsamplePixels);
            _screenBloomScratchRgb.EnsureSize(downsamplePixels);
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
                var dstOffset = (y * _downsampleWidth) + x;
                _screenBloomSourceRgb.R[dstOffset] = 0f;
                _screenBloomSourceRgb.G[dstOffset] = 0f;
                _screenBloomSourceRgb.B[dstOffset] = 0f;
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
                _screenBloomSourceRgb.R[dstOffset] = sumR * inv;
                _screenBloomSourceRgb.G[dstOffset] = sumG * inv;
                _screenBloomSourceRgb.B[dstOffset] = sumB * inv;
                any = true;
                minBloomX = Math.Min(minBloomX, x);
                minBloomY = Math.Min(minBloomY, y);
                maxBloomX = Math.Max(maxBloomX, x);
                maxBloomY = Math.Max(maxBloomY, y);
            }
        }
        return any;
    }

    private static void CompositeBloom(byte[] target, int width, int height, RgbPlaneBuffer nearBlur, RgbPlaneBuffer farBlur, int bloomWidth, int bloomHeight, int minBloomX, int minBloomY, int maxBloomX, int maxBloomY, float effectiveNearStrength, float effectiveFarStrength, BloomProfile profile)
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
                var near = SampleBilinear(nearBlur, bloomWidth, bloomHeight, bloomU, bloomV);
                var far = SampleBilinear(farBlur, bloomWidth, bloomHeight, bloomU, bloomV);

                var targetOffset = ((y * width) + x) * 4;
                target[targetOffset + 2] = (byte)Math.Clamp(target[targetOffset + 2] + (near.R * nearStrength) + (far.R * farStrength), 0f, 255f);
                target[targetOffset + 1] = (byte)Math.Clamp(target[targetOffset + 1] + (near.G * nearStrength) + (far.G * farStrength), 0f, 255f);
                target[targetOffset] = (byte)Math.Clamp(target[targetOffset] + (near.B * nearStrength) + (far.B * farStrength), 0f, 255f);
            }
        }
    }

    private static void CompositeBloomSingleLane(byte[] target, int width, int height, RgbPlaneBuffer laneBlur, int bloomWidth, int bloomHeight, int minBloomX, int minBloomY, int maxBloomX, int maxBloomY, float effectiveStrength, BloomProfile profile)
    {
        var strength = effectiveStrength;
        var startX = Math.Max(0, minBloomX * profile.ScaleDivisor);
        var startY = Math.Max(0, minBloomY * profile.ScaleDivisor);
        var endX = Math.Min(width - 1, ((maxBloomX + 1) * profile.ScaleDivisor) - 1);
        var endY = Math.Min(height - 1, ((maxBloomY + 1) * profile.ScaleDivisor) - 1);

        for (var y = startY; y <= endY; y++)
        {
            for (var x = startX; x <= endX; x++)
            {
                // Conversational note: single-lane composite avoids bilinear fetch + sum for a lane that was never rendered.
                var bloomU = ((x + 0.5f) / profile.ScaleDivisor) - 0.5f;
                var bloomV = ((y + 0.5f) / profile.ScaleDivisor) - 0.5f;
                var lane = SampleBilinear(laneBlur, bloomWidth, bloomHeight, bloomU, bloomV);
                var targetOffset = ((y * width) + x) * 4;
                target[targetOffset + 2] = (byte)Math.Clamp(target[targetOffset + 2] + (lane.R * strength), 0f, 255f);
                target[targetOffset + 1] = (byte)Math.Clamp(target[targetOffset + 1] + (lane.G * strength), 0f, 255f);
                target[targetOffset] = (byte)Math.Clamp(target[targetOffset] + (lane.B * strength), 0f, 255f);
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

    private static bool HasAnyLitLed(RgbPlaneBuffer rgb)
    {
        for (var i = 0; i < rgb.Length; i++)
        {
            if (rgb.R[i] > 0.5f || rgb.G[i] > 0.5f || rgb.B[i] > 0.5f)
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

    private static RgbSample SampleBilinear(RgbPlaneBuffer source, int width, int height, float x, float y)
    {
        var x0 = Math.Clamp((int)Math.Floor(x), 0, width - 1);
        var y0 = Math.Clamp((int)Math.Floor(y), 0, height - 1);
        var x1 = Math.Min(width - 1, x0 + 1);
        var y1 = Math.Min(height - 1, y0 + 1);
        var tx = x - x0;
        var ty = y - y0;

        var topLeft = (y0 * width) + x0;
        var topRight = (y0 * width) + x1;
        var bottomLeft = (y1 * width) + x0;
        var bottomRight = (y1 * width) + x1;

        // We run RGB through one SIMD batch so interpolation arithmetic stays in lockstep per channel.
        Span<float> a = stackalloc float[System.Numerics.Vector<float>.Count];
        Span<float> b = stackalloc float[System.Numerics.Vector<float>.Count];
        Span<float> c = stackalloc float[System.Numerics.Vector<float>.Count];
        Span<float> d = stackalloc float[System.Numerics.Vector<float>.Count];
        a[0] = source.R[topLeft];
        a[1] = source.G[topLeft];
        a[2] = source.B[topLeft];
        b[0] = source.R[topRight];
        b[1] = source.G[topRight];
        b[2] = source.B[topRight];
        c[0] = source.R[bottomLeft];
        c[1] = source.G[bottomLeft];
        c[2] = source.B[bottomLeft];
        d[0] = source.R[bottomRight];
        d[1] = source.G[bottomRight];
        d[2] = source.B[bottomRight];

        var va = new System.Numerics.Vector<float>(a);
        var vb = new System.Numerics.Vector<float>(b);
        var vc = new System.Numerics.Vector<float>(c);
        var vd = new System.Numerics.Vector<float>(d);
        var vtx = new System.Numerics.Vector<float>(tx);
        var vty = new System.Numerics.Vector<float>(ty);
        var vab = va + ((vb - va) * vtx);
        var vcd = vc + ((vd - vc) * vtx);
        var sample = vab + ((vcd - vab) * vty);
        return new RgbSample(sample[0], sample[1], sample[2]);
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

    private void ResetRasterDirtyBounds()
    {
        _rasterDirtyMinX = _surfaceWidth;
        _rasterDirtyMinY = _surfaceHeight;
        _rasterDirtyMaxX = -1;
        _rasterDirtyMaxY = -1;
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

    private void MarkRasterDirtyBounds(int originX, int originY, int width, int height)
    {
        var minX = Math.Clamp(originX, 0, _surfaceWidth - 1);
        var minY = Math.Clamp(originY, 0, _surfaceHeight - 1);
        var maxX = Math.Clamp(originX + width - 1, 0, _surfaceWidth - 1);
        var maxY = Math.Clamp(originY + height - 1, 0, _surfaceHeight - 1);
        if (minX > maxX || minY > maxY)
        {
            return;
        }

        _rasterDirtyMinX = Math.Min(_rasterDirtyMinX, minX);
        _rasterDirtyMinY = Math.Min(_rasterDirtyMinY, minY);
        _rasterDirtyMaxX = Math.Max(_rasterDirtyMaxX, maxX);
        _rasterDirtyMaxY = Math.Max(_rasterDirtyMaxY, maxY);
    }

    private bool TryGetRasterEmissiveBounds(out int minX, out int minY, out int maxX, out int maxY)
    {
        minX = _rasterEmissiveMinX;
        minY = _rasterEmissiveMinY;
        maxX = _rasterEmissiveMaxX;
        maxY = _rasterEmissiveMaxY;
        return maxX >= minX && maxY >= minY;
    }

    private bool TryGetRasterDirtyBounds(out int minX, out int minY, out int maxX, out int maxY)
    {
        minX = _rasterDirtyMinX;
        minY = _rasterDirtyMinY;
        maxX = _rasterDirtyMaxX;
        maxY = _rasterDirtyMaxY;
        return maxX >= minX && maxY >= minY;
    }

#if DEBUG
    private bool DebugHasUncoveredPixelDiffs(IReadOnlyList<DirtyRect> dirtyRects)
    {
        // Debug-only full compare: verify ROI strips cover every changed pixel without paying release costs.
        var coverage = new bool[_surfaceWidth * _surfaceHeight];
        foreach (var rect in dirtyRects)
        {
            for (var y = rect.Y; y < rect.Y + rect.Height; y++)
            {
                var rowOffset = y * _surfaceWidth;
                for (var x = rect.X; x < rect.X + rect.Width; x++)
                {
                    coverage[rowOffset + x] = true;
                }
            }
        }

        for (var y = 0; y < _surfaceHeight; y++)
        {
            var rowStart = y * _stride;
            var coverRowStart = y * _surfaceWidth;
            for (var x = 0; x < _surfaceWidth; x++)
            {
                var pixelOffset = rowStart + (x * 4);
                var changed =
                    _surfaceBgra[pixelOffset] != _previousSurfaceBgra[pixelOffset] ||
                    _surfaceBgra[pixelOffset + 1] != _previousSurfaceBgra[pixelOffset + 1] ||
                    _surfaceBgra[pixelOffset + 2] != _previousSurfaceBgra[pixelOffset + 2] ||
                    _surfaceBgra[pixelOffset + 3] != _previousSurfaceBgra[pixelOffset + 3];
                if (changed && !coverage[coverRowStart + x])
                {
                    return true;
                }
            }
        }

        return false;
    }
#endif

    private static void ExpandDownsampleRoi(int minX, int minY, int maxX, int maxY, int expandBy, int width, int height, out int expandedMinX, out int expandedMinY, out int expandedMaxX, out int expandedMaxY)
    {
        expandedMinX = Math.Max(0, minX - expandBy);
        expandedMinY = Math.Max(0, minY - expandBy);
        expandedMaxX = Math.Min(width - 1, maxX + expandBy);
        expandedMaxY = Math.Min(height - 1, maxY + expandBy);
    }

    private static void ClearBloomRoi(RgbPlaneBuffer rgb, int width, int minX, int minY, int maxX, int maxY)
    {
        for (var y = minY; y <= maxY; y++)
        {
            var offset = (y * width) + minX;
            var length = (maxX - minX) + 1;
            Array.Clear(rgb.R, offset, length);
            Array.Clear(rgb.G, offset, length);
            Array.Clear(rgb.B, offset, length);
        }
    }

    private readonly record struct RgbSample(float R, float G, float B);

    private void MarkChangedCellsDirtyBoundsWithBloomMargin()
    {
        if (_config is null || _kernel is null)
        {
            return;
        }

        // We invalidate around changed dots so local bloom spill and off-state fades don't leave stale pixels behind.
        var localMargin = ComputeBloomInvalidationMarginPixels();
        for (var y = 0; y < _config.Height; y++)
        {
            for (var x = 0; x < _config.Width; x++)
            {
                var matrixIndex = (y * _config.Width) + x;
                if (!_changedMatrixCells[matrixIndex])
                {
                    continue;
                }

                var dstX = _dotSpacing + (x * _dotStride);
                var dstY = _dotSpacing + (y * _dotStride);
                MarkRasterDirtyBounds(dstX - localMargin, dstY - localMargin, _kernel.Size + (localMargin * 2), _kernel.Size + (localMargin * 2));
            }
        }
    }

    private int ComputeBloomInvalidationMarginPixels()
    {
        if (_config is null)
        {
            return 0;
        }

        var profile = BloomProfileResolver.Resolve(_config.Bloom);
        if (!profile.Enabled)
        {
            return 0;
        }

        var maxRadius = Math.Max(profile.NearRadius, profile.FarRadius);
        if (maxRadius <= 0)
        {
            return 0;
        }

        return (maxRadius + 2) * Math.Max(1, profile.ScaleDivisor);
    }

    private bool MatrixCellIntersectsDirtyBounds(int matrixX, int matrixY)
    {
        if (_kernel is null)
        {
            return false;
        }

        if (!TryGetRasterDirtyBounds(out var minX, out var minY, out var maxX, out var maxY))
        {
            return false;
        }

        var cellMinX = _dotSpacing + (matrixX * _dotStride);
        var cellMinY = _dotSpacing + (matrixY * _dotStride);
        var cellMaxX = cellMinX + _kernel.Size - 1;
        var cellMaxY = cellMinY + _kernel.Size - 1;
        return cellMaxX >= minX && cellMinX <= maxX && cellMaxY >= minY && cellMinY <= maxY;
    }

    private void ClearSurfaceRegion(byte[] surface, int minX, int minY, int maxX, int maxY)
    {
        var clampedMinX = Math.Clamp(minX, 0, _surfaceWidth - 1);
        var clampedMinY = Math.Clamp(minY, 0, _surfaceHeight - 1);
        var clampedMaxX = Math.Clamp(maxX, 0, _surfaceWidth - 1);
        var clampedMaxY = Math.Clamp(maxY, 0, _surfaceHeight - 1);
        if (clampedMinX > clampedMaxX || clampedMinY > clampedMaxY)
        {
            return;
        }

        for (var y = clampedMinY; y <= clampedMaxY; y++)
        {
            var rowStart = (y * _stride) + (clampedMinX * 4);
            var rowLength = ((clampedMaxX - clampedMinX) + 1) * 4;
            Array.Clear(surface, rowStart, rowLength);
            for (var alphaOffset = rowStart + 3; alphaOffset < rowStart + rowLength; alphaOffset += 4)
            {
                surface[alphaOffset] = 255;
            }
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

internal readonly record struct DirtyRect(int X, int Y, int Width, int Height);
