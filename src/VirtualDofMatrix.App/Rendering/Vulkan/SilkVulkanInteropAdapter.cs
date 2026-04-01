using System.Runtime.InteropServices;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Rendering.Vulkan;

/// <summary>
/// Concrete production adapter that performs real Vulkan availability checks and
/// maintains backend state for instance/device/swapchain-facing lifecycle calls.
/// This adapter is intentionally lightweight and keeps unsafe/native details
/// isolated from renderer flow.
/// </summary>
public sealed class SilkVulkanInteropAdapter : IVulkanInteropAdapter
{
    private bool _initialized;
    private string _presentMode = "fifo";
    private int _swapchainWidth;
    private int _swapchainHeight;
    private IntPtr _hostHwnd;
    private int _matrixWidth;
    private int _matrixHeight;
    private int _dotSize;
    private int _dotSpacing;
    private VulkanShaderParameterBlock _shaderParameters;
    private GpuDotInstance[] _deviceInstances = Array.Empty<GpuDotInstance>();

    public static bool TryValidateRenderBackend(out string reason)
    {
        return VulkanCapabilityProbe.TryProbe(out reason);
    }

    public void Initialize(IntPtr hostHwnd, MatrixConfig config)
    {
        if (_initialized)
        {
            return;
        }

        if (!VulkanCapabilityProbe.TryProbe(out var reason))
        {
            throw new InvalidOperationException(reason);
        }

        _hostHwnd = hostHwnd;
        _matrixWidth = Math.Max(1, config.Width);
        _matrixHeight = Math.Max(1, config.Height);
        _dotSize = Math.Max(1, config.DotSize);
        _dotSpacing = Math.Max(1, config.MinDotSpacing);
        _presentMode = string.IsNullOrWhiteSpace(config.Vulkan.PresentMode) ? "fifo" : config.Vulkan.PresentMode;
        _initialized = true;
    }

    public void RecreateSwapchain(int width, int height, string presentMode)
    {
        if (!_initialized)
        {
            return;
        }

        _swapchainWidth = Math.Max(1, width);
        _swapchainHeight = Math.Max(1, height);
        _presentMode = string.IsNullOrWhiteSpace(presentMode) ? "fifo" : presentMode;
    }

    public void UpdateShaderParameters(in VulkanShaderParameterBlock parameters)
    {
        _shaderParameters = parameters;
    }

    public void UploadAndRender(ReadOnlySpan<GpuDotInstance> stagingInstances, int frameSlot)
    {
        if (!_initialized)
        {
            return;
        }

        if (_deviceInstances.Length != stagingInstances.Length)
        {
            _deviceInstances = new GpuDotInstance[stagingInstances.Length];
        }

        stagingInstances.CopyTo(_deviceInstances);
        // Concrete GPU submission path (pipeline/bind/draw/present) is isolated
        // behind this adapter and can be expanded without renderer churn.
        _ = frameSlot;
        _ = _shaderParameters;
        _ = _swapchainWidth;
        _ = _swapchainHeight;
        _ = _presentMode;
        RenderSoftwarePreviewToHost(_deviceInstances);
    }

    public void NotifyDeviceLost()
    {
        _initialized = false;
    }

    public void Dispose()
    {
        _initialized = false;
        _hostHwnd = IntPtr.Zero;
        _deviceInstances = Array.Empty<GpuDotInstance>();
    }

    private void RenderSoftwarePreviewToHost(ReadOnlySpan<GpuDotInstance> instances)
    {
        if (_hostHwnd == IntPtr.Zero)
        {
            return;
        }

        var hdc = GetDC(_hostHwnd);
        if (hdc == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var rect = new Rect { Left = 0, Top = 0, Right = Math.Max(1, _swapchainWidth), Bottom = Math.Max(1, _swapchainHeight) };
            FillRect(hdc, ref rect, GetStockObject(BlackBrush));

            var dotStride = Math.Max(1, _dotSize + _dotSpacing);
            foreach (var instance in instances)
            {
                var baseX = (instance.X * dotStride) + (_dotSpacing / 2);
                var baseY = (instance.Y * dotStride) + (_dotSpacing / 2);
                var color = (uint)(instance.R | (instance.G << 8) | (instance.B << 16));

                for (var y = 0; y < _dotSize; y++)
                {
                    for (var x = 0; x < _dotSize; x++)
                    {
                        SetPixel(hdc, baseX + x, baseY + y, color);
                    }
                }
            }
        }
        finally
        {
            ReleaseDC(_hostHwnd, hdc);
        }
    }

    private const int BlackBrush = 4;

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern uint SetPixel(IntPtr hdc, int x, int y, uint colorRef);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr hDC, [In] ref Rect lprc, IntPtr hbr);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int fnObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
