using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using VirtualDofMatrix.Core;
using static Vortice.Direct3D11.D3D11;

namespace VirtualDofMatrix.App.Rendering;

public sealed class GpuInstancedMatrixRenderer : IMatrixRenderer
{
    private const int Channels = 3;
    private const float TemporalSmoothingOffSnapThreshold = 1.0f;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11Buffer? _instanceBuffer;
    private ID3D11Texture2D? _frameTexture;
    private ID3D11ShaderResourceView? _frameSrv;
    private readonly object _gate = new();
    private Image? _host;
    private WriteableBitmap? _fallbackBitmap;
    private FramePresentation _stagedFrame = new(Array.Empty<byte>(), 0, 0, 0, DateTimeOffset.UnixEpoch);
    private int[] _logicalToRaster = Array.Empty<int>();
    private DotStyleConfig? _style;
    private int _width;
    private int _height;
    private int _surfaceWidth;
    private int _surfaceHeight;
    private int _dotStride;
    private int _dotSize;
    private int _dotPadding;
    private float[] _dotBodyMask = Array.Empty<float>();
    private float[] _dotCoreMask = Array.Empty<float>();
    private float[] _dotSpecularMask = Array.Empty<float>();
    private byte[] _bgra = Array.Empty<byte>();
    private float[] _smoothedRgb = Array.Empty<float>();
    private float[] _workingRgb = Array.Empty<float>();
    private float[] _thresholdRgb = Array.Empty<float>();
    private float[] _smallBlurRgb = Array.Empty<float>();
    private float[] _wideBlurRgb = Array.Empty<float>();
    private float[] _blurScratchRgb = Array.Empty<float>();
    private int _downsampleWidth;
    private int _downsampleHeight;
    private readonly byte[] _toneMapLut = new byte[256];
    private double _lutBrightness = double.NaN;
    private double _lutGamma = double.NaN;
    private bool _lutToneEnabled;
    private double _lutKneeStart = double.NaN;
    private double _lutToneStrength = double.NaN;

    public string BackendName => "gpu";

    public bool UsesImageHost => true;

    public void Initialize(MatrixRendererSurface renderSurface, int width, int height, DotStyleConfig dotStyleConfig)
    {
        DisposeDeviceResources();
        _host = renderSurface.BitmapHost;
        _host.Source = null;
        renderSurface.PrimitiveCanvas.Children.Clear();

        _width = width;
        _height = height;
        _style = dotStyleConfig;
        _logicalToRaster = MatrixFrameIndexMap.BuildLogicalToRasterMap(width, height, dotStyleConfig.Mapping);
        ConfigureDotSurface(dotStyleConfig, width, height);
        _bgra = new byte[checked(_surfaceWidth * _surfaceHeight * 4)];
        _smoothedRgb = new float[checked(width * height * Channels)];
        _workingRgb = new float[checked(width * height * Channels)];
        _thresholdRgb = new float[checked(width * height * Channels)];
        _smallBlurRgb = Array.Empty<float>();
        _wideBlurRgb = Array.Empty<float>();
        _blurScratchRgb = Array.Empty<float>();
        _downsampleWidth = 0;
        _downsampleHeight = 0;

        var hr = D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_0 },
            out _device,
            out _,
            out _context);

        if (hr.Failure || _device is null || _context is null)
        {
            throw new InvalidOperationException($"GPU renderer initialization failed: HRESULT=0x{hr.Code:X8}");
        }

        var instanceData = new uint[checked(width * height)];
        for (var i = 0; i < instanceData.Length; i++)
        {
            instanceData[i] = (uint)i;
        }

        var byteWidth = (uint)(instanceData.Length * sizeof(uint));
        _instanceBuffer = _device.CreateBuffer(new BufferDescription(byteWidth, BindFlags.VertexBuffer, ResourceUsage.Default));

        _frameTexture = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Math.Max(1, _surfaceWidth),
            Height = (uint)Math.Max(1, _surfaceHeight),
            ArraySize = 1,
            MipLevels = 1,
            Format = Format.R8G8B8A8_UNorm,
            BindFlags = BindFlags.ShaderResource,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Dynamic,
            CPUAccessFlags = CpuAccessFlags.Write,
        });

        _frameSrv = _device.CreateShaderResourceView(_frameTexture);
        _fallbackBitmap = new WriteableBitmap(_surfaceWidth, _surfaceHeight, 96, 96, PixelFormats.Bgra32, null);
        _host.Source = _fallbackBitmap;
        _host.Stretch = Stretch.Fill;

        Console.WriteLine($"[renderer] gpu initialized fastpath leds={width * height} surface={_surfaceWidth}x{_surfaceHeight}");
        var bloomProfile = BloomProfileResolver.Resolve(dotStyleConfig.Bloom);
        Console.WriteLine($"[renderer] gpu bloom enabled={bloomProfile.Enabled} preset={dotStyleConfig.Bloom.QualityPreset} threshold={bloomProfile.Threshold:F2} scale={bloomProfile.ScaleDivisor} smallRadius={bloomProfile.SmallRadius} wideRadius={bloomProfile.WideRadius} smallStrength={bloomProfile.SmallStrength:F2} wideStrength={bloomProfile.WideStrength:F2}");
    }

    public void UpdateFrame(FramePresentation presentation)
    {
        lock (_gate)
        {
            _stagedFrame = presentation;
        }
    }

    public void Resize(double viewportWidth, double viewportHeight)
    {
    }

    public void Render()
    {
        if (_context is null || _frameTexture is null || _fallbackBitmap is null || _style is null)
        {
            return;
        }

        FramePresentation frame;
        lock (_gate)
        {
            frame = _stagedFrame;
        }

        BuildToneMapLutIfNeeded(_style);
        Array.Clear(_bgra, 0, _bgra.Length);
        Array.Clear(_workingRgb, 0, _workingRgb.Length);

        var rgb = frame.RgbMemory.Span;
        var ledCount = Math.Min(_logicalToRaster.Length, rgb.Length / Channels);
        var smoothing = _style.TemporalSmoothing;
        var smoothingEnabled = smoothing.Enabled;
        var rise = (float)Math.Clamp(smoothing.RiseAlpha, 0.0, 1.0);
        var fall = (float)Math.Clamp(smoothing.FallAlpha, 0.0, 1.0);

        for (var logical = 0; logical < ledCount; logical++)
        {
            var src = logical * Channels;
            var smoothingOffset = logical * Channels;
            var r = ApplySmoothing(smoothingOffset, _toneMapLut[rgb[src]], rise, fall, smoothingEnabled);
            var g = ApplySmoothing(smoothingOffset + 1, _toneMapLut[rgb[src + 1]], rise, fall, smoothingEnabled);
            var b = ApplySmoothing(smoothingOffset + 2, _toneMapLut[rgb[src + 2]], rise, fall, smoothingEnabled);

            var raster = _logicalToRaster[logical];
            if ((uint)raster >= (uint)(_width * _height))
            {
                continue;
            }

            var rasterOffset = raster * Channels;
            _workingRgb[rasterOffset] = r;
            _workingRgb[rasterOffset + 1] = g;
            _workingRgb[rasterOffset + 2] = b;
        }

        ApplyBloomIfEnabled(_style);

        for (var raster = 0; raster < _width * _height; raster++)
        {
            var rasterOffset = raster * Channels;
            var r = _workingRgb[rasterOffset];
            var g = _workingRgb[rasterOffset + 1];
            var b = _workingRgb[rasterOffset + 2];
            var baseX = _dotPadding + ((raster % _width) * _dotStride);
            var baseY = _dotPadding + ((raster / _width) * _dotStride);
            RasterFastDot(baseX, baseY, r, g, b);
        }

        var map = _context.Map(_frameTexture, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        Marshal.Copy(_bgra, 0, map.DataPointer, _bgra.Length);
        _context.Unmap(_frameTexture, 0);

        _context.DrawInstanced(4u, (uint)(_width * _height), 0u, 0u);
        _fallbackBitmap.WritePixels(new System.Windows.Int32Rect(0, 0, _surfaceWidth, _surfaceHeight), _bgra, _surfaceWidth * 4, 0);
    }

    public void Dispose() => DisposeDeviceResources();

    public void Clear()
    {
        if (_fallbackBitmap is null)
        {
            return;
        }

        Array.Clear(_smoothedRgb, 0, _smoothedRgb.Length);
        Array.Clear(_bgra, 0, _bgra.Length);
        _fallbackBitmap.WritePixels(new System.Windows.Int32Rect(0, 0, _surfaceWidth, _surfaceHeight), _bgra, _surfaceWidth * 4, 0);
    }

    private float ApplySmoothing(int channel, byte targetByte, float rise, float fall, bool enabled)
    {
        var target = targetByte;
        if (!enabled)
        {
            _smoothedRgb[channel] = target;
            return target;
        }

        var current = _smoothedRgb[channel];
        if (targetByte == byte.MaxValue)
        {
            _smoothedRgb[channel] = byte.MaxValue;
            return byte.MaxValue;
        }

        var delta = target - current;
        var alpha = delta >= 0 ? rise : fall;
        var next = current + (alpha * delta);
        if (targetByte == 0 && next <= TemporalSmoothingOffSnapThreshold)
        {
            next = 0f;
        }

        _smoothedRgb[channel] = next;
        return next;
    }

    private void BuildToneMapLutIfNeeded(DotStyleConfig style)
    {
        if (_lutBrightness.Equals(style.Brightness) &&
            _lutGamma.Equals(style.Gamma) &&
            _lutToneEnabled == style.ToneMapping.Enabled &&
            _lutKneeStart.Equals(style.ToneMapping.KneeStart) &&
            _lutToneStrength.Equals(style.ToneMapping.Strength))
        {
            return;
        }

        _lutBrightness = style.Brightness;
        _lutGamma = style.Gamma;
        _lutToneEnabled = style.ToneMapping.Enabled;
        _lutKneeStart = style.ToneMapping.KneeStart;
        _lutToneStrength = style.ToneMapping.Strength;

        for (var i = 0; i < 256; i++)
        {
            var normalized = i / 255.0;
            var adjusted = Math.Pow(Math.Clamp(normalized, 0.0, 1.0), Math.Clamp(style.Gamma, 0.1, 5.0));
            var scaled = adjusted * Math.Clamp(style.Brightness, 0.0, 1.0);
            if (style.ToneMapping.Enabled && scaled > 1.0)
            {
                var kneeStart = Math.Clamp(style.ToneMapping.KneeStart, 1.0, 2.0);
                var strength = Math.Clamp(style.ToneMapping.Strength, 0.0, 8.0);
                if (scaled > kneeStart)
                {
                    var excess = scaled - kneeStart;
                    scaled = kneeStart + (excess / (1.0 + (strength * excess)));
                }
            }

            _toneMapLut[i] = (byte)Math.Round(Math.Clamp(scaled, 0.0, 1.0) * 255.0);
        }
    }

    private void ConfigureDotSurface(DotStyleConfig style, int width, int height)
    {
        if (style.DotShape.Equals("circle", StringComparison.OrdinalIgnoreCase) && !style.Visual.FlatShading)
        {
            _dotSize = Math.Clamp(style.DotSize, 2, 5);
        }
        else
        {
            _dotSize = 1;
        }

        _dotPadding = _dotSize > 1 ? 1 : 0;
        _dotStride = _dotSize + _dotPadding;
        _surfaceWidth = (_dotPadding * 2) + (width * _dotStride);
        _surfaceHeight = (_dotPadding * 2) + (height * _dotStride);
        BuildDotMasks(style, _dotSize);
    }

    private void BuildDotMasks(DotStyleConfig style, int dotSize)
    {
        var shape = style.DotShape;
        var visual = style.Visual;
        _dotBodyMask = new float[dotSize * dotSize];
        _dotCoreMask = new float[dotSize * dotSize];
        _dotSpecularMask = new float[dotSize * dotSize];
        if (dotSize == 1)
        {
            _dotBodyMask[0] = 1f;
            _dotCoreMask[0] = 1f;
            _dotSpecularMask[0] = 1f;
            return;
        }

        var center = (dotSize - 1) * 0.5;
        var radius = Math.Max(0.5, dotSize * 0.5);
        var fullRadius = Math.Clamp(visual.FullBrightnessRadiusMinPct, 0.0, 1.0);
        var lensFalloff = Math.Clamp(visual.LensFalloff, 0.0, 1.0);
        var specHotspot = Math.Clamp(visual.SpecularHotspot, 0.0, 1.0);
        var rim = Math.Clamp(visual.RimHighlight, 0.0, 1.0);
        var offAlpha = Math.Clamp(visual.OffStateAlpha, 0.0, 1.0);
        for (var y = 0; y < dotSize; y++)
        {
            for (var x = 0; x < dotSize; x++)
            {
                var idx = (y * dotSize) + x;
                if (shape.Equals("square", StringComparison.OrdinalIgnoreCase))
                {
                    _dotBodyMask[idx] = 1f;
                    _dotCoreMask[idx] = 1f;
                    _dotSpecularMask[idx] = 0.15f;
                    continue;
                }

                var dx = (x - center) / radius;
                var dy = (y - center) / radius;
                var radial = Math.Sqrt((dx * dx) + (dy * dy));
                if (radial <= fullRadius)
                {
                    radial = fullRadius;
                }

                if (radial > 1.0)
                {
                    continue;
                }

                var normalizedRadial = (radial - fullRadius) / Math.Max(0.0001, 1.0 - fullRadius);
                var edge = Math.Clamp(1.0 - normalizedRadial, 0.0, 1.0);
                _dotBodyMask[idx] = (float)(offAlpha * ((0.25 + (0.55 * Math.Pow(edge, 0.5 + lensFalloff))) + (rim * 0.08 * (1.0 - edge))));
                _dotCoreMask[idx] = (float)Math.Pow(edge, 1.1 + (lensFalloff * 1.6));

                var hx = (x / (double)Math.Max(1, dotSize - 1)) - 0.50;
                var hy = (y / (double)Math.Max(1, dotSize - 1)) - 0.35;
                var hotspotDist2 = (hx * hx) + (hy * hy);
                _dotSpecularMask[idx] = (float)(Math.Exp(-hotspotDist2 / Math.Max(0.01, 0.02 + (0.12 * specHotspot))) * (0.35 + (0.55 * specHotspot)));
            }
        }

        NormalizeMask(_dotBodyMask);
        NormalizeMask(_dotCoreMask);
        NormalizeMask(_dotSpecularMask);
    }

    private static void NormalizeMask(float[] mask)
    {
        var maxMask = 0f;
        for (var i = 0; i < mask.Length; i++)
        {
            if (mask[i] > maxMask)
            {
                maxMask = mask[i];
            }
        }

        if (maxMask > 0f && maxMask < 1f)
        {
            for (var i = 0; i < mask.Length; i++)
            {
                if (mask[i] > 0f)
                {
                    mask[i] /= maxMask;
                }
            }
        }
    }

    private void RasterFastDot(int baseX, int baseY, float r, float g, float b)
    {
        if (_style is null)
        {
            return;
        }

        var visual = _style.Visual;
        if (visual.FlatShading)
        {
            RasterFlatDot(baseX, baseY, r, g, b);
            return;
        }

        var intensity = Math.Clamp(Math.Max(r, Math.Max(g, b)) / 255f, 0f, 1f);
        var rootIntensity = Math.Sqrt(intensity);
        var coreOpacity = intensity > 0f ? Math.Clamp(0.35 + (rootIntensity * 0.65), 0.0, 1.0) : 0.0;
        var specOpacity = intensity > 0f ? Math.Clamp((rootIntensity * 0.45) + 0.08, 0.0, 0.65) : 0.0;
        var offBlend = 1.0 - (intensity * intensity);
        var offR = visual.OffStateTintR;
        var offG = visual.OffStateTintG;
        var offB = visual.OffStateTintB;
        var hasOffState = visual.OffStateAlpha > 0.0001 && (offR > 0 || offG > 0 || offB > 0);
        if (!hasOffState && intensity <= 0f)
        {
            return;
        }

        for (var y = 0; y < _dotSize; y++)
        {
            var py = baseY + y;
            if ((uint)py >= (uint)_surfaceHeight)
            {
                continue;
            }

            for (var x = 0; x < _dotSize; x++)
            {
                var idx = (y * _dotSize) + x;
                var body = _dotBodyMask[idx];
                var core = _dotCoreMask[idx] * (float)coreOpacity;
                var spec = _dotSpecularMask[idx] * (float)specOpacity;
                if (body <= 0f && core <= 0f && spec <= 0f)
                {
                    continue;
                }

                var px = baseX + x;
                if ((uint)px >= (uint)_surfaceWidth)
                {
                    continue;
                }

                var o = ((py * _surfaceWidth) + px) * 4;
                var outR = (offR * body * offBlend) + (r * core) + (255.0 * spec);
                var outG = (offG * body * offBlend) + (g * core) + (255.0 * spec);
                var outB = (offB * body * offBlend) + (b * core) + (255.0 * spec);
                _bgra[o] = (byte)Math.Clamp(outB, 0.0, 255.0);
                _bgra[o + 1] = (byte)Math.Clamp(outG, 0.0, 255.0);
                _bgra[o + 2] = (byte)Math.Clamp(outR, 0.0, 255.0);
                _bgra[o + 3] = 255;
            }
        }
    }

    private void RasterFlatDot(int baseX, int baseY, float r, float g, float b)
    {
        for (var y = 0; y < _dotSize; y++)
        {
            var py = baseY + y;
            if ((uint)py >= (uint)_surfaceHeight)
            {
                continue;
            }

            for (var x = 0; x < _dotSize; x++)
            {
                var idx = (y * _dotSize) + x;
                if (_dotBodyMask[idx] <= 0f)
                {
                    continue;
                }

                var px = baseX + x;
                if ((uint)px >= (uint)_surfaceWidth)
                {
                    continue;
                }

                var o = ((py * _surfaceWidth) + px) * 4;
                _bgra[o] = (byte)Math.Clamp(b, 0f, 255f);
                _bgra[o + 1] = (byte)Math.Clamp(g, 0f, 255f);
                _bgra[o + 2] = (byte)Math.Clamp(r, 0f, 255f);
                _bgra[o + 3] = 255;
            }
        }
    }

    private void ApplyBloomIfEnabled(DotStyleConfig style)
    {
        var matrixCapacity = _width * _height;
        var bloomProfile = BloomProfileResolver.Resolve(style.Bloom);
        if (!bloomProfile.Enabled || (bloomProfile.SmallStrength <= 0.0 && bloomProfile.WideStrength <= 0.0))
        {
            return;
        }

        Array.Copy(_workingRgb, _thresholdRgb, matrixCapacity * Channels);
        if (!ThresholdEmissive(_thresholdRgb, matrixCapacity, bloomProfile.Threshold))
        {
            return;
        }

        Downsample(_thresholdRgb, _width, _height, bloomProfile.ScaleDivisor);
        Array.Copy(_smallBlurRgb, _wideBlurRgb, _smallBlurRgb.Length);
        BoxBlurRgbSeparable(_smallBlurRgb, _downsampleWidth, _downsampleHeight, bloomProfile.SmallRadius);
        BoxBlurRgbSeparable(_wideBlurRgb, _downsampleWidth, _downsampleHeight, bloomProfile.WideRadius);
        CompositeBloom(_workingRgb, _smallBlurRgb, _wideBlurRgb, _width, _height, _downsampleWidth, _downsampleHeight, bloomProfile);
    }

    private void Downsample(float[] source, int width, int height, int scaleDivisor)
    {
        _downsampleWidth = Math.Max(1, width / scaleDivisor);
        _downsampleHeight = Math.Max(1, height / scaleDivisor);
        var downsamplePixels = _downsampleWidth * _downsampleHeight * Channels;
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
                var srcOffset = ((srcY * width) + srcX) * Channels;
                var dstOffset = ((y * _downsampleWidth) + x) * Channels;
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
            var offset = i * Channels;
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
                var sampleOffset = ((y * width) + sx) * Channels;
                sumR += source[sampleOffset];
                sumG += source[sampleOffset + 1];
                sumB += source[sampleOffset + 2];
                samples++;
            }

            for (var x = 0; x < width; x++)
            {
                var dstOffset = ((y * width) + x) * Channels;
                destination[dstOffset] = sumR / Math.Max(1, samples);
                destination[dstOffset + 1] = sumG / Math.Max(1, samples);
                destination[dstOffset + 2] = sumB / Math.Max(1, samples);

                var removeX = x - radius;
                if (removeX >= 0)
                {
                    var removeOffset = ((y * width) + removeX) * Channels;
                    sumR -= source[removeOffset];
                    sumG -= source[removeOffset + 1];
                    sumB -= source[removeOffset + 2];
                    samples--;
                }

                var addX = x + radius + 1;
                if (addX < width)
                {
                    var addOffset = ((y * width) + addX) * Channels;
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
                var sampleOffset = ((sy * width) + x) * Channels;
                sumR += source[sampleOffset];
                sumG += source[sampleOffset + 1];
                sumB += source[sampleOffset + 2];
                samples++;
            }

            for (var y = 0; y < height; y++)
            {
                var dstOffset = ((y * width) + x) * Channels;
                destination[dstOffset] = sumR / Math.Max(1, samples);
                destination[dstOffset + 1] = sumG / Math.Max(1, samples);
                destination[dstOffset + 2] = sumB / Math.Max(1, samples);

                var removeY = y - radius;
                if (removeY >= 0)
                {
                    var removeOffset = ((removeY * width) + x) * Channels;
                    sumR -= source[removeOffset];
                    sumG -= source[removeOffset + 1];
                    sumB -= source[removeOffset + 2];
                    samples--;
                }

                var addY = y + radius + 1;
                if (addY < height)
                {
                    var addOffset = ((addY * width) + x) * Channels;
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
                var targetOffset = ((y * width) + x) * Channels;
                var bloomX = Math.Min(bloomWidth - 1, x / profile.ScaleDivisor);
                var bloomY = Math.Min(bloomHeight - 1, y / profile.ScaleDivisor);
                var bloomOffset = ((bloomY * bloomWidth) + bloomX) * Channels;
                target[targetOffset] = Math.Clamp(target[targetOffset] + (smallBlur[bloomOffset] * (float)profile.SmallStrength) + (wideBlur[bloomOffset] * (float)profile.WideStrength), 0f, 255f);
                target[targetOffset + 1] = Math.Clamp(target[targetOffset + 1] + (smallBlur[bloomOffset + 1] * (float)profile.SmallStrength) + (wideBlur[bloomOffset + 1] * (float)profile.WideStrength), 0f, 255f);
                target[targetOffset + 2] = Math.Clamp(target[targetOffset + 2] + (smallBlur[bloomOffset + 2] * (float)profile.SmallStrength) + (wideBlur[bloomOffset + 2] * (float)profile.WideStrength), 0f, 255f);
            }
        }
    }

    private void DisposeDeviceResources()
    {
        _frameSrv?.Dispose();
        _frameSrv = null;
        _frameTexture?.Dispose();
        _frameTexture = null;
        _instanceBuffer?.Dispose();
        _instanceBuffer = null;
        _context?.Dispose();
        _context = null;
        _device?.Dispose();
        _device = null;
    }
}
