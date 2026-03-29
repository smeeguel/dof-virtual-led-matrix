using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VirtualDofMatrix.App.Rendering;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App;

public partial class MainWindow : Window
{
    private readonly AppConfig _config;
    private readonly IMatrixRenderer _matrixRenderer;

    private FramePresentation? _latestPresentation;

    public MainWindow(AppConfig config)
        : this(config, new WpfPrimitiveMatrixRenderer())
    {
    }

    internal MainWindow(AppConfig config, IMatrixRenderer matrixRenderer)
    {
        _config = config;
        _matrixRenderer = matrixRenderer;

        InitializeComponent();

        ApplyPersistedWindowSettings();
        ApplyPersistedVisualSettings();

        _matrixRenderer.Initialize(MatrixCanvas, _config.Matrix);
    }

    public void ApplyPresentation(FramePresentation presentation)
    {
        _latestPresentation = presentation;

        OutputSequenceText.Text = $"Output sequence: {presentation.OutputSequence}";
        PresentedAtText.Text = $"Presented at UTC: {presentation.PresentedAtUtc:O}";
        PayloadLengthText.Text = $"Payload bytes: {presentation.RgbBytes.Length}";
        LedRangeText.Text = $"LED range: {presentation.LowestLedWritten}..{Math.Max(presentation.LowestLedWritten, presentation.HighestLedWritten - 1)}";

        _matrixRenderer.Render(presentation);
    }

    public void SyncWindowSettingsToConfig()
    {
        _config.Window.Left = Left;
        _config.Window.Top = Top;
        _config.Window.Width = Width;
        _config.Window.Height = Height;
        _config.Window.AlwaysOnTop = Topmost;
        _config.Window.Borderless = WindowStyle == WindowStyle.None;
    }

    private void ApplyPersistedWindowSettings()
    {
        Topmost = _config.Window.AlwaysOnTop;
        WindowStyle = _config.Window.Borderless ? WindowStyle.None : WindowStyle.SingleBorderWindow;

        Left = _config.Window.Left;
        Top = _config.Window.Top;
        Width = _config.Window.Width;
        Height = _config.Window.Height;
    }

    private void ApplyPersistedVisualSettings()
    {
        Background = Brushes.Black;
        DotShapeText.Text = $"Dot shape: {_config.Matrix.DotShape}";
        DotSizeText.Text = $"Dot size: {_config.Matrix.DotSize}";
        DotSpacingText.Text = $"Dot spacing: {_config.Matrix.DotSpacing}";
        BrightnessText.Text = $"Brightness: {_config.Matrix.Brightness:0.###}";
        GammaText.Text = $"Gamma: {_config.Matrix.Gamma:0.###}";
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
