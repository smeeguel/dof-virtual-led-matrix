using System.Windows;
using System.Windows.Threading;
using VirtualDofMatrix.App.Configuration;
using VirtualDofMatrix.App.Presentation;
using VirtualDofMatrix.App.Serial;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App;

public partial class App : Application
{
    private const string ConfigFilePath = "settings.json";

    private readonly AppConfigurationStore _configurationStore = new();
    private readonly CabinetXmlService _cabinetXmlService = new();

    private AppConfig? _config;
    private MainWindow? _window;
    private SerialEmulatorHost? _serialHost;
    private FramePresentationDispatcher? _presentationDispatcher;
    private DispatcherTimer? _windowSettingsSaveTimer;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _config = _configurationStore.Load(ConfigFilePath);
        _configurationStore.Save(ConfigFilePath, _config);

        _window = new MainWindow(_config)
        {
            DataContext = _config,
        };

        _window.SettingsRequested += (_, _) => ShowSettingsDialog();
        _window.ReloadRequested += async (_, _) => await ReloadRuntimeAsync();

        _windowSettingsSaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _windowSettingsSaveTimer.Tick += (_, _) =>
        {
            _windowSettingsSaveTimer.Stop();
            PersistWindowSettings();
        };

        _window.LocationChanged += (_, _) => ScheduleWindowSettingsPersist();
        _window.SizeChanged += (_, _) => ScheduleWindowSettingsPersist();
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

        base.OnExit(e);
    }

    private async Task ReloadRuntimeAsync()
    {
        if (_serialHost is null)
        {
            return;
        }

        await _serialHost.StopAsync();
        await _serialHost.StartAsync();
    }

    private void ShowSettingsDialog()
    {
        if (_window is null || _config is null)
        {
            return;
        }

        var dialog = new SettingsWindow(_config, _cabinetXmlService)
        {
            Owner = _window,
        };

        dialog.Applied += (_, cfg) => ApplySettings(cfg);
        var accepted = dialog.ShowDialog();
        if (accepted == true && dialog.Result is not null)
        {
            ApplySettings(dialog.Result);
        }
    }

    private void ApplySettings(AppConfig updated)
    {
        if (_config is null || _window is null)
        {
            return;
        }

        var originalWidth = _config.Matrix.Width;
        var originalHeight = _config.Matrix.Height;

        CopyConfig(updated, _config);

        if (_config.Settings.AutoUpdateCabinetOnResolutionChange
            && (originalWidth != _config.Matrix.Width || originalHeight != _config.Matrix.Height))
        {
            TryUpdateCabinetResolution(_config.Matrix.Width, _config.Matrix.Height);
        }

        _window.ApplyRuntimeSettings();
        PersistWindowSettings();
    }

    private void TryUpdateCabinetResolution(int width, int height)
    {
        if (_config is null || _window is null)
        {
            return;
        }

        var resolvedPath = _cabinetXmlService.ResolveCabinetXmlPath(_config.Settings.CabinetXmlPath);
        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            MessageBox.Show(_window,
                "Cabinet.xml path is not set. Open Settings > DOF / Cabinet and choose the file.",
                "Cabinet.xml not found",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            _cabinetXmlService.UpdateLedStripResolution(resolvedPath, _config.Settings.CabinetToyName, width, height);
            _config.Settings.CabinetXmlPath = resolvedPath;
        }
        catch (Exception ex)
        {
            MessageBox.Show(_window,
                $"Failed to update Cabinet.xml: {ex.Message}",
                "Cabinet update failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private static void CopyConfig(AppConfig source, AppConfig destination)
    {
        destination.Transport = source.Transport;
        destination.Serial = source.Serial;
        destination.Matrix = source.Matrix;
        destination.Window = source.Window;
        destination.Debug = source.Debug;
        destination.Settings = source.Settings;
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

    private void ScheduleWindowSettingsPersist()
    {
        if (_windowSettingsSaveTimer is null)
        {
            return;
        }

        _windowSettingsSaveTimer.Stop();
        _windowSettingsSaveTimer.Start();
    }
}
