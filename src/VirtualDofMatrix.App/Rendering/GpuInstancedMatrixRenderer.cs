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
    private Image? _host;
    private WriteableBitmap? _fallbackBitmap;
    private int[] _logicalToRaster = Array.Empty<int>();
    private Rgb24[] _stagedFrame = Array.Empty<Rgb24>();
    private int _width;
    private int _height;

    public string BackendName => "gpu";

    public bool UsesImageHost => true;

    public void Initialize(MatrixRendererSurface renderSurface, int width, int height, DotStyleConfig dotStyleConfig)
    {
        _host = renderSurface.BitmapHost;
        renderSurface.PrimitiveCanvas.Children.Clear();
        _width = width;
        _height = height;
        _logicalToRaster = MatrixFrameIndexMap.BuildLogicalToRasterMap(width, height, dotStyleConfig.Mapping);

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
        var instanceDesc = new BufferDescription(byteWidth, BindFlags.VertexBuffer, ResourceUsage.Immutable, CpuAccessFlags.None, ResourceOptionFlags.None, sizeof(uint));
        _instanceBuffer = _device.CreateBuffer(instanceDesc);

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

        Console.WriteLine($"[renderer] gpu initialized adapter=hardware leds={width * height}");
    }

    public void UpdateFrame(ReadOnlySpan<Rgb24> logicalFrame)
    {
        lock (_gate)
        {
            _stagedFrame = logicalFrame.ToArray();
        }
    }

    public void Resize(double viewportWidth, double viewportHeight)
    {
    }

    public void Render()
    {
        if (_context is null || _frameTexture is null || _fallbackBitmap is null)
        {
            return;
        }

        Rgb24[] frame;
        lock (_gate)
        {
            frame = _stagedFrame;
        }

        var data = GpuFrameUpload.BuildBgraFrame(frame, _logicalToRaster, _width, _height);

        var map = _context.Map(_frameTexture, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        Marshal.Copy(data, 0, map.DataPointer, data.Length);
        _context.Unmap(_frameTexture, 0);

        _context.DrawInstanced(4u, (uint)(_width * _height), 0u, 0u);

        _fallbackBitmap.WritePixels(new System.Windows.Int32Rect(0, 0, _width, _height), data, _width * 4, 0);
    }

    public void Dispose()
    {
        _frameSrv?.Dispose();
        _frameTexture?.Dispose();
        _instanceBuffer?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
    }
}
