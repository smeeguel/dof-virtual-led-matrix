using System.Windows;
using System.Windows.Controls;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Rendering.Vulkan;

public sealed class VulkanMatrixRenderer : IMatrixRenderer
{
    private readonly VulkanHostControl _hostControl = new();
    private readonly IVulkanInteropAdapter _interopAdapter;
    private readonly VulkanFrameUploadPipeline _uploadPipeline = new();
    private MatrixConfig? _config;

    public VulkanMatrixRenderer()
        : this(new NullVulkanInteropAdapter())
    {
    }

    internal VulkanMatrixRenderer(IVulkanInteropAdapter interopAdapter)
    {
        _interopAdapter = interopAdapter;
    }

    public bool UsesImageHost => false;

    public bool UsesNativeHost => true;

    public FrameworkElement? GetNativeHostElement() => _hostControl;

    public void Initialize(Canvas primitiveCanvas, Image bitmapHost, MatrixConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));

        primitiveCanvas.Children.Clear();
        primitiveCanvas.Width = 0;
        primitiveCanvas.Height = 0;
        bitmapHost.Source = null;
        bitmapHost.Width = 0;
        bitmapHost.Height = 0;

        _hostControl.Width = Math.Max(1, (config.Width * (config.DotSize + config.MinDotSpacing)) + config.MinDotSpacing);
        _hostControl.Height = Math.Max(1, (config.Height * (config.DotSize + config.MinDotSpacing)) + config.MinDotSpacing);
        _uploadPipeline.Configure(config);
        _interopAdapter.UpdateShaderParameters(VulkanShaderParameterBlock.FromVisual(config.Visual));

        TryInitializeInterop();
    }

    public void Render(FramePresentation framePresentation)
    {
        if (_config is null)
        {
            throw new InvalidOperationException("Renderer must be initialized before rendering.");
        }

        TryInitializeInterop();
        var prepared = _uploadPipeline.Prepare(framePresentation);
        _interopAdapter.UploadAndRender(prepared.StagingInstances.Span, prepared.FrameSlot);
    }

    public void SetNativeHostHandle(IntPtr hostHwnd)
    {
        TryInitializeInterop();
    }

    public void NotifyHostResized(int width, int height)
    {
        TryInitializeInterop();
        _interopAdapter.Resize(width, height);
    }

    public void NotifyDeviceLost()
    {
        _interopAdapter.NotifyDeviceLost();
    }

    public void DisposeRenderer()
    {
        _interopAdapter.Dispose();
    }

    private void TryInitializeInterop()
    {
        if (_config is null || _hostControl.ChildHandle == IntPtr.Zero)
        {
            return;
        }

        _interopAdapter.Initialize(_hostControl.ChildHandle, _config);
    }
}
