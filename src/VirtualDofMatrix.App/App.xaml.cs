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
    private IVirtualComPairBackend? _serviceVirtualComBackend;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _config = _configurationStore.Load(ConfigFilePath);
        _configurationStore.Save(ConfigFilePath, _config);

        var providerMode = (_config.Serial.VirtualProviderMode ?? "service").Trim().ToLowerInvariant();
        if (_config.VirtualCom.Enabled && providerMode != "disabled")
        {
            _serviceVirtualComBackend = providerMode switch
            {
                "processcommand" => new LegacyProcessVirtualComPairBackend(_config.VirtualCom),
                _ => new ServiceVirtualComPairBackend(_config.VirtualCom),
            };

            try
            {
                var health = await _serviceVirtualComBackend.GetHealthAsync();
                if (!health.IsHealthy)
                {
                    throw new InvalidOperationException($"Virtual COM provider health check failed: {health.Message}");
                }

                await _serviceVirtualComBackend.CreatePairAsync(_config.VirtualCom.TxPortName, _config.VirtualCom.RxPortName);
            }
            catch (Exception ex) when (providerMode == "service" && !_config.VirtualCom.DisableFallbackToProcessCommand)
            {
                Console.Error.WriteLine($"[WARN] Service provisioning failed, trying processCommand fallback: {ex.Message}");
                _serviceVirtualComBackend = new LegacyProcessVirtualComPairBackend(_config.VirtualCom);
                await _serviceVirtualComBackend.CreatePairAsync(_config.VirtualCom.TxPortName, _config.VirtualCom.RxPortName);
            }

            _config.Serial.PortName = _config.VirtualCom.RxPortName;
            _configurationStore.Save(ConfigFilePath, _config);

            if (_config.VirtualCom.VerboseProvisioningLogs)
            {
                Console.WriteLine($"[VirtualCom] Mode={providerMode}, Listener={_config.Serial.PortName}, Pair={_config.VirtualCom.TxPortName}<->{_config.VirtualCom.RxPortName}");
            }
        }

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

        if (_serviceVirtualComBackend is not null && _config?.VirtualCom.Enabled == true)
        {
            try
            {
                await _serviceVirtualComBackend.DeletePairAsync(_config.VirtualCom.TxPortName, _config.VirtualCom.RxPortName);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WARN] Non-fatal virtual COM cleanup failure: {ex.Message}");
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
