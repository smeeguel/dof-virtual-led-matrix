using System.Windows.Controls;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Rendering;

public interface IMatrixRenderer
{
    bool UsesImageHost { get; }

    void Initialize(Canvas primitiveCanvas, Image bitmapHost, MatrixConfig config);

    void Render(FramePresentation framePresentation);

    // Optional GPU lifecycle hooks for renderers that own native swapchains/devices.
    void SetNativeHostHandle(IntPtr hostHwnd) { }

    void NotifyHostResized(int width, int height) { }

    void NotifyDeviceLost() { }

    void DisposeRenderer() { }
}
