using System.Windows;
using System.Windows.Input;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App;

public partial class MainWindow : Window
{
    private FramePresentation? _latestPresentation;

    public MainWindow(AppConfig config)
    {
        InitializeComponent();

        Topmost = config.Window.AlwaysOnTop;
        Left = config.Window.Left;
        Top = config.Window.Top;
        Width = config.Window.Width;
        Height = config.Window.Height;
    }

    public void ApplyPresentation(FramePresentation presentation)
    {
        _latestPresentation = presentation;

        OutputSequenceText.Text = $"Output sequence: {presentation.OutputSequence}";
        PresentedAtText.Text = $"Presented at UTC: {presentation.PresentedAtUtc:O}";
        PayloadLengthText.Text = $"Payload bytes: {presentation.RgbBytes.Length}";
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
