using System.Windows;
using System.Windows.Threading;
using VirtualDofMatrix.App.Configuration;
using VirtualDofMatrix.App.Presentation;
using VirtualDofMatrix.App.Transport;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App;

public partial class App : Application
{
    private const string ConfigFilePath = "settings.json";

    private readonly AppConfigurationStore _configurationStore = new();
    private readonly CabinetXmlService _cabinetXmlService = new();

    private AppConfig? _config;
    private MainWindow? _window;
    private FrameTransportHost? _transportHost;
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

        _transportHost = new FrameTransportHost(_config);
        _presentationDispatcher = new FramePresentationDispatcher(Dispatcher);
        _presentationDispatcher.Attach(_transportHost);
        _presentationDispatcher.FramePresentedOnUiThread += (_, frame) => _window.ApplyPresentation(frame);

        await _transportHost.StartAsync();

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

        if (_transportHost is not null)
        {
            await _transportHost.StopAsync();
        }

        base.OnExit(e);
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
            var updatedCabinet = TryUpdateCabinetResolution(_config.Matrix.Width, _config.Matrix.Height);
            if (updatedCabinet)
            {
                MessageBox.Show(_window,
                    "Cabinet.xml was updated. Restart the current table (or reset DOF) so DOF reloads the new matrix settings.",
                    "DOF restart required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }

        _window.ApplyRuntimeSettings();
        PersistWindowSettings();
    }

    private bool TryUpdateCabinetResolution(int width, int height)
    {
        if (_config is null || _window is null)
        {
            return false;
        }

        var resolvedPath = _cabinetXmlService.ResolveCabinetXmlPath(_config.Settings.CabinetXmlPath);
        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            var browse = MessageBox.Show(_window,
                "Cabinet.xml could not be found. It is typically located in DirectOutput/Config.\n\nWould you like to browse for Cabinet.xml now?",
                "Cabinet.xml not found",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (browse == MessageBoxResult.Yes)
            {
                ShowSettingsDialog();
            }
            return false;
        }

        try
        {
            _cabinetXmlService.UpdateLedStripResolution(resolvedPath, _config.Settings.CabinetToyName, width, height);
            _config.Settings.CabinetXmlPath = resolvedPath;
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(_window,
                $"Failed to update Cabinet.xml: {ex.Message}",
                "Cabinet update failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }
    }

    private static void CopyConfig(AppConfig source, AppConfig destination)
    {
        destination.Transport = source.Transport;
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
