using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls;
using VirtualDofMatrix.App.Rendering;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App;

public partial class MainWindow : Window
{
    private const int MinimumDotSpacing = 2;
    private readonly AppConfig _config;
    private readonly IMatrixRenderer _matrixRenderer;
    private bool _isApplyingAspectLock;
    private double _lockedAspectRatio;

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
        ApplyDebugVisibility();
        _lockedAspectRatio = Math.Max(1.0, _config.Matrix.Width / (double)_config.Matrix.Height);

        Loaded += (_, _) => ReinitializeRendererForViewport();
        SizeChanged += OnWindowSizeChanged;
    }

    public void ApplyPresentation(FramePresentation presentation)
    {
        _latestPresentation = presentation;

        OutputSequenceText.Text = $"Output sequence: {presentation.OutputSequence}";
        PresentedAtText.Text = $"Presented at UTC: {presentation.PresentedAtUtc:O}";
        PayloadLengthText.Text = $"Payload bytes: {presentation.RgbBytes.Length}";

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
        DotSizeText.Text = "Dot size: auto";
        DotSpacingText.Text = "Dot spacing: auto";
        BrightnessText.Text = $"Brightness: {_config.Matrix.Brightness:0.###}";
        GammaText.Text = $"Gamma: {_config.Matrix.Gamma:0.###}";
    }

    private void ApplyDebugVisibility()
    {
        if (_config.Debug.ShowDebug)
        {
            return;
        }

        RootGrid.Margin = new Thickness(0);
        DebugPanel.Visibility = Visibility.Collapsed;
        DebugSpacerColumn.Width = new GridLength(0);
        DebugPanelColumn.Width = new GridLength(0);
        MatrixViewportBorder.Padding = new Thickness(0);
        MatrixViewportBorder.BorderThickness = new Thickness(0);
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isApplyingAspectLock || !IsLoaded)
        {
            return;
        }

        var widthDelta = Math.Abs(e.NewSize.Width - e.PreviousSize.Width);
        var heightDelta = Math.Abs(e.NewSize.Height - e.PreviousSize.Height);

        if (widthDelta > 0.01 || heightDelta > 0.01)
        {
            try
            {
                _isApplyingAspectLock = true;

                if (widthDelta >= heightDelta)
                {
                    Height = Math.Max(MinHeight, Width / _lockedAspectRatio);
                }
                else
                {
                    Width = Math.Max(MinWidth, Height * _lockedAspectRatio);
                }
            }
            finally
            {
                _isApplyingAspectLock = false;
            }
        }

        ReinitializeRendererForViewport();
    }

    private void ReinitializeRendererForViewport()
    {
        if (!IsLoaded)
        {
            return;
        }

        var effectiveMatrixConfig = BuildViewportAdaptiveMatrixConfig();
        _matrixRenderer.Initialize(MatrixCanvas, effectiveMatrixConfig);

        DotShapeText.Text = $"Dot shape: {effectiveMatrixConfig.DotShape}";
        DotSizeText.Text = $"Dot size: auto ({effectiveMatrixConfig.DotSize})";
        DotSpacingText.Text = $"Dot spacing: auto ({effectiveMatrixConfig.DotSpacing})";

        if (_latestPresentation is not null)
        {
            _matrixRenderer.Render(_latestPresentation);
        }
    }

    private MatrixConfig BuildViewportAdaptiveMatrixConfig()
    {
        const double borderPadding = 16.0;
        var viewportWidth = Math.Max(1.0, MatrixViewportBorder.ActualWidth - borderPadding);
        var viewportHeight = Math.Max(1.0, MatrixViewportBorder.ActualHeight - borderPadding);

        var strideFromWidth = (int)Math.Floor(viewportWidth / Math.Max(1, _config.Matrix.Width));
        var strideFromHeight = (int)Math.Floor(viewportHeight / Math.Max(1, _config.Matrix.Height));
        var stride = Math.Max(1, Math.Min(strideFromWidth, strideFromHeight));
        var spacing = MinimumDotSpacing;
        var dotSize = Math.Max(1, stride - spacing);

        return new MatrixConfig
        {
            Width = _config.Matrix.Width,
            Height = _config.Matrix.Height,
            Mapping = _config.Matrix.Mapping,
            DotShape = _config.Matrix.DotShape,
            DotSize = dotSize,
            DotSpacing = spacing,
            Brightness = _config.Matrix.Brightness,
            Gamma = _config.Matrix.Gamma,
            InstantTrigger = _config.Matrix.InstantTrigger,
            Visual = new MatrixVisualConfig
            {
                OffStateTintR = _config.Matrix.Visual.OffStateTintR,
                OffStateTintG = _config.Matrix.Visual.OffStateTintG,
                OffStateTintB = _config.Matrix.Visual.OffStateTintB,
                OffStateAlpha = _config.Matrix.Visual.OffStateAlpha,
                LensFalloff = _config.Matrix.Visual.LensFalloff,
                SpecularHotspot = _config.Matrix.Visual.SpecularHotspot,
                RimHighlight = _config.Matrix.Visual.RimHighlight,
            },
            Bloom = new BloomConfig
            {
                Enabled = _config.Matrix.Bloom.Enabled,
                QualityPreset = _config.Matrix.Bloom.QualityPreset,
                Threshold = _config.Matrix.Bloom.Threshold,
                SmallRadius = _config.Matrix.Bloom.SmallRadius,
                WideRadius = _config.Matrix.Bloom.WideRadius,
                SmallStrength = _config.Matrix.Bloom.SmallStrength,
                WideStrength = _config.Matrix.Bloom.WideStrength,
                BufferScaleDivisor = _config.Matrix.Bloom.BufferScaleDivisor,
            },
        };
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
