using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
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
using DxgiFormat = Vortice.DXGI.Format;
using Viewport = Vortice.Mathematics.Viewport;
using Image = System.Windows.Controls.Image;

namespace VirtualDofMatrix.App.Rendering;

public sealed class GpuInstancedMatrixRenderer : IMatrixRenderer
{
    private const int Channels = 3;
    private const float TemporalSmoothingOffSnapThreshold = 4.0f;
    private const DxgiFormat LedColorTextureFormat = DxgiFormat.R8G8B8A8_UNorm;
    private const string LedColorChannelContract = "RGBA";
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11Buffer? _instanceBuffer;
    private ID3D11Texture2D? _gpuLedColorTexture;
    private ID3D11ShaderResourceView? _gpuLedColorSrv;
    private ID3D11UnorderedAccessView? _gpuLedColorUav;
    private ID3D11RenderTargetView? _gpuLedColorRtv;
    private ID3D11Texture2D? _gpuLedReadbackTexture;
    private ID3D11Texture2D? _gpuRawLedTexture;
    private ID3D11ShaderResourceView? _gpuRawLedSrv;
    private ID3D11Texture2D? _gpuPrevLedTexture;
    private ID3D11UnorderedAccessView? _gpuPrevLedUav;
    private ID3D11Texture2D? _gpuLogicalToRasterTexture;
    private ID3D11ShaderResourceView? _gpuLogicalToRasterSrv;
    private ID3D11Buffer? _gpuPreprocessConstantsBuffer;
    private ID3D11ComputeShader? _gpuPreprocessShader;
    private ID3D11SamplerState? _linearSampler;
    private ID3D11VertexShader? _fullscreenVertexShader;
    private ID3D11PixelShader? _dotPassShader;
    private ID3D11PixelShader? _brightPassShader;
    private ID3D11PixelShader? _blurPassShader;
    private ID3D11PixelShader? _compositeShader;
    private ID3D11Buffer? _bloomConstantsBuffer;
    private ID3D11Texture2D? _gpuBaseTexture;
    private ID3D11RenderTargetView? _gpuBaseRtv;
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
    private bool _directPresentEnabled;
    private bool _directPresentParityValidated;
    private bool _directPresentParitySamplingEnabled;
    private bool _diagnosticReadbackCaptureEnabled;
    private string _directPresentStatus = "uninitialized";
    private IDXGISwapChain1? _directPresentSwapChain;
    private ID3D11Texture2D? _directPresentBackBuffer;
    private ID3D11RenderTargetView? _directPresentBackBufferRtv;
    private int _directPresentBackBufferWidth;
    private int _directPresentBackBufferHeight;
    private bool _gpuBloomSupported;
    private bool _gpuDotPassSupported;
    private bool _useCpuBloomFallback;
    private int _gpuBloomScaleDivisor;
    private static readonly ID3D11ShaderResourceView[] NullPixelShaderSrvs = [null!, null!, null!];
    private string _gpuBloomStage = "idle";
    private string _gpuBloomTrace = string.Empty;
    private ulong _gpuBloomAttemptCount;
    private ulong _interopDisabledFallbackCount;
    private ulong _cpuBloomFallbackCount;
    private ulong _legacyReadbackForcedCount;
    private ulong _lastOutputSequence;
    private RenderLane _renderLane = RenderLane.GpuPrimary;
    private RenderLane? _lastLoggedLane;
    private bool _loggedReadbackForCurrentLane;
    private bool _hasAnyRawLitLed;
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
    private int _surfaceInset;
    private int _dotStride;
    private int _dotSize;
    private int _dotPadding;
    private float[] _dotBodyMask = Array.Empty<float>();
    private float[] _dotCoreMask = Array.Empty<float>();
    private float[] _dotSpecularMask = Array.Empty<float>();
    private static readonly Color4 BlackClearColor = new(0f, 0f, 0f, 0f);
    private byte[] _bgra = Array.Empty<byte>();
    // Note: this is raw RGB byte upload scratch only (no CPU-expanded RGBA/BGRA assembly).
    private byte[] _rawRgbUpload = Array.Empty<byte>();
    // Note: this buffer is readback-only for CPU fallback rendering paths.
    private byte[] _cpuLedReadback = Array.Empty<byte>();
    private float[] _screenBloomSourceRgb = Array.Empty<float>();
    private float[] _screenBloomNearRgb = Array.Empty<float>();
    private float[] _screenBloomFarRgb = Array.Empty<float>();
    private float[] _screenBloomScratchRgb = Array.Empty<float>();
    private int _downsampleWidth;
    private int _downsampleHeight;
    private const float BloomLaneStrengthEpsilon = 0.0001f;
    private ulong _cpuBloomNearBlurPassCount;
    private ulong _cpuBloomFarBlurPassCount;
    private ulong _cpuBloomNearCompositePassCount;
    private ulong _cpuBloomFarCompositePassCount;
    private long _cpuBloomNearBlurTicks;
    private long _cpuBloomFarBlurTicks;
    private long _cpuBloomNearCompositeTicks;
    private long _cpuBloomFarCompositeTicks;
    private bool _transparentBackground;

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
        _transparentBackground = dotStyleConfig.Visual.TransparentBackground;
        // Note: cache these booleans once per initialize pass so the render hot-path stays branch-light.
        _directPresentParitySamplingEnabled = dotStyleConfig.Visual.EnableDirectPresentParitySampling;
        _diagnosticReadbackCaptureEnabled = dotStyleConfig.Visual.EnableDiagnosticReadbackCapture;
        _logicalToRaster = MatrixFrameIndexMap.BuildLogicalToRasterMap(width, height, dotStyleConfig.Mapping);
        ConfigureDotSurface(dotStyleConfig, width, height);
        _bgra = new byte[checked(_surfaceWidth * _surfaceHeight * 4)];
        _rawRgbUpload = new byte[checked(width * height * Channels)];
        _cpuLedReadback = new byte[checked(width * height * 4)];
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

        _fallbackBitmap = new WriteableBitmap(_surfaceWidth, _surfaceHeight, 96, 96, PixelFormats.Bgra32, null);
        _host.Source = _fallbackBitmap;
        _host.Stretch = Stretch.Fill;
        _directPresentEnabled = false;
        _directPresentParityValidated = false;
        _directPresentStatus = "waiting-for-bloom-init";
        InitializeGpuBloomPipeline();

        AppLogger.Info($"[renderer] gpu initialized fastpath leds={width * height} surface={_surfaceWidth}x{_surfaceHeight}");
        var bloomProfile = BloomProfileResolver.Resolve(dotStyleConfig.Bloom);
        AppLogger.Info($"[renderer] gpu bloom enabled={bloomProfile.Enabled} threshold={bloomProfile.Threshold:F2} softKnee={bloomProfile.SoftKnee:F2} scale={bloomProfile.ScaleDivisor} nearRadius={bloomProfile.NearRadius} farRadius={bloomProfile.FarRadius} nearStrength={bloomProfile.NearStrength:F2} farStrength={bloomProfile.FarStrength:F2}");
        AppLogger.Info($"[renderer] gpu direct present mode={dotStyleConfig.Visual.GpuPresentMode} enabled={_directPresentEnabled} status={_directPresentStatus} paritySampling={_directPresentParitySamplingEnabled} diagnosticCapture={_diagnosticReadbackCaptureEnabled}");
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
        if (_context is null || _fallbackBitmap is null || _style is null)
        {
            return;
        }

        FramePresentation frame;
        lock (_gate)
        {
            frame = _stagedFrame;
        }
        _lastOutputSequence = frame.OutputSequence;

        var rgb = frame.RgbMemory.Span;
        var ledCount = Math.Min(_logicalToRaster.Length, rgb.Length / Channels);
        DispatchLedPreprocess(rgb, ledCount, _style);

        var useGpuDotPass = ShouldUseGpuDotPass(_style);
        if (useGpuDotPass)
        {
            // Note: preprocessing now writes directly into the GPU LED texture, so we go straight to dot shading.
            if (!RenderGpuDotsToBaseTexture())
            {
                useGpuDotPass = false;
            }
        }

        _renderLane = ResolveRenderLane(useGpuDotPass);
        LogLaneTransitionIfNeeded();

        if (_renderLane == RenderLane.CpuDotFallback)
        {
            // Note: compatibility path keeps the original CPU dot raster behavior exactly intact.
            Array.Clear(_bgra, 0, _bgra.Length);
            EnsureBackgroundAlpha(_bgra, _transparentBackground);
            if (!TryReadProcessedLedsToCpu("cpu-dot-fallback"))
            {
                return;
            }

            for (var raster = 0; raster < _width * _height; raster++)
            {
                var ledOffset = raster * 4;
                var r = _cpuLedReadback[ledOffset];
                var g = _cpuLedReadback[ledOffset + 1];
                var b = _cpuLedReadback[ledOffset + 2];
                var baseX = _surfaceInset + ((raster % _width) * _dotStride);
                var baseY = _surfaceInset + ((raster / _width) * _dotStride);
                RasterFastDot(baseX, baseY, r, g, b);
            }
        }

        ApplyBloomIfEnabled(_style, useGpuDotPass, _renderLane);

        if (!_directPresentEnabled)
        {
            PresentFallbackBitmap(_fallbackBitmap, _bgra, _surfaceWidth, _surfaceHeight);
        }
    }

    public void Dispose() => DisposeDeviceResources();

    public void Clear()
    {
        if (_fallbackBitmap is null)
        {
            return;
        }

        Array.Clear(_bgra, 0, _bgra.Length);
        if (_context is not null && _gpuLedColorRtv is not null)
        {
            _context.ClearRenderTargetView(_gpuLedColorRtv, BlackClearColor);
        }
        if (_context is not null && _gpuPrevLedTexture is not null)
        {
            var prevInit = new byte[Math.Max(16, _width * _height * 16)];
            var prevHandle = GCHandle.Alloc(prevInit, GCHandleType.Pinned);
            try
            {
                _context.UpdateSubresource(_gpuPrevLedTexture, 0, null, prevHandle.AddrOfPinnedObject(), (uint)Math.Max(16, _width * 16), 0);
            }
            finally
            {
                prevHandle.Free();
            }
        }
        PresentFallbackBitmap(_fallbackBitmap, _bgra, _surfaceWidth, _surfaceHeight);
    }

    private void DispatchLedPreprocess(ReadOnlySpan<byte> rgb, int ledCount, DotStyleConfig style)
    {
        if (_context is null || _gpuRawLedTexture is null || _gpuLedColorUav is null || _gpuLedColorRtv is null ||
            _gpuRawLedSrv is null || _gpuLogicalToRasterSrv is null || _gpuPrevLedUav is null ||
            _gpuPreprocessConstantsBuffer is null || _gpuPreprocessShader is null)
        {
            return;
        }

        // Note: per-frame upload stays raw-only (RgbMemory bytes) and never assembles full-color LED texels on CPU.
        var byteCount = ledCount * Channels;
        rgb[..byteCount].CopyTo(_rawRgbUpload);
        if (byteCount < _rawRgbUpload.Length)
        {
            Array.Clear(_rawRgbUpload, byteCount, _rawRgbUpload.Length - byteCount);
        }

        _hasAnyRawLitLed = false;
        for (var i = 0; i < byteCount; i++)
        {
            if (_rawRgbUpload[i] > 0)
            {
                _hasAnyRawLitLed = true;
                break;
            }
        }

        var handle = GCHandle.Alloc(_rawRgbUpload, GCHandleType.Pinned);
        try
        {
            _context.UpdateSubresource(_gpuRawLedTexture, 0, null, handle.AddrOfPinnedObject(), (uint)_rawRgbUpload.Length, 0);
        }
        finally
        {
            handle.Free();
        }

        var constants = new LedPreprocessGpuConstants
        {
            LedCount = (uint)ledCount,
            RasterCount = (uint)(_width * _height),
            Width = (uint)_width,
            Height = (uint)_height,
            SmoothingEnabled = style.TemporalSmoothing.Enabled ? 1u : 0u,
            RiseAlpha = (float)Math.Clamp(style.TemporalSmoothing.RiseAlpha, 0.0, 1.0),
            FallAlpha = (float)Math.Clamp(style.TemporalSmoothing.FallAlpha, 0.0, 1.0),
            Brightness = (float)Math.Clamp(style.Brightness, 0.0, 1.0),
            Gamma = (float)Math.Clamp(style.Gamma, 0.1, 5.0),
            ToneEnabled = style.ToneMapping.Enabled ? 1u : 0u,
            ToneKneeStart = (float)Math.Clamp(style.ToneMapping.KneeStart, 1.0, 2.0),
            ToneStrength = (float)Math.Clamp(style.ToneMapping.Strength, 0.0, 8.0),
            OffSnapThreshold = TemporalSmoothingOffSnapThreshold,
        };

        var constantsHandle = GCHandle.Alloc(new[] { constants }, GCHandleType.Pinned);
        try
        {
            _context.UpdateSubresource(_gpuPreprocessConstantsBuffer, 0, null, constantsHandle.AddrOfPinnedObject(), (uint)Marshal.SizeOf<LedPreprocessGpuConstants>(), 0);
        }
        finally
        {
            constantsHandle.Free();
        }

        // Note: every dispatch starts from a black raster so short packets cannot leave stale LED colors behind.
        _context.ClearRenderTargetView(_gpuLedColorRtv, BlackClearColor);
        _context.CSSetShader(_gpuPreprocessShader);
        _context.CSSetConstantBuffer(1, _gpuPreprocessConstantsBuffer);
        // Note: preprocess shader intentionally uses t3/t4 so it can coexist with bloom bindings in shared HLSL.
        _context.CSSetShaderResource(3, _gpuRawLedSrv);
        _context.CSSetShaderResource(4, _gpuLogicalToRasterSrv);
        _context.CSSetUnorderedAccessView(0, _gpuPrevLedUav);
        _context.CSSetUnorderedAccessView(1, _gpuLedColorUav);
        _context.Dispatch((uint)Math.Max(1, (ledCount + 63) / 64), 1, 1);
        _context.CSSetUnorderedAccessView(0, null);
        _context.CSSetUnorderedAccessView(1, null);
        _context.CSSetShaderResource(3, null);
        _context.CSSetShaderResource(4, null);
        _context.CSSetShader(null);
    }

    private bool TryReadProcessedLedsToCpu(string reason)
    {
        if (_renderLane == RenderLane.GpuPrimary)
        {
            AppLogger.Warn($"[renderer] readback suppressed on GPU primary lane. reason={reason} seq={_lastOutputSequence}");
            return false;
        }

        if (_context is null || _gpuLedColorTexture is null || _gpuLedReadbackTexture is null)
        {
            return false;
        }

        if (!_loggedReadbackForCurrentLane)
        {
            AppLogger.Info($"[renderer] gpu led readback entered reason={reason} seq={_lastOutputSequence} lane={_renderLane}");
            _loggedReadbackForCurrentLane = true;
        }
        _context.CopyResource(_gpuLedReadbackTexture, _gpuLedColorTexture);
        var mapped = _context.Map(_gpuLedReadbackTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            // Note: `_cpuLedReadback` stores LED bytes in RGBA order (R+0,G+1,B+2), matching the preprocess texture contract.
            ReadBgraRows(mapped.DataPointer, mapped.RowPitch, _cpuLedReadback, _width, _height);
            return true;
        }
        finally
        {
            _context.Unmap(_gpuLedReadbackTexture, 0);
        }
    }

    private void ConfigureDotSurface(DotStyleConfig style, int width, int height)
    {
        if (style.FillGapEnabled)
        {
            // Fill-gap mode computes final cell coverage from surface size in the shader, so we keep the adaptive
            // dot size here to ensure the backing surface itself scales up to viewport-sized geometry.
            _dotSize = Math.Max(1, style.DotSize);
        }
        else if (style.DotShape.Equals("circle", StringComparison.OrdinalIgnoreCase) && !style.Visual.FlatShading)
        {
            // Note: keep dot sizing aligned with viewport-adaptive sizing from window config so GPU and CPU layouts match.
            _dotSize = Math.Max(1, style.DotSize);
        }
        else
        {
            _dotSize = 1;
        }

        // Keep spacing behavior shape-agnostic and inter-dot only: spacing belongs between cells, not around viewport edges.
        _dotPadding = Math.Max(0, style.DotSpacing);
        _dotStride = _dotSize + _dotPadding;
        _surfaceInset = style.Bloom.Enabled ? Math.Max(0, style.Bloom.FarRadiusPx) : 0;
        _surfaceWidth = (width * _dotSize) + (Math.Max(0, width - 1) * _dotPadding) + (_surfaceInset * 2);
        _surfaceHeight = (height * _dotSize) + (Math.Max(0, height - 1) * _dotPadding) + (_surfaceInset * 2);
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

    private void ApplyBloomIfEnabled(DotStyleConfig style, bool baseFrameIsGpuRendered, RenderLane lane)
    {
        var bloomProfile = BloomProfileResolver.Resolve(style.Bloom);
        // If both lanes are effectively off, skip bloom and keep the frame path cheap.
        if (!bloomProfile.Enabled || (bloomProfile.NearStrength <= 0.0 && bloomProfile.FarStrength <= 0.0))
        {
            if (baseFrameIsGpuRendered)
            {
                // Note: when bloom is disabled, we still need to present the GPU dot base frame so off-state bulbs stay visible.
                TryPresentGpuBaseWithoutBloom("bloom-disabled");
            }
            return;
        }
        // If there are no lit LEDs in this frame, we skip bloom so "off bulb" shading stays clean.
        if (!HasAnyLitLed())
        {
            if (baseFrameIsGpuRendered)
            {
                // Note: no emissive pixels still means we should show off-state bulbs from the GPU dot pass.
                TryPresentGpuBaseWithoutBloom("no-emissive-leds");
            }
            return;
        }

        // We only run shader bloom when this frame actually has a GPU-rendered base surface.
        // Note: CPU dot fallback surfaces are authored in `_bgra`; running GPU bloom there can
        // composite against stale/black GPU buffers and produce full-strip dark bars in transparent modes.
        if (baseFrameIsGpuRendered && !_useCpuBloomFallback && _gpuBloomSupported)
        {
            try
            {
                if (TryApplyGpuBloom(bloomProfile, baseFrameIsGpuRendered))
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                // Note: if GPU bloom faults mid-frame, we rebuild bloom resources once before dropping to CPU fallback.
                TryRecoverGpuBloomPipeline(ex);
                AppLogger.Warn($"[renderer] gpu bloom execution failed at stage='{_gpuBloomStage}'; switching to CPU fallback. reason={ex.Message} {TryGetDeviceRemovedReasonText()} trace={_gpuBloomTrace}");
            }
        }

        if (!_useCpuBloomFallback)
        {
            _useCpuBloomFallback = true;
            _cpuBloomFallbackCount++;
            LogFallbackCounter("cpu bloom fallback", _cpuBloomFallbackCount, $"seq={_lastOutputSequence} stage={_gpuBloomStage}");
        }
        if (lane != RenderLane.CpuDotFallback)
        {
            _renderLane = RenderLane.CpuBloomFallback;
            LogLaneTransitionIfNeeded();
        }

        if (baseFrameIsGpuRendered && _context is not null && _gpuBaseTexture is not null && _gpuReadbackTexture is not null)
        {
            // Note: CPU bloom expects a CPU-side BGRA surface, so we read back only when GPU dots were used.
            _context.CopyResource(_gpuReadbackTexture, _gpuBaseTexture);
            var readback = _context.Map(_gpuReadbackTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            ReadBgraRows(readback.DataPointer, readback.RowPitch, _bgra, _surfaceWidth, _surfaceHeight);
            _context.Unmap(_gpuReadbackTexture, 0);
        }

        // Extract emissive data from the final rasterized surface so bloom feels spatially natural.
        if (!DownsampleEmissive(_bgra, _surfaceWidth, _surfaceHeight, bloomProfile, out var minBloomX, out var minBloomY, out var maxBloomX, out var maxBloomY))
        {
            return;
        }

        // Split into near/far blur lanes so we can shape a tight glow plus a soft halo.
        var effectiveNearRadius = GetEffectiveBloomRadius(bloomProfile.NearRadius, bloomProfile.ScaleDivisor, _dotSize);
        var effectiveFarRadius = GetEffectiveBloomRadius(bloomProfile.FarRadius, bloomProfile.ScaleDivisor, _dotSize);
        // Note: we only keep a lane alive if both radius and strength make it materially visible.
        var nearActive = effectiveNearRadius > 0 && bloomProfile.NearStrength > BloomLaneStrengthEpsilon;
        var farActive = effectiveFarRadius > 0 && bloomProfile.FarStrength > BloomLaneStrengthEpsilon;
        if (!nearActive && !farActive)
        {
            return;
        }

        // Near bloom uses a smooth blur so tiny radii don't hard-light an entire adjacent dot.
        if (nearActive)
        {
            Array.Copy(_screenBloomSourceRgb, _screenBloomNearRgb, _screenBloomSourceRgb.Length);
            var nearBlurStart = Stopwatch.GetTimestamp();
            BoxBlurRgbSeparable(_screenBloomNearRgb, _downsampleWidth, _downsampleHeight, effectiveNearRadius);
            _cpuBloomNearBlurTicks += Stopwatch.GetTimestamp() - nearBlurStart;
            _cpuBloomNearBlurPassCount++;
        }

        if (farActive)
        {
            Array.Copy(_screenBloomSourceRgb, _screenBloomFarRgb, _screenBloomSourceRgb.Length);
            var farBlurStart = Stopwatch.GetTimestamp();
            BoxBlurRgbSeparable(_screenBloomFarRgb, _downsampleWidth, _downsampleHeight, effectiveFarRadius);
            _cpuBloomFarBlurTicks += Stopwatch.GetTimestamp() - farBlurStart;
            _cpuBloomFarBlurPassCount++;
        }

        var effectiveNearStrength = (float)bloomProfile.NearStrength;
        var effectiveFarStrength = (float)bloomProfile.FarStrength;
        var compositeStart = Stopwatch.GetTimestamp();
        if (nearActive && farActive)
        {
            CompositeBloom(_bgra, _surfaceWidth, _surfaceHeight, _screenBloomNearRgb, _screenBloomFarRgb, _downsampleWidth, _downsampleHeight, minBloomX, minBloomY, maxBloomX, maxBloomY, effectiveNearRadius, effectiveFarRadius, effectiveNearStrength, effectiveFarStrength, bloomProfile, _transparentBackground);
            _cpuBloomNearCompositePassCount++;
            _cpuBloomFarCompositePassCount++;
        }
        else if (nearActive)
        {
            CompositeBloomSingleLane(_bgra, _surfaceWidth, _surfaceHeight, _screenBloomNearRgb, _downsampleWidth, _downsampleHeight, minBloomX, minBloomY, maxBloomX, maxBloomY, effectiveNearRadius, effectiveNearStrength, bloomProfile, _transparentBackground);
            _cpuBloomNearCompositePassCount++;
        }
        else
        {
            CompositeBloomSingleLane(_bgra, _surfaceWidth, _surfaceHeight, _screenBloomFarRgb, _downsampleWidth, _downsampleHeight, minBloomX, minBloomY, maxBloomX, maxBloomY, effectiveFarRadius, effectiveFarStrength, bloomProfile, _transparentBackground);
            _cpuBloomFarCompositePassCount++;
        }

        var compositeTicks = Stopwatch.GetTimestamp() - compositeStart;
        if (nearActive)
        {
            _cpuBloomNearCompositeTicks += compositeTicks;
        }

        if (farActive)
        {
            _cpuBloomFarCompositeTicks += compositeTicks;
        }

        LogCpuBloomLaneCountersIfNeeded();
    }

    private void LogCpuBloomLaneCountersIfNeeded()
    {
        if (ShouldLogBloomCounter(_cpuBloomNearBlurPassCount))
        {
            var avgNearBlurMs = ToMilliseconds(_cpuBloomNearBlurTicks, _cpuBloomNearBlurPassCount);
            var avgNearCompositeMs = ToMilliseconds(_cpuBloomNearCompositeTicks, _cpuBloomNearCompositePassCount);
            AppLogger.Info($"[renderer] cpu bloom near lane blurPasses={_cpuBloomNearBlurPassCount} compositePasses={_cpuBloomNearCompositePassCount} avgBlurMs={avgNearBlurMs:F4} avgCompositeMs={avgNearCompositeMs:F4}");
        }

        if (ShouldLogBloomCounter(_cpuBloomFarBlurPassCount))
        {
            var avgFarBlurMs = ToMilliseconds(_cpuBloomFarBlurTicks, _cpuBloomFarBlurPassCount);
            var avgFarCompositeMs = ToMilliseconds(_cpuBloomFarCompositeTicks, _cpuBloomFarCompositePassCount);
            AppLogger.Info($"[renderer] cpu bloom far lane blurPasses={_cpuBloomFarBlurPassCount} compositePasses={_cpuBloomFarCompositePassCount} avgBlurMs={avgFarBlurMs:F4} avgCompositeMs={avgFarCompositeMs:F4}");
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

    private void TryPresentGpuBaseWithoutBloom(string reason)
    {
        if (_context is null || _gpuBaseTexture is null || _gpuCompositeTexture is null)
        {
            return;
        }

        _context.CopyResource(_gpuCompositeTexture, _gpuBaseTexture);
        var trace = new StringBuilder($"base-present:{reason}");
        TryPresentGpuCompositeFrame(trace);
    }

    private bool TryApplyGpuBloom(BloomProfile profile, bool baseFrameIsGpuRendered)
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

        if (!baseFrameIsGpuRendered)
        {
            // Note: CPU dots still render into _bgra, so we upload that only for the fallback dot path.
            _gpuBloomStage = "upload-base";
            trace.Append(" ->upload-base");
            _gpuBloomTrace = trace.ToString();
            UploadCpuSurfaceToGpuBase();
        }

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

        // Note: explicitly switch away from the composite RTV before CopyResource.
        _gpuBloomStage = "unbind-composite";
        trace.Append(" ->unbind-composite");
        _gpuBloomTrace = trace.ToString();
        _context.OMSetRenderTargets(_gpuBrightRtv);

        _gpuBloomStage = "present";
        trace.Append(" ->present");
        _gpuBloomTrace = trace.ToString();
        if (!TryPresentGpuCompositeFrame(trace))
        {
            return false;
        }
        _gpuBloomStage = "done";
        trace.Append($" ->done elapsedMs={(DateTime.UtcNow - started).TotalMilliseconds:F3}");
        _gpuBloomTrace = trace.ToString();
        return true;
    }

    private bool TryPresentGpuCompositeFrame(StringBuilder trace)
    {
        if (_context is null || _gpuCompositeTexture is null)
        {
            return false;
        }

        if (IsLegacyReadbackMode())
        {
            if (_directPresentStatus != "forced:legacy-readback")
            {
                // Note: this switch is a user escape hatch for systems where WPF interop is flaky.
                _directPresentStatus = "forced:legacy-readback";
                AppLogger.Info("[renderer] gpu direct present bypassed by config; using legacy readback.");
            }

            _legacyReadbackForcedCount++;
            LogFallbackCounter("legacy readback forced", _legacyReadbackForcedCount, $"seq={_lastOutputSequence}");
            return TryReadbackCompositeToCpu(trace);
        }

        // Note: when direct present is online, we intentionally avoid GPU->CPU readback.
        if (_directPresentEnabled && _context is not null && _directPresentSwapChain is not null && _directPresentBackBuffer is not null)
        {
            try
            {
                BlitCompositeToDirectPresentBackBuffer();
                _directPresentSwapChain.Present(0, PresentFlags.None);
                trace.Append(" direct-present");

                // Note: parity readback is opt-in so normal direct present does not touch CPU readback.
                if (_directPresentParitySamplingEnabled && !_directPresentParityValidated)
                {
                    _directPresentParityValidated = TryCaptureFallbackReadbackSample(trace);
                }

                return true;
            }
            catch (Exception ex)
            {
                _directPresentEnabled = false;
                _directPresentBackBufferRtv?.Dispose();
                _directPresentBackBufferRtv = null;
                _directPresentStatus = $"disabled:interop-present-failed:{ex.GetType().Name}";
                if (_host is not null && _fallbackBitmap is not null)
                {
                    _host.Source = _fallbackBitmap;
                }
                AppLogger.Warn($"[renderer] gpu direct present disabled during frame; falling back to readback. reason={ex.Message}");
            }
        }

        if (!_directPresentEnabled)
        {
            _interopDisabledFallbackCount++;
            LogFallbackCounter("interop disabled", _interopDisabledFallbackCount, $"seq={_lastOutputSequence} status={_directPresentStatus}");
            trace.Append(" interop-disabled-fallback-readback");
        }

        if (_host is not null && _fallbackBitmap is not null)
        {
            _host.Source = _fallbackBitmap;
        }

        return TryReadbackCompositeToCpu(trace);
    }

    private void BlitCompositeToDirectPresentBackBuffer()
    {
        if (_context is null || _gpuCompositeTexture is null || _directPresentBackBuffer is null || _directPresentBackBufferRtv is null)
        {
            return;
        }

        // Note: copy composed output centered into the present buffer without scaling to preserve CPU-parity dot geometry.
        var visual = _style?.Visual;
        var clearColor = visual?.BackgroundVisible is false
            ? BlackClearColor
            : new Color4(
                Math.Clamp(visual?.BackgroundColorR ?? 0f, 0f, 1f),
                Math.Clamp(visual?.BackgroundColorG ?? 0f, 0f, 1f),
                Math.Clamp(visual?.BackgroundColorB ?? 0f, 0f, 1f),
                1f);
        _context.ClearRenderTargetView(_directPresentBackBufferRtv, clearColor);
        var dstWidth = Math.Max(1, _directPresentBackBufferWidth);
        var dstHeight = Math.Max(1, _directPresentBackBufferHeight);
        var copyWidth = Math.Min(dstWidth, _surfaceWidth);
        var copyHeight = Math.Min(dstHeight, _surfaceHeight);
        var dstX = (dstWidth - copyWidth) / 2;
        var dstY = (dstHeight - copyHeight) / 2;
        var srcX = (_surfaceWidth - copyWidth) / 2;
        var srcY = (_surfaceHeight - copyHeight) / 2;
        var srcBox = new Box(srcX, srcY, 0, srcX + copyWidth, srcY + copyHeight, 1);
        _context.CopySubresourceRegion(_directPresentBackBuffer, 0, (uint)dstX, (uint)dstY, 0, _gpuCompositeTexture, 0, srcBox);
    }

    private bool ShouldUseGpuDotPass(DotStyleConfig style)
    {
        if (style.ForceCpuDotRasterFallback)
        {
            return false;
        }

        return _gpuDotPassSupported &&
               _context is not null &&
               _gpuLedColorTexture is not null &&
               _gpuLedColorRtv is not null &&
               _gpuLedColorUav is not null &&
               _gpuLedColorSrv is not null &&
               _gpuRawLedTexture is not null &&
               _gpuRawLedSrv is not null &&
               _gpuPreprocessConstantsBuffer is not null &&
               _gpuPreprocessShader is not null &&
               _gpuBaseRtv is not null &&
               _gpuBaseSrv is not null &&
               _dotPassShader is not null;
    }

    private RenderLane ResolveRenderLane(bool gpuDotPassReady)
    {
        if (!gpuDotPassReady)
        {
            return RenderLane.CpuDotFallback;
        }

        if (_useCpuBloomFallback)
        {
            return RenderLane.CpuBloomFallback;
        }

        return RenderLane.GpuPrimary;
    }

    private void LogLaneTransitionIfNeeded()
    {
        if (_lastLoggedLane == _renderLane)
        {
            return;
        }

        AppLogger.Info($"[renderer] lane={_renderLane} seq={_lastOutputSequence}");
        _lastLoggedLane = _renderLane;
        _loggedReadbackForCurrentLane = false;
    }

    private bool IsLegacyReadbackMode()
    {
        var mode = _style?.Visual.GpuPresentMode;
        return mode is not null && mode.Equals("LegacyReadback", StringComparison.OrdinalIgnoreCase);
    }

    private static void LogFallbackCounter(string label, ulong count, string detail)
    {
        // Note: this throttles noise while still giving clear progression (1,2,4,8...).
        if (count <= 3 || (count & (count - 1)) == 0)
        {
            AppLogger.Info($"[renderer] fallback trigger '{label}' count={count} {detail}");
        }
    }

    private void UploadCpuSurfaceToGpuBase()
    {
        if (_context is null || _gpuBaseTexture is null)
        {
            return;
        }

        var handle = GCHandle.Alloc(_bgra, GCHandleType.Pinned);
        try
        {
            _context.UpdateSubresource(_gpuBaseTexture, 0, null, handle.AddrOfPinnedObject(), (uint)(_surfaceWidth * 4), 0);
        }
        finally
        {
            handle.Free();
        }
    }

    private bool RenderGpuDotsToBaseTexture()
    {
        if (_context is null || _dotPassShader is null || _fullscreenVertexShader is null || _linearSampler is null || _bloomConstantsBuffer is null || _gpuBaseRtv is null || _gpuLedColorSrv is null)
        {
            return false;
        }

        SetBloomConstants(null, radius: _dotSize, directionX: _dotStride, directionY: _dotPadding);
        _context.OMSetRenderTargets(_gpuBaseRtv);
        _context.RSSetViewport(new Viewport(0, 0, _surfaceWidth, _surfaceHeight, 0f, 1f));
        _context.VSSetShader(_fullscreenVertexShader);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.PSSetShader(_dotPassShader);
        _context.PSSetShaderResource(0, _gpuLedColorSrv);
        _context.PSSetSampler(0, _linearSampler);
        _context.PSSetConstantBuffer(0, _bloomConstantsBuffer);
        _context.Draw(3, 0);
        _context.PSSetShaderResources(0, NullPixelShaderSrvs);
        return true;
    }

    private bool TryReadbackCompositeToCpu(StringBuilder trace)
    {
        if (_context is null || _gpuReadbackTexture is null || _gpuCompositeTexture is null)
        {
            return false;
        }

        _gpuBloomStage = "copy-readback";
        trace.Append(" ->copy-readback");
        _gpuBloomTrace = trace.ToString();
        _context.CopyResource(_gpuReadbackTexture, _gpuCompositeTexture);
        _gpuBloomStage = "map-readback";
        trace.Append(" ->map-readback");
        _gpuBloomTrace = trace.ToString();
        var readback = _context.Map(_gpuReadbackTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        trace.Append($"(rowPitch={readback.RowPitch})");
        _gpuBloomTrace = trace.ToString();
        ReadBgraRows(readback.DataPointer, readback.RowPitch, _bgra, _surfaceWidth, _surfaceHeight);
        _context.Unmap(_gpuReadbackTexture, 0);
        return true;
    }

    private bool TryCaptureFallbackReadbackSample(StringBuilder trace)
    {
        if (_context is null || _gpuReadbackTexture is null || _gpuCompositeTexture is null)
        {
            return false;
        }

        // Note: this is a one-time sample to validate the legacy fallback path still sees the same composed frame.
        _context.CopyResource(_gpuReadbackTexture, _gpuCompositeTexture);
        if (_diagnosticReadbackCaptureEnabled)
        {
            // Note: explicit diagnostic capture mode is the only place we keep a forced flush.
            _context.Flush();
        }
        var readback = _context.Map(_gpuReadbackTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            trace.Append($" paritySample(rowPitch={readback.RowPitch})");
            return true;
        }
        finally
        {
            _context.Unmap(_gpuReadbackTexture, 0);
            AppLogger.Info("[renderer] gpu direct present parity sample captured with legacy readback path.");
        }
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

        var visual = _style?.Visual;
        var constants = new BloomGpuConstants
        {
            Threshold = (float)(profile?.Threshold ?? 0.0),
            SoftKnee = (float)(profile?.SoftKnee ?? 0.0),
            NearStrength = (float)(profile?.NearStrength ?? 0.0),
            FarStrength = (float)(profile?.FarStrength ?? 0.0),
            ScaleDivisor = (float)(profile?.ScaleDivisor ?? Math.Max(1, _gpuBloomScaleDivisor)),
            // Note: blur pass radius must stay bounded (shader loops are fixed to +/-8),
            // but dot pass uses this same slot for actual dot size and must not be clamped.
            Radius = profile is null ? Math.Max(0f, radius) : Math.Clamp(radius, 0f, 8f),
            DirectionX = directionX,
            DirectionY = directionY,
            SurfaceOffsetX = _surfaceInset,
            SurfaceOffsetY = _surfaceInset,
            SurfaceWidth = _surfaceWidth,
            SurfaceHeight = _surfaceHeight,
            BloomWidth = profile is null ? _width : _downsampleWidth,
            BloomHeight = profile is null ? _height : _downsampleHeight,
            DotShapeCircle = _style is not null && _style.DotShape.Equals("circle", StringComparison.OrdinalIgnoreCase) ? 1f : 0f,
            FillGapEnabled = _style?.FillGapEnabled is true ? 1f : 0f,
            FlatShading = visual?.FlatShading is true ? 1f : 0f,
            FullBrightnessRadius = (float)Math.Clamp(visual?.FullBrightnessRadiusMinPct ?? 0.8, 0.0, 1.0),
            OffStateAlpha = (float)Math.Clamp(visual?.OffStateAlpha ?? 0.0, 0.0, 1.0),
            OffTintR = (visual?.OffStateTintR ?? 0) / 255f,
            OffTintG = (visual?.OffStateTintG ?? 0) / 255f,
            OffTintB = (visual?.OffStateTintB ?? 0) / 255f,
            LensFalloff = (float)Math.Clamp(visual?.LensFalloff ?? 0.45, 0.0, 1.0),
            SpecularHotspot = (float)Math.Clamp(visual?.SpecularHotspot ?? 0.28, 0.0, 1.0),
            RimHighlight = (float)Math.Clamp(visual?.RimHighlight ?? 0.22, 0.0, 1.0),
            BackgroundVisible = visual?.BackgroundVisible is false ? 0f : 1f,
            BackgroundColorR = Math.Clamp(visual?.BackgroundColorR ?? 0f, 0f, 1f),
            BackgroundColorG = Math.Clamp(visual?.BackgroundColorG ?? 0f, 0f, 1f),
            BackgroundColorB = Math.Clamp(visual?.BackgroundColorB ?? 0f, 0f, 1f),
            Padding0 = 0f,
            Padding1 = 0f,
            Padding2 = 0f,
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

    private static void CompositeBloom(byte[] target, int width, int height, float[] nearBlur, float[] farBlur, int bloomWidth, int bloomHeight, int minBloomX, int minBloomY, int maxBloomX, int maxBloomY, int effectiveNearRadius, int effectiveFarRadius, float effectiveNearStrength, float effectiveFarStrength, BloomProfile profile, bool transparentBackground)
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
                if (transparentBackground)
                {
                    // Note: alpha should reflect bloom contribution only, not pre-existing dark off-state shading.
                    var bloomR = (nearR * nearStrength) + (farR * farStrength);
                    var bloomG = (nearG * nearStrength) + (farG * farStrength);
                    var bloomB = (nearB * nearStrength) + (farB * farStrength);
                    var alpha = (byte)Math.Clamp(Math.Max(bloomR, Math.Max(bloomG, bloomB)), 0f, 255f);
                    if (alpha < 6)
                    {
                        alpha = 0;
                    }
                    target[targetOffset + 3] = (byte)Math.Max(target[targetOffset + 3], alpha);
                }
            }
        }
    }

    private static void CompositeBloomSingleLane(byte[] target, int width, int height, float[] laneBlur, int bloomWidth, int bloomHeight, int minBloomX, int minBloomY, int maxBloomX, int maxBloomY, int effectiveLaneRadius, float effectiveLaneStrength, BloomProfile profile, bool transparentBackground)
    {
        var laneStrength = effectiveLaneStrength;
        var pad = effectiveLaneRadius + 1;
        var startX = Math.Max(0, (minBloomX - pad) * profile.ScaleDivisor);
        var startY = Math.Max(0, (minBloomY - pad) * profile.ScaleDivisor);
        var endX = Math.Min(width - 1, ((maxBloomX + pad + 1) * profile.ScaleDivisor) - 1);
        var endY = Math.Min(height - 1, ((maxBloomY + pad + 1) * profile.ScaleDivisor) - 1);

        for (var y = startY; y <= endY; y++)
        {
            for (var x = startX; x <= endX; x++)
            {
                // Note: this path intentionally samples one lane only, so we skip the missing texture fetch entirely.
                var bloomU = ((x + 0.5f) / profile.ScaleDivisor) - 0.5f;
                var bloomV = ((y + 0.5f) / profile.ScaleDivisor) - 0.5f;
                var laneR = SampleBilinear(laneBlur, bloomWidth, bloomHeight, bloomU, bloomV, 0);
                var laneG = SampleBilinear(laneBlur, bloomWidth, bloomHeight, bloomU, bloomV, 1);
                var laneB = SampleBilinear(laneBlur, bloomWidth, bloomHeight, bloomU, bloomV, 2);

                var targetOffset = ((y * width) + x) * 4;
                target[targetOffset + 2] = (byte)Math.Clamp(target[targetOffset + 2] + (laneR * laneStrength), 0f, 255f);
                target[targetOffset + 1] = (byte)Math.Clamp(target[targetOffset + 1] + (laneG * laneStrength), 0f, 255f);
                target[targetOffset] = (byte)Math.Clamp(target[targetOffset] + (laneB * laneStrength), 0f, 255f);
                if (transparentBackground)
                {
                    // Note: preserve off-state dots while only expanding alpha where bloom actually adds light.
                    var alpha = (byte)Math.Clamp(Math.Max(laneR, Math.Max(laneG, laneB)) * laneStrength, 0f, 255f);
                    if (alpha < 6)
                    {
                        alpha = 0;
                    }
                    target[targetOffset + 3] = (byte)Math.Max(target[targetOffset + 3], alpha);
                }
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

    private bool HasAnyLitLed()
    {
        // Note: bloom gating now uses preprocess input knowledge only, so healthy GPU frames never trigger CPU-side LED scans.
        return _hasAnyRawLitLed;
    }

    private static int GetEffectiveBloomRadius(int configuredRadius, int scaleDivisor, int dotSize)
    {
        // Radius is interpreted as pure spill distance in screen pixels (mapped to bloom space), not dot-size inflated.
        _ = dotSize;
        _ = scaleDivisor;
        return Math.Clamp(configuredRadius, 0, 8);
    }

    private static void EnsureBackgroundAlpha(byte[] bgra, bool transparentBackground)
    {
        if (transparentBackground)
        {
            return;
        }

        // We keep the surface opaque black so bloom in the spacing between dots can actually be seen.
        for (var i = 3; i < bgra.Length; i += 4)
        {
            bgra[i] = 255;
        }
    }

    private static void PresentFallbackBitmap(WriteableBitmap bitmap, byte[] bgra, int width, int height)
    {
        // Note: fallback presentation is intentionally direct (WritePixels only) and bypasses any staging texture.
        bitmap.WritePixels(new Int32Rect(0, 0, width, height), bgra, width * 4, 0);
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

    private void TryRecoverGpuBloomPipeline(Exception trigger)
    {
        try
        {
            DisposeGpuBloomResources();
            InitializeGpuBloomPipeline();
            AppLogger.Info($"[renderer] gpu bloom recovery attempted after {trigger.GetType().Name}; directPresent={_directPresentEnabled} status={_directPresentStatus}");
        }
        catch (Exception recoveryEx)
        {
            _gpuBloomSupported = false;
            _directPresentEnabled = false;
            _directPresentStatus = $"disabled:recovery-failed:{recoveryEx.GetType().Name}";
            AppLogger.Warn($"[renderer] gpu bloom recovery failed. reason={recoveryEx.Message}");
        }
    }

    private void DisposeDeviceResources()
    {
        DisposeGpuBloomResources();
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

        var initStage = "start";
        try
        {
            // We keep shaders inline for now so deployment stays single-binary and easier for community sharing.
            initStage = "compile-vs";
            using var vsBlob = CompileShaderOrThrow("VSMain", "vs_5_0");
            initStage = "compile-dot";
            using var dotBlob = CompileShaderOrThrow("PSDotPass", "ps_5_0");
            initStage = "compile-bright";
            using var brightBlob = CompileShaderOrThrow("PSBrightPass", "ps_5_0");
            initStage = "compile-blur";
            using var blurBlob = CompileShaderOrThrow("PSSeparableBlur", "ps_5_0");
            initStage = "compile-composite";
            using var compositeBlob = CompileShaderOrThrow("PSComposite", "ps_5_0");
            initStage = "compile-led-preprocess-cs";
            using var preprocessBlob = CompileShaderOrThrow("CSPreprocessLeds", "cs_5_0");
            initStage = "create-shaders";
            _fullscreenVertexShader = _device.CreateVertexShader(vsBlob);
            _dotPassShader = _device.CreatePixelShader(dotBlob);
            _brightPassShader = _device.CreatePixelShader(brightBlob);
            _blurPassShader = _device.CreatePixelShader(blurBlob);
            _compositeShader = _device.CreatePixelShader(compositeBlob);
            _gpuPreprocessShader = _device.CreateComputeShader(preprocessBlob);
            initStage = "create-sampler";
            _linearSampler = _device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp, 0, 1, ComparisonFunction.Never, new Color4(0f, 0f, 0f, 0f), 0, float.MaxValue));
            initStage = "create-constants";
            _bloomConstantsBuffer = _device.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<BloomGpuConstants>(), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
            _gpuPreprocessConstantsBuffer = _device.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<LedPreprocessGpuConstants>(), BindFlags.ConstantBuffer, ResourceUsage.Default));
            initStage = "create-targets";
            CreateBaseAndCompositeTargets();
            AppLogger.Info($"[renderer] led preprocess texture contract format={LedColorTextureFormat} channels={LedColorChannelContract}");
            initStage = "init-direct-present";
            TryInitializeDirectPresentSurface();
            _gpuBloomSupported = true;
            _gpuDotPassSupported = true;
            _useCpuBloomFallback = false;
        }
        catch (Exception ex)
        {
            _gpuBloomSupported = false;
            _gpuDotPassSupported = false;
            if (!_useCpuBloomFallback)
            {
                _useCpuBloomFallback = true;
                _cpuBloomFallbackCount++;
                LogFallbackCounter("cpu bloom fallback", _cpuBloomFallbackCount, $"stage={initStage} reason=init-failed");
            }
            _directPresentEnabled = false;
            _directPresentStatus = $"disabled:init-failed:{initStage}:{ex.GetType().Name}";
            AppLogger.Warn($"[renderer] gpu bloom pipeline unavailable; using CPU fallback. stage={initStage} surface={_surfaceWidth}x{_surfaceHeight} reason={ex.Message}");
        }
    }

    private void TryInitializeDirectPresentSurface()
    {
        _directPresentEnabled = false;
        _directPresentBackBufferRtv?.Dispose();
        _directPresentBackBufferRtv = null;
        _directPresentBackBuffer?.Dispose();
        _directPresentBackBuffer = null;
        _directPresentSwapChain?.Dispose();
        _directPresentSwapChain = null;
        _directPresentBackBufferWidth = 0;
        _directPresentBackBufferHeight = 0;

        if (_gpuCompositeTexture is null || _host is null)
        {
            _directPresentStatus = "disabled:missing-composite-or-host";
            AppLogger.Info($"[renderer] gpu direct present disabled. reason={_directPresentStatus}");
            return;
        }

        if (IsLegacyReadbackMode())
        {
            _directPresentStatus = "disabled:legacy-readback-mode";
            AppLogger.Info("[renderer] gpu direct present disabled by config mode=LegacyReadback; using readback fallback.");
            return;
        }

        var interopStage = "start";
        try
        {
            if (_device is null)
            {
                _directPresentStatus = "disabled:missing-d3d11-device";
                return;
            }

            interopStage = "resolve-host-hwnd";
            var hostHandle = (PresentationSource.FromVisual(_host) as HwndSource)?.Handle ?? IntPtr.Zero;
            if (hostHandle == IntPtr.Zero)
            {
                _directPresentStatus = "disabled:interop-missing-hwnd";
                AppLogger.Info($"[renderer] gpu direct present disabled. reason={_directPresentStatus}");
                return;
            }

            interopStage = "query-dxgi-factory";
            using var dxgiDevice = _device.QueryInterfaceOrNull<IDXGIDevice>();
            using var adapter = dxgiDevice?.GetAdapter();
            using var factory = adapter?.GetParent<IDXGIFactory2>();

            if (factory is null)
            {
                _directPresentStatus = "disabled:missing-dxgi-factory";
                AppLogger.Info($"[renderer] gpu direct present disabled. reason={_directPresentStatus}");
                return;
            }

            interopStage = "create-swapchain";
            // Note: prefer host visual size so direct-present tracks the matrix viewport instead of the full window client.
            var clientWidth = Math.Max(1, (int)Math.Round(_host.ActualWidth));
            var clientHeight = Math.Max(1, (int)Math.Round(_host.ActualHeight));
            if (clientWidth <= 1 || clientHeight <= 1)
            {
                GetClientSize(hostHandle, out clientWidth, out clientHeight);
            }
            var desc = new SwapChainDescription1
            {
                // Note: create the swapchain at host-client size; we letterbox the composed surface ourselves.
                Width = (uint)Math.Max(1, clientWidth),
                Height = (uint)Math.Max(1, clientHeight),
                Format = LedColorTextureFormat,
                Stereo = false,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2,
                // Note: keep source aspect so strip dots do not get anisotropically stretched by the host window size.
                Scaling = Scaling.AspectRatioStretch,
                SwapEffect = SwapEffect.FlipDiscard,
                AlphaMode = AlphaMode.Ignore,
                Flags = SwapChainFlags.None,
            };

            try
            {
                _directPresentSwapChain = factory.CreateSwapChainForHwnd(_device, hostHandle, desc);
            }
            catch (Exception ex) when (ex.HResult == unchecked((int)0x887A0001))
            {
                // Note: some drivers reject AspectRatioStretch for this swapchain path; retry with Stretch
                // so we stay on GPU direct-present instead of dropping to readback fallback.
                desc.Scaling = Scaling.Stretch;
                _directPresentSwapChain = factory.CreateSwapChainForHwnd(_device, hostHandle, desc);
                AppLogger.Warn("[renderer] AspectRatioStretch swapchain scaling unsupported on this adapter; retried with Stretch.");
            }
            if (_directPresentSwapChain is null)
            {
                _directPresentStatus = "disabled:swapchain-create-failed";
                return;
            }

            interopStage = "get-swapchain-backbuffer";
            _directPresentBackBuffer = _directPresentSwapChain.GetBuffer<ID3D11Texture2D>(0);
            if (_directPresentBackBuffer is null)
            {
                _directPresentStatus = "disabled:swapchain-backbuffer-unavailable";
                return;
            }
            _directPresentBackBufferRtv = _device.CreateRenderTargetView(_directPresentBackBuffer);
            _directPresentBackBufferWidth = Math.Max(1, clientWidth);
            _directPresentBackBufferHeight = Math.Max(1, clientHeight);

            _host.Source = null;
            _directPresentEnabled = true;
            _directPresentStatus = "enabled:d3d11-swapchain";
            AppLogger.Info($"[renderer] gpu direct present enabled. status={_directPresentStatus}");
        }
        catch (Exception ex)
        {
            _directPresentEnabled = false;
            _directPresentBackBufferRtv?.Dispose();
            _directPresentBackBufferRtv = null;
            _directPresentBackBuffer?.Dispose();
            _directPresentBackBuffer = null;
            _directPresentSwapChain?.Dispose();
            _directPresentSwapChain = null;
            _directPresentBackBufferWidth = 0;
            _directPresentBackBufferHeight = 0;
            var hr = ex.HResult;
            var hrText = $"0x{hr:X8}";
            var expectedUnsupported = hr == unchecked((int)0x887A0004) || hr == unchecked((int)0x80070057);
            _directPresentStatus = expectedUnsupported
                ? $"disabled:swapchain-unsupported:{interopStage}:{hrText}"
                : $"disabled:swapchain-init-exception:{interopStage}:{ex.GetType().Name}:{hrText}";
            if (expectedUnsupported)
            {
                AppLogger.Info($"[renderer] gpu d3d11 swapchain present unsupported on this adapter/driver; using readback fallback. stage={interopStage} hresult={hrText}");
            }
            else
            {
                AppLogger.Warn($"[renderer] gpu d3d11 swapchain present disabled. stage={interopStage} reason={ex.Message} hresult={hrText}");
            }
        }
    }

    private static Blob CompileShaderOrThrow(string entryPoint, string shaderProfile)
    {
        // Note: CompileFromFile is the most deterministic overload in Vortice across SDK bindings.
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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out NativeRect lpRect);

    private static void GetClientSize(IntPtr hwnd, out int width, out int height)
    {
        if (hwnd != IntPtr.Zero && GetClientRect(hwnd, out var rect))
        {
            width = Math.Max(1, rect.Right - rect.Left);
            height = Math.Max(1, rect.Bottom - rect.Top);
            return;
        }

        width = 1;
        height = 1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeRect
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;
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

        var createStage = "start";
        try
        {
            var ledTextureDesc = new Texture2DDescription
            {
                Width = (uint)Math.Max(1, _width),
                Height = (uint)Math.Max(1, _height),
                ArraySize = 1,
                MipLevels = 1,
                Format = LedColorTextureFormat,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                // Note: preprocess texture is cleared via RTV and consumed via UAV/SRV, so all three bind flags are required.
                BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess | BindFlags.RenderTarget,
                CPUAccessFlags = CpuAccessFlags.None,
            };
            createStage = "led-color-texture";
            _gpuLedColorTexture = _device.CreateTexture2D(ledTextureDesc);
            createStage = "led-color-srv";
            _gpuLedColorSrv = _device.CreateShaderResourceView(_gpuLedColorTexture);
            createStage = "led-color-uav";
            _gpuLedColorUav = _device.CreateUnorderedAccessView(_gpuLedColorTexture);
            createStage = "led-color-rtv";
            _gpuLedColorRtv = _device.CreateRenderTargetView(_gpuLedColorTexture);

            var ledReadbackDesc = ledTextureDesc;
            ledReadbackDesc.Usage = ResourceUsage.Staging;
            ledReadbackDesc.BindFlags = BindFlags.None;
            ledReadbackDesc.CPUAccessFlags = CpuAccessFlags.Read;
            createStage = "led-readback-texture";
            _gpuLedReadbackTexture = _device.CreateTexture2D(ledReadbackDesc);

            var rawRgbDesc = new Texture2DDescription
            {
                Width = (uint)Math.Max(1, _width * _height * Channels),
                Height = 1,
                ArraySize = 1,
                MipLevels = 1,
                Format = DxgiFormat.R8_UInt,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
            };
            createStage = "raw-led-texture";
            _gpuRawLedTexture = _device.CreateTexture2D(rawRgbDesc);
            createStage = "raw-led-srv";
            _gpuRawLedSrv = _device.CreateShaderResourceView(_gpuRawLedTexture);

            var prevDesc = new Texture2DDescription
            {
                Width = (uint)Math.Max(1, _width),
                Height = (uint)Math.Max(1, _height),
                ArraySize = 1,
                MipLevels = 1,
                Format = DxgiFormat.R32G32B32A32_Float,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.UnorderedAccess,
            };
            createStage = "prev-led-texture";
            _gpuPrevLedTexture = _device.CreateTexture2D(prevDesc);
            createStage = "prev-led-uav";
            _gpuPrevLedUav = _device.CreateUnorderedAccessView(_gpuPrevLedTexture);
            // Note: zeroing this buffer ensures smoothing starts from dark pixels on first frame.
            var prevInit = new byte[Math.Max(16, _width * _height * 16)];
            var prevHandle = GCHandle.Alloc(prevInit, GCHandleType.Pinned);
            try
            {
                _context?.UpdateSubresource(_gpuPrevLedTexture, 0, null, prevHandle.AddrOfPinnedObject(), (uint)Math.Max(16, _width * 16), 0);
            }
            finally
            {
                prevHandle.Free();
            }

            var mappingData = new int[Math.Max(1, _logicalToRaster.Length)];
            if (_logicalToRaster.Length > 0)
            {
                Array.Copy(_logicalToRaster, mappingData, _logicalToRaster.Length);
            }
            var mappingHandle = GCHandle.Alloc(mappingData, GCHandleType.Pinned);
            try
            {
                var mappingDesc = new Texture2DDescription
                {
                    Width = (uint)Math.Max(1, _width),
                    Height = (uint)Math.Max(1, _height),
                    ArraySize = 1,
                    MipLevels = 1,
                    Format = DxgiFormat.R32_SInt,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Immutable,
                    BindFlags = BindFlags.ShaderResource,
                };
                createStage = "logical-to-raster-texture";
                _gpuLogicalToRasterTexture = _device.CreateTexture2D(mappingDesc, new SubresourceData(mappingHandle.AddrOfPinnedObject(), (uint)Math.Max(sizeof(int), _width * sizeof(int)), 0));
            }
            finally
            {
                mappingHandle.Free();
            }

            createStage = "logical-to-raster-srv";
            _gpuLogicalToRasterSrv = _device.CreateShaderResourceView(_gpuLogicalToRasterTexture);

            var fullDesc = new Texture2DDescription
            {
                Width = (uint)Math.Max(1, _surfaceWidth),
                Height = (uint)Math.Max(1, _surfaceHeight),
                ArraySize = 1,
                MipLevels = 1,
                Format = DxgiFormat.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                CPUAccessFlags = CpuAccessFlags.None,
            };
            createStage = "base-texture";
            _gpuBaseTexture = _device.CreateTexture2D(fullDesc);
            createStage = "base-srv";
            _gpuBaseSrv = _device.CreateShaderResourceView(_gpuBaseTexture);
            createStage = "base-rtv";
            _gpuBaseRtv = _device.CreateRenderTargetView(_gpuBaseTexture);

            fullDesc.Usage = ResourceUsage.Default;
            fullDesc.BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget;
            fullDesc.CPUAccessFlags = CpuAccessFlags.None;
            // Note: some drivers reject shared textures for this format/usage, so we gracefully fall back to a non-shared composite target.
            try
            {
                createStage = "composite-shared-texture";
                fullDesc.MiscFlags = ResourceOptionFlags.Shared;
                _gpuCompositeTexture = _device.CreateTexture2D(fullDesc);
            }
            catch (Exception ex)
            {
                createStage = "composite-nonshared-fallback";
                fullDesc.MiscFlags = ResourceOptionFlags.None;
                _gpuCompositeTexture = _device.CreateTexture2D(fullDesc);
                _directPresentStatus = $"disabled:interop-shared-texture-unsupported:{ex.GetType().Name}";
                AppLogger.Warn($"[renderer] gpu direct present unavailable; shared composite texture not supported on this device. reason={ex.Message}");
            }

            createStage = "composite-srv";
            _gpuCompositeSrv = _device.CreateShaderResourceView(_gpuCompositeTexture);
            createStage = "composite-rtv";
            _gpuCompositeRtv = _device.CreateRenderTargetView(_gpuCompositeTexture);

            fullDesc.Usage = ResourceUsage.Staging;
            fullDesc.BindFlags = BindFlags.None;
            fullDesc.CPUAccessFlags = CpuAccessFlags.Read;
            fullDesc.MiscFlags = ResourceOptionFlags.None;
            createStage = "readback-texture";
            _gpuReadbackTexture = _device.CreateTexture2D(fullDesc);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"CreateBaseAndCompositeTargets failed at stage='{createStage}' width={_surfaceWidth} height={_surfaceHeight}.", ex);
        }
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
            Format = DxgiFormat.R8G8B8A8_UNorm,
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
        _directPresentEnabled = false;
        _directPresentBackBufferRtv?.Dispose();
        _directPresentBackBufferRtv = null;
        _directPresentBackBuffer?.Dispose();
        _directPresentBackBuffer = null;
        _directPresentSwapChain?.Dispose();
        _directPresentSwapChain = null;
        _directPresentBackBufferWidth = 0;
        _directPresentBackBufferHeight = 0;
        DisposeBloomIntermediateTargets();
        _gpuReadbackTexture?.Dispose();
        _gpuReadbackTexture = null;
        _gpuLedColorRtv?.Dispose();
        _gpuLedColorRtv = null;
        _gpuCompositeSrv?.Dispose();
        _gpuCompositeSrv = null;
        _gpuCompositeRtv?.Dispose();
        _gpuCompositeRtv = null;
        _gpuCompositeTexture?.Dispose();
        _gpuCompositeTexture = null;
        _gpuLogicalToRasterSrv?.Dispose();
        _gpuLogicalToRasterSrv = null;
        _gpuLogicalToRasterTexture?.Dispose();
        _gpuLogicalToRasterTexture = null;
        _gpuPrevLedUav?.Dispose();
        _gpuPrevLedUav = null;
        _gpuPrevLedTexture?.Dispose();
        _gpuPrevLedTexture = null;
        _gpuRawLedSrv?.Dispose();
        _gpuRawLedSrv = null;
        _gpuRawLedTexture?.Dispose();
        _gpuRawLedTexture = null;
        _gpuLedReadbackTexture?.Dispose();
        _gpuLedReadbackTexture = null;
        _gpuLedColorUav?.Dispose();
        _gpuLedColorUav = null;
        _gpuLedColorSrv?.Dispose();
        _gpuLedColorSrv = null;
        _gpuLedColorTexture?.Dispose();
        _gpuLedColorTexture = null;
        _gpuBaseRtv?.Dispose();
        _gpuBaseRtv = null;
        _gpuBaseSrv?.Dispose();
        _gpuBaseSrv = null;
        _gpuBaseTexture?.Dispose();
        _gpuBaseTexture = null;
        _bloomConstantsBuffer?.Dispose();
        _bloomConstantsBuffer = null;
        _gpuPreprocessConstantsBuffer?.Dispose();
        _gpuPreprocessConstantsBuffer = null;
        _linearSampler?.Dispose();
        _linearSampler = null;
        _compositeShader?.Dispose();
        _compositeShader = null;
        _gpuPreprocessShader?.Dispose();
        _gpuPreprocessShader = null;
        _dotPassShader?.Dispose();
        _dotPassShader = null;
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
        public float SurfaceOffsetX;
        public float SurfaceOffsetY;
        public float SurfaceWidth;
        public float SurfaceHeight;
        public float BloomWidth;
        public float BloomHeight;
        public float DotShapeCircle;
        public float FillGapEnabled;
        public float FlatShading;
        public float FullBrightnessRadius;
        public float OffStateAlpha;
        public float OffTintR;
        public float OffTintG;
        public float OffTintB;
        public float LensFalloff;
        public float SpecularHotspot;
        public float RimHighlight;
        public float BackgroundVisible;
        public float BackgroundColorR;
        public float BackgroundColorG;
        public float BackgroundColorB;
        // Note: keep this struct at a 16-byte multiple to match HLSL cbuffer packing exactly.
        public float Padding0;
        public float Padding1;
        public float Padding2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LedPreprocessGpuConstants
    {
        public uint LedCount;
        public uint RasterCount;
        public uint Width;
        public uint Height;
        public uint SmoothingEnabled;
        public float RiseAlpha;
        public float FallAlpha;
        public float Brightness;
        public float Gamma;
        public uint ToneEnabled;
        public float ToneKneeStart;
        public float ToneStrength;
        public float OffSnapThreshold;
        // Note: pad to a 16-byte multiple so D3D11 constant buffer uploads stay deterministic.
        public float Padding0;
        public float Padding1;
        public float Padding2;
    }

    private enum RenderLane
    {
        GpuPrimary,
        CpuDotFallback,
        CpuBloomFallback,
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
    float2 SurfaceOffset;
    float2 SurfaceSize;
    float2 BloomSize;
    float DotShapeCircle;
    float FillGapEnabled;
    float FlatShading;
    float FullBrightnessRadius;
    float OffStateAlpha;
    float3 OffTint;
    float LensFalloff;
    float SpecularHotspot;
    float RimHighlight;
    float BackgroundVisible;
    float3 BackgroundColor;
}
cbuffer LedPreprocessConstants : register(b1)
{
    uint LedCount;
    uint RasterCount;
    uint LedWidth;
    uint LedHeight;
    uint SmoothingEnabled;
    float RiseAlpha;
    float FallAlpha;
    float Brightness;
    float Gamma;
    uint ToneEnabled;
    float ToneKneeStart;
    float ToneStrength;
    float OffSnapThreshold;
    float3 LedPreprocessPadding;
}
Texture2D BaseTexture : register(t0);
Texture2D NearTexture : register(t1);
Texture2D FarTexture : register(t2);
Texture2D<uint> RawLedBytes : register(t3);
Texture2D<int> LogicalToRaster : register(t4);
RWTexture2D<float4> PrevFrameRgb : register(u0);
RWTexture2D<float4> LedOutputTexture : register(u1);
SamplerState LinearSampler : register(s0);
struct VsOut
{
    float4 Position : SV_Position;
    float2 Uv : TEXCOORD0;
};
VsOut VSMain(uint vertexId : SV_VertexID)
{
    VsOut output;
    // Note: explicit full-screen triangle vertices are friendlier across drivers than bit-manipulated UV generation.
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
float4 PSDotPass(VsOut input) : SV_Target
{
    // Note: solid window backgrounds should be baked into the GPU base pass so bloom/composite stay fully GPU-side.
    float3 bgColor = BackgroundVisible > 0.5f ? BackgroundColor : float3(0.0f, 0.0f, 0.0f);
    float spacing = max(Direction.y, 0.0f);
    // Note: in fill-gap mode we derive per-axis cell sizes from the actual viewport surface so
    // dots stretch to fill available space while still reserving explicit pixel gaps between neighbors.
    float stride = max(Direction.x, 1.0f);
    float2 pixel = (input.Uv * SurfaceSize) - SurfaceOffset;
    float2 ledCoord;
    float2 within;
    float2 dotExtent;
    if (FillGapEnabled > 0.5f)
    {
        float cols = max(BloomSize.x, 1.0f);
        float rows = max(BloomSize.y, 1.0f);
        float dotWidth = max(1.0f, (SurfaceSize.x - (spacing * max(0.0f, cols - 1.0f))) / cols);
        float dotHeight = max(1.0f, (SurfaceSize.y - (spacing * max(0.0f, rows - 1.0f))) / rows);
        float stepX = dotWidth + spacing;
        float stepY = dotHeight + spacing;
        ledCoord = floor(float2(pixel.x / max(stepX, 1.0f), pixel.y / max(stepY, 1.0f)));
        if (ledCoord.x < 0.0f || ledCoord.y < 0.0f || ledCoord.x >= cols || ledCoord.y >= rows) return float4(bgColor, 1.0f);
        within = float2(pixel.x - (ledCoord.x * stepX), pixel.y - (ledCoord.y * stepY));
        if (within.x >= dotWidth || within.y >= dotHeight) return float4(bgColor, 1.0f);
        dotExtent = float2(dotWidth, dotHeight);
    }
    else
    {
        ledCoord = floor(pixel / stride);
        if (ledCoord.x < 0.0f || ledCoord.y < 0.0f || ledCoord.x >= BloomSize.x || ledCoord.y >= BloomSize.y) return float4(bgColor, 1.0f);
        within = frac(pixel / stride) * stride;
        if (within.x >= Radius || within.y >= Radius) return float4(bgColor, 1.0f);
        dotExtent = float2(Radius, Radius);
    }

    float radial = 0.0f;
    float edge = 1.0f;
    if (DotShapeCircle > 0.5f)
    {
        float2 center = (dotExtent - 1.0f) * 0.5f;
        float2 denom = max(float2(0.5f, 0.5f), dotExtent * 0.5f);
        float dx = (within.x - center.x) / denom.x;
        float dy = (within.y - center.y) / denom.y;
        radial = sqrt(dx * dx + dy * dy);
        if (radial > 1.0f) return float4(bgColor, 1.0f);
        float fullRadius = saturate(FullBrightnessRadius);
        float adjusted = max(radial, fullRadius);
        float normalized = (adjusted - fullRadius) / max(0.0001f, 1.0f - fullRadius);
        edge = saturate(1.0f - normalized);
    }

    float2 ledUv = (ledCoord + 0.5f) / max(BloomSize, float2(1.0f, 1.0f));
    float3 ledColor = BaseTexture.SampleLevel(LinearSampler, ledUv, 0).rgb;
    float intensity = saturate(max(ledColor.r, max(ledColor.g, ledColor.b)));
    float offBlend = 1.0f - (intensity * intensity);
    float hasOffState = OffStateAlpha > 0.0001f && max(OffTint.r, max(OffTint.g, OffTint.b)) > 0.0f ? 1.0f : 0.0f;
    if (hasOffState < 0.5f && intensity <= 0.0f) return float4(bgColor, 1.0f);

    if (FlatShading > 0.5f)
    {
        float3 flatColor = (OffTint * OffStateAlpha * offBlend) + ledColor;
        return float4(saturate(flatColor), 1.0f);
    }

    float rootIntensity = sqrt(intensity);
    float coreOpacity = intensity > 0.0f ? saturate(0.35f + (rootIntensity * 0.65f)) : 0.0f;
    float specOpacity = intensity > 0.0f ? saturate((rootIntensity * 0.45f) + 0.08f) : 0.0f;
    float bodyRaw = OffStateAlpha * ((0.25f + (0.55f * pow(edge, 0.5f + LensFalloff))) + (RimHighlight * 0.08f * (1.0f - edge)));
    // Note: CPU masks are normalized to max=1, so we mirror that here to keep off-state visibility/specularity in parity.
    float bodyNorm = saturate(bodyRaw / max(0.0001f, OffStateAlpha * 0.8f));
    float core = pow(edge, 1.1f + (LensFalloff * 1.6f)) * coreOpacity;

    float hx = (within.x / max(1.0f, Radius - 1.0f)) - 0.50f;
    float hy = (within.y / max(1.0f, Radius - 1.0f)) - 0.35f;
    float hotspotDist2 = (hx * hx) + (hy * hy);
    float specBase = max(0.01f, 0.35f + (0.55f * SpecularHotspot));
    float specMask = exp(-hotspotDist2 / max(0.01f, 0.02f + (0.12f * SpecularHotspot))) * specBase;
    float specNorm = saturate(specMask / specBase);
    float spec = specNorm * specOpacity;

    float3 outColor = (OffTint * bodyNorm * offBlend) + (ledColor * core) + spec.xxx;
    return float4(saturate(outColor), 1.0f);
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
    // Note: match CPU bloom extraction by averaging over the downsample footprint
    // and only counting emissive contributors.
    int scale = max(1, (int)round(ScaleDivisor));
    float2 texel = 1.0f / max(SurfaceSize, float2(1.0f, 1.0f));
    float2 footprint = float2(scale, scale);
    float2 start = input.Uv - ((footprint * 0.5f - 0.5f) * texel);
    float3 sum = 0;
    float samples = 0;
    [loop]
    for (int y = 0; y < scale; y++)
    {
        [loop]
        for (int x = 0; x < scale; x++)
        {
            float2 uv = start + float2(x, y) * texel;
            float3 c = BaseTexture.Sample(LinearSampler, uv).rgb;
            float e = SoftKneeWeight(c);
            if (e <= 0.0f) continue;
            sum += c * e;
            samples += 1.0f;
        }
    }
    if (samples <= 0.0f) return float4(0, 0, 0, 1);
    return float4(sum / samples, 1.0f);
}
float4 PSSeparableBlur(VsOut input) : SV_Target
{
    float r = clamp(Radius, 0.0f, 8.0f);
    if (r <= 0.001f) return BaseTexture.Sample(LinearSampler, input.Uv);
    float3 sum = 0;
    float weightSum = 0;
    // Note: fixed bounds + unroll avoids driver issues with dynamic loop trip counts.
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
float ToneMapChannel(uint rawByte)
{
    float normalized = clamp((float)rawByte / 255.0f, 0.0f, 1.0f);
    float adjusted = pow(normalized, clamp(Gamma, 0.1f, 5.0f));
    float scaled = adjusted * clamp(Brightness, 0.0f, 1.0f);
    if (ToneEnabled > 0 && scaled > 1.0f)
    {
        float kneeStart = clamp(ToneKneeStart, 1.0f, 2.0f);
        float strength = clamp(ToneStrength, 0.0f, 8.0f);
        if (scaled > kneeStart)
        {
            float excess = scaled - kneeStart;
            scaled = kneeStart + (excess / (1.0f + (strength * excess)));
        }
    }

    return clamp(scaled, 0.0f, 1.0f) * 255.0f;
}
float SmoothChannel(float current, float target)
{
    if (target >= 255.0f) return 255.0f;
    float delta = target - current;
    float alpha = delta >= 0.0f ? RiseAlpha : FallAlpha;
    float nextValue = current + (alpha * delta);
    if (target <= 0.0f && nextValue <= OffSnapThreshold)
    {
        nextValue = 0.0f;
    }

    return nextValue;
}
[numthreads(64, 1, 1)]
void CSPreprocessLeds(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint logical = dispatchThreadId.x;
    if (logical >= LedCount || logical >= RasterCount) return;

    uint byteOffset = logical * 3u;
    uint2 logicalCoord = uint2(logical % max(1u, LedWidth), logical / max(1u, LedWidth));
    float3 target = float3(
        ToneMapChannel(RawLedBytes[uint2(byteOffset + 0, 0)]),
        ToneMapChannel(RawLedBytes[uint2(byteOffset + 1, 0)]),
        ToneMapChannel(RawLedBytes[uint2(byteOffset + 2, 0)]));

    float3 smoothed = target;
    if (SmoothingEnabled > 0)
    {
        float3 current = PrevFrameRgb[logicalCoord].rgb;
        // Note: keep per-channel smoothing explicit to avoid cs_5_0 l-value indexing issues on some drivers.
        smoothed = float3(
            SmoothChannel(current.r, target.r),
            SmoothChannel(current.g, target.g),
            SmoothChannel(current.b, target.b));
    }

    PrevFrameRgb[logicalCoord] = float4(smoothed, 1.0f);
    int rasterIndex = LogicalToRaster[logicalCoord];
    if (rasterIndex < 0 || rasterIndex >= (int)RasterCount) return;
    uint raster = (uint)rasterIndex;
    uint x = raster % max(1u, LedWidth);
    uint y = raster / max(1u, LedWidth);
    LedOutputTexture[uint2(x, y)] = float4(clamp(smoothed / 255.0f, 0.0f, 1.0f), 1.0f);
}
""";
    }
}
