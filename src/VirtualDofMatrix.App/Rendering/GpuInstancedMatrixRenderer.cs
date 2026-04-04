using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using VirtualDofMatrix.App.Logging;
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
    private ID3D11SamplerState? _linearSampler;
    private ID3D11VertexShader? _fullscreenVertexShader;
    private ID3D11PixelShader? _brightPassShader;
    private ID3D11PixelShader? _blurPassShader;
    private ID3D11PixelShader? _compositeShader;
    private ID3D11Buffer? _bloomConstantsBuffer;
    private ID3D11Texture2D? _gpuBaseTexture;
    private ID3D11ShaderResourceView? _gpuBaseSrv;
    private ID3D11Texture2D? _gpuBrightTexture;
    private ID3D11RenderTargetView? _gpuBrightRtv;
    private ID3D11ShaderResourceView? _gpuBrightSrv;
    private ID3D11Texture2D? _gpuNearPingTexture;
    private ID3D11RenderTargetView? _gpuNearPingRtv;
    private ID3D11ShaderResourceView? _gpuNearPingSrv;
    private ID3D11Texture2D? _gpuNearPongTexture;
    private ID3D11RenderTargetView? _gpuNearPongRtv;
    private ID3D11ShaderResourceView? _gpuNearPongSrv;
    private ID3D11Texture2D? _gpuFarPingTexture;
    private ID3D11RenderTargetView? _gpuFarPingRtv;
    private ID3D11ShaderResourceView? _gpuFarPingSrv;
    private ID3D11Texture2D? _gpuFarPongTexture;
    private ID3D11RenderTargetView? _gpuFarPongRtv;
    private ID3D11ShaderResourceView? _gpuFarPongSrv;
    private ID3D11Texture2D? _gpuCompositeTexture;
    private ID3D11RenderTargetView? _gpuCompositeRtv;
    private ID3D11ShaderResourceView? _gpuCompositeSrv;
    private ID3D11Texture2D? _gpuReadbackTexture;
    private bool _gpuBloomSupported;
    private bool _useCpuBloomFallback;
    private int _gpuBloomScaleDivisor;
    private static readonly ID3D11ShaderResourceView[] NullPixelShaderSrvs = [null!, null!, null!];
    private string _gpuBloomStage = "idle";
    private string _gpuBloomTrace = string.Empty;
    private ulong _gpuBloomAttemptCount;
    private ulong _lastOutputSequence;
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
    private float[] _screenBloomSourceRgb = Array.Empty<float>();
    private float[] _screenBloomNearRgb = Array.Empty<float>();
    private float[] _screenBloomFarRgb = Array.Empty<float>();
    private float[] _screenBloomScratchRgb = Array.Empty<float>();
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
        _screenBloomSourceRgb = Array.Empty<float>();
        _screenBloomNearRgb = Array.Empty<float>();
        _screenBloomFarRgb = Array.Empty<float>();
        _screenBloomScratchRgb = Array.Empty<float>();
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
        InitializeGpuBloomPipeline();

        AppLogger.Info($"[renderer] gpu initialized fastpath leds={width * height} surface={_surfaceWidth}x{_surfaceHeight}");
        var bloomProfile = BloomProfileResolver.Resolve(dotStyleConfig.Bloom);
        AppLogger.Info($"[renderer] gpu bloom enabled={bloomProfile.Enabled} threshold={bloomProfile.Threshold:F2} softKnee={bloomProfile.SoftKnee:F2} scale={bloomProfile.ScaleDivisor} nearRadius={bloomProfile.NearRadius} farRadius={bloomProfile.FarRadius} nearStrength={bloomProfile.NearStrength:F2} farStrength={bloomProfile.FarStrength:F2}");
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
        _lastOutputSequence = frame.OutputSequence;

        BuildToneMapLutIfNeeded(_style);
        Array.Clear(_bgra, 0, _bgra.Length);
        EnsureOpaqueBackground(_bgra);
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
        ApplyBloomIfEnabled(_style);

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
        var bloomProfile = BloomProfileResolver.Resolve(style.Bloom);
        // If both lanes are effectively off, skip bloom and keep the frame path cheap.
        if (!bloomProfile.Enabled || (bloomProfile.NearStrength <= 0.0 && bloomProfile.FarStrength <= 0.0))
        {
            return;
        }
        // If there are no lit LEDs in this frame, we skip bloom so "off bulb" shading stays clean.
        if (!HasAnyLitLed(_workingRgb))
        {
            return;
        }

        // We prefer shader-driven bloom, but keep CPU fallback alive so unsupported GPUs still render correctly.
        if (!_useCpuBloomFallback && _gpuBloomSupported)
        {
            try
            {
                if (TryApplyGpuBloom(bloomProfile))
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[renderer] gpu bloom execution failed at stage='{_gpuBloomStage}'; switching to CPU fallback. reason={ex.Message} {TryGetDeviceRemovedReasonText()} trace={_gpuBloomTrace}");
            }
        }

        _useCpuBloomFallback = true;

        // Extract emissive data from the final rasterized surface so bloom feels spatially natural.
        if (!DownsampleEmissive(_bgra, _surfaceWidth, _surfaceHeight, bloomProfile, out var minBloomX, out var minBloomY, out var maxBloomX, out var maxBloomY))
        {
            return;
        }

        // Split into near/far blur lanes so we can shape a tight glow plus a soft halo.
        Array.Copy(_screenBloomSourceRgb, _screenBloomNearRgb, _screenBloomSourceRgb.Length);
        Array.Copy(_screenBloomSourceRgb, _screenBloomFarRgb, _screenBloomSourceRgb.Length);
        var effectiveNearRadius = GetEffectiveBloomRadius(bloomProfile.NearRadius, bloomProfile.ScaleDivisor, _dotSize);
        var effectiveFarRadius = GetEffectiveBloomRadius(bloomProfile.FarRadius, bloomProfile.ScaleDivisor, _dotSize);
        // Near bloom uses a smooth blur so tiny radii don't hard-light an entire adjacent dot.
        BoxBlurRgbSeparable(_screenBloomNearRgb, _downsampleWidth, _downsampleHeight, effectiveNearRadius);
        BoxBlurRgbSeparable(_screenBloomFarRgb, _downsampleWidth, _downsampleHeight, effectiveFarRadius);
        var effectiveNearStrength = (float)bloomProfile.NearStrength;
        var effectiveFarStrength = (float)bloomProfile.FarStrength;
        CompositeBloom(_bgra, _surfaceWidth, _surfaceHeight, _screenBloomNearRgb, _screenBloomFarRgb, _downsampleWidth, _downsampleHeight, minBloomX, minBloomY, maxBloomX, maxBloomY, effectiveNearRadius, effectiveFarRadius, effectiveNearStrength, effectiveFarStrength, bloomProfile);
    }

    private bool TryApplyGpuBloom(BloomProfile profile)
    {
        var trace = new StringBuilder(256);
        var started = DateTime.UtcNow;
        _gpuBloomAttemptCount++;
        trace.Append($"attempt={_gpuBloomAttemptCount} seq={_lastOutputSequence} surface={_surfaceWidth}x{_surfaceHeight}");
        _gpuBloomTrace = trace.ToString();
        _gpuBloomStage = "validate";
        trace.Append(" stage=validate");
        _gpuBloomTrace = trace.ToString();
        if (_device is null || _context is null ||
            _gpuBaseTexture is null || _gpuBaseSrv is null ||
            _gpuCompositeTexture is null || _gpuCompositeRtv is null || _gpuReadbackTexture is null ||
            _brightPassShader is null || _blurPassShader is null || _compositeShader is null ||
            _fullscreenVertexShader is null || _linearSampler is null || _bloomConstantsBuffer is null)
        {
            return false;
        }

        EnsureGpuBloomTargets(profile.ScaleDivisor);
        trace.Append($" downsample={_downsampleWidth}x{_downsampleHeight} profile(scale={profile.ScaleDivisor},nearRadius={profile.NearRadius},farRadius={profile.FarRadius},nearStrength={profile.NearStrength:F2},farStrength={profile.FarStrength:F2})");
        _gpuBloomTrace = trace.ToString();
        if (_gpuBrightRtv is null || _gpuBrightSrv is null ||
            _gpuNearPingRtv is null || _gpuNearPingSrv is null ||
            _gpuNearPongRtv is null || _gpuNearPongSrv is null ||
            _gpuFarPingRtv is null || _gpuFarPingSrv is null ||
            _gpuFarPongRtv is null || _gpuFarPongSrv is null)
        {
            return false;
        }

        // Upload the already-rasterized frame so bloom extraction happens from the same visual basis as CPU mode.
        _gpuBloomStage = "upload-base";
        trace.Append(" ->upload-base");
        _gpuBloomTrace = trace.ToString();
        var mapped = _context.Map(_gpuBaseTexture, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        trace.Append($"(rowPitch={mapped.RowPitch})");
        _gpuBloomTrace = trace.ToString();
        WriteBgraRows(mapped.DataPointer, mapped.RowPitch, _bgra, _surfaceWidth, _surfaceHeight);
        _context.Unmap(_gpuBaseTexture, 0);

        var nearRadius = GetEffectiveBloomRadius(profile.NearRadius, profile.ScaleDivisor, _dotSize);
        var farRadius = GetEffectiveBloomRadius(profile.FarRadius, profile.ScaleDivisor, _dotSize);

        _gpuBloomStage = "bright-pass";
        trace.Append(" ->bright-pass");
        _gpuBloomTrace = trace.ToString();
        RunBrightPass(profile);
        _gpuBloomStage = "blur-near";
        trace.Append(" ->blur-near");
        _gpuBloomTrace = trace.ToString();
        RunBlurLane(_gpuBrightSrv, _gpuNearPingRtv, _gpuNearPingSrv, _gpuNearPongRtv, _gpuNearPongSrv, nearRadius);
        _gpuBloomStage = "blur-far";
        trace.Append(" ->blur-far");
        _gpuBloomTrace = trace.ToString();
        RunBlurLane(_gpuBrightSrv, _gpuFarPingRtv, _gpuFarPingSrv, _gpuFarPongRtv, _gpuFarPongSrv, farRadius);
        _gpuBloomStage = "composite";
        trace.Append(" ->composite");
        _gpuBloomTrace = trace.ToString();
        RunCompositePass(profile);

        // Conversational note: explicitly switch away from the composite RTV before CopyResource.
        _gpuBloomStage = "unbind-composite";
        trace.Append(" ->unbind-composite");
        _gpuBloomTrace = trace.ToString();
        _context.OMSetRenderTargets(_gpuBrightRtv);

        _gpuBloomStage = "copy-readback";
        trace.Append(" ->copy-readback");
        _gpuBloomTrace = trace.ToString();
        _context.CopyResource(_gpuReadbackTexture, _gpuCompositeTexture);
        _context.Flush();
        _gpuBloomStage = "map-readback";
        trace.Append(" ->map-readback");
        _gpuBloomTrace = trace.ToString();
        var readback = _context.Map(_gpuReadbackTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        trace.Append($"(rowPitch={readback.RowPitch})");
        _gpuBloomTrace = trace.ToString();
        ReadBgraRows(readback.DataPointer, readback.RowPitch, _bgra, _surfaceWidth, _surfaceHeight);
        _context.Unmap(_gpuReadbackTexture, 0);
        _gpuBloomStage = "done";
        trace.Append($" ->done elapsedMs={(DateTime.UtcNow - started).TotalMilliseconds:F3}");
        _gpuBloomTrace = trace.ToString();
        return true;
    }

    private static void WriteBgraRows(IntPtr destination, uint destinationRowPitch, byte[] source, int width, int height)
    {
        var rowBytes = width * 4;
        for (var y = 0; y < height; y++)
        {
            var srcOffset = y * rowBytes;
            var dstRow = IntPtr.Add(destination, checked(y * (int)destinationRowPitch));
            Marshal.Copy(source, srcOffset, dstRow, rowBytes);
        }
    }

    private static void ReadBgraRows(IntPtr source, uint sourceRowPitch, byte[] destination, int width, int height)
    {
        var rowBytes = width * 4;
        for (var y = 0; y < height; y++)
        {
            var dstOffset = y * rowBytes;
            var srcRow = IntPtr.Add(source, checked(y * (int)sourceRowPitch));
            Marshal.Copy(srcRow, destination, dstOffset, rowBytes);
        }
    }

    private void RunBrightPass(BloomProfile profile)
    {
        if (_context is null || _gpuBrightRtv is null || _gpuBaseSrv is null || _brightPassShader is null)
        {
            return;
        }

        SetBloomConstants(profile, radius: 0f, directionX: 0f, directionY: 0f);
        _context.OMSetRenderTargets(_gpuBrightRtv);
        _context.RSSetViewport(new Viewport(0, 0, _downsampleWidth, _downsampleHeight, 0f, 1f));
        _context.VSSetShader(_fullscreenVertexShader);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.PSSetShader(_brightPassShader);
        _context.PSSetShaderResource(0, _gpuBaseSrv);
        _context.PSSetSampler(0, _linearSampler);
        _context.PSSetConstantBuffer(0, _bloomConstantsBuffer);
        _context.Draw(3, 0);
        _context.PSSetShaderResources(0, NullPixelShaderSrvs);
    }

    private void RunBlurLane(
        ID3D11ShaderResourceView sourceSrv,
        ID3D11RenderTargetView pingRtv,
        ID3D11ShaderResourceView pingSrv,
        ID3D11RenderTargetView pongRtv,
        ID3D11ShaderResourceView pongSrv,
        int radius)
    {
        if (_context is null || _blurPassShader is null)
        {
            return;
        }

        var texelX = 1f / Math.Max(1, _downsampleWidth);
        var texelY = 1f / Math.Max(1, _downsampleHeight);

        SetBloomConstants(null, radius, texelX, 0f);
        _context.OMSetRenderTargets(pingRtv);
        _context.RSSetViewport(new Viewport(0, 0, _downsampleWidth, _downsampleHeight, 0f, 1f));
        _context.VSSetShader(_fullscreenVertexShader);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.PSSetShader(_blurPassShader);
        _context.PSSetShaderResource(0, sourceSrv);
        _context.PSSetSampler(0, _linearSampler);
        _context.PSSetConstantBuffer(0, _bloomConstantsBuffer);
        _context.Draw(3, 0);
        _context.PSSetShaderResources(0, NullPixelShaderSrvs);

        SetBloomConstants(null, radius, 0f, texelY);
        _context.OMSetRenderTargets(pongRtv);
        _context.PSSetShader(_blurPassShader);
        _context.PSSetShaderResource(0, pingSrv);
        _context.PSSetSampler(0, _linearSampler);
        _context.PSSetConstantBuffer(0, _bloomConstantsBuffer);
        _context.Draw(3, 0);
        _context.PSSetShaderResources(0, NullPixelShaderSrvs);
    }

    private void RunCompositePass(BloomProfile profile)
    {
        if (_context is null || _compositeShader is null || _gpuCompositeRtv is null || _gpuBaseSrv is null || _gpuNearPongSrv is null || _gpuFarPongSrv is null)
        {
            return;
        }

        SetBloomConstants(profile, radius: 0f, directionX: 0f, directionY: 0f);
        _context.OMSetRenderTargets(_gpuCompositeRtv);
        _context.RSSetViewport(new Viewport(0, 0, _surfaceWidth, _surfaceHeight, 0f, 1f));
        _context.VSSetShader(_fullscreenVertexShader);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.PSSetShader(_compositeShader);
        _context.PSSetShaderResource(0, _gpuBaseSrv);
        _context.PSSetShaderResource(1, _gpuNearPongSrv);
        _context.PSSetShaderResource(2, _gpuFarPongSrv);
        _context.PSSetSampler(0, _linearSampler);
        _context.PSSetConstantBuffer(0, _bloomConstantsBuffer);
        _context.Draw(3, 0);
        _context.PSSetShaderResources(0, NullPixelShaderSrvs);
    }

    private void SetBloomConstants(BloomProfile? profile, float radius, float directionX, float directionY)
    {
        if (_context is null || _bloomConstantsBuffer is null)
        {
            return;
        }

        var constants = new BloomGpuConstants
        {
            Threshold = (float)(profile?.Threshold ?? 0.0),
            SoftKnee = (float)(profile?.SoftKnee ?? 0.0),
            NearStrength = (float)(profile?.NearStrength ?? 0.0),
            FarStrength = (float)(profile?.FarStrength ?? 0.0),
            ScaleDivisor = (float)(profile?.ScaleDivisor ?? Math.Max(1, _gpuBloomScaleDivisor)),
            // Conversational note: clamp radius defensively so bad constant data cannot create a runaway shader loop.
            Radius = Math.Clamp(radius, 0f, 8f),
            DirectionX = directionX,
            DirectionY = directionY,
            SurfaceWidth = _surfaceWidth,
            SurfaceHeight = _surfaceHeight,
            BloomWidth = _downsampleWidth,
            BloomHeight = _downsampleHeight,
        };

        var mapped = _context.Map(_bloomConstantsBuffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        Marshal.StructureToPtr(constants, mapped.DataPointer, false);
        _context.Unmap(_bloomConstantsBuffer, 0);
    }

    private bool DownsampleEmissive(byte[] bgra, int width, int height, BloomProfile profile, out int minBloomX, out int minBloomY, out int maxBloomX, out int maxBloomY)
    {
        var scaleDivisor = profile.ScaleDivisor;
        _downsampleWidth = Math.Max(1, width / scaleDivisor);
        _downsampleHeight = Math.Max(1, height / scaleDivisor);
        var downsamplePixels = _downsampleWidth * _downsampleHeight * Channels;
        if (_screenBloomSourceRgb.Length != downsamplePixels)
        {
            _screenBloomSourceRgb = new float[downsamplePixels];
            _screenBloomNearRgb = new float[downsamplePixels];
            _screenBloomFarRgb = new float[downsamplePixels];
            _screenBloomScratchRgb = new float[downsamplePixels];
        }

        // Reset every frame to avoid carrying stale bloom into subsequent frames.
        Array.Clear(_screenBloomSourceRgb, 0, _screenBloomSourceRgb.Length);
        var any = false;
        minBloomX = _downsampleWidth;
        minBloomY = _downsampleHeight;
        maxBloomX = -1;
        maxBloomY = -1;
        for (var y = 0; y < _downsampleHeight; y++)
        {
            for (var x = 0; x < _downsampleWidth; x++)
            {
                var dstOffset = ((y * _downsampleWidth) + x) * Channels;
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
                        // Soft-knee weighting makes bright pixels "fade into" bloom more naturally.
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

    private void BoxBlurRgbSeparable(float[] rgb, int width, int height, int radius)
    {
        if (radius <= 0)
        {
            return;
        }

        if (_screenBloomScratchRgb.Length != rgb.Length)
        {
            _screenBloomScratchRgb = new float[rgb.Length];
        }

        HorizontalBlurRgb(rgb, _screenBloomScratchRgb, width, height, radius);
        VerticalBlurRgb(_screenBloomScratchRgb, rgb, width, height, radius);
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

    private static void CompositeBloom(byte[] target, int width, int height, float[] nearBlur, float[] farBlur, int bloomWidth, int bloomHeight, int minBloomX, int minBloomY, int maxBloomX, int maxBloomY, int effectiveNearRadius, int effectiveFarRadius, float effectiveNearStrength, float effectiveFarStrength, BloomProfile profile)
    {
        var nearStrength = effectiveNearStrength;
        var farStrength = effectiveFarStrength;
        // We only composite inside the active emissive neighborhood to avoid wasting cycles on black pixels.
        var pad = Math.Max(effectiveNearRadius, effectiveFarRadius) + 1;
        var startX = Math.Max(0, (minBloomX - pad) * profile.ScaleDivisor);
        var startY = Math.Max(0, (minBloomY - pad) * profile.ScaleDivisor);
        var endX = Math.Min(width - 1, ((maxBloomX + pad + 1) * profile.ScaleDivisor) - 1);
        var endY = Math.Min(height - 1, ((maxBloomY + pad + 1) * profile.ScaleDivisor) - 1);

        for (var y = startY; y <= endY; y++)
        {
            for (var x = startX; x <= endX; x++)
            {
                // Bilinear sampling keeps the upsampled bloom field smooth and avoids block stepping.
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
        for (var i = 0; i < rgb.Length; i += Channels)
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
        // We keep the surface opaque black so bloom in the spacing between dots can actually be seen.
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

        var a = source[((y0 * width + x0) * Channels) + channel];
        var b = source[((y0 * width + x1) * Channels) + channel];
        var c = source[((y1 * width + x0) * Channels) + channel];
        var d = source[((y1 * width + x1) * Channels) + channel];

        var ab = a + ((b - a) * tx);
        var cd = c + ((d - c) * tx);
        return ab + ((cd - ab) * ty);
    }

    private string TryGetDeviceRemovedReasonText()
    {
        if (_device is null)
        {
            return "deviceReason=unavailable";
        }

        try
        {
            var reason = _device.DeviceRemovedReason;
            return $"deviceReason=0x{reason.Code:X8}";
        }
        catch
        {
            return "deviceReason=unavailable";
        }
    }

    private void DisposeDeviceResources()
    {
        DisposeGpuBloomResources();
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

    private void InitializeGpuBloomPipeline()
    {
        if (_device is null)
        {
            _gpuBloomSupported = false;
            return;
        }

        try
        {
            // We keep shaders inline for now so deployment stays single-binary and easier for community sharing.
            using var vsBlob = CompileShaderOrThrow("VSMain", "vs_5_0");
            using var brightBlob = CompileShaderOrThrow("PSBrightPass", "ps_5_0");
            using var blurBlob = CompileShaderOrThrow("PSSeparableBlur", "ps_5_0");
            using var compositeBlob = CompileShaderOrThrow("PSComposite", "ps_5_0");
            _fullscreenVertexShader = _device.CreateVertexShader(vsBlob);
            _brightPassShader = _device.CreatePixelShader(brightBlob);
            _blurPassShader = _device.CreatePixelShader(blurBlob);
            _compositeShader = _device.CreatePixelShader(compositeBlob);
            _linearSampler = _device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp, 0, 1, ComparisonFunction.Never, new Color4(0f, 0f, 0f, 0f), 0, float.MaxValue));
            _bloomConstantsBuffer = _device.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<BloomGpuConstants>(), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
            CreateBaseAndCompositeTargets();
            _gpuBloomSupported = true;
            _useCpuBloomFallback = false;
        }
        catch (Exception ex)
        {
            _gpuBloomSupported = false;
            _useCpuBloomFallback = true;
            AppLogger.Warn($"[renderer] gpu bloom pipeline unavailable; using CPU fallback. reason={ex.Message}");
        }
    }

    private static Blob CompileShaderOrThrow(string entryPoint, string shaderProfile)
    {
        // Conversational note: CompileFromFile is the most deterministic overload in Vortice across SDK bindings.
        var shaderPath = Path.Combine(Path.GetTempPath(), "VirtualDofMatrix.BloomShaders.hlsl");
        File.WriteAllText(shaderPath, BloomShaders.Source);
        var compileResult = Compiler.CompileFromFile(
            shaderPath,
            null!,
            null!,
            entryPoint,
            shaderProfile,
            ShaderFlags.OptimizationLevel3,
            EffectFlags.None,
            out var shaderBlob,
            out var errorBlob);
        if (compileResult.Failure || shaderBlob is null)
        {
            var errors = errorBlob is null ? "unknown shader compile failure" : Marshal.PtrToStringAnsi(errorBlob.BufferPointer);
            throw new InvalidOperationException($"Failed to compile bloom shader {entryPoint}/{shaderProfile}: {errors}");
        }

        return shaderBlob!;
    }

    private void EnsureGpuBloomTargets(int scaleDivisor)
    {
        if (!_gpuBloomSupported || _device is null)
        {
            return;
        }

        if (_gpuBrightTexture is not null && _gpuBloomScaleDivisor == scaleDivisor)
        {
            return;
        }

        DisposeBloomIntermediateTargets();
        _gpuBloomScaleDivisor = Math.Max(1, scaleDivisor);
        _downsampleWidth = Math.Max(1, _surfaceWidth / _gpuBloomScaleDivisor);
        _downsampleHeight = Math.Max(1, _surfaceHeight / _gpuBloomScaleDivisor);
        CreateBloomIntermediateTargets(_downsampleWidth, _downsampleHeight);
    }

    private void CreateBaseAndCompositeTargets()
    {
        if (_device is null)
        {
            return;
        }

        var fullDesc = new Texture2DDescription
        {
            Width = (uint)Math.Max(1, _surfaceWidth),
            Height = (uint)Math.Max(1, _surfaceHeight),
            ArraySize = 1,
            MipLevels = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.Write,
        };
        _gpuBaseTexture = _device.CreateTexture2D(fullDesc);
        _gpuBaseSrv = _device.CreateShaderResourceView(_gpuBaseTexture);

        fullDesc.Usage = ResourceUsage.Default;
        fullDesc.BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget;
        fullDesc.CPUAccessFlags = CpuAccessFlags.None;
        _gpuCompositeTexture = _device.CreateTexture2D(fullDesc);
        _gpuCompositeSrv = _device.CreateShaderResourceView(_gpuCompositeTexture);
        _gpuCompositeRtv = _device.CreateRenderTargetView(_gpuCompositeTexture);

        fullDesc.Usage = ResourceUsage.Staging;
        fullDesc.BindFlags = BindFlags.None;
        fullDesc.CPUAccessFlags = CpuAccessFlags.Read;
        _gpuReadbackTexture = _device.CreateTexture2D(fullDesc);
    }

    private void CreateBloomIntermediateTargets(int width, int height)
    {
        if (_device is null)
        {
            return;
        }

        var desc = new Texture2DDescription
        {
            Width = (uint)Math.Max(1, width),
            Height = (uint)Math.Max(1, height),
            ArraySize = 1,
            MipLevels = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        };

        _gpuBrightTexture = _device.CreateTexture2D(desc);
        _gpuBrightRtv = _device.CreateRenderTargetView(_gpuBrightTexture);
        _gpuBrightSrv = _device.CreateShaderResourceView(_gpuBrightTexture);
        _gpuNearPingTexture = _device.CreateTexture2D(desc);
        _gpuNearPingRtv = _device.CreateRenderTargetView(_gpuNearPingTexture);
        _gpuNearPingSrv = _device.CreateShaderResourceView(_gpuNearPingTexture);
        _gpuNearPongTexture = _device.CreateTexture2D(desc);
        _gpuNearPongRtv = _device.CreateRenderTargetView(_gpuNearPongTexture);
        _gpuNearPongSrv = _device.CreateShaderResourceView(_gpuNearPongTexture);
        _gpuFarPingTexture = _device.CreateTexture2D(desc);
        _gpuFarPingRtv = _device.CreateRenderTargetView(_gpuFarPingTexture);
        _gpuFarPingSrv = _device.CreateShaderResourceView(_gpuFarPingTexture);
        _gpuFarPongTexture = _device.CreateTexture2D(desc);
        _gpuFarPongRtv = _device.CreateRenderTargetView(_gpuFarPongTexture);
        _gpuFarPongSrv = _device.CreateShaderResourceView(_gpuFarPongTexture);
    }

    private void DisposeGpuBloomResources()
    {
        DisposeBloomIntermediateTargets();
        _gpuReadbackTexture?.Dispose();
        _gpuReadbackTexture = null;
        _gpuCompositeSrv?.Dispose();
        _gpuCompositeSrv = null;
        _gpuCompositeRtv?.Dispose();
        _gpuCompositeRtv = null;
        _gpuCompositeTexture?.Dispose();
        _gpuCompositeTexture = null;
        _gpuBaseSrv?.Dispose();
        _gpuBaseSrv = null;
        _gpuBaseTexture?.Dispose();
        _gpuBaseTexture = null;
        _bloomConstantsBuffer?.Dispose();
        _bloomConstantsBuffer = null;
        _linearSampler?.Dispose();
        _linearSampler = null;
        _compositeShader?.Dispose();
        _compositeShader = null;
        _blurPassShader?.Dispose();
        _blurPassShader = null;
        _brightPassShader?.Dispose();
        _brightPassShader = null;
        _fullscreenVertexShader?.Dispose();
        _fullscreenVertexShader = null;
    }

    private void DisposeBloomIntermediateTargets()
    {
        _gpuFarPongSrv?.Dispose();
        _gpuFarPongSrv = null;
        _gpuFarPongRtv?.Dispose();
        _gpuFarPongRtv = null;
        _gpuFarPongTexture?.Dispose();
        _gpuFarPongTexture = null;
        _gpuFarPingSrv?.Dispose();
        _gpuFarPingSrv = null;
        _gpuFarPingRtv?.Dispose();
        _gpuFarPingRtv = null;
        _gpuFarPingTexture?.Dispose();
        _gpuFarPingTexture = null;
        _gpuNearPongSrv?.Dispose();
        _gpuNearPongSrv = null;
        _gpuNearPongRtv?.Dispose();
        _gpuNearPongRtv = null;
        _gpuNearPongTexture?.Dispose();
        _gpuNearPongTexture = null;
        _gpuNearPingSrv?.Dispose();
        _gpuNearPingSrv = null;
        _gpuNearPingRtv?.Dispose();
        _gpuNearPingRtv = null;
        _gpuNearPingTexture?.Dispose();
        _gpuNearPingTexture = null;
        _gpuBrightSrv?.Dispose();
        _gpuBrightSrv = null;
        _gpuBrightRtv?.Dispose();
        _gpuBrightRtv = null;
        _gpuBrightTexture?.Dispose();
        _gpuBrightTexture = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BloomGpuConstants
    {
        public float Threshold;
        public float SoftKnee;
        public float NearStrength;
        public float FarStrength;
        public float ScaleDivisor;
        public float Radius;
        public float DirectionX;
        public float DirectionY;
        public float SurfaceWidth;
        public float SurfaceHeight;
        public float BloomWidth;
        public float BloomHeight;
    }

    private static class BloomShaders
    {
        public const string Source = """
cbuffer BloomConstants : register(b0)
{
    float Threshold;
    float SoftKnee;
    float NearStrength;
    float FarStrength;
    float ScaleDivisor;
    float Radius;
    float2 Direction;
    float2 SurfaceSize;
    float2 BloomSize;
}
Texture2D BaseTexture : register(t0);
Texture2D NearTexture : register(t1);
Texture2D FarTexture : register(t2);
SamplerState LinearSampler : register(s0);
struct VsOut
{
    float4 Position : SV_Position;
    float2 Uv : TEXCOORD0;
};
VsOut VSMain(uint vertexId : SV_VertexID)
{
    VsOut output;
    // Conversational note: explicit full-screen triangle vertices are friendlier across drivers than bit-manipulated UV generation.
    float2 positions[3] =
    {
        float2(-1.0f, -1.0f),
        float2(-1.0f,  3.0f),
        float2( 3.0f, -1.0f)
    };
    float2 uvs[3] =
    {
        float2(0.0f, 1.0f),
        float2(0.0f,-1.0f),
        float2(2.0f, 1.0f)
    };
    output.Position = float4(positions[vertexId], 0, 1);
    output.Uv = uvs[vertexId];
    return output;
}
float SoftKneeWeight(float3 color)
{
    float peak = max(color.r, max(color.g, color.b));
    if (SoftKnee <= 0.0001f) return peak >= Threshold ? 1.0f : 0.0f;
    float t = saturate((peak - Threshold) / max(SoftKnee, 0.0001f));
    return t * t * (3.0f - (2.0f * t));
}
float4 PSBrightPass(VsOut input) : SV_Target
{
    float3 baseColor = BaseTexture.Sample(LinearSampler, input.Uv).rgb;
    float emissive = SoftKneeWeight(baseColor);
    return float4(baseColor * emissive, 1.0f);
}
float4 PSSeparableBlur(VsOut input) : SV_Target
{
    float r = clamp(Radius, 0.0f, 8.0f);
    if (r <= 0.001f) return BaseTexture.Sample(LinearSampler, input.Uv);
    float3 sum = 0;
    float weightSum = 0;
    // Conversational note: fixed bounds + unroll avoids driver issues with dynamic loop trip counts.
    [unroll]
    for (int i = -8; i <= 8; i++)
    {
        if (abs((float)i) > r) continue;
        float2 uv = input.Uv + (Direction * (float)i);
        float w = 1.0f - abs((float)i) / (r + 1.0f);
        sum += BaseTexture.Sample(LinearSampler, uv).rgb * w;
        weightSum += w;
    }
    return float4(sum / max(weightSum, 0.0001f), 1.0f);
}
float4 PSComposite(VsOut input) : SV_Target
{
    float3 baseColor = BaseTexture.Sample(LinearSampler, input.Uv).rgb;
    float3 nearColor = NearTexture.Sample(LinearSampler, input.Uv).rgb;
    float3 farColor = FarTexture.Sample(LinearSampler, input.Uv).rgb;
    float3 outColor = saturate(baseColor + (nearColor * NearStrength) + (farColor * FarStrength));
    return float4(outColor, 1.0f);
}
""";
    }
}
