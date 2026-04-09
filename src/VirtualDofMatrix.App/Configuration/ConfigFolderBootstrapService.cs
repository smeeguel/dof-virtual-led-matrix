using System.IO;
using System.Windows;
using System.Windows.Forms;
using VirtualDofMatrix.Core;
using WpfMessageBox = System.Windows.MessageBox;

namespace VirtualDofMatrix.App.Configuration;

// Conversational note: this service keeps first-run startup behavior explicit and deterministic:
// 1) try C:\DirectOutput\Config first, 2) prompt if missing/unreadable, 3) persist chosen folder, 4) validate Cabinet.xml.
public sealed class ConfigFolderBootstrapService
{
    public const string DefaultConfigFolderPath = @"C:\DirectOutput\Config";

    public StartupConfigStatus ResolveAndPersist(AppConfig config)
    {
        config.Settings ??= new SettingsConfig();

        var configuredPath = string.IsNullOrWhiteSpace(config.Settings.DofConfigFolderPath)
            ? DefaultConfigFolderPath
            : config.Settings.DofConfigFolderPath.Trim();

        var activePath = IsReadableDirectory(DefaultConfigFolderPath)
            ? DefaultConfigFolderPath
            : configuredPath;

        if (!IsReadableDirectory(activePath))
        {
            activePath = PromptForFolder(activePath);
        }

        config.Settings.DofConfigFolderPath = activePath;

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

    private static string PromptForFolder(string fallbackPath)
    {
        WpfMessageBox.Show(
            $"Virtual DOF Matrix could not read '{DefaultConfigFolderPath}'.\n\nChoose your DOF Config folder to continue.",
            "Select DOF Config folder",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        using var picker = new FolderBrowserDialog
        {
            Description = "Select your DirectOutput Config folder (must contain Cabinet.xml).",
            SelectedPath = Directory.Exists(fallbackPath) ? fallbackPath : DefaultConfigFolderPath,
            ShowNewFolderButton = false,
        };

        if (picker.ShowDialog() == DialogResult.OK && IsReadableDirectory(picker.SelectedPath))
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
