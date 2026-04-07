using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using VirtualDofMatrix.App.Rendering;
using VirtualDofMatrix.App.Logging;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App;

// Overview: MainWindow owns viewport behavior (always-on-top/borderless/aspect lock) and forwards frames to the active renderer.
public partial class MainWindow : Window
{
    private enum AspectLockAxis
    {
        None,
        Width,
        Height,
    }

    private const int WmEnterSizeMove = 0x0231;
    private const int WmExitSizeMove = 0x0232;
    private const int HardMinimumDotSpacing = 2;
    private static readonly TimeSpan MinRenderInterval = TimeSpan.FromMilliseconds(33);
    private readonly AppConfig _config;
    private IMatrixRenderer _matrixRenderer;
    private bool _isApplyingAspectLock;
    private bool _isInResizeMove;
    private bool _pendingViewportReinitialize;
    private AspectLockAxis _activeResizeAspectAxis = AspectLockAxis.None;
    private double _lockedAspectRatio;

    private FramePresentation? _latestPresentation;
    private readonly DispatcherTimer _idleClearTimer;
    private readonly DispatcherTimer _fpsTimer;
    private DateTimeOffset _lastFrameUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastRenderUtc = DateTimeOffset.MinValue;
    private bool _idleCleared;
    private FramePresentation? _idleOffPresentation;
    private int _framesSinceFpsSample;
    private DateTimeOffset _fpsSampleStartUtc = DateTimeOffset.UtcNow;
    private int _forcedRenderBurstsRemaining;

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

        _idleClearTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        _idleClearTimer.Tick += OnIdleClearTick;

        _fpsTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _fpsTimer.Tick += OnFpsTick;

        ApplyPersistedWindowSettings();
        ApplyPersistedVisualSettings();
        ApplyDebugVisibility();
        _lockedAspectRatio = Math.Max(1.0, _config.Matrix.Width / (double)_config.Matrix.Height);

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => ReinitializeRendererForViewport();
        SizeChanged += OnWindowSizeChanged;
        Closed += (_, _) =>
        {
            _idleClearTimer.Stop();
            _fpsTimer.Stop();
            _matrixRenderer.Dispose();
        };
        _idleClearTimer.Start();
        _fpsTimer.Start();
    }

    public void ApplyPresentation(FramePresentation presentation)
    {
        // Keep lightweight UI metadata updates here; heavy composition is delegated to the renderer implementation.
        _latestPresentation = presentation;
        _lastFrameUtc = DateTimeOffset.UtcNow;
        _idleCleared = false;

        OutputSequenceText.Text = $"Output sequence: {presentation.OutputSequence}";
        PresentedAtText.Text = $"Presented at UTC: {presentation.PresentedAtUtc:O}";
        PayloadLengthText.Text = $"Payload bytes: {presentation.RgbBytes.Length}";

        _matrixRenderer.UpdateFrame(presentation);
        // Render() is intentionally lightweight now (CPU compose happens off-thread), so ApplyPresentation stays responsive.
        var now = DateTimeOffset.UtcNow;
        var isCpuBackend = _matrixRenderer.BackendName.Equals("cpu", StringComparison.OrdinalIgnoreCase);
        if (isCpuBackend || _forcedRenderBurstsRemaining > 0 || (now - _lastRenderUtc) >= MinRenderInterval)
        {
            _matrixRenderer.Render();
            _lastRenderUtc = now;
            if (_forcedRenderBurstsRemaining > 0)
            {
                _forcedRenderBurstsRemaining--;
            }
        }
        _framesSinceFpsSample++;
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
        RendererText.Text = $"Renderer: {NormalizeRendererLabel(_config.Matrix.Renderer)}";
        VisualQualityText.Text = $"Visual quality: {_config.Settings.VisualQuality}";
        DotShapeText.Text = $"Dot shape: {_config.Matrix.DotShape}";
        DotSizeText.Text = "Dot size: auto";
        DotSpacingText.Text = "Min dot spacing: auto";
        BrightnessText.Text = $"Brightness: {_config.Matrix.Brightness:0.###}";
        GammaText.Text = $"Gamma: {_config.Matrix.Gamma:0.###}";
    }

    private void ApplyDebugVisibility()
    {
        ShowDebugMenuItem.IsChecked = _config.Debug.ShowDebug;

        RootGrid.Margin = _config.Debug.ShowDebug ? new Thickness(16) : new Thickness(0);
        DebugPanel.Visibility = _config.Debug.ShowDebug ? Visibility.Visible : Visibility.Collapsed;
        DebugSpacerColumn.Width = _config.Debug.ShowDebug ? new GridLength(24) : new GridLength(0);
        DebugPanelColumn.Width = _config.Debug.ShowDebug ? GridLength.Auto : new GridLength(0);
        MatrixViewportBorder.Padding = _config.Debug.ShowDebug ? new Thickness(8) : new Thickness(0);
        MatrixViewportBorder.BorderThickness = _config.Debug.ShowDebug ? new Thickness(1) : new Thickness(0);
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
            EnforceAspectRatio(widthDelta, heightDelta);
        }

        if (_isInResizeMove)
        {
            _pendingViewportReinitialize = true;
            return;
        }

        ReinitializeRendererForViewport();
    }

    private void ReinitializeRendererForViewport()
    {
        if (!IsLoaded)
        {
            return;
        }

        // Reinitialize on viewport changes so dot pitch and render targets match the new window geometry.
        var effectiveMatrixConfig = BuildViewportAdaptiveMatrixConfig();
        MatrixImage.Visibility = _matrixRenderer.UsesImageHost ? Visibility.Visible : Visibility.Collapsed;
        MatrixCanvas.Visibility = _matrixRenderer.UsesImageHost ? Visibility.Collapsed : Visibility.Visible;
        var dotStyle = new DotStyleConfig(
            effectiveMatrixConfig.DotShape,
            effectiveMatrixConfig.Mapping,
            effectiveMatrixConfig.DotSize,
            effectiveMatrixConfig.MinDotSpacing,
            effectiveMatrixConfig.FillGapEnabled,
            effectiveMatrixConfig.Brightness,
            effectiveMatrixConfig.Gamma,
            effectiveMatrixConfig.Visual,
            effectiveMatrixConfig.ToneMapping,
            effectiveMatrixConfig.TemporalSmoothing,
            effectiveMatrixConfig.Bloom,
            effectiveMatrixConfig.Visual.ForceCpuDotRasterFallback,
            effectiveMatrixConfig.Bloom.Enabled,
            effectiveMatrixConfig.Bloom.NearStrength);
        _matrixRenderer.Initialize(new MatrixRendererSurface(MatrixCanvas, MatrixImage), effectiveMatrixConfig.Width, effectiveMatrixConfig.Height, dotStyle);
        _matrixRenderer.Resize(MatrixViewportBorder.ActualWidth, MatrixViewportBorder.ActualHeight);
        _lastRenderUtc = DateTimeOffset.MinValue;

        DotShapeText.Text = $"Dot shape: {effectiveMatrixConfig.DotShape}";
        DotSizeText.Text = $"Dot size: auto ({effectiveMatrixConfig.DotSize})";
        DotSpacingText.Text = $"Min dot spacing: auto ({effectiveMatrixConfig.MinDotSpacing})";

        if (_latestPresentation is not null)
        {
            _matrixRenderer.UpdateFrame(_latestPresentation);
            _matrixRenderer.Render();
            return;
        }

        _idleOffPresentation ??= BuildIdleOffPresentation();
        _matrixRenderer.UpdateFrame(_idleOffPresentation);
        _matrixRenderer.Render();
    }

    private MatrixConfig BuildViewportAdaptiveMatrixConfig()
    {
        const double borderPadding = 16.0;
        var viewportWidth = Math.Max(1.0, MatrixViewportBorder.ActualWidth - borderPadding);
        var viewportHeight = Math.Max(1.0, MatrixViewportBorder.ActualHeight - borderPadding);

        var strideFromWidth = (int)Math.Floor(viewportWidth / Math.Max(1, _config.Matrix.Width));
        var strideFromHeight = (int)Math.Floor(viewportHeight / Math.Max(1, _config.Matrix.Height));
        // fillGapEnabled intentionally biases toward the larger axis so narrow strips (for example 5x1) don't leave oversized visual gaps.
        var stride = _config.Matrix.FillGapEnabled
            ? Math.Max(1, Math.Max(strideFromWidth, strideFromHeight))
            : Math.Max(1, Math.Min(strideFromWidth, strideFromHeight));
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
            FillGapEnabled = _config.Matrix.FillGapEnabled,
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
                GpuPresentMode = _config.Matrix.Visual.GpuPresentMode,
                ForceCpuDotRasterFallback = _config.Matrix.Visual.ForceCpuDotRasterFallback,
                EnableDirectPresentParitySampling = _config.Matrix.Visual.EnableDirectPresentParitySampling,
                EnableDiagnosticReadbackCapture = _config.Matrix.Visual.EnableDiagnosticReadbackCapture,
                FlatShading = _config.Matrix.Visual.FlatShading,
                FullBrightnessRadiusMinPct = _config.Matrix.Visual.FullBrightnessRadiusMinPct,
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
                Threshold = _config.Matrix.Bloom.Threshold,
                SoftKnee = _config.Matrix.Bloom.SoftKnee,
                DownsampleDivisor = _config.Matrix.Bloom.DownsampleDivisor,
                NearRadiusPx = _config.Matrix.Bloom.NearRadiusPx,
                FarRadiusPx = _config.Matrix.Bloom.FarRadiusPx,
                NearStrength = _config.Matrix.Bloom.NearStrength,
                FarStrength = _config.Matrix.Bloom.FarStrength,
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
            hwndSource.AddHook(WndProc);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmEnterSizeMove:
                _isInResizeMove = true;
                _activeResizeAspectAxis = AspectLockAxis.None;
                break;
            case WmExitSizeMove:
                _isInResizeMove = false;
                _activeResizeAspectAxis = AspectLockAxis.None;
                if (_pendingViewportReinitialize)
                {
                    _pendingViewportReinitialize = false;
                    ReinitializeRendererForViewport();
                }
                break;
        }

        return IntPtr.Zero;
    }

    private void EnforceAspectRatio(double widthDelta, double heightDelta)
    {
        try
        {
            _isApplyingAspectLock = true;

            if (_isInResizeMove && _activeResizeAspectAxis == AspectLockAxis.None)
            {
                _activeResizeAspectAxis = widthDelta >= heightDelta ? AspectLockAxis.Width : AspectLockAxis.Height;
            }

            var axis = _isInResizeMove
                ? _activeResizeAspectAxis
                : (widthDelta >= heightDelta ? AspectLockAxis.Width : AspectLockAxis.Height);

            if (axis == AspectLockAxis.Height)
            {
                Width = Math.Max(MinWidth, Height * _lockedAspectRatio);
                return;
            }

            Height = Math.Max(MinHeight, Width / _lockedAspectRatio);
        }
        finally
        {
            _isApplyingAspectLock = false;
        }
    }

    public void ApplyRuntimeSettings()
    {
        var previousRenderer = _matrixRenderer.BackendName;
        ApplyPersistedWindowSettings();
        ApplyPersistedVisualSettings();
        _lockedAspectRatio = Math.Max(1.0, _config.Matrix.Width / (double)_config.Matrix.Height);
        _idleOffPresentation = null;
        ReplaceRenderer(CreateRenderer(_config));
        _forcedRenderBurstsRemaining = 6;
        AppLogger.Info($"[window] renderer-switch {previousRenderer}->{_matrixRenderer.BackendName} seq={_latestPresentation?.OutputSequence ?? 0}");
        ApplyDebugVisibility();
        ReinitializeRendererForViewport();

        // Conversational note: one extra replay pass makes renderer backend switches (GPU<->CPU) deterministic mid-flight.
        if (_latestPresentation is not null)
        {
            _matrixRenderer.UpdateFrame(_latestPresentation);
            _matrixRenderer.Render();
        }
    }

    public void SetShowDebug(bool showDebug)
    {
        _config.Debug.ShowDebug = showDebug;
        ApplyDebugVisibility();
    }

    private void OnSettingsMenuClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void OnShowDebugClick(object sender, RoutedEventArgs e) => SetShowDebug(ShowDebugMenuItem.IsChecked);

    private void OnExitMenuClick(object sender, RoutedEventArgs e) => Close();

    private void OnIdleClearTick(object? sender, EventArgs e)
    {
        if (_idleCleared)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (_lastFrameUtc == DateTimeOffset.MinValue || (now - _lastFrameUtc) < TimeSpan.FromSeconds(1.5))
        {
            return;
        }

        // Note: when DOF goes quiet we hard-reset renderer state so temporal smoothing
        // and bloom history cannot keep the previous table frame visible underneath the "off" dots.
        _matrixRenderer.Clear();
        _matrixRenderer.Render();
        _idleCleared = true;
    }

    private FramePresentation BuildIdleOffPresentation()
    {
        var ledCount = Math.Max(1, _config.Matrix.Width * _config.Matrix.Height);
        return new FramePresentation(new byte[ledCount * 3], ledCount, ledCount, 0, DateTimeOffset.UtcNow);
    }

    private void OnFpsTick(object? sender, EventArgs e)
    {
        var now = DateTimeOffset.UtcNow;
        var elapsed = (now - _fpsSampleStartUtc).TotalSeconds;
        if (elapsed <= 0)
        {
            return;
        }

        var fps = _framesSinceFpsSample / elapsed;
        FpsText.Text = $"FPS: {fps:0.0}";
        _framesSinceFpsSample = 0;
        _fpsSampleStartUtc = now;
    }

    private static string NormalizeRendererLabel(string renderer)
    {
        return renderer.Equals("cpu", StringComparison.OrdinalIgnoreCase) ? "CPU" : "GPU";
    }

    private void ReplaceRenderer(IMatrixRenderer nextRenderer)
    {
        _matrixRenderer.Dispose();
        MatrixImage.Source = null;
        MatrixCanvas.Children.Clear();
        _matrixRenderer = nextRenderer;
        AppLogger.Info($"[window] renderer-replaced backend={_matrixRenderer.BackendName}");
    }


    private static IMatrixRenderer CreateRenderer(AppConfig config)
    {
        // Fill-gap mode relies on per-axis stretch behavior that the CPU path already supports robustly for short strips.
        if (config.Matrix.FillGapEnabled)
        {
            return new CpuMatrixRenderer();
        }

        if (config.Matrix.Renderer.Equals("cpu", StringComparison.OrdinalIgnoreCase) ||
            config.Matrix.Renderer.Equals("primitive", StringComparison.OrdinalIgnoreCase))
        {
            return new CpuMatrixRenderer();
        }

        return new GpuInstancedMatrixRenderer();
    }
}
