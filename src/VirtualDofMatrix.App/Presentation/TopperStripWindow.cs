using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using VirtualDofMatrix.Core.Toys;
using Image = System.Windows.Controls.Image;

namespace VirtualDofMatrix.App.Presentation;

// Overview: lightweight topper/strip window that renders a toy frame without the full MainWindow debug chrome.
public sealed class TopperStripWindow : Window
{
    private readonly Image _image;

    public TopperStripWindow(string toyId)
    {
        Title = $"Virtual DOF {toyId}";
        Background = Brushes.Black;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        Topmost = true;
        SnapsToDevicePixels = true;

        // Conversational note: this mirrors MainWindow chrome behavior so strip windows are draggable/resizable too.
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            CornerRadius = new CornerRadius(0),
            GlassFrameThickness = new Thickness(0),
            ResizeBorderThickness = new Thickness(8),
            UseAeroCaptionButtons = false,
        });

        _image = new Image
        {
            Stretch = Stretch.Uniform,
            SnapsToDevicePixels = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        Content = _image;

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        };
    }

    public void ApplyFrame(ToyFrame frame)
    {
        var sourceBytes = frame.PayloadBytes ?? ToBytes(frame.Pixels ?? Array.Empty<VirtualDofMatrix.Core.Rgb24>());
        var width = Math.Max(1, frame.Width);
        var height = Math.Max(1, frame.Height);

        // Conversational note: convert packed RGB into BGRA32 so WPF can upload in a single WritePixels call.
        var bgra = new byte[width * height * 4];
        var pixelCount = Math.Min(width * height, sourceBytes.Length / 3);
        for (var i = 0; i < pixelCount; i++)
        {
            var srcOffset = i * 3;
            var dstOffset = i * 4;
            bgra[dstOffset] = sourceBytes[srcOffset + 2];
            bgra[dstOffset + 1] = sourceBytes[srcOffset + 1];
            bgra[dstOffset + 2] = sourceBytes[srcOffset];
            bgra[dstOffset + 3] = 255;
        }

        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, width, height), bgra, width * 4, 0);
        _image.Source = bitmap;
    }

    private static byte[] ToBytes(IReadOnlyList<VirtualDofMatrix.Core.Rgb24> pixels)
    {
        var bytes = new byte[pixels.Count * 3];
        for (var i = 0; i < pixels.Count; i++)
        {
            var offset = i * 3;
            bytes[offset] = pixels[i].R;
            bytes[offset + 1] = pixels[i].G;
            bytes[offset + 2] = pixels[i].B;
        }

        return bytes;
    }
}
