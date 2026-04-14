using System.Collections.Concurrent;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using VirtualDofMatrix.App.Configuration;
using VirtualDofMatrix.Core;
using VirtualDofMatrix.Core.Toys;

namespace VirtualDofMatrix.App.Presentation;

// Overview: routes toy frames to one WPF window per visual toy, reusing MainWindow for the first toy.
public sealed class WpfWindowOutputAdapter : IOutputAdapter
{
    private readonly Dispatcher _dispatcher;
    private readonly AppConfig _config;
    private readonly MainWindow _mainWindow;
    private readonly Action _persistConfig;
    private readonly Action _openSettings;
    private readonly Action _requestAppExit;
    private readonly Action<string> _notifyToyWindowSelected;
    private readonly Action<string> _requestToyEdit;
    private readonly string _defaultGpuPresentMode;
    private readonly double _defaultOffStateAlpha;
    private readonly ConcurrentDictionary<string, ToyWindowBinding> _bindings = new(StringComparer.OrdinalIgnoreCase);
    private bool _layoutEditModeEnabled;
    private string? _selectedToyId;
    private string? _mainHostToyId;

    public WpfWindowOutputAdapter(
        Dispatcher dispatcher,
        AppConfig config,
        MainWindow mainWindow,
        Action persistConfig,
        Action openSettings,
        Action requestAppExit,
        Action<string> notifyToyWindowSelected,
        Action<string> requestToyEdit)
    {
        _dispatcher = dispatcher;
        _config = config;
        _mainWindow = mainWindow;
        _persistConfig = persistConfig;
        _openSettings = openSettings;
        _requestAppExit = requestAppExit;
        _notifyToyWindowSelected = notifyToyWindowSelected;
        _requestToyEdit = requestToyEdit;
        _defaultGpuPresentMode = _config.Matrix.Visual.GpuPresentMode;
        _defaultOffStateAlpha = _config.Matrix.Visual.OffStateAlpha;
        _mainHostToyId = ResolveMainHostToyId(currentHostToyId: null, _config.Routing.Toys, Name);

        EnsureInitialViewerToyWindows();
    }

    public string Name => "viewer";

    public void Write(ToyFrame frame, RoutingFrameContext context)
    {
        _ = context;

        if (_dispatcher.CheckAccess())
        {
            WriteOnUiThread(frame);
            return;
        }

        _dispatcher.BeginInvoke(() => WriteOnUiThread(frame));
    }

    public void SyncVisibilityFromConfig()
    {
        if (_dispatcher.CheckAccess())
        {
            SyncVisibilityFromConfigOnUiThread();
            return;
        }

        _dispatcher.Invoke(SyncVisibilityFromConfigOnUiThread);
    }

    public void RebuildViewerBindings()
    {
        if (_dispatcher.CheckAccess())
        {
            RebuildViewerBindingsOnUiThread();
            return;
        }

        _dispatcher.Invoke(RebuildViewerBindingsOnUiThread);
    }

    public void FocusToyWindow(string toyId)
    {
        if (string.IsNullOrWhiteSpace(toyId))
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            FocusToyWindowOnUiThread(toyId);
            return;
        }

        _dispatcher.Invoke(() => FocusToyWindowOnUiThread(toyId));
    }

    public void SetLayoutEditMode(bool enabled)
    {
        if (_dispatcher.CheckAccess())
        {
            SetLayoutEditModeOnUiThread(enabled);
            return;
        }

        _dispatcher.Invoke(() => SetLayoutEditModeOnUiThread(enabled));
    }

    private void EnsureInitialViewerToyWindows()
    {
        if (_dispatcher.CheckAccess())
        {
            EnsureInitialViewerToyWindowsOnUiThread();
            return;
        }

        _dispatcher.Invoke(EnsureInitialViewerToyWindowsOnUiThread);
    }

    private void RebuildViewerBindingsOnUiThread()
    {
        EnsureMainHostToySelection();

        // Note: when toy geometry changes at runtime, secondary windows keep their old renderer
        // dimensions unless we recreate bindings. We intentionally keep MainWindow alive and rebuild others.
        foreach (var pair in _bindings.ToArray())
        {
            if (!ReferenceEquals(pair.Value.Window, _mainWindow))
            {
                pair.Value.Window.Close();
            }
        }

        _bindings.Clear();

        CreateInitialViewerBindings();
        SyncVisibilityFromConfigOnUiThread();
        RefreshLayoutOverlays();
    }

    private void EnsureInitialViewerToyWindowsOnUiThread()
    {
        if (!_mainWindow.IsLoaded)
        {
            // Note: owner windows can only be assigned after MainWindow is shown/loaded.
            _mainWindow.Loaded += OnMainWindowLoaded;
            return;
        }

        CreateInitialViewerBindings();
    }

    private void OnMainWindowLoaded(object? sender, RoutedEventArgs e)
    {
        _mainWindow.Loaded -= OnMainWindowLoaded;
        CreateInitialViewerBindings();
    }

    private void CreateInitialViewerBindings()
    {
        EnsureMainHostToySelection();

        // Note: pre-create viewer toy windows so users immediately see one viewport per enabled toy.
        foreach (var toy in _config.Routing.Toys.Where(t => t.Enabled))
        {
            var hasViewerTarget = toy.OutputTargets.Any(target => target.Enabled &&
                string.Equals(target.Adapter, Name, StringComparison.OrdinalIgnoreCase));
            if (!hasViewerTarget)
            {
                continue;
            }

            _bindings.GetOrAdd(toy.Id, CreateBindingForToy);
        }
    }

    private void WriteOnUiThread(ToyFrame frame)
    {
        var toyConfig = FindToyConfig(frame.ToyId);
        var toyEnabledForViewer = toyConfig is not null
            && toyConfig.Enabled
            && toyConfig.OutputTargets.Any(target => target.Enabled && string.Equals(target.Adapter, Name, StringComparison.OrdinalIgnoreCase));

        if (!toyEnabledForViewer)
        {
            SetBindingVisible(frame.ToyId, isVisible: false);
            return;
        }

        SetBindingVisible(frame.ToyId, isVisible: true);
        var binding = _bindings.GetOrAdd(frame.ToyId, CreateBindingForToy);
        binding.Render(frame);
    }

    private ToyWindowBinding CreateBindingForToy(string toyId)
    {
        var toyConfig = FindToyConfig(toyId);
        var isPrimaryToy = IsPrimaryVisualToy(toyId);

        if (isPrimaryToy)
        {
            // Note: keep the primary host window in parity with secondary toy windows, including
            // transparent/colored backgrounds, so matrix toys get the same background behavior as strips.
            ApplyPrimaryToyVisualOverrides(toyConfig);
            WireGeometryPersistence(_mainWindow, toyId);
            WireWindowSelectionCallbacks(_mainWindow, toyId);
            ApplyLayoutOverlay(toyId, _mainWindow);
            EnsureEditToyContextMenuItem(_mainWindow, toyId);
            return new ToyWindowBinding(_mainWindow, frame => _mainWindow.ApplyPresentation(ToPresentation(frame)));
        }

        // Note: secondary toys now reuse MainWindow rendering stack for consistent dot/bloom behavior.
        var toyWindowConfig = BuildToyWindowAppConfig(toyConfig, toyId);
        var toyWindow = new MainWindow(
            toyWindowConfig,
            new StartupConfigStatus
            {
                ActiveConfigPath = _config.Settings.DofConfigFolderPath,
                CabinetFileStatus = "Cabinet.xml status mirrors primary window",
                LastLoadedUtc = DateTimeOffset.UtcNow,
            })
        {
            DataContext = toyWindowConfig,
        };

        // Note: keep toy windows unowned so each window's layout popup can render reliably above its own swapchain surface.
        // Owned-window z-order behavior can suppress per-window overlay popups for secondary toys while Settings is open.

        toyWindow.Show();
        toyWindow.SettingsRequested += (_, _) => _openSettings();
        toyWindow.ExitRequested += (_, _) => _requestAppExit();
        WireWindowSelectionCallbacks(toyWindow, toyId);
        ApplyLayoutOverlay(toyId, toyWindow);
        EnsureEditToyContextMenuItem(toyWindow, toyId);
        WireGeometryPersistence(toyWindow, toyId);

        return new ToyWindowBinding(toyWindow, frame => toyWindow.ApplyPresentation(ToPresentation(frame)));
    }

    private void ApplyPrimaryToyVisualOverrides(ToyRouteConfig? toyConfig)
    {
        if (toyConfig is null)
        {
            return;
        }

        ResolveToyBackgroundRgb(toyConfig.Window, out var backgroundR, out var backgroundG, out var backgroundB);

        // Note: primary toy should use the same renderer/window background policy that secondary toy windows use.
        _config.Matrix.Visual.TransparentBackground = true;
        // Note: transparent mode should keep the GPU interop present path so alpha remains intact.
        _config.Matrix.Visual.GpuPresentMode = _defaultGpuPresentMode;
        _config.Matrix.Visual.OffStateAlpha = toyConfig.Window.BackgroundVisible
            ? _defaultOffStateAlpha
            : Math.Min(_defaultOffStateAlpha, 0.08);
        _config.Matrix.Visual.BackgroundColorR = backgroundR;
        _config.Matrix.Visual.BackgroundColorG = backgroundG;
        _config.Matrix.Visual.BackgroundColorB = backgroundB;
        _config.Matrix.Visual.BackgroundVisible = toyConfig.Window.BackgroundVisible;

        _config.Window.BackgroundVisible = toyConfig.Window.BackgroundVisible;
        _config.Window.BackgroundColor = toyConfig.Window.BackgroundColor;

        _mainWindow.ApplyRuntimeSettings();
    }

    private AppConfig BuildToyWindowAppConfig(ToyRouteConfig? toyConfig, string toyId)
    {
        var toy = toyConfig ?? new ToyRouteConfig { Id = toyId };
        ResolveToyBackgroundRgb(toy.Window, out var backgroundR, out var backgroundG, out var backgroundB);

        var clone = new AppConfig
        {
            Transport = _config.Transport,
            Debug = new DebugConfig
            {
                ShowDebug = false,
                LogProtocol = _config.Debug.LogProtocol,
                LogFrames = _config.Debug.LogFrames,
            },
            Settings = _config.Settings,
            Routing = _config.Routing,
            Matrix = new MatrixConfig
            {
                Renderer = _config.Matrix.Renderer,
                Width = Math.Max(1, toy.Mapping.Width),
                Height = Math.Max(1, toy.Mapping.Height),
                Mapping = toy.Mapping.Mode,
                DotShape = toy.Render.DotShape,
                MinDotSpacing = toy.Render.MinDotSpacing,
                FillGapEnabled = toy.Render.FillGapEnabled,
                Brightness = toy.Render.Brightness,
                Gamma = toy.Render.Gamma,
                ToneMapping = _config.Matrix.ToneMapping,
                TemporalSmoothing = _config.Matrix.TemporalSmoothing,
                Visual = new MatrixVisualConfig
                {
                    // Note: keep renderer output transparent so toy background colors show through
                    // directly behind each LED instead of behind an opaque black strip texture.
                    TransparentBackground = true,
                    // Note: transparent toy windows now stay on GPU interop present so alpha composition
                    // matches the strip pipeline fix (no forced fallback to opaque legacy readback).
                    GpuPresentMode = _config.Matrix.Visual.GpuPresentMode,
                    // Note: keep GPU dot path active for transparent toys/strips; only explicit global force flag should use CPU dots.
                    ForceCpuDotRasterFallback = _config.Matrix.Visual.ForceCpuDotRasterFallback,
                    EnableDirectPresentParitySampling = _config.Matrix.Visual.EnableDirectPresentParitySampling,
                    EnableDiagnosticReadbackCapture = _config.Matrix.Visual.EnableDiagnosticReadbackCapture,
                    FlatShading = _config.Matrix.Visual.FlatShading,
                    FullBrightnessRadiusMinPct = _config.Matrix.Visual.FullBrightnessRadiusMinPct,
                    OffStateTintR = _config.Matrix.Visual.OffStateTintR,
                    OffStateTintG = _config.Matrix.Visual.OffStateTintG,
                    OffStateTintB = _config.Matrix.Visual.OffStateTintB,
                    // Note: keep off-state dots in transparent toys, but tone down opacity to avoid dark strip fill.
                    OffStateAlpha = toy.Window.BackgroundVisible
                        ? _config.Matrix.Visual.OffStateAlpha
                        : Math.Min(_config.Matrix.Visual.OffStateAlpha, 0.08),
                    LensFalloff = _config.Matrix.Visual.LensFalloff,
                    SpecularHotspot = _config.Matrix.Visual.SpecularHotspot,
                    RimHighlight = _config.Matrix.Visual.RimHighlight,
                    BackgroundColorR = backgroundR,
                    BackgroundColorG = backgroundG,
                    BackgroundColorB = backgroundB,
                    BackgroundVisible = toy.Window.BackgroundVisible,
                },
                Bloom = new BloomConfig
                {
                    Enabled = toy.Bloom.Enabled,
                    Threshold = toy.Bloom.Threshold,
                    SoftKnee = toy.Bloom.SoftKnee,
                    DownsampleDivisor = _config.Matrix.Bloom.DownsampleDivisor,
                    NearRadiusPx = toy.Bloom.NearRadiusPx,
                    FarRadiusPx = toy.Bloom.FarRadiusPx,
                    NearStrength = toy.Bloom.NearStrength,
                    FarStrength = toy.Bloom.FarStrength,
                },
            },
            Window = new WindowConfig
            {
                AlwaysOnTop = toy.Window.AlwaysOnTop,
                Borderless = toy.Window.Borderless,
                LockAspectRatio = toy.Window.LockAspectRatio,
                BackgroundVisible = toy.Window.BackgroundVisible,
                BackgroundColor = toy.Window.BackgroundColor,
                Left = toy.Window.Left ?? _config.Window.Left,
                Top = toy.Window.Top ?? _config.Window.Top,
                Width = toy.Window.Width ?? _config.Window.Width,
                Height = toy.Window.Height ?? _config.Window.Height,
            },
        };

        return clone;
    }

    private static void ResolveToyBackgroundRgb(ToyWindowOptionsConfig window, out float r, out float g, out float b)
    {
        // Note: transparent toy windows use black as the renderer fallback color.
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
                System.Windows.Media.ColorConverter.ConvertFromString(window.BackgroundColor) is System.Windows.Media.Color parsed)
            {
                r = parsed.R / 255f;
                g = parsed.G / 255f;
                b = parsed.B / 255f;
                return;
            }
        }
        catch (FormatException)
        {
            // Note: invalid color strings intentionally fall back to black for deterministic behavior.
        }

        r = 0f;
        g = 0f;
        b = 0f;
    }

    private bool IsPrimaryVisualToy(string toyId)
    {
        var mainHostToyId = GetMainHostToyId();
        return !string.IsNullOrWhiteSpace(mainHostToyId)
            && string.Equals(mainHostToyId, toyId, StringComparison.OrdinalIgnoreCase);
    }

    private ToyRouteConfig? FindToyConfig(string toyId)
    {
        return _config.Routing.Toys.FirstOrDefault(t => string.Equals(t.Id, toyId, StringComparison.OrdinalIgnoreCase));
    }

    private static FramePresentation ToPresentation(ToyFrame frame)
    {
        var bytes = frame.PayloadBytes ?? ToBytes(frame.Pixels ?? Array.Empty<Rgb24>());
        return new FramePresentation(
            bytes,
            HighestLedWritten: bytes.Length / 3,
            LedsPerChannel: Math.Max(0, frame.Width * frame.Height),
            OutputSequence: frame.FrameId,
            PresentedAtUtc: frame.TimestampUtc);
    }

    private static byte[] ToBytes(IReadOnlyList<Rgb24> pixels)
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

    private void EnsureEditToyContextMenuItem(Window window, string toyId)
    {
        window.ContextMenu ??= new System.Windows.Controls.ContextMenu();

        if (window.ContextMenu.Items.OfType<System.Windows.Controls.MenuItem>()
            .Any(item => string.Equals(item.Tag as string, $"edit-toy:{toyId}", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var editToyItem = new System.Windows.Controls.MenuItem
        {
            Header = "Edit Toy...",
            Tag = $"edit-toy:{toyId}",
        };
        editToyItem.Click += (_, _) => _requestToyEdit(toyId);
        window.ContextMenu.Items.Insert(0, editToyItem);
    }

    private void ApplyToyWindowConfig(Window window, ToyWindowOptionsConfig? options)
    {
        if (options is null)
        {
            return;
        }

        window.Topmost = options.AlwaysOnTop;
        window.WindowStyle = options.Borderless ? WindowStyle.None : WindowStyle.SingleBorderWindow;
        if (options.Left.HasValue)
        {
            window.Left = options.Left.Value;
        }

        if (options.Top.HasValue)
        {
            window.Top = options.Top.Value;
        }

        if (options.Width.HasValue)
        {
            window.Width = options.Width.Value;
        }

        if (options.Height.HasValue)
        {
            window.Height = options.Height.Value;
        }
    }

    private void WireGeometryPersistence(Window window, string toyId)
    {
        void Sync()
        {
            var toy = FindToyConfig(toyId);
            if (toy is null)
            {
                return;
            }

            toy.Window ??= new ToyWindowOptionsConfig();
            toy.Window.Left = window.Left;
            toy.Window.Top = window.Top;
            toy.Window.Width = window.Width;
            toy.Window.Height = window.Height;
            toy.Window.AlwaysOnTop = window.Topmost;
            toy.Window.Borderless = window.WindowStyle == WindowStyle.None;
            if (window is MainWindow mainWindow)
            {
                toy.Window.LockAspectRatio = mainWindow.IsAspectRatioLocked;
            }

            _persistConfig();
        }

        window.LocationChanged += (_, _) => Sync();
        window.SizeChanged += (_, _) => Sync();
        window.Closed += (_, _) => _bindings.TryRemove(toyId, out _);
    }

    private void SyncVisibilityFromConfigOnUiThread()
    {
        EnsureMainHostToySelection();

        var enabledToyIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var toy in _config.Routing.Toys)
        {
            var enabledForViewer = toy.Enabled
                && toy.OutputTargets.Any(target => target.Enabled && string.Equals(target.Adapter, Name, StringComparison.OrdinalIgnoreCase));
            if (enabledForViewer)
            {
                enabledToyIds.Add(toy.Id);
            }

            SetBindingVisible(toy.Id, enabledForViewer);
        }

        // Note: bindings can outlive config edits; hide any stale viewer windows no longer enabled in routing.
        foreach (var bindingToyId in _bindings.Keys)
        {
            if (!enabledToyIds.Contains(bindingToyId))
            {
                SetBindingVisible(bindingToyId, isVisible: false);
            }
        }

        // Note: rebuilds can leave MainWindow unbound when the old primary toy is disabled.
        // Keep the host window hidden unless it is actively bound to a currently enabled viewer toy.
        var mainWindowBoundToEnabledToy = _bindings.Any(pair =>
            ReferenceEquals(pair.Value.Window, _mainWindow)
            && enabledToyIds.Contains(pair.Key));
        if (mainWindowBoundToEnabledToy)
        {
            if (!_mainWindow.IsVisible)
            {
                _mainWindow.Show();
            }
        }
        else if (_mainWindow.IsVisible)
        {
            _mainWindow.Hide();
        }
    }

    private void SetBindingVisible(string toyId, bool isVisible)
    {
        if (!_bindings.TryGetValue(toyId, out var binding))
        {
            if (!isVisible)
            {
                return;
            }

            // Note: toys enabled after startup may have no binding yet; create one so the window can appear immediately.
            binding = _bindings.GetOrAdd(toyId, CreateBindingForToy);

            if (!binding.Window.IsVisible)
            {
                binding.Window.Show();
            }

            return;
        }

        if (isVisible)
        {
            if (!binding.Window.IsVisible)
            {
                binding.Window.Show();
            }

            ApplyLayoutOverlay(toyId, binding.Window);

            return;
        }

        if (binding.Window.IsVisible)
        {
            binding.Window.Hide();
        }
    }

    private void FocusToyWindowOnUiThread(string toyId)
    {
        var toyConfig = FindToyConfig(toyId);
        var enabledForViewer = toyConfig is not null
            && toyConfig.Enabled
            && toyConfig.OutputTargets.Any(target => target.Enabled && string.Equals(target.Adapter, Name, StringComparison.OrdinalIgnoreCase));

        if (!enabledForViewer)
        {
            return;
        }

        var binding = _bindings.GetOrAdd(toyId, CreateBindingForToy);
        if (!binding.Window.IsVisible)
        {
            binding.Window.Show();
        }

        // Note: topmost pulse nudges focus/highlight without permanently changing user window preferences.
        var wasTopmost = binding.Window.Topmost;
        binding.Window.Topmost = true;
        binding.Window.Activate();
        binding.Window.Focus();
        binding.Window.Topmost = wasTopmost;
        _selectedToyId = toyId;
        _notifyToyWindowSelected(toyId);
        RefreshLayoutOverlays();
    }

    private void WireWindowSelectionCallbacks(MainWindow window, string toyId)
    {
        window.LayoutWindowSelected += (_, _) =>
        {
            _selectedToyId = toyId;
            _notifyToyWindowSelected(toyId);
            RefreshLayoutOverlays();
        };
    }

    private void SetLayoutEditModeOnUiThread(bool enabled)
    {
        _layoutEditModeEnabled = enabled;
        if (enabled)
        {
            // Note: start each settings/layout session with no pre-selected toy so outlines only appear after an explicit user action.
            _selectedToyId = null;
        }

        if (enabled)
        {
            foreach (var toy in _config.Routing.Toys.Where(t =>
                         t.Enabled && t.OutputTargets.Any(target => target.Enabled && string.Equals(target.Adapter, Name, StringComparison.OrdinalIgnoreCase))))
            {
                _bindings.GetOrAdd(toy.Id, CreateBindingForToy);
            }
        }

        RefreshLayoutOverlays();
    }

    private void RefreshLayoutOverlays()
    {
        // Note: the main window remains attached to one stable host toy identity.
        var primaryToyId = GetMainHostToyId();
        if (!string.IsNullOrWhiteSpace(primaryToyId))
        {
            ApplyLayoutOverlay(primaryToyId, _mainWindow);
        }

        foreach (var pair in _bindings)
        {
            ApplyLayoutOverlay(pair.Key, pair.Value.Window);
        }
    }

    private void ApplyLayoutOverlay(string toyId, Window window)
    {
        if (window is not MainWindow toyWindow)
        {
            return;
        }

        var toyLabel = FindToyConfig(toyId)?.Name;
        if (string.IsNullOrWhiteSpace(toyLabel))
        {
            toyLabel = toyId;
        }

        var selected = !string.IsNullOrWhiteSpace(_selectedToyId)
            && string.Equals(_selectedToyId, toyId, StringComparison.OrdinalIgnoreCase);
        toyWindow.SetLayoutEditOverlay(toyLabel, _layoutEditModeEnabled, selected);
    }

    private string? GetMainHostToyId()
    {
        EnsureMainHostToySelection();
        return _mainHostToyId;
    }

    private void EnsureMainHostToySelection()
    {
        // Note: keep the current host stable while it remains enabled; only fail over
        // when the host toy is disabled/unroutable so enabling another toy does not steal MainWindow.
        _mainHostToyId = ResolveMainHostToyId(_mainHostToyId, _config.Routing.Toys, Name);
    }

    internal static string? ResolveMainHostToyId(string? currentHostToyId, IReadOnlyList<ToyRouteConfig> toys, string adapterName)
    {
        if (toys is null || toys.Count == 0 || string.IsNullOrWhiteSpace(adapterName))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(currentHostToyId))
        {
            var currentHost = toys.FirstOrDefault(toy => string.Equals(toy.Id, currentHostToyId, StringComparison.OrdinalIgnoreCase));
            if (currentHost is not null && IsEnabledForAdapter(currentHost, adapterName))
            {
                return currentHost.Id;
            }
        }

        foreach (var toy in toys)
        {
            if (IsEnabledForAdapter(toy, adapterName))
            {
                return toy.Id;
            }
        }

        return null;
    }

    private static bool IsEnabledForAdapter(ToyRouteConfig toy, string adapterName)
    {
        return toy.Enabled
            && toy.OutputTargets.Any(target =>
                target.Enabled
                && string.Equals(target.Adapter, adapterName, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record ToyWindowBinding(Window Window, Action<ToyFrame> Render);
}
