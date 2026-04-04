using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.D3DCompiler;
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
    private const string BloomFullscreenShader = """
                                                struct VSOut
                                                {
                                                    float4 Position : SV_POSITION;
                                                    float2 UV : TEXCOORD0;
                                                };

                                                VSOut VSMain(uint vertexId : SV_VertexID)
                                                {
                                                    VSOut output;
                                                    float2 uv = float2((vertexId << 1) & 2, vertexId & 2);
                                                    output.UV = uv;
                                                    output.Position = float4((uv * float2(2, -2)) + float2(-1, 1), 0, 1);
                                                    return output;
                                                }

                                                cbuffer BloomParams : register(b0)
                                                {
                                                    float2 InvSourceSize;
                                                    float2 BlurDirection;
                                                    float Threshold;
                                                    float SoftKnee;
                                                    float NearStrength;
                                                    float FarStrength;
                                                    float2 Pad;
                                                };

                                                Texture2D SourceTex : register(t0);
                                                Texture2D NearTex : register(t1);
                                                Texture2D FarTex : register(t2);
                                                SamplerState LinearSampler : register(s0);

                                                float EmissiveWeight(float3 rgb)
                                                {
                                                    float peak = max(rgb.r, max(rgb.g, rgb.b));
                                                    if (SoftKnee <= 0.0001)
                                                    {
                                                        return peak >= Threshold ? 1.0 : 0.0;
                                                    }

                                                    float t = saturate((peak - Threshold) / max(SoftKnee, 0.0001));
                                                    return t * t * (3.0 - (2.0 * t));
                                                }

                                                float4 PSExtract(VSOut input) : SV_TARGET
                                                {
                                                    float3 color = SourceTex.Sample(LinearSampler, input.UV).rgb;
                                                    float weight = EmissiveWeight(color);
                                                    return float4(color * weight, 1.0);
                                                }

                                                float4 PSBlur(VSOut input) : SV_TARGET
                                                {
                                                    float2 offset = BlurDirection * InvSourceSize;
                                                    float3 center = SourceTex.Sample(LinearSampler, input.UV).rgb * 0.4;
                                                    float3 nearA = SourceTex.Sample(LinearSampler, input.UV + offset * 1.0).rgb * 0.25;
                                                    float3 nearB = SourceTex.Sample(LinearSampler, input.UV - offset * 1.0).rgb * 0.25;
                                                    float3 farA = SourceTex.Sample(LinearSampler, input.UV + offset * 2.5).rgb * 0.05;
                                                    float3 farB = SourceTex.Sample(LinearSampler, input.UV - offset * 2.5).rgb * 0.05;
                                                    return float4(center + nearA + nearB + farA + farB, 1.0);
                                                }

                                                float4 PSComposite(VSOut input) : SV_TARGET
                                                {
                                                    float3 baseColor = SourceTex.Sample(LinearSampler, input.UV).rgb;
                                                    float3 nearColor = NearTex.Sample(LinearSampler, input.UV).rgb * NearStrength;
                                                    float3 farColor = FarTex.Sample(LinearSampler, input.UV).rgb * FarStrength;
                                                    return float4(saturate(baseColor + nearColor + farColor), 1.0);
                                                }
                                                """;

    [StructLayout(LayoutKind.Sequential)]
    private struct GpuBloomParams
    {
        public float InvSourceWidth;
        public float InvSourceHeight;
        public float BlurDirectionX;
        public float BlurDirectionY;
        public float Threshold;
        public float SoftKnee;
        public float NearStrength;
        public float FarStrength;
        public float Pad0;
        public float Pad1;
    }
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11Buffer? _instanceBuffer;
    private ID3D11Texture2D? _frameTexture;
    private ID3D11ShaderResourceView? _frameSrv;
    private ID3D11Texture2D? _gpuBloomCompositeTexture;
    private ID3D11RenderTargetView? _gpuBloomCompositeRtv;
    private ID3D11ShaderResourceView? _gpuBloomCompositeSrv;
    private ID3D11Texture2D? _gpuBloomReadbackTexture;
    private ID3D11Texture2D? _gpuBloomExtractTexture;
    private ID3D11RenderTargetView? _gpuBloomExtractRtv;
    private ID3D11ShaderResourceView? _gpuBloomExtractSrv;
    private ID3D11Texture2D? _gpuBloomNearTextureA;
    private ID3D11Texture2D? _gpuBloomNearTextureB;
    private ID3D11RenderTargetView? _gpuBloomNearRtvA;
    private ID3D11RenderTargetView? _gpuBloomNearRtvB;
    private ID3D11ShaderResourceView? _gpuBloomNearSrvA;
    private ID3D11ShaderResourceView? _gpuBloomNearSrvB;
    private ID3D11Texture2D? _gpuBloomFarTextureA;
    private ID3D11Texture2D? _gpuBloomFarTextureB;
    private ID3D11RenderTargetView? _gpuBloomFarRtvA;
    private ID3D11RenderTargetView? _gpuBloomFarRtvB;
    private ID3D11ShaderResourceView? _gpuBloomFarSrvA;
    private ID3D11ShaderResourceView? _gpuBloomFarSrvB;
    private ID3D11SamplerState? _gpuBloomLinearSampler;
    private ID3D11VertexShader? _gpuBloomFullscreenVs;
    private ID3D11PixelShader? _gpuBloomExtractPs;
    private ID3D11PixelShader? _gpuBloomBlurPs;
    private ID3D11PixelShader? _gpuBloomCompositePs;
    private ID3D11Buffer? _gpuBloomParamsBuffer;
    private bool _gpuBloomSupported;
    private string _gpuBloomDisableReason = "not initialized";
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
        InitializeGpuBloomResources(dotStyleConfig.Bloom);
        _fallbackBitmap = new WriteableBitmap(_surfaceWidth, _surfaceHeight, 96, 96, PixelFormats.Bgra32, null);
        _host.Source = _fallbackBitmap;
        _host.Stretch = Stretch.Fill;

        Console.WriteLine($"[renderer] gpu initialized fastpath leds={width * height} surface={_surfaceWidth}x{_surfaceHeight}");
        var bloomProfile = BloomProfileResolver.Resolve(dotStyleConfig.Bloom);
        Console.WriteLine($"[renderer] gpu bloom enabled={bloomProfile.Enabled} threshold={bloomProfile.Threshold:F2} softKnee={bloomProfile.SoftKnee:F2} scale={bloomProfile.ScaleDivisor} nearRadius={bloomProfile.NearRadius} farRadius={bloomProfile.FarRadius} nearStrength={bloomProfile.NearStrength:F2} farStrength={bloomProfile.FarStrength:F2}");
        if (!_gpuBloomSupported)
        {
            Console.WriteLine($"[renderer] gpu bloom fallback=cpu reason={_gpuBloomDisableReason}");
        }
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
        var map = _context.Map(_frameTexture, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        Marshal.Copy(_bgra, 0, map.DataPointer, _bgra.Length);
        _context.Unmap(_frameTexture, 0);
        ApplyBloomIfEnabled(_style);

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

        // Prefer the GPU bloom path because it avoids CPU-side downsample/blur/composite loops.
        if (_gpuBloomSupported && TryApplyGpuBloom(bloomProfile))
        {
            return;
        }

        // We intentionally preserve the old CPU pipeline as a compatibility fallback.
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

    private void InitializeGpuBloomResources(BloomConfig bloomConfig)
    {
        _gpuBloomSupported = false;
        _gpuBloomDisableReason = "resources not created";
        if (_device is null)
        {
            _gpuBloomDisableReason = "device unavailable";
            return;
        }

        var profile = BloomProfileResolver.Resolve(bloomConfig);
        var downsampleWidth = Math.Max(1, _surfaceWidth / profile.ScaleDivisor);
        var downsampleHeight = Math.Max(1, _surfaceHeight / profile.ScaleDivisor);
        try
        {
            // We compile one small shader pack so bloom stays self-contained in this file.
            using var vsBlob = CompileShader("VSMain", "vs_5_0");
            using var extractBlob = CompileShader("PSExtract", "ps_5_0");
            using var blurBlob = CompileShader("PSBlur", "ps_5_0");
            using var compositeBlob = CompileShader("PSComposite", "ps_5_0");
            _gpuBloomFullscreenVs = _device.CreateVertexShader(vsBlob);
            _gpuBloomExtractPs = _device.CreatePixelShader(extractBlob);
            _gpuBloomBlurPs = _device.CreatePixelShader(blurBlob);
            _gpuBloomCompositePs = _device.CreatePixelShader(compositeBlob);

            _gpuBloomLinearSampler = _device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                MaxLOD = float.MaxValue
            });

            _gpuBloomParamsBuffer = _device.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<GpuBloomParams>(), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
            CreateDownsampleRenderTargets(downsampleWidth, downsampleHeight);
            CreateCompositeRenderTargets();
            _gpuBloomSupported = _gpuBloomFullscreenVs is not null &&
                                 _gpuBloomExtractPs is not null &&
                                 _gpuBloomBlurPs is not null &&
                                 _gpuBloomCompositePs is not null &&
                                 _gpuBloomLinearSampler is not null &&
                                 _gpuBloomParamsBuffer is not null &&
                                 _gpuBloomExtractRtv is not null &&
                                 _gpuBloomNearRtvA is not null &&
                                 _gpuBloomNearRtvB is not null &&
                                 _gpuBloomFarRtvA is not null &&
                                 _gpuBloomFarRtvB is not null &&
                                 _gpuBloomCompositeRtv is not null &&
                                 _gpuBloomReadbackTexture is not null;
            _gpuBloomDisableReason = _gpuBloomSupported ? string.Empty : "incomplete bloom resource allocation";
        }
        catch (Exception ex)
        {
            _gpuBloomDisableReason = ex.Message;
            _gpuBloomSupported = false;
        }
    }

    private bool TryApplyGpuBloom(BloomProfile profile)
    {
        if (_context is null ||
            _frameSrv is null ||
            _gpuBloomExtractRtv is null ||
            _gpuBloomExtractSrv is null ||
            _gpuBloomNearRtvA is null ||
            _gpuBloomNearRtvB is null ||
            _gpuBloomNearSrvA is null ||
            _gpuBloomNearSrvB is null ||
            _gpuBloomFarRtvA is null ||
            _gpuBloomFarRtvB is null ||
            _gpuBloomFarSrvA is null ||
            _gpuBloomFarSrvB is null ||
            _gpuBloomCompositeRtv is null ||
            _gpuBloomCompositeSrv is null ||
            _gpuBloomReadbackTexture is null ||
            _gpuBloomFullscreenVs is null ||
            _gpuBloomExtractPs is null ||
            _gpuBloomBlurPs is null ||
            _gpuBloomCompositePs is null ||
            _gpuBloomLinearSampler is null ||
            _gpuBloomParamsBuffer is null)
        {
            return false;
        }

        // This sequence mirrors a classic bloom post stack: extract -> blur near/far -> composite.
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        _context.VSSetShader(_gpuBloomFullscreenVs);
        _context.PSSetSamplers(0, new[] { _gpuBloomLinearSampler });
        _context.OMSetBlendState(null, default, uint.MaxValue);

        DrawFullscreenPass(_gpuBloomExtractRtv, _gpuBloomExtractPs, _frameSrv, null, null, CreateBloomParams(profile, _surfaceWidth, _surfaceHeight, 0f, 0f));
        DrawBlurLane(_gpuBloomExtractSrv, _gpuBloomNearRtvA, _gpuBloomNearRtvB, _gpuBloomNearSrvA, profile.NearRadius, profile, isNearLane: true);
        DrawBlurLane(_gpuBloomExtractSrv, _gpuBloomFarRtvA, _gpuBloomFarRtvB, _gpuBloomFarSrvA, profile.FarRadius, profile, isNearLane: false);
        DrawFullscreenPass(_gpuBloomCompositeRtv, _gpuBloomCompositePs, _frameSrv, _gpuBloomNearSrvB, _gpuBloomFarSrvB, CreateBloomParams(profile, _surfaceWidth, _surfaceHeight, 0f, 0f));

        _context.CopyResource(_gpuBloomReadbackTexture, _gpuBloomCompositeTexture);
        var map = _context.Map(_gpuBloomReadbackTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        for (var y = 0; y < _surfaceHeight; y++)
        {
            var srcPtr = IntPtr.Add(map.DataPointer, (int)(y * map.RowPitch));
            Marshal.Copy(srcPtr, _bgra, y * _surfaceWidth * 4, _surfaceWidth * 4);
        }
        _context.Unmap(_gpuBloomReadbackTexture, 0);
        return true;
    }

    private void DrawBlurLane(ID3D11ShaderResourceView sourceSrv, ID3D11RenderTargetView laneRtvA, ID3D11RenderTargetView laneRtvB, ID3D11ShaderResourceView laneSrvA, int radius, BloomProfile profile, bool isNearLane)
    {
        if (radius <= 0)
        {
            DrawFullscreenPass(laneRtvB, _gpuBloomBlurPs, sourceSrv, null, null, CreateBloomParams(profile, _downsampleWidth, _downsampleHeight, 0f, 0f));
            return;
        }

        // We run separable blur in two fullscreen draws so the shader stays tiny and predictable.
        DrawFullscreenPass(laneRtvA, _gpuBloomBlurPs, sourceSrv, null, null, CreateBloomParams(profile, _downsampleWidth, _downsampleHeight, radius, 0f));
        DrawFullscreenPass(laneRtvB, _gpuBloomBlurPs, laneSrvA, null, null, CreateBloomParams(profile, _downsampleWidth, _downsampleHeight, 0f, radius));
        _ = isNearLane;
    }

    private void DrawFullscreenPass(ID3D11RenderTargetView targetRtv, ID3D11PixelShader? pixelShader, ID3D11ShaderResourceView sourceSrv, ID3D11ShaderResourceView? nearSrv, ID3D11ShaderResourceView? farSrv, GpuBloomParams parameters)
    {
        if (_context is null || pixelShader is null || _gpuBloomParamsBuffer is null)
        {
            return;
        }

        // We always set a viewport that exactly matches the destination texture dimensions.
        targetRtv.Resource.QueryInterface<ID3D11Texture2D>(out var targetTexture);
        using (targetTexture)
        {
            var desc = targetTexture!.Description;
            _context.RSSetViewport(new Viewport(0, 0, desc.Width, desc.Height, 0, 1));
        }

        _context.OMSetRenderTargets(targetRtv);
        _context.PSSetShader(pixelShader);
        _context.PSSetShaderResources(0, new[] { sourceSrv, nearSrv, farSrv });
        var map = _context.Map(_gpuBloomParamsBuffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        Marshal.StructureToPtr(parameters, map.DataPointer, false);
        _context.Unmap(_gpuBloomParamsBuffer, 0);
        _context.PSSetConstantBuffers(0, new[] { _gpuBloomParamsBuffer });
        _context.Draw(4, 0);
        _context.PSSetShaderResources(0, new ID3D11ShaderResourceView?[] { null, null, null });
    }

    private GpuBloomParams CreateBloomParams(BloomProfile profile, int sourceWidth, int sourceHeight, float directionX, float directionY)
    {
        return new GpuBloomParams
        {
            InvSourceWidth = 1f / Math.Max(1, sourceWidth),
            InvSourceHeight = 1f / Math.Max(1, sourceHeight),
            BlurDirectionX = directionX,
            BlurDirectionY = directionY,
            Threshold = (float)profile.Threshold,
            SoftKnee = (float)profile.SoftKnee,
            NearStrength = (float)profile.NearStrength,
            FarStrength = (float)profile.FarStrength,
            Pad0 = 0,
            Pad1 = 0
        };
    }

    private Blob CompileShader(string entryPoint, string profile)
    {
        var compilation = Compiler.Compile(BloomFullscreenShader, entryPoint, profile, ShaderFlags.OptimizationLevel3);
        if (compilation.HasErrors)
        {
            throw new InvalidOperationException($"Bloom shader compile failed ({entryPoint}): {compilation.Message}");
        }

        return compilation;
    }

    private void CreateDownsampleRenderTargets(int width, int height)
    {
        if (_device is null)
        {
            return;
        }

        _downsampleWidth = width;
        _downsampleHeight = height;
        var desc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            ArraySize = 1,
            MipLevels = 1,
            Format = Format.R8G8B8A8_UNorm,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            Usage = ResourceUsage.Default,
            SampleDescription = new SampleDescription(1, 0),
        };
        _gpuBloomExtractTexture = _device.CreateTexture2D(desc);
        _gpuBloomNearTextureA = _device.CreateTexture2D(desc);
        _gpuBloomNearTextureB = _device.CreateTexture2D(desc);
        _gpuBloomFarTextureA = _device.CreateTexture2D(desc);
        _gpuBloomFarTextureB = _device.CreateTexture2D(desc);
        _gpuBloomExtractRtv = _device.CreateRenderTargetView(_gpuBloomExtractTexture);
        _gpuBloomNearRtvA = _device.CreateRenderTargetView(_gpuBloomNearTextureA);
        _gpuBloomNearRtvB = _device.CreateRenderTargetView(_gpuBloomNearTextureB);
        _gpuBloomFarRtvA = _device.CreateRenderTargetView(_gpuBloomFarTextureA);
        _gpuBloomFarRtvB = _device.CreateRenderTargetView(_gpuBloomFarTextureB);
        _gpuBloomExtractSrv = _device.CreateShaderResourceView(_gpuBloomExtractTexture);
        _gpuBloomNearSrvA = _device.CreateShaderResourceView(_gpuBloomNearTextureA);
        _gpuBloomNearSrvB = _device.CreateShaderResourceView(_gpuBloomNearTextureB);
        _gpuBloomFarSrvA = _device.CreateShaderResourceView(_gpuBloomFarTextureA);
        _gpuBloomFarSrvB = _device.CreateShaderResourceView(_gpuBloomFarTextureB);
    }

    private void CreateCompositeRenderTargets()
    {
        if (_device is null)
        {
            return;
        }

        _gpuBloomCompositeTexture = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Math.Max(1, _surfaceWidth),
            Height = (uint)Math.Max(1, _surfaceHeight),
            ArraySize = 1,
            MipLevels = 1,
            Format = Format.R8G8B8A8_UNorm,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            Usage = ResourceUsage.Default,
            SampleDescription = new SampleDescription(1, 0),
        });
        _gpuBloomCompositeRtv = _device.CreateRenderTargetView(_gpuBloomCompositeTexture);
        _gpuBloomCompositeSrv = _device.CreateShaderResourceView(_gpuBloomCompositeTexture);
        _gpuBloomReadbackTexture = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Math.Max(1, _surfaceWidth),
            Height = (uint)Math.Max(1, _surfaceHeight),
            ArraySize = 1,
            MipLevels = 1,
            Format = Format.R8G8B8A8_UNorm,
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
            BindFlags = BindFlags.None,
            SampleDescription = new SampleDescription(1, 0),
        });
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

    private void DisposeDeviceResources()
    {
        _gpuBloomParamsBuffer?.Dispose();
        _gpuBloomParamsBuffer = null;
        _gpuBloomCompositeSrv?.Dispose();
        _gpuBloomCompositeSrv = null;
        _gpuBloomCompositeRtv?.Dispose();
        _gpuBloomCompositeRtv = null;
        _gpuBloomCompositeTexture?.Dispose();
        _gpuBloomCompositeTexture = null;
        _gpuBloomReadbackTexture?.Dispose();
        _gpuBloomReadbackTexture = null;
        _gpuBloomExtractSrv?.Dispose();
        _gpuBloomExtractSrv = null;
        _gpuBloomExtractRtv?.Dispose();
        _gpuBloomExtractRtv = null;
        _gpuBloomExtractTexture?.Dispose();
        _gpuBloomExtractTexture = null;
        _gpuBloomNearSrvA?.Dispose();
        _gpuBloomNearSrvA = null;
        _gpuBloomNearSrvB?.Dispose();
        _gpuBloomNearSrvB = null;
        _gpuBloomNearRtvA?.Dispose();
        _gpuBloomNearRtvA = null;
        _gpuBloomNearRtvB?.Dispose();
        _gpuBloomNearRtvB = null;
        _gpuBloomNearTextureA?.Dispose();
        _gpuBloomNearTextureA = null;
        _gpuBloomNearTextureB?.Dispose();
        _gpuBloomNearTextureB = null;
        _gpuBloomFarSrvA?.Dispose();
        _gpuBloomFarSrvA = null;
        _gpuBloomFarSrvB?.Dispose();
        _gpuBloomFarSrvB = null;
        _gpuBloomFarRtvA?.Dispose();
        _gpuBloomFarRtvA = null;
        _gpuBloomFarRtvB?.Dispose();
        _gpuBloomFarRtvB = null;
        _gpuBloomFarTextureA?.Dispose();
        _gpuBloomFarTextureA = null;
        _gpuBloomFarTextureB?.Dispose();
        _gpuBloomFarTextureB = null;
        _gpuBloomLinearSampler?.Dispose();
        _gpuBloomLinearSampler = null;
        _gpuBloomFullscreenVs?.Dispose();
        _gpuBloomFullscreenVs = null;
        _gpuBloomExtractPs?.Dispose();
        _gpuBloomExtractPs = null;
        _gpuBloomBlurPs?.Dispose();
        _gpuBloomBlurPs = null;
        _gpuBloomCompositePs?.Dispose();
        _gpuBloomCompositePs = null;
        _gpuBloomSupported = false;
        _gpuBloomDisableReason = "disposed";
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
