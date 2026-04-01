using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Rendering.Vulkan;

public sealed class VulkanMatrixRenderer : IMatrixRenderer
{
    private readonly VulkanHostControl _hostControl = new();
    private readonly IVulkanInteropAdapter _interopAdapter;
    private readonly VulkanFrameUploadPipeline _uploadPipeline = new();
    private readonly object _frameSync = new();
    private MatrixConfig? _config;
    private FramePresentation? _latestPendingFrame;
    private Timer? _renderTimer;
    private TimeSpan _tickInterval = TimeSpan.FromMilliseconds(16.666);
    private bool _isMinimized;
    private long _droppedFrames;
    private DateTimeOffset _lastMetricsLogUtc = DateTimeOffset.MinValue;

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
        ConfigureRenderLoop(config.Vulkan);

        TryInitializeInterop();
    }

    public void Render(FramePresentation framePresentation)
    {
        if (_config is null)
        {
            throw new InvalidOperationException("Renderer must be initialized before rendering.");
        }

        lock (_frameSync)
        {
            if (_latestPendingFrame is not null)
            {
                Interlocked.Increment(ref _droppedFrames);
            }

            _latestPendingFrame = framePresentation;
        }
    }

    public void SetNativeHostHandle(IntPtr hostHwnd)
    {
        TryInitializeInterop();
    }

    public void NotifyHostResized(int width, int height)
    {
        TryInitializeInterop();
        _isMinimized = width <= 1 || height <= 1;
        if (_isMinimized)
        {
            return;
        }

        var presentMode = _config?.Vulkan.PresentMode ?? "fifo";
        _interopAdapter.RecreateSwapchain(width, height, presentMode);
    }

    public void NotifyDeviceLost()
    {
        _interopAdapter.NotifyDeviceLost();
    }

    public void DisposeRenderer()
    {
        _renderTimer?.Dispose();
        _renderTimer = null;
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

    private void ConfigureRenderLoop(VulkanRenderConfig renderConfig)
    {
        var clampedFps = Math.Clamp(renderConfig.TargetFps, 10, 240);
        _tickInterval = TimeSpan.FromSeconds(1.0 / clampedFps);
        _renderTimer?.Dispose();
        _renderTimer = new Timer(RenderTick, null, _tickInterval, _tickInterval);
    }

    private void RenderTick(object? _)
    {
        if (_config is null || _isMinimized)
        {
            return;
        }

        FramePresentation? frameToRender;
        lock (_frameSync)
        {
            frameToRender = _latestPendingFrame;
            _latestPendingFrame = null;
        }

        if (frameToRender is null)
        {
            return;
        }

        TryInitializeInterop();

        var uploadStart = Stopwatch.GetTimestamp();
        var prepared = _uploadPipeline.Prepare(frameToRender);
        var uploadElapsedMs = Stopwatch.GetElapsedTime(uploadStart).TotalMilliseconds;

        var drawStart = Stopwatch.GetTimestamp();
        _interopAdapter.UploadAndRender(prepared.StagingInstances.Span, prepared.FrameSlot);
        var drawElapsedMs = Stopwatch.GetElapsedTime(drawStart).TotalMilliseconds;

        // Placeholder until concrete adapter returns present wait timings.
        const double presentWaitMs = 0.0;
        LogMetrics(uploadElapsedMs, drawElapsedMs, presentWaitMs);
    }

    private void LogMetrics(double uploadMs, double drawMs, double presentWaitMs)
    {
        var now = DateTimeOffset.UtcNow;
        if ((now - _lastMetricsLogUtc) < TimeSpan.FromSeconds(1))
        {
            return;
        }

        _lastMetricsLogUtc = now;
        var dropped = Interlocked.Exchange(ref _droppedFrames, 0);
        var presentMode = _config?.Vulkan.PresentMode ?? "fifo";
        Console.WriteLine(
            $"[{now:O}] Vulkan tick metrics: upload={uploadMs:0.###}ms, draw={drawMs:0.###}ms, presentWait={presentWaitMs:0.###}ms, dropped={dropped}, presentMode={presentMode}");
    }
}
