using System.IO;
using System.Windows;
using VirtualDofMatrix.App.Logging;
using VirtualDofMatrix.Core;
using FormsDialogResult = System.Windows.Forms.DialogResult;
using FormsFolderBrowserDialog = System.Windows.Forms.FolderBrowserDialog;
using WpfMessageBox = System.Windows.MessageBox;

namespace VirtualDofMatrix.App.Configuration;

// Note: this service keeps first-run startup behavior explicit and deterministic:
// 1) try C:\DirectOutput\Config first, 2) prompt if missing/unreadable, 3) persist chosen folder, 4) validate Cabinet.xml.
public sealed class ConfigFolderBootstrapService
{
    public const string DefaultConfigFolderPath = @"C:\DirectOutput\Config";
    private readonly GlobalConfigBootstrapService _globalConfigBootstrapService = new();
    private readonly InstallerSelectionRegistryHintService _installerSelectionRegistryHintService = new();

    public StartupConfigStatus ResolveAndPersist(AppConfig config)
    {
        config.Settings ??= new SettingsConfig();

        var configuredPath = string.IsNullOrWhiteSpace(config.Settings.DofConfigFolderPath)
            ? DefaultConfigFolderPath
            : config.Settings.DofConfigFolderPath.Trim();

        // Keep installer-discovered custom DOF locations first-launch friendly by honoring
        // the setup hint when settings are still at the built-in default value.
        var settingsStillOnDefaultPath = string.IsNullOrWhiteSpace(config.Settings.DofConfigFolderPath)
            || configuredPath.Equals(DefaultConfigFolderPath, StringComparison.OrdinalIgnoreCase);
        var installHintPath = settingsStillOnDefaultPath
            ? _installerSelectionRegistryHintService.TryResolveDofConfigFolder()
            : null;

        var activePath = IsReadableDirectory(DefaultConfigFolderPath)
            ? DefaultConfigFolderPath
            : (IsReadableDirectory(installHintPath) ? installHintPath! : configuredPath);

        if (!IsReadableDirectory(activePath))
        {
            activePath = PromptForFolder(activePath);
        }

        config.Settings.DofConfigFolderPath = activePath;
        var globalConfigResult = _globalConfigBootstrapService.EnsureRequiredTableConfigPattern(activePath);
        HandleGlobalConfigBootstrapResult(globalConfigResult, activePath);

        var cabinetXmlPath = Path.Combine(activePath, "Cabinet.xml");
        var remediationHint = string.Empty;
        var cabinetStatus = "Cabinet.xml: missing";

        if (File.Exists(cabinetXmlPath))
        {
            config.Settings.CabinetXmlPath = cabinetXmlPath;
            cabinetStatus = "Cabinet.xml: found";
        }
        else
        {
            remediationHint = "Add Cabinet.xml to the selected config folder, or choose the folder that already contains it.";
            ShowCabinetMissingGuidance(activePath);
        }

        return new StartupConfigStatus
        {
            ActiveConfigPath = activePath,
            CabinetFileStatus = cabinetStatus,
            LastLoadedUtc = DateTimeOffset.UtcNow,
            RemediationHint = remediationHint,
        };
    }

    private static void HandleGlobalConfigBootstrapResult(GlobalConfigBootstrapResult result, string activePath)
    {
        switch (result.Status)
        {
            case GlobalConfigBootstrapStatus.Created:
            case GlobalConfigBootstrapStatus.Merged:
            case GlobalConfigBootstrapStatus.Unchanged:
                AppLogger.Info(result.Details);
                return;
            case GlobalConfigBootstrapStatus.Failed:
            default:
                AppLogger.Warn(result.Details);
                WpfMessageBox.Show(
                    $"Virtual DOF Matrix could not update {GlobalConfigBootstrapService.GlobalConfigFileName} in:\n{activePath}\n\n{result.Details}\n\nTry running the app as Administrator.",
                    "GlobalConfig update warning",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
        }
    }

    private static string PromptForFolder(string fallbackPath)
    {
        WpfMessageBox.Show(
            $"Virtual DOF Matrix could not read '{DefaultConfigFolderPath}'.\n\nChoose your DOF Config folder to continue.",
            "Select DOF Config folder",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        var initialDirectory = Directory.Exists(fallbackPath) ? fallbackPath : DefaultConfigFolderPath;
        using var picker = new FormsFolderBrowserDialog
        {
            Description = "Select your DirectOutput Config folder (must contain Cabinet.xml).",
            SelectedPath = initialDirectory,
            ShowNewFolderButton = false,
            UseDescriptionForTitle = true,
        };

        if (picker.ShowDialog() == FormsDialogResult.OK && IsReadableDirectory(picker.SelectedPath))
        {
            return picker.SelectedPath;
        }

        return fallbackPath;
    }

    private static bool IsReadableDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return false;
        }

        try
        {
            _ = Directory.EnumerateFileSystemEntries(path).Take(1).ToArray();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ShowCabinetMissingGuidance(string selectedPath)
    {
        WpfMessageBox.Show(
            $"Cabinet.xml was not found in:\n{selectedPath}\n\nGuided remediation:\n1) Copy Cabinet.xml into this folder, or\n2) Open Settings > DOF / Cabinet and browse to the correct file.",
            "Cabinet.xml missing",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }
}
