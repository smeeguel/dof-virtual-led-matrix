using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Threading;
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
    private readonly ConcurrentDictionary<string, ToyWindowBinding> _bindings = new(StringComparer.OrdinalIgnoreCase);

    public WpfWindowOutputAdapter(Dispatcher dispatcher, AppConfig config, MainWindow mainWindow, Action persistConfig)
    {
        _dispatcher = dispatcher;
        _config = config;
        _mainWindow = mainWindow;
        _persistConfig = persistConfig;
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
        var binding = _bindings.GetOrAdd(frame.ToyId, CreateBindingForToy);
        binding.Render(frame);
    }

    private ToyWindowBinding CreateBindingForToy(string toyId)
    {
        var toyConfig = FindToyConfig(toyId);
        var isPrimaryToy = IsPrimaryVisualToy(toyId);

        if (isPrimaryToy)
        {
            WireGeometryPersistence(_mainWindow, toyId);
            return new ToyWindowBinding(_mainWindow, frame => _mainWindow.ApplyPresentation(ToPresentation(frame)));
        }

        // Conversational note: secondary toys now reuse MainWindow rendering stack for consistent dot/bloom behavior.
        var toyWindowConfig = BuildToyWindowAppConfig(toyConfig, toyId);
        var toyWindow = new MainWindow(toyWindowConfig)
        {
            DataContext = toyWindowConfig,
        };

        if (_mainWindow.IsLoaded)
        {
            toyWindow.Owner = _mainWindow;
        }

        toyWindow.Show();
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
        var primary = _config.Routing.Toys.FirstOrDefault(t => t.Enabled);
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

            _persistConfig();
        }

        window.LocationChanged += (_, _) => Sync();
        window.SizeChanged += (_, _) => Sync();
        window.Closed += (_, _) => _bindings.TryRemove(toyId, out _);
    }

    private sealed record ToyWindowBinding(Window Window, Action<ToyFrame> Render);
}
