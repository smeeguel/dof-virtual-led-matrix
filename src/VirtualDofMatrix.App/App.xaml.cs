using System;
using System.Windows;
using VirtualDofMatrix.App.Configuration;
using VirtualDofMatrix.App.Presentation;
using VirtualDofMatrix.App.Serial;
using VirtualDofMatrix.App.Transport;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App;

public partial class App : Application
{
    private const string ConfigFilePath = "settings.json";

    private readonly AppConfigurationStore _configurationStore = new();

    private AppConfig? _config;
    private MainWindow? _window;
    private IFrameSourceHost? _frameSourceHost;
    private FramePresentationDispatcher? _presentationDispatcher;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _config = _configurationStore.Load(ConfigFilePath);
        _configurationStore.Save(ConfigFilePath, _config);

        _window = new MainWindow(_config)
        {
            DataContext = _config,
        };

        _window.LocationChanged += (_, _) => PersistWindowSettings();
        _window.SizeChanged += (_, _) => PersistWindowSettings();
        _window.Closing += (_, _) => PersistWindowSettings();

        _frameSourceHost = CreateFrameSourceHost(_config);
        _presentationDispatcher = new FramePresentationDispatcher(Dispatcher);
        _presentationDispatcher.Attach(_frameSourceHost);
        _presentationDispatcher.FramePresentedOnUiThread += (_, frame) => _window.ApplyPresentation(frame);

        await _frameSourceHost.StartAsync();

        MainWindow = _window;
        _window.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        PersistWindowSettings();

        if (_presentationDispatcher is not null)
        {
            _presentationDispatcher.Dispose();
            _presentationDispatcher = null;
        }

        if (_frameSourceHost is not null)
        {
            await _frameSourceHost.StopAsync();
        }

        base.OnExit(e);
    }

    private void PersistWindowSettings()
    {
        if (_window is null || _config is null)
        {
            return;
        }

        _window.SyncWindowSettingsToConfig();
        _configurationStore.Save(ConfigFilePath, _config);
    }

    private static IFrameSourceHost CreateFrameSourceHost(AppConfig config)
    {
        if (string.Equals(config.Transport.Mode, "pinone", StringComparison.OrdinalIgnoreCase))
        {
            return new PinOnePipeHost(config);
        }

        return new SerialEmulatorHost(config);
    }
}
