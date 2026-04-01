using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Interop;
using VirtualDofMatrix.App.Rendering;
using VirtualDofMatrix.App.Rendering.Vulkan;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App;

public partial class MainWindow : Window
{
    private const int WmEnterSizeMove = 0x0231;
    private const int WmExitSizeMove = 0x0232;
    private const int HardMinimumDotSpacing = 2;
    private readonly AppConfig _config;
    private IMatrixRenderer _matrixRenderer;
    private bool _isApplyingAspectLock;
    private bool _isInResizeMove;
    private bool _pendingViewportReinitialize;
    private double _lockedAspectRatio;
    private IntPtr _windowHandle;

    private FramePresentation? _latestPresentation;
    private bool _isRenderingPaused;

    public event EventHandler? SettingsRequested;

    public MainWindow(AppConfig config)
        : this(config, CreateRenderer(config))
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

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => ReinitializeRendererForViewport();
        SizeChanged += OnWindowSizeChanged;
        Closed += (_, _) => _matrixRenderer.DisposeRenderer();
    }

    public void ApplyPresentation(FramePresentation presentation)
    {
        _latestPresentation = presentation;

        OutputSequenceText.Text = $"Output sequence: {presentation.OutputSequence}";
        PresentedAtText.Text = $"Presented at UTC: {presentation.PresentedAtUtc:O}";
        PayloadLengthText.Text = $"Payload bytes: {presentation.RgbBytes.Length}";

        if (!_isRenderingPaused)
        {
            _matrixRenderer.Render(presentation);
        }
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
        DotShapeText.Text = $"Dot shape: {_config.Matrix.DotShape} (renderer: {_config.Matrix.Renderer})";
        DotSizeText.Text = "Dot size: auto";
        DotSpacingText.Text = "Min dot spacing: auto";
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

        if (_isInResizeMove)
        {
            _pendingViewportReinitialize = true;
            _matrixRenderer.NotifyHostResized((int)Math.Max(1, e.NewSize.Width), (int)Math.Max(1, e.NewSize.Height));
            return;
        }

        _matrixRenderer.NotifyHostResized((int)Math.Max(1, e.NewSize.Width), (int)Math.Max(1, e.NewSize.Height));
        ReinitializeRendererForViewport();
    }

    private void ReinitializeRendererForViewport()
    {
        if (!IsLoaded)
        {
            return;
        }

        var effectiveMatrixConfig = BuildViewportAdaptiveMatrixConfig();
        MatrixNativeHost.Content = _matrixRenderer.GetNativeHostElement();
        MatrixNativeHost.Visibility = _matrixRenderer.UsesNativeHost ? Visibility.Visible : Visibility.Collapsed;
        MatrixImage.Visibility = _matrixRenderer.UsesImageHost ? Visibility.Visible : Visibility.Collapsed;
        MatrixCanvas.Visibility = (!_matrixRenderer.UsesImageHost && !_matrixRenderer.UsesNativeHost)
            ? Visibility.Visible
            : Visibility.Collapsed;
        _matrixRenderer.Initialize(MatrixCanvas, MatrixImage, effectiveMatrixConfig);

        DotShapeText.Text = $"Dot shape: {effectiveMatrixConfig.DotShape}";
        DotSizeText.Text = $"Dot size: auto ({effectiveMatrixConfig.DotSize})";
        DotSpacingText.Text = $"Min dot spacing: auto ({effectiveMatrixConfig.MinDotSpacing})";

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
        var spacing = Math.Max(HardMinimumDotSpacing, _config.Matrix.MinDotSpacing);
        var dotSize = Math.Max(1, stride - spacing);

        return new MatrixConfig
        {
            Width = _config.Matrix.Width,
            Height = _config.Matrix.Height,
            Renderer = _config.Matrix.Renderer,
            Mapping = _config.Matrix.Mapping,
            DotShape = _config.Matrix.DotShape,
            DotSize = dotSize,
            MinDotSpacing = spacing,
            Brightness = _config.Matrix.Brightness,
            Gamma = _config.Matrix.Gamma,
            ToneMapping = new ToneMappingConfig
            {
                Enabled = _config.Matrix.ToneMapping.Enabled,
                KneeStart = _config.Matrix.ToneMapping.KneeStart,
                Strength = _config.Matrix.ToneMapping.Strength,
            },
            TemporalSmoothing = new TemporalSmoothingConfig
            {
                Enabled = _config.Matrix.TemporalSmoothing.Enabled,
                RiseAlpha = _config.Matrix.TemporalSmoothing.RiseAlpha,
                FallAlpha = _config.Matrix.TemporalSmoothing.FallAlpha,
            },
            Visual = new MatrixVisualConfig
            {
                FlatShading = _config.Matrix.Visual.FlatShading,
                UseRgbBulbShading = _config.Matrix.Visual.UseRgbBulbShading,
                DisableDynamicLayerOpacity = _config.Matrix.Visual.DisableDynamicLayerOpacity,
                BodyContribution = _config.Matrix.Visual.BodyContribution,
                CoreContribution = _config.Matrix.Visual.CoreContribution,
                SpecularContribution = _config.Matrix.Visual.SpecularContribution,
                CoreBase = _config.Matrix.Visual.CoreBase,
                CoreIntensityScale = _config.Matrix.Visual.CoreIntensityScale,
                SpecularBase = _config.Matrix.Visual.SpecularBase,
                SpecularIntensityScale = _config.Matrix.Visual.SpecularIntensityScale,
                SpecularMax = _config.Matrix.Visual.SpecularMax,
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
            Vulkan = new VulkanRenderConfig
            {
                TargetFps = _config.Matrix.Vulkan.TargetFps,
                PresentMode = _config.Matrix.Vulkan.PresentMode,
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

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (PresentationSource.FromVisual(this) is HwndSource hwndSource)
        {
            _windowHandle = hwndSource.Handle;
            hwndSource.AddHook(WndProc);
            _matrixRenderer.SetNativeHostHandle(_windowHandle);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmEnterSizeMove:
                _isInResizeMove = true;
                break;
            case WmExitSizeMove:
                _isInResizeMove = false;
                if (_pendingViewportReinitialize)
                {
                    _pendingViewportReinitialize = false;
                    ReinitializeRendererForViewport();
                }
                break;
        }

        return IntPtr.Zero;
    }

    public void ApplyRuntimeSettings()
    {
        ApplyPersistedWindowSettings();
        ApplyPersistedVisualSettings();
        _lockedAspectRatio = Math.Max(1.0, _config.Matrix.Width / (double)_config.Matrix.Height);
        _matrixRenderer.DisposeRenderer();
        _matrixRenderer = CreateRenderer(_config);
        if (_windowHandle != IntPtr.Zero)
        {
            _matrixRenderer.SetNativeHostHandle(_windowHandle);
            _matrixRenderer.NotifyHostResized((int)Math.Max(1, ActualWidth), (int)Math.Max(1, ActualHeight));
        }
        ReinitializeRendererForViewport();
    }

    public void SetRenderingPaused(bool paused)
    {
        _isRenderingPaused = paused;
        PauseRenderingMenuItem.IsChecked = paused;
        if (!paused && _latestPresentation is not null)
        {
            _matrixRenderer.Render(_latestPresentation);
        }
    }

    private void OnSettingsMenuClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void OnPauseRenderingClick(object sender, RoutedEventArgs e) => SetRenderingPaused(PauseRenderingMenuItem.IsChecked);

    private void OnExitMenuClick(object sender, RoutedEventArgs e) => Close();

    private static IMatrixRenderer CreateRenderer(AppConfig config)
    {
        if (config.Matrix.Renderer.Equals("vulkan", StringComparison.OrdinalIgnoreCase))
        {
            return new VulkanMatrixRenderer();
        }

        return new WpfPrimitiveMatrixRenderer();
    }
}
