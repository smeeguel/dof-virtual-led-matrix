using System.Windows;
using VirtualDofMatrix.App.Configuration;
using VirtualDofMatrix.App.Presentation;
using VirtualDofMatrix.App.Serial;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App;

public partial class App : Application
{
    private const string ConfigFilePath = "settings.json";

    private readonly AppConfigurationStore _configurationStore = new();

    private AppConfig? _config;
    private MainWindow? _window;
    private SerialEmulatorHost? _serialHost;
    private FramePresentationDispatcher? _presentationDispatcher;
    private IVirtualComPairManager? _virtualComPairManager;
    private ComPairReservation? _activeComPairReservation;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _config = _configurationStore.Load(ConfigFilePath);

        if (_config.Serial.AutoProvisionVirtualPair)
        {
            _virtualComPairManager = new VirtualComPairManager(
                _config,
                new ProcessCommandVirtualComPairBackend(_config.Serial),
                Console.WriteLine);

            try
            {
                _activeComPairReservation = await _virtualComPairManager.EnsureActivePairAsync(CancellationToken.None);
                _configurationStore.Save(ConfigFilePath, _config);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VirtualCom] Failed to provision virtual COM pair: {ex.Message}");
            }
        }

        _configurationStore.Save(ConfigFilePath, _config);

        _window = new MainWindow(_config)
        {
            DataContext = _config,
        };

        _window.LocationChanged += (_, _) => PersistWindowSettings();
        _window.SizeChanged += (_, _) => PersistWindowSettings();
        _window.Closing += (_, _) => PersistWindowSettings();

        _serialHost = new SerialEmulatorHost(_config);
        _presentationDispatcher = new FramePresentationDispatcher(Dispatcher);
        _presentationDispatcher.Attach(_serialHost);
        _presentationDispatcher.FramePresentedOnUiThread += (_, frame) => _window.ApplyPresentation(frame);

        await _serialHost.StartAsync();

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

        if (_serialHost is not null)
        {
            await _serialHost.StopAsync();
        }

        if (_virtualComPairManager is not null && _activeComPairReservation is not null)
        {
            try
            {
                await _virtualComPairManager.DeactivateAsync(_activeComPairReservation, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VirtualCom] Failed to deactivate virtual COM pair: {ex.Message}");
            }
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
}
