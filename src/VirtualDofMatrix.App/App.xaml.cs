using System.IO;
using System.Windows;
using System.Windows.Threading;
using VirtualDofMatrix.App.Configuration;
using VirtualDofMatrix.App.Logging;
using VirtualDofMatrix.App.Presentation;
using VirtualDofMatrix.App.Transport;
using VirtualDofMatrix.Core;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace VirtualDofMatrix.App;

// Overview: App wires together config loading, transport startup, UI lifecycle, and local control commands.
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
    private CancellationTokenSource? _controlCts;
    private Task? _controlTask;
    private bool _isAppReady;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Start each launch from a predictable baseline so logs/config-backed defaults are deterministic.
        AppLogger.ClearForNewLaunch();
        _config = _configurationStore.Load(ConfigFilePath);
        _configurationStore.Save(ConfigFilePath, _config);
        AppLogger.Configure(_config.Debug.LogProtocol);

        if (TryHandleControlClientMode(e.Args, _config))
        {
            Shutdown();
            return;
        }

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
        StartControlServer(_config);

        MainWindow = _window;
        _isAppReady = true;

        var visibleOnStartup = ResolveInitialVisibility(e.Args);
        SetMatrixVisibility(visibleOnStartup, "startup");
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        // Shutdown is ordered so background control/transport tasks stop before WPF tears down.
        if (_controlCts is not null)
        {
            _controlCts.Cancel();
        }

        if (_controlTask is not null)
        {
            try
            {
                await _controlTask;
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

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

        var appliedViaApplyButton = false;
        dialog.Applied += (_, cfg) =>
        {
            appliedViaApplyButton = true;
            ApplySettings(cfg);
        };
        var accepted = dialog.ShowDialog();
        if (accepted == true && dialog.Result is not null && !appliedViaApplyButton)
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
        AppLogger.SetEnabled(_config.Debug.LogProtocol);

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

    private bool ResolveInitialVisibility(string[] args)
    {
        if (HasArg(args, "--show-virtual-led"))
        {
            return true;
        }

        if (HasArg(args, "--hide-virtual-led"))
        {
            return false;
        }

        if (HasArg(args, "--table-launch"))
        {
            var defaultVisible = HasArg(args, "--default-show-virtual-led");
            return PopperLaunchOptions.ResolveTableLaunchVisibility(args, defaultVisible);
        }

        return true;
    }

    private void StartControlServer(AppConfig config)
    {
        var pipeName = string.IsNullOrWhiteSpace(config.Transport.ControlPipeName)
            ? "VirtualDofMatrix.Control"
            : config.Transport.ControlPipeName;

        _controlCts = new CancellationTokenSource();
        _controlTask = Task.Run(() => RunControlServerLoopAsync(pipeName, _controlCts.Token), _controlCts.Token);
    }

    private async Task RunControlServerLoopAsync(string pipeName, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.In,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
                var message = await reader.ReadToEndAsync();
                if (string.IsNullOrWhiteSpace(message))
                {
                    continue;
                }

                var command = JsonSerializer.Deserialize<ControlCommandMessage>(message);
                if (command is null)
                {
                    continue;
                }

                await Dispatcher.InvokeAsync(() => HandleControlCommand(command));
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
            catch
            {
                // Ignore malformed control messages.
            }
        }
    }

    private void HandleControlCommand(ControlCommandMessage command)
    {
        if (!_isAppReady)
        {
            return;
        }

        var commandText = command.Command ?? string.Empty;
        if (commandText.Equals("show", StringComparison.OrdinalIgnoreCase) ||
            commandText.Equals("frontend-return", StringComparison.OrdinalIgnoreCase))
        {
            SetMatrixVisibility(true, commandText);
            return;
        }

        if (commandText.Equals("hide", StringComparison.OrdinalIgnoreCase))
        {
            SetMatrixVisibility(false, commandText);
            return;
        }

        if (commandText.Equals("table-launch", StringComparison.OrdinalIgnoreCase))
        {
            var tokens = command.Args ?? [];
            var defaultVisible = HasArg(tokens, "--default-show-virtual-led");
            var show = PopperLaunchOptions.ResolveTableLaunchVisibility(tokens, defaultVisible);
            SetMatrixVisibility(show, "table-launch");
        }
    }

    private void SetMatrixVisibility(bool visible, string reason)
    {
        if (_window is null || _transportHost is null)
        {
            return;
        }

        _transportHost.SetActive(visible);

        if (visible)
        {
            _window.ShowInTaskbar = true;
            if (!_window.IsVisible)
            {
                _window.Show();
            }
        }
        else
        {
            _window.ShowInTaskbar = false;
            _window.Hide();
        }

        if (_config?.Debug.LogProtocol == true)
        {
            AppLogger.Info($"Control visibility='{(visible ? "visible" : "hidden")}' reason='{reason}'.");
        }
    }

    private static bool TryHandleControlClientMode(string[] args, AppConfig config)
    {
        var commandIndex = Array.FindIndex(args, x => x.Equals("--command", StringComparison.OrdinalIgnoreCase));
        if (commandIndex < 0 || commandIndex + 1 >= args.Length)
        {
            return false;
        }

        var command = args[commandIndex + 1];
        var commandArgs = args.Skip(commandIndex + 2).ToArray();
        var message = new ControlCommandMessage(command, commandArgs);
        var payload = JsonSerializer.Serialize(message);
        var pipeName = string.IsNullOrWhiteSpace(config.Transport.ControlPipeName)
            ? "VirtualDofMatrix.Control"
            : config.Transport.ControlPipeName;

        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            client.Connect(750);
            using var writer = new StreamWriter(client, Encoding.UTF8, bufferSize: 1024, leaveOpen: true);
            writer.Write(payload);
            writer.Flush();
        }
        catch
        {
            // App may not be running yet; command mode is best-effort.
        }

        return true;
    }

    private static bool HasArg(string[] args, string expected)
    {
        return args.Any(a => a.Equals(expected, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record ControlCommandMessage(string Command, string[] Args);
}
