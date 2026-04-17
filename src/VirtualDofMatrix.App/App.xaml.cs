using System.IO;
using System.Windows;
using System.Windows.Threading;
using VirtualDofMatrix.App.Configuration;
using VirtualDofMatrix.App.Logging;
using VirtualDofMatrix.App.Presentation;
using VirtualDofMatrix.App.Transport;
using VirtualDofMatrix.Core;
using VirtualDofMatrix.Core.Toys;
using System.IO.Pipes;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using WpfMessageBox = System.Windows.MessageBox;

namespace VirtualDofMatrix.App;

// Overview: App wires together config loading, transport startup, UI lifecycle, and local control commands.
public partial class App : System.Windows.Application
{
    private const string ConfigFileName = "settings.json";

    private readonly AppConfigurationStore _configurationStore = new();
    private readonly CabinetXmlService _cabinetXmlService = new();
    private readonly ConfigFolderBootstrapService _configFolderBootstrapService = new();

    private AppConfig? _config;
    private StartupConfigStatus? _startupConfigStatus;
    private MainWindow? _window;
    private SettingsWindow? _settingsWindow;
    private FrameTransportHost? _transportHost;
    private NamedPipeBroadcastAdapter? _broadcastAdapter;
    private WpfWindowOutputAdapter? _windowOutputAdapter;
    private DispatcherTimer? _windowSettingsSaveTimer;
    private CancellationTokenSource? _controlCts;
    private Task? _controlTask;
    private bool _isAppReady;
    private string? _activeTableOrRomName;
    private string? _runtimeTableOrRomName;
    private readonly string _configFilePath;

    public App()
    {
        // Note: pin config lookup to the executable directory so Popper/PinUP working dir changes cannot redirect toys.ini.
        _configFilePath = ResolveConfigFilePath();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Start each launch from a predictable baseline so logs/config-backed defaults are deterministic.
        AppLogger.ClearForNewLaunch();
        _config = _configurationStore.Load(_configFilePath);
        AppLogger.Configure(_config.Debug.LogProtocol);
        _startupConfigStatus = _configFolderBootstrapService.ResolveAndPersist(_config);
        _activeTableOrRomName = ResolveActiveTableOrRomName(e.Args);
        RefreshActiveScopeRoutingAndVisibility();
        _configurationStore.Save(_configFilePath, _config);

        if (TryHandleControlClientMode(e.Args, _config))
        {
            Shutdown();
            return;
        }

        _window = new MainWindow(_config, _startupConfigStatus)
        {
            DataContext = _config,
        };

        _window.SettingsRequested += (_, _) => ShowSettingsDialog();
        _window.ExitRequested += (_, _) => Shutdown();

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

        var routingPlanProvider = new ConfigRoutingPlanProvider(_config);
        var toyRouter = new ToyRouter(_config.Routing.Policy);
        _broadcastAdapter = new NamedPipeBroadcastAdapter(_config);
        _windowOutputAdapter = new WpfWindowOutputAdapter(
            Dispatcher,
            _config,
            _window,
            PersistWindowSettings,
            ShowSettingsDialog,
            Shutdown,
            toyId => _settingsWindow?.SelectToy(toyId),
            OpenToyEditorFromWindow,
            () => _config.Routing.ActiveTableOverrideKey);
        var outputAdapters = new List<IOutputAdapter>
        {
            _windowOutputAdapter,
            _broadcastAdapter,
        };

        _transportHost = new FrameTransportHost(_config, toyRouter, routingPlanProvider, outputAdapters);
        _transportHost.TableContextMetadataReceived += OnTableContextMetadataReceived;

        await _transportHost.StartAsync();
        StartControlServer(_config);

        MainWindow = _window;
        _isAppReady = true;

        var visibleOnStartup = ResolveInitialVisibility(e.Args);
        SetMatrixVisibility(visibleOnStartup, "startup");
        if (visibleOnStartup)
        {
            // Note: startup visibility control shows MainWindow for global "show" state, but per-toy
            // enabled flags still decide which toy windows should actually remain visible.
            _windowOutputAdapter?.SyncVisibilityFromConfig();
        }
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

        if (_transportHost is not null)
        {
            await _transportHost.StopAsync();
        }

        _broadcastAdapter?.Dispose();
        _broadcastAdapter = null;

        base.OnExit(e);
    }

    private void ShowSettingsDialog()
    {
        if (_window is null || _config is null)
        {
            return;
        }

        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        var tableScope = !string.IsNullOrWhiteSpace(_runtimeTableOrRomName) ? _runtimeTableOrRomName : _activeTableOrRomName;
        var dialog = new SettingsWindow(_config, _cabinetXmlService, tableScope, ApplySettings, ApplyScopedToyVisibilityOverrides)
        {
            Owner = _window,
        };

        dialog.SettingsApplied += (_, appliedConfig) => ApplySettings(appliedConfig);
        dialog.ToyHoverChanged += (_, toyId) =>
        {
            if (string.IsNullOrWhiteSpace(toyId))
            {
                _windowOutputAdapter?.ClearToyPreview();
                return;
            }

            _windowOutputAdapter?.PreviewToyWindow(toyId);
        };
        dialog.ToySelected += (_, toyId) =>
        {
            if (string.IsNullOrWhiteSpace(toyId))
            {
                _windowOutputAdapter?.ClearToySelection();
                return;
            }

            _windowOutputAdapter?.FocusToyWindow(toyId);
        };
        dialog.Closed += (_, _) =>
        {
            _windowOutputAdapter?.SetLayoutEditMode(false);
            _windowOutputAdapter?.ClearToyPreview();
            _settingsWindow = null;
        };
        _settingsWindow = dialog;
        _windowOutputAdapter?.SetLayoutEditMode(true);
        dialog.Show();
    }

    private void OpenToyEditorFromWindow(string toyId)
    {
        if (string.IsNullOrWhiteSpace(toyId))
        {
            return;
        }

        ShowSettingsDialog();
        _settingsWindow?.BeginEditToy(toyId);
    }

    private void ApplySettings(AppConfig updated)
    {
        if (_config is null || _window is null)
        {
            return;
        }

        var previousRenderer = _config.Matrix.Renderer;
        var originalWidth = _config.Matrix.Width;
        var originalHeight = _config.Matrix.Height;
        var originalRoutingFingerprint = BuildRoutingFingerprint(_config.Routing.Toys);
        var originalToyKeys = _config.Routing.Toys
            .Select(GetToyDeletionKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        CopyConfig(updated, _config);
        AppLogger.SetEnabled(_config.Debug.LogProtocol);
        var currentToyKeys = _config.Routing.Toys
            .Select(GetToyDeletionKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deletedToyKeys = originalToyKeys
            .Where(key => !currentToyKeys.Contains(key))
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var resolutionChanged = originalWidth != _config.Matrix.Width || originalHeight != _config.Matrix.Height;
        var routingChanged = !string.Equals(
            originalRoutingFingerprint,
            BuildRoutingFingerprint(_config.Routing.Toys),
            StringComparison.Ordinal);
        if (_config.Settings.AutoUpdateCabinetOnResolutionChange && (resolutionChanged || routingChanged))
        {
            var updatedCabinet = TrySyncCabinetManagedVirtualToys(deletedToyKeys);
            if (updatedCabinet)
            {
                WpfMessageBox.Show(_window,
                    "Cabinet.xml was updated. Restart the current table (or reset DOF) so DOF reloads the new matrix settings.",
                    "DOF restart required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }

        // DOF users usually expect renderer switches to "just work"; when dropping from GPU -> CPU we relaunch
        // immediately so the new renderer path is active without requiring manual intervention.
        if (previousRenderer.Equals("gpu", StringComparison.OrdinalIgnoreCase) &&
            _config.Matrix.Renderer.Equals("cpu", StringComparison.OrdinalIgnoreCase))
        {
            PersistWindowSettings();
            RestartForRendererSwitch();
            return;
        }

        _window.ApplyRuntimeSettings();
        _windowOutputAdapter?.RebuildViewerBindings();
        _windowOutputAdapter?.SyncVisibilityFromConfig();
        PersistWindowSettings();
    }

    private void ApplyScopedToyVisibilityOverrides(string scopeKey, IReadOnlyDictionary<string, bool> toyEnabledOverrides)
    {
        if (_config is null || string.IsNullOrWhiteSpace(scopeKey))
        {
            return;
        }

        _config.Routing.TableToyVisibilityOverrides ??= [];
        var existing = _config.Routing.TableToyVisibilityOverrides
            .FirstOrDefault(entry => entry.TableKey.Equals(scopeKey, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            existing = new TableToyVisibilityOverrideConfig { TableKey = scopeKey };
            _config.Routing.TableToyVisibilityOverrides.Add(existing);
        }

        // Note: scoped visibility writes are isolated from global Routing.Toys[].Enabled so global defaults remain untouched.
        // The override model is field-wise nullable, so only Enabled is updated here while future fields are preserved.
        existing.ToyOverrides ??= new Dictionary<string, TableToyOverrideConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in toyEnabledOverrides)
        {
            var globalEnabled = _config.Routing.Toys
                .FirstOrDefault(toy => toy.Id.Equals(pair.Key, StringComparison.OrdinalIgnoreCase))
                ?.Enabled ?? true;

            // Note: if scoped enabled matches global enabled, remove the scoped entry instead of persisting a redundant override.
            if (pair.Value == globalEnabled)
            {
                if (existing.ToyOverrides.TryGetValue(pair.Key, out var existingOverride)
                    && (existingOverride.Window?.Left.HasValue == true
                        || existingOverride.Window?.Top.HasValue == true
                        || existingOverride.Window?.Width.HasValue == true
                        || existingOverride.Window?.Height.HasValue == true))
                {
                    // Note: preserve reserved geometry fields if present; only clear Enabled so runtime still falls back to global.
                    existingOverride.Enabled = null;
                }
                else
                {
                    existing.ToyOverrides.Remove(pair.Key);
                }

                continue;
            }

            if (!existing.ToyOverrides.TryGetValue(pair.Key, out var toyOverride))
            {
                toyOverride = new TableToyOverrideConfig();
                existing.ToyOverrides[pair.Key] = toyOverride;
            }
            toyOverride.Enabled = pair.Value;
        }

        // Note: remove empty table scopes so the sidecar only contains meaningful per-table deltas.
        if (existing.ToyOverrides.Count == 0)
        {
            _config.Routing.TableToyVisibilityOverrides.Remove(existing);
        }

        _configurationStore.Save(_configFilePath, _config);
        RefreshActiveScopeRoutingAndVisibility();
    }

    private void RestartForRendererSwitch()
    {
        if (_window is null)
        {
            return;
        }

        try
        {
            // Re-launch this same executable with its current command-line so startup behavior remains identical
            // (for example --table-launch / visibility args sent by frontends).
            var currentExePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(currentExePath))
            {
                throw new InvalidOperationException("Could not resolve current process path for restart.");
            }

            var args = Environment.GetCommandLineArgs().Skip(1).Select(QuoteArgument);
            var startInfo = new ProcessStartInfo
            {
                FileName = currentExePath,
                Arguments = string.Join(" ", args),
                UseShellExecute = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };

            Process.Start(startInfo);
            AppLogger.Info("[app] restarting process to apply GPU->CPU renderer switch.");
            _window.Close();
            Shutdown();
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(_window,
                $"CPU renderer selection was saved, but automatic restart failed: {ex.Message}\nPlease restart Virtual DOF Matrix manually.",
                "Restart required for GPU -> CPU switch",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private static string QuoteArgument(string arg)
    {
        if (string.IsNullOrEmpty(arg))
        {
            return "\"\"";
        }

        return arg.Contains(' ') || arg.Contains('"')
            ? $"\"{arg.Replace("\"", "\\\"")}\""
            : arg;
    }

    private bool TrySyncCabinetManagedVirtualToys(IReadOnlyCollection<string>? deletedToyKeys = null)
    {
        if (_config is null || _window is null)
        {
            return false;
        }

        var resolvedPath = _cabinetXmlService.ResolveCabinetXmlPath(_config.Settings.CabinetXmlPath);
        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            var browse = WpfMessageBox.Show(_window,
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
            // Note: explicit deletes from the toy list should flow through to Cabinet.xml automatically.
            // Non-delete edits keep non-destructive behavior by leaving this false.
            var requestManagedRemovals = deletedToyKeys is not null && deletedToyKeys.Count > 0;

            var hasEnabledRoutingToys = _config.Routing.Toys.Any(toy => toy.Enabled);
            CabinetXmlMergePlan mergePlan;
            if (hasEnabledRoutingToys)
            {
                mergePlan = _cabinetXmlService.BuildVirtualToyMergePlanFromRouting(
                    resolvedPath,
                    _config.Routing.Toys,
                    removeMissingManagedToys: requestManagedRemovals);
            }
            else
            {
                var controllerName = _cabinetXmlService.GetLedStripOutputControllerName(resolvedPath, _config.Settings.CabinetToyName);
                if (string.IsNullOrWhiteSpace(controllerName))
                {
                    throw new InvalidOperationException(
                        $"Could not resolve OutputControllerName for LedStrip toy '{_config.Settings.CabinetToyName}'.");
                }

                mergePlan = _cabinetXmlService.BuildVirtualToyMergePlan(
                    resolvedPath,
                    [new VirtualLedToyDefinition(_config.Settings.CabinetToyName, _config.Matrix.Width, _config.Matrix.Height, controllerName)],
                    removeMissingManagedToys: requestManagedRemovals);
            }

            if (!mergePlan.PlannedChanges.Any())
            {
                return false;
            }

            // Note: we always show a dry-run summary first so Cabinet.xml writes are explicit
            // and users can cancel if the planned managed virtual-toy edits look wrong.
            var dryRunResult = _cabinetXmlService.ApplyVirtualToyMerge(resolvedPath, mergePlan, dryRun: true);
            var confirmationMessage = $"{dryRunResult.Summary}\n\nApply these Cabinet.xml changes now?";
            var confirm = WpfMessageBox.Show(
                _window,
                confirmationMessage,
                "Review Cabinet.xml changes",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes)
            {
                AppLogger.Info("[cabinet] user cancelled Cabinet.xml write after dry-run preview.");
                return false;
            }

            _cabinetXmlService.ApplyVirtualToyMerge(resolvedPath, mergePlan, dryRun: false);
            _config.Settings.CabinetXmlPath = resolvedPath;
            return true;
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(_window,
                $"Failed to update Cabinet.xml: {ex.Message}",
                "Cabinet update failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }
    }

    private static string GetToyDeletionKey(ToyRouteConfig toy)
    {
        if (!string.IsNullOrWhiteSpace(toy.Id))
        {
            return toy.Id.Trim();
        }

        if (!string.IsNullOrWhiteSpace(toy.Name))
        {
            return toy.Name.Trim();
        }

        return string.Empty;
    }

    private static string BuildRoutingFingerprint(IEnumerable<ToyRouteConfig> toys)
    {
        // Note: this keeps Cabinet.xml prompts focused on real toy layout changes instead of
        // unrelated settings edits elsewhere in the app.
        var items = toys
            .OrderBy(toy => toy.Id, StringComparer.OrdinalIgnoreCase)
            .Select(toy => $"{toy.Id}|{toy.Name}|{toy.Enabled}|{toy.Mapping.Width}|{toy.Mapping.Height}");
        return string.Join("||", items);
    }

    private static void CopyConfig(AppConfig source, AppConfig destination)
    {
        destination.Transport = source.Transport;
        destination.Matrix = source.Matrix;
        destination.Window = source.Window;
        destination.Debug = source.Debug;
        destination.Settings = source.Settings;
        destination.Routing = source.Routing;
    }

    private void PersistWindowSettings()
    {
        if (_window is null || _config is null)
        {
            return;
        }

        _window.SyncWindowSettingsToConfig();
        _configurationStore.Save(_configFilePath, _config);
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
            _runtimeTableOrRomName = null;
            _activeTableOrRomName = null;
            RefreshActiveScopeRoutingAndVisibility();
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
            _activeTableOrRomName = ResolveActiveTableOrRomName(tokens);
            _runtimeTableOrRomName = null;
            RefreshActiveScopeRoutingAndVisibility();
            var defaultVisible = HasArg(tokens, "--default-show-virtual-led");
            var show = PopperLaunchOptions.ResolveTableLaunchVisibility(tokens, defaultVisible);
            SetMatrixVisibility(show, "table-launch");
        }
    }

    private void OnTableContextMetadataReceived(TableContextMetadata metadata)
    {
        var contextValue = !string.IsNullOrWhiteSpace(metadata.TableName) ? metadata.TableName : metadata.RomName;
        _runtimeTableOrRomName = string.IsNullOrWhiteSpace(contextValue) ? null : contextValue;
        RefreshActiveScopeRoutingAndVisibility();
    }

    private void RefreshActiveScopeRoutingAndVisibility()
    {
        if (_config?.Routing is null)
        {
            return;
        }

        // Note: table metadata has precedence at runtime; launch args provide fallback until metadata arrives.
        var activeScopeKey = !string.IsNullOrWhiteSpace(_runtimeTableOrRomName)
            ? _runtimeTableOrRomName
            : _activeTableOrRomName;
        _config.Routing.ActiveTableOverrideKey = activeScopeKey;
        var enabledCount = TableToyVisibilityResolver.CountEnabledToysForActiveScope(_config.Routing);
        AppLogger.Info($"[scope] activeTableKey='{(string.IsNullOrWhiteSpace(activeScopeKey) ? "(none)" : activeScopeKey)}' enabledToyCount={enabledCount}");

        // Note: routing/visibility must be refreshed together so disabled toys stop receiving frames and windows hide promptly.
        _windowOutputAdapter?.RebuildViewerBindings();
        _windowOutputAdapter?.SyncVisibilityFromConfig();
    }

    private static string? ResolveActiveTableOrRomName(IEnumerable<string> args)
    {
        var tableName = TryGetNamedArg(args, "--table")
            ?? TryGetNamedArg(args, "--table-name")
            ?? TryGetNamedArg(args, "table");
        var romName = TryGetNamedArg(args, "--rom")
            ?? TryGetNamedArg(args, "--rom-name")
            ?? TryGetNamedArg(args, "rom");

        if (!string.IsNullOrWhiteSpace(tableName))
        {
            return tableName;
        }

        return !string.IsNullOrWhiteSpace(romName) ? romName : null;
    }

    private static string? TryGetNamedArg(IEnumerable<string> args, string expectedKey)
    {
        var values = args as string[] ?? args.ToArray();
        for (var i = 0; i < values.Length; i++)
        {
            var current = values[i];
            if (string.IsNullOrWhiteSpace(current))
            {
                continue;
            }

            if (current.Equals(expectedKey, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < values.Length && !string.IsNullOrWhiteSpace(values[i + 1]))
                {
                    return values[i + 1];
                }

                continue;
            }

            var delimiterIndex = current.IndexOf('=');
            if (delimiterIndex <= 0)
            {
                continue;
            }

            var key = current[..delimiterIndex];
            if (!key.Equals(expectedKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = current[(delimiterIndex + 1)..];
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
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

    private static string ResolveConfigFilePath()
    {
        // Note: single-file publishes can leave Assembly.Location blank, so we anchor to BaseDirectory first.
        var baseDirectory = AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            // Note: this guard mostly helps unusual host/test environments where BaseDirectory is unexpectedly empty.
            baseDirectory = Environment.CurrentDirectory;
        }

        return Path.GetFullPath(Path.Combine(baseDirectory, ConfigFileName));
    }

    private static bool HasArg(string[] args, string expected)
    {
        return args.Any(a => a.Equals(expected, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record ControlCommandMessage(string Command, string[] Args);
}
