using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<string, ToyWindowBinding> _bindings = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _enabledAtStartup = new(StringComparer.OrdinalIgnoreCase);
    private bool _layoutEditModeEnabled;
    private string? _selectedToyId;

    public WpfWindowOutputAdapter(
        Dispatcher dispatcher,
        AppConfig config,
        MainWindow mainWindow,
        Action persistConfig,
        Action openSettings,
        Action requestAppExit,
        Action<string> notifyToyWindowSelected)
    {
        _dispatcher = dispatcher;
        _config = config;
        _mainWindow = mainWindow;
        _persistConfig = persistConfig;
        _openSettings = openSettings;
        _requestAppExit = requestAppExit;
        _notifyToyWindowSelected = notifyToyWindowSelected;
        foreach (var toy in _config.Routing.Toys.Where(t => t.Enabled))
        {
            _enabledAtStartup.Add(toy.Id);
        }

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

    private void EnsureInitialViewerToyWindowsOnUiThread()
    {
        if (!_mainWindow.IsLoaded)
        {
            // Conversational note: owner windows can only be assigned after MainWindow is shown/loaded.
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
        // Conversational note: pre-create viewer toy windows so users immediately see one viewport per enabled toy.
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

        if (isPrimaryToy && _enabledAtStartup.Contains(toyId))
        {
            WireGeometryPersistence(_mainWindow, toyId);
            WireWindowSelectionCallbacks(_mainWindow, toyId);
            ApplyLayoutOverlay(toyId, _mainWindow);
            return new ToyWindowBinding(_mainWindow, frame => _mainWindow.ApplyPresentation(ToPresentation(frame)));
        }

        // Conversational note: secondary toys now reuse MainWindow rendering stack for consistent dot/bloom behavior.
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

        // Conversational note: keep toy windows unowned so each window's layout popup can render reliably above its own swapchain surface.
        // Owned-window z-order behavior can suppress per-window overlay popups for secondary toys while Settings is open.

        toyWindow.Show();
        toyWindow.SettingsRequested += (_, _) => _openSettings();
        toyWindow.ExitRequested += (_, _) => _requestAppExit();
        WireWindowSelectionCallbacks(toyWindow, toyId);
        ApplyLayoutOverlay(toyId, toyWindow);
        WireGeometryPersistence(toyWindow, toyId);

        return new ToyWindowBinding(toyWindow, frame => toyWindow.ApplyPresentation(ToPresentation(frame)));
    }

    private AppConfig BuildToyWindowAppConfig(ToyRouteConfig? toyConfig, string toyId)
    {
        var toy = toyConfig ?? new ToyRouteConfig { Id = toyId };

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
                Visual = _config.Matrix.Visual,
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
                Left = toy.Window.Left ?? _config.Window.Left,
                Top = toy.Window.Top ?? _config.Window.Top,
                Width = toy.Window.Width ?? _config.Window.Width,
                Height = toy.Window.Height ?? _config.Window.Height,
            },
        };

        return clone;
    }

    private bool IsPrimaryVisualToy(string toyId)
    {
        // Conversational note: keep one stable primary toy identity so windows don't swap roles when enabled flags change.
        var primary = _config.Routing.Toys.FirstOrDefault();
        return primary is not null && string.Equals(primary.Id, toyId, StringComparison.OrdinalIgnoreCase);
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

        // Conversational note: bindings can outlive config edits; hide any stale viewer windows no longer enabled in routing.
        foreach (var bindingToyId in _bindings.Keys)
        {
            if (!enabledToyIds.Contains(bindingToyId))
            {
                SetBindingVisible(bindingToyId, isVisible: false);
            }
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

            // Conversational note: toys enabled after startup may have no binding yet; create one so the window can appear immediately.
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

        // Conversational note: topmost pulse nudges focus/highlight without permanently changing user window preferences.
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
            // Conversational note: start each settings/layout session with no pre-selected toy so outlines only appear after an explicit user action.
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
        // Conversational note: the main window should be labeled as the first visual/viewer toy, not simply the first routing entry.
        var primaryToyId = _config.Routing.Toys
            .FirstOrDefault(t => t.Enabled && t.OutputTargets.Any(target =>
                target.Enabled && string.Equals(target.Adapter, Name, StringComparison.OrdinalIgnoreCase)))
            ?.Id;
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

        var selected = !string.IsNullOrWhiteSpace(_selectedToyId)
            && string.Equals(_selectedToyId, toyId, StringComparison.OrdinalIgnoreCase);
        toyWindow.SetLayoutEditOverlay(toyId, _layoutEditModeEnabled, selected);
    }

    private sealed record ToyWindowBinding(Window Window, Action<ToyFrame> Render);
}
