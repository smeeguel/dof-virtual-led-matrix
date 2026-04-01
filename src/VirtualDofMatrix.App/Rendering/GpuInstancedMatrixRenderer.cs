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
        _bgra = new byte[checked(width * height * 4)];
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
            Width = (uint)Math.Max(1, width),
            Height = (uint)Math.Max(1, height),
            ArraySize = 1,
            MipLevels = 1,
            Format = Format.R8G8B8A8_UNorm,
            BindFlags = BindFlags.ShaderResource,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Dynamic,
            CPUAccessFlags = CpuAccessFlags.Write,
        });

        _frameSrv = _device.CreateShaderResourceView(_frameTexture);
        _fallbackBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        _host.Source = _fallbackBitmap;
        _host.Stretch = Stretch.Fill;

        Console.WriteLine($"[renderer] gpu initialized fastpath leds={width * height}");
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

            var o = raster * 4;
            _bgra[o] = (byte)b;
            _bgra[o + 1] = (byte)g;
            _bgra[o + 2] = (byte)r;
            _bgra[o + 3] = 255;
        }

        var map = _context.Map(_frameTexture, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        Marshal.Copy(_bgra, 0, map.DataPointer, _bgra.Length);
        _context.Unmap(_frameTexture, 0);

        _context.DrawInstanced(4u, (uint)(_width * _height), 0u, 0u);
        _fallbackBitmap.WritePixels(new System.Windows.Int32Rect(0, 0, _width, _height), _bgra, _width * 4, 0);
    }

    public void Dispose() => DisposeDeviceResources();

    private float ApplySmoothing(int channel, byte targetByte, float rise, float fall, bool enabled)
    {
        var target = targetByte;
        if (!enabled)
        {
            _smoothedRgb[channel] = target;
            return target;
        }

        var current = _smoothedRgb[channel];
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
            var scaled = adjusted * Math.Clamp(style.Brightness, 0.0, 4.0);
            if (style.ToneMapping.Enabled)
            {
                var kneeStart = Math.Clamp(style.ToneMapping.KneeStart, 0.5, 0.99);
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
