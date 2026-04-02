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
    private float[] _dotMask = Array.Empty<float>();
    private byte[] _bgra = Array.Empty<byte>();
    private float[] _smoothedRgb = Array.Empty<float>();
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

        var rgb = frame.RgbMemory.Span;
        var ledCount = Math.Min(_logicalToRaster.Length, rgb.Length / Channels);
        var smoothing = _style.TemporalSmoothing;
        var smoothingEnabled = smoothing.Enabled;
        var rise = (float)Math.Clamp(smoothing.RiseAlpha, 0.0, 1.0);
        var fall = (float)Math.Clamp(smoothing.FallAlpha, 0.0, 1.0);

        for (var logical = 0; logical < ledCount; logical++)
        {
            var src = logical * Channels;
            var dst = logical * Channels;
            var r = ApplySmoothing(dst, _toneMapLut[rgb[src]], rise, fall, smoothingEnabled);
            var g = ApplySmoothing(dst + 1, _toneMapLut[rgb[src + 1]], rise, fall, smoothingEnabled);
            var b = ApplySmoothing(dst + 2, _toneMapLut[rgb[src + 2]], rise, fall, smoothingEnabled);

            var raster = _logicalToRaster[logical];
            if ((uint)raster >= (uint)(_width * _height))
            {
                continue;
            }

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
        BuildDotMask(style.DotShape, _dotSize);
    }

    private void BuildDotMask(string shape, int dotSize)
    {
        _dotMask = new float[dotSize * dotSize];
        if (dotSize == 1)
        {
            _dotMask[0] = 1f;
            return;
        }

        var center = (dotSize - 1) * 0.5;
        var radius = Math.Max(0.5, dotSize * 0.5);
        for (var y = 0; y < dotSize; y++)
        {
            for (var x = 0; x < dotSize; x++)
            {
                var idx = (y * dotSize) + x;
                if (shape.Equals("square", StringComparison.OrdinalIgnoreCase))
                {
                    _dotMask[idx] = 1f;
                    continue;
                }

                var dx = (x - center) / radius;
                var dy = (y - center) / radius;
                var radial = Math.Sqrt((dx * dx) + (dy * dy));
                _dotMask[idx] = radial <= 1.0 ? (float)Math.Pow(1.0 - radial, 0.55) : 0f;
            }
        }

        var maxMask = 0f;
        for (var i = 0; i < _dotMask.Length; i++)
        {
            if (_dotMask[i] > maxMask)
            {
                maxMask = _dotMask[i];
            }
        }
        if (maxMask > 0f && maxMask < 1f)
        {
            for (var i = 0; i < _dotMask.Length; i++)
            {
                if (_dotMask[i] > 0f)
                {
                    _dotMask[i] /= maxMask;
                }
            }
        }
    }

    private void RasterFastDot(int baseX, int baseY, float r, float g, float b)
    {
        const float emissiveFloor = 0.35f;

        for (var y = 0; y < _dotSize; y++)
        {
            var py = baseY + y;
            if ((uint)py >= (uint)_surfaceHeight)
            {
                continue;
            }

            for (var x = 0; x < _dotSize; x++)
            {
                var mask = _dotMask[(y * _dotSize) + x];
                if (mask <= 0f)
                {
                    continue;
                }

                var brightnessFactor = emissiveFloor + ((1f - emissiveFloor) * mask);

                var px = baseX + x;
                if ((uint)px >= (uint)_surfaceWidth)
                {
                    continue;
                }

                var o = ((py * _surfaceWidth) + px) * 4;
                _bgra[o] = (byte)Math.Clamp(b * brightnessFactor, 0f, 255f);
                _bgra[o + 1] = (byte)Math.Clamp(g * brightnessFactor, 0f, 255f);
                _bgra[o + 2] = (byte)Math.Clamp(r * brightnessFactor, 0f, 255f);
                _bgra[o + 3] = 255;
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
