using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using VirtualDofMatrix.App.Configuration;
using VirtualDofMatrix.App.Rendering;
using VirtualDofMatrix.App.Logging;
using VirtualDofMatrix.Core;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;

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
    private readonly StartupConfigStatus _startupConfigStatus;
    private IMatrixRenderer _matrixRenderer;
    private bool _isApplyingAspectLock;
    private bool _isInResizeMove;
    private bool _pendingViewportReinitialize;
    private Window? _layoutOverlayWindow;
    private Border? _layoutOverlaySelectionBorder;
    private Border? _layoutOverlayNameBorder;
    private TextBlock? _layoutOverlayNameText;
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
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    public event EventHandler? SettingsRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler? LayoutWindowSelected;

    public bool IsAspectRatioLocked => _config.Window.LockAspectRatio;

    public MainWindow(AppConfig config, StartupConfigStatus startupConfigStatus)
        : this(config, startupConfigStatus, CreateRenderer(config))
    {
    }

    internal MainWindow(AppConfig config, StartupConfigStatus startupConfigStatus, IMatrixRenderer matrixRenderer)
    {
        _config = config;
        _startupConfigStatus = startupConfigStatus;
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
        ApplyStartupStatus();
        ApplyDebugVisibility();
        RefreshLockedAspectRatioFromWindow();

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => ReinitializeRendererForViewport();
        SizeChanged += OnWindowSizeChanged;
        LocationChanged += (_, _) => SyncLayoutOverlayWindowBounds();
        Closed += (_, _) =>
        {
            _idleClearTimer.Stop();
            _fpsTimer.Stop();
            _matrixRenderer.Dispose();
            if (_layoutOverlayWindow is not null)
            {
                _layoutOverlayWindow.Close();
                _layoutOverlayWindow = null;
            }
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
        _config.Window.LockAspectRatio = LockAspectRatioMenuItem.IsChecked;
    }

    private void ApplyPersistedWindowSettings()
    {
        Topmost = _config.Window.AlwaysOnTop;
        WindowStyle = _config.Window.Borderless ? WindowStyle.None : WindowStyle.SingleBorderWindow;
        // Note: WPF forbids changing AllowsTransparency after a window handle exists.
        // Apply it only during pre-show initialization; runtime toggles are deferred until window recreation.
        if (!IsLoaded && !IsVisible)
        {
            AllowsTransparency = _config.Window.Borderless && !_config.Window.BackgroundVisible;
        }

        Left = _config.Window.Left;
        Top = _config.Window.Top;
        Width = _config.Window.Width;
        Height = _config.Window.Height;
    }

    private void ApplyPersistedVisualSettings()
    {
        ApplyWindowBackground();
        RendererText.Text = $"Renderer: {NormalizeRendererLabel(_config.Matrix.Renderer)}";
        VisualQualityText.Text = $"Visual quality: {_config.Settings.VisualQuality}";
        DotShapeText.Text = $"Dot shape: {_config.Matrix.DotShape}";
        DotSizeText.Text = "Dot size: auto";
        DotSpacingText.Text = "Min dot spacing: auto";
        BrightnessText.Text = $"Brightness: {_config.Matrix.Brightness:0.###}";
        GammaText.Text = $"Gamma: {_config.Matrix.Gamma:0.###}";
    }

    private void ApplyWindowBackground()
    {
        // Note: toy windows can now request transparent backgrounds so strips can float over arbitrary artwork.
        var brush = BuildWindowBackgroundBrush(_config.Window);
        Background = brush;
        MatrixViewportBorder.Background = brush;
        MatrixHostGrid.Background = brush;
        MatrixCanvas.Background = brush;
        TransparentHoverCapture.Visibility = _config.Window.BackgroundVisible ? Visibility.Collapsed : Visibility.Visible;
        TransparentHoverCapture.Background = _config.Window.BackgroundVisible
            ? WpfBrushes.Transparent
            : new SolidColorBrush(WpfColor.FromArgb(1, 0, 0, 0));
    }

    private void OnTransparentHoverCaptureMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_config.Window.BackgroundVisible)
        {
            return;
        }

        // Note: transparent windows can be hard to grab; show a temporary dark backing while hovered.
        TransparentHoverCapture.Background = new SolidColorBrush(WpfColor.FromArgb(160, 0, 0, 0));
    }

    private void OnTransparentHoverCaptureMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_config.Window.BackgroundVisible)
        {
            return;
        }

        TransparentHoverCapture.Background = new SolidColorBrush(WpfColor.FromArgb(1, 0, 0, 0));
    }

    private static System.Windows.Media.Brush BuildWindowBackgroundBrush(WindowConfig window)
    {
        if (!window.BackgroundVisible)
        {
            return WpfBrushes.Transparent;
        }

        if (!string.IsNullOrWhiteSpace(window.BackgroundColor))
        {
            try
            {
                if (System.Windows.Media.ColorConverter.ConvertFromString(window.BackgroundColor) is WpfColor parsed)
                {
                    return new SolidColorBrush(parsed);
                }
            }
            catch (FormatException)
            {
                // Note: invalid color input safely falls back to black instead of blocking startup.
            }
        }

        return WpfBrushes.Black;
    }

    private static void ResolveWindowBackgroundRgb(WindowConfig window, out float r, out float g, out float b)
    {
        // Note: transparent window mode still resolves to black so renderer fallbacks stay deterministic.
        if (!window.BackgroundVisible)
        {
            r = 0f;
            g = 0f;
            b = 0f;
            return;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(window.BackgroundColor) &&
                System.Windows.Media.ColorConverter.ConvertFromString(window.BackgroundColor) is WpfColor parsed)
            {
                r = parsed.R / 255f;
                g = parsed.G / 255f;
                b = parsed.B / 255f;
                return;
            }
        }
        catch (FormatException)
        {
            // Note: fallback mirrors brush generation behavior (invalid color => black).
        }

        r = 0f;
        g = 0f;
        b = 0f;
    }

    private void ApplyStartupStatus()
    {
        // Note: startup diagnostics are logged instead of rendered in-window to keep viewer windows uncluttered.
        AppLogger.Info($"[startup] activeConfigPath={_startupConfigStatus.ActiveConfigPath}");
        AppLogger.Info($"[startup] cabinetStatus={_startupConfigStatus.CabinetFileStatus}");
        AppLogger.Info($"[startup] lastLoadedUtc={_startupConfigStatus.LastLoadedUtc:O}");
        if (!string.IsNullOrWhiteSpace(_startupConfigStatus.RemediationHint))
        {
            AppLogger.Warn($"[startup] remediationHint={_startupConfigStatus.RemediationHint}");
        }
    }

    private void ApplyDebugVisibility()
    {
        ShowDebugMenuItem.IsChecked = _config.Debug.ShowDebug;
        LockAspectRatioMenuItem.IsChecked = _config.Window.LockAspectRatio;

        RootGrid.Margin = _config.Debug.ShowDebug ? new Thickness(16) : new Thickness(0);
        DebugPanel.Visibility = _config.Debug.ShowDebug ? Visibility.Visible : Visibility.Collapsed;
        DebugSpacerColumn.Width = _config.Debug.ShowDebug ? new GridLength(24) : new GridLength(0);
        DebugPanelColumn.Width = _config.Debug.ShowDebug ? GridLength.Auto : new GridLength(0);
        // Note: bloom can extend beyond lit pixels; keep extra container padding so far-radius glow isn't clipped.
        var bloomPadding = Math.Max(0, _config.Matrix.Bloom.FarRadiusPx);
        MatrixViewportBorder.Padding = _config.Debug.ShowDebug
            ? new Thickness(8 + bloomPadding)
            : new Thickness(bloomPadding);
        MatrixViewportBorder.BorderThickness = _config.Debug.ShowDebug ? new Thickness(1) : new Thickness(0);
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        SyncLayoutOverlayWindowBounds();

        if (_isApplyingAspectLock || !IsLoaded)
        {
            return;
        }

        var widthDelta = Math.Abs(e.NewSize.Width - e.PreviousSize.Width);
        var heightDelta = Math.Abs(e.NewSize.Height - e.PreviousSize.Height);

        if (_config.Window.LockAspectRatio && (widthDelta > 0.01 || heightDelta > 0.01))
        {
            EnforceAspectRatio(widthDelta, heightDelta);
        }

        if (_isInResizeMove)
        {
            // Note: keep dots filling the viewport during live drag-resize; final reinit still runs on WM_EXITSIZEMOVE.
            _matrixRenderer.Resize(MatrixViewportBorder.ActualWidth, MatrixViewportBorder.ActualHeight);
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
        // Note: this keeps renderer-side background fills exactly in sync with the selected window background color.
        ResolveWindowBackgroundRgb(_config.Window, out var backgroundR, out var backgroundG, out var backgroundB);

        var strideFromWidth = (int)Math.Floor(viewportWidth / Math.Max(1, _config.Matrix.Width));
        var strideFromHeight = (int)Math.Floor(viewportHeight / Math.Max(1, _config.Matrix.Height));
        var isSingleAxisStrip = _config.Matrix.Width == 1 || _config.Matrix.Height == 1;
        var spacing = Math.Max(HardMinimumDotSpacing, _config.Matrix.MinDotSpacing);
        int dotSize;

        if (isSingleAxisStrip)
        {
            var ledCount = Math.Max(_config.Matrix.Width, _config.Matrix.Height);
            var majorAxisPixels = _config.Matrix.Width > 1 ? viewportWidth : viewportHeight;
            var minorAxisPixels = _config.Matrix.Width > 1 ? viewportHeight : viewportWidth;
            var farBloomRadius = Math.Max(0, _config.Matrix.Bloom.FarRadiusPx);
            // Note: strip bulbs should primarily size from the short axis (thickness), while bloom
            // still needs breathing room on both sides so the glow doesn't get clipped.
            var candidateDotSizeFromMinorAxis = (int)Math.Floor(minorAxisPixels - (farBloomRadius * 2.0));
            // Note: major-axis fit remains a safety rail so long strips still fit end-to-end.
            var candidateDotSizeFromMajorAxis = (int)Math.Floor((majorAxisPixels - (spacing * Math.Max(0, ledCount - 1))) / Math.Max(1, ledCount));
            dotSize = Math.Max(1, Math.Min(candidateDotSizeFromMinorAxis, candidateDotSizeFromMajorAxis));

            if (ledCount > 1)
            {
                var remainingAxisPixels = majorAxisPixels - (dotSize * ledCount);
                var stretchedSpacing = (int)Math.Floor(remainingAxisPixels / (ledCount - 1));
                spacing = Math.Max(spacing, stretchedSpacing);
            }
        }
        else
        {
            // fillGapEnabled intentionally biases toward the larger axis so narrow matrices don't leave oversized visual gaps.
            var stride = _config.Matrix.FillGapEnabled
                ? Math.Max(1, Math.Max(strideFromWidth, strideFromHeight))
                : Math.Max(1, Math.Min(strideFromWidth, strideFromHeight));
            dotSize = Math.Max(1, stride - spacing);
        }

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
                // Note: render the dot field with transparent pixels so the configured window color
                // is the actual backdrop behind bulbs (instead of an opaque black raster strip).
                TransparentBackground = true,
                // Note: transparent toy windows now use the GPU interop present path so per-pixel alpha
                // matches the strip-window behavior instead of falling back to opaque readback compositing.
                GpuPresentMode = _config.Matrix.Visual.GpuPresentMode,
                // Note: keep dot raster on GPU for both solid and transparent windows unless explicitly forced by config.
                ForceCpuDotRasterFallback = _config.Matrix.Visual.ForceCpuDotRasterFallback,
                EnableDirectPresentParitySampling = _config.Matrix.Visual.EnableDirectPresentParitySampling,
                EnableDiagnosticReadbackCapture = _config.Matrix.Visual.EnableDiagnosticReadbackCapture,
                FlatShading = _config.Matrix.Visual.FlatShading,
                FullBrightnessRadiusMinPct = _config.Matrix.Visual.FullBrightnessRadiusMinPct,
                OffStateTintR = _config.Matrix.Visual.OffStateTintR,
                OffStateTintG = _config.Matrix.Visual.OffStateTintG,
                OffStateTintB = _config.Matrix.Visual.OffStateTintB,
                // Note: transparent strips still show off-state dots, but with reduced alpha so they
                // don't read as a solid dark bar once active LEDs begin updating.
                OffStateAlpha = _config.Window.BackgroundVisible
                    ? _config.Matrix.Visual.OffStateAlpha
                    : Math.Min(_config.Matrix.Visual.OffStateAlpha, 0.08),
                LensFalloff = _config.Matrix.Visual.LensFalloff,
                SpecularHotspot = _config.Matrix.Visual.SpecularHotspot,
                RimHighlight = _config.Matrix.Visual.RimHighlight,
                BackgroundColorR = backgroundR,
                BackgroundColorG = backgroundG,
                BackgroundColorB = backgroundB,
                BackgroundVisible = _config.Window.BackgroundVisible,
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
            LayoutWindowSelected?.Invoke(this, EventArgs.Empty);
            DragMove();
        }
    }

    public void SetLayoutEditOverlay(string toyLabel, bool isEditModeEnabled, bool isSelected)
    {
        // Note: compact mode keeps labels readable even on very short toy windows (for example narrow flashers).
        var compactOverlay = MatrixViewportBorder.ActualHeight > 0 && MatrixViewportBorder.ActualHeight < 80;
        // Note: avoid duplicate artifacts by keeping the in-window overlay layer hidden and relying on detached overlay rendering.
        LayoutToyNameOverlay.Visibility = Visibility.Collapsed;
        LayoutSelectionBorder.BorderThickness = new Thickness(0);

        // Note: this companion overlay window guarantees labels/outlines stay above all render paths, including direct-present.
        UpdateDetachedLayoutOverlay(toyLabel, isEditModeEnabled, isSelected, compactOverlay);
    }

    private void UpdateDetachedLayoutOverlay(string toyLabel, bool isEditModeEnabled, bool isSelected, bool compactOverlay)
    {
        if (!isEditModeEnabled)
        {
            _layoutOverlayWindow?.Hide();
            return;
        }

        EnsureLayoutOverlayWindow();
        SyncLayoutOverlayWindowBounds();
        if (_layoutOverlayWindow is null || _layoutOverlaySelectionBorder is null || _layoutOverlayNameBorder is null || _layoutOverlayNameText is null)
        {
            return;
        }

        _layoutOverlayNameBorder.Margin = compactOverlay ? new Thickness(2) : new Thickness(6);
        _layoutOverlayNameBorder.Padding = compactOverlay ? new Thickness(4, 2, 4, 2) : new Thickness(6, 3, 6, 3);
        _layoutOverlayNameText.FontSize = compactOverlay ? 10 : 12;
        _layoutOverlayNameText.Text = string.IsNullOrWhiteSpace(toyLabel) ? "(unnamed toy)" : toyLabel;
        _layoutOverlaySelectionBorder.BorderThickness = isSelected ? new Thickness(4) : new Thickness(0);
        _layoutOverlaySelectionBorder.BorderBrush = isSelected ? WpfBrushes.Yellow : WpfBrushes.Transparent;

        if (!_layoutOverlayWindow.IsVisible)
        {
            _layoutOverlayWindow.Show();
        }
    }

    private void EnsureLayoutOverlayWindow()
    {
        if (_layoutOverlayWindow is not null)
        {
            return;
        }

        var overlayRoot = new Grid
        {
            Background = WpfBrushes.Transparent,
            IsHitTestVisible = false,
        };

        _layoutOverlaySelectionBorder = new Border
        {
            BorderBrush = WpfBrushes.Yellow,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(2),
            IsHitTestVisible = false,
        };
        overlayRoot.Children.Add(_layoutOverlaySelectionBorder);

        _layoutOverlayNameText = new TextBlock
        {
            Foreground = WpfBrushes.White,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Text = "Toy",
        };
        _layoutOverlayNameBorder = new Border
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = System.Windows.VerticalAlignment.Top,
            Margin = new Thickness(6),
            Padding = new Thickness(6, 3, 6, 3),
            Background = new SolidColorBrush(WpfColor.FromArgb(136, 0, 0, 0)),
            BorderBrush = new SolidColorBrush(WpfColor.FromArgb(153, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            MaxWidth = 420,
            Child = _layoutOverlayNameText,
            IsHitTestVisible = false,
        };
        overlayRoot.Children.Add(_layoutOverlayNameBorder);

        _layoutOverlayWindow = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            AllowsTransparency = true,
            Background = WpfBrushes.Transparent,
            Content = overlayRoot,
            Width = Math.Max(1, ActualWidth),
            Height = Math.Max(1, ActualHeight),
            IsHitTestVisible = false,
            Owner = this,
        };

        _layoutOverlayWindow.SourceInitialized += (_, _) =>
        {
            if (new WindowInteropHelper(_layoutOverlayWindow).Handle is var hwnd && hwnd != IntPtr.Zero)
            {
                var exStyle = GetWindowLong(hwnd, GwlExStyle);
                SetWindowLong(hwnd, GwlExStyle, exStyle | WsExTransparent | WsExToolWindow | WsExNoActivate);
            }
        };
    }

    private void SyncLayoutOverlayWindowBounds()
    {
        if (_layoutOverlayWindow is null)
        {
            return;
        }

        _layoutOverlayWindow.Left = Left;
        _layoutOverlayWindow.Top = Top;
        _layoutOverlayWindow.Width = Math.Max(1, ActualWidth);
        _layoutOverlayWindow.Height = Math.Max(1, ActualHeight);
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
        RefreshLockedAspectRatioFromWindow();
        _idleOffPresentation = null;
        ReplaceRenderer(CreateRenderer(_config));
        _forcedRenderBurstsRemaining = 6;
        AppLogger.Info($"[window] renderer-switch {previousRenderer}->{_matrixRenderer.BackendName} seq={_latestPresentation?.OutputSequence ?? 0}");
        ApplyDebugVisibility();
        ReinitializeRendererForViewport();

        // Note: one extra replay pass makes renderer backend switches (GPU<->CPU) deterministic mid-flight.
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

    private void OnLockAspectRatioClick(object sender, RoutedEventArgs e)
    {
        _config.Window.LockAspectRatio = LockAspectRatioMenuItem.IsChecked;
        if (_config.Window.LockAspectRatio)
        {
            // Note: when re-locking, capture the user's current free-resize shape as the new lock ratio.
            RefreshLockedAspectRatioFromWindow();
        }
    }

    private void RefreshLockedAspectRatioFromWindow()
    {
        var width = Math.Max(MinWidth, Width);
        var height = Math.Max(MinHeight, Height);
        _lockedAspectRatio = Math.Max(1.0 / 64.0, width / Math.Max(1.0, height));
    }

    private void OnExitMenuClick(object sender, RoutedEventArgs e)
    {
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

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
        if (config.Matrix.Renderer.Equals("cpu", StringComparison.OrdinalIgnoreCase) ||
            config.Matrix.Renderer.Equals("primitive", StringComparison.OrdinalIgnoreCase))
        {
            return new CpuMatrixRenderer();
        }

        return new GpuInstancedMatrixRenderer();
    }
}
