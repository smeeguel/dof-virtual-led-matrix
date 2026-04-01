using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace VirtualDofMatrix.App.Rendering.Vulkan;

public sealed class VulkanHostControl : HwndHost
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private IntPtr _hwndHost = IntPtr.Zero;

    public IntPtr ChildHandle => _hwndHost;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _hwndHost = CreateWindowEx(
            0,
            "static",
            string.Empty,
            WsChild | WsVisible,
            0,
            0,
            Math.Max(1, (int)Width),
            Math.Max(1, (int)Height),
            hwndParent.Handle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);

        return new HandleRef(this, _hwndHost);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (hwnd.Handle != IntPtr.Zero)
        {
            DestroyWindow(hwnd.Handle);
        }

        _hwndHost = IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr CreateWindowEx(
        int exStyle,
        string className,
        string windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        IntPtr hwndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);
}
