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
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11Buffer? _instanceBuffer;
    private ID3D11Texture2D? _frameTexture;
    private ID3D11ShaderResourceView? _frameSrv;
    private readonly object _gate = new();
    private readonly MatrixFrameRasterComposer _composer = new();
    private Image? _host;
    private WriteableBitmap? _fallbackBitmap;
    private MatrixConfig? _config;
    private FramePresentation _stagedFrame = new(Array.Empty<byte>(), 0, 0, 0, DateTimeOffset.UnixEpoch);
    private int _surfaceWidth;
    private int _surfaceHeight;

    public string BackendName => "gpu";

    public bool UsesImageHost => true;

    public void Initialize(MatrixRendererSurface renderSurface, int width, int height, DotStyleConfig dotStyleConfig)
    {
        DisposeDeviceResources();
        _host = renderSurface.BitmapHost;
        _host.Source = null;
        renderSurface.PrimitiveCanvas.Children.Clear();

        _config = new MatrixConfig
        {
            Width = width,
            Height = height,
            DotShape = dotStyleConfig.DotShape,
            DotSize = dotStyleConfig.DotSize,
            Mapping = dotStyleConfig.Mapping,
            MinDotSpacing = dotStyleConfig.DotSpacing,
            Brightness = dotStyleConfig.Brightness,
            Gamma = dotStyleConfig.Gamma,
            Visual = dotStyleConfig.Visual,
            ToneMapping = dotStyleConfig.ToneMapping,
            TemporalSmoothing = dotStyleConfig.TemporalSmoothing,
            Bloom = dotStyleConfig.Bloom,
        };
        _composer.Configure(_config);
        var initial = _composer.Compose(new FramePresentation(Array.Empty<byte>(), 0, 0, 0, DateTimeOffset.UnixEpoch));
        _surfaceWidth = initial.Width;
        _surfaceHeight = initial.Height;

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
        var instanceDesc = new BufferDescription(byteWidth, BindFlags.VertexBuffer, ResourceUsage.Default);
        _instanceBuffer = _device.CreateBuffer(instanceDesc);

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

        Console.WriteLine($"[renderer] gpu initialized adapter=hardware leds={width * height} surface={_surfaceWidth}x{_surfaceHeight}");
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
        if (_context is null || _frameTexture is null || _fallbackBitmap is null || _config is null)
        {
            return;
        }

        FramePresentation frame;
        lock (_gate)
        {
            frame = _stagedFrame;
        }
        var composed = _composer.Compose(frame);

        var map = _context.Map(_frameTexture, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        Marshal.Copy(composed.Pixels, 0, map.DataPointer, composed.Pixels.Length);
        _context.Unmap(_frameTexture, 0);

        _context.DrawInstanced(4u, (uint)(_config.Width * _config.Height), 0u, 0u);

        _fallbackBitmap.WritePixels(new System.Windows.Int32Rect(0, 0, composed.Width, composed.Height), composed.Pixels, composed.Stride, 0);
    }

    public void Dispose()
    {
        DisposeDeviceResources();
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
