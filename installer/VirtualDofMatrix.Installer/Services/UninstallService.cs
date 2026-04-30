using System.IO;
using Microsoft.Win32;

namespace VirtualDofMatrix.Installer.Services;

public sealed record InstalledProductInfo(string InstallFolder, string DisplayVersion);

public static class UninstallService
{
    private const string ArpKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\VirtualDofMatrix";
    private const string HklmHintsKeyPath = @"Software\VirtualDofMatrix";
    private const string HkcuShortcutKeyPath = @"Software\Virtual DOF Matrix\Virtual DOF Matrix";

    public static InstalledProductInfo? GetInstalledVersion()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ArpKeyPath);
            if (key is null) return null;

            var installFolder = (string?)key.GetValue("InstallLocation") ?? string.Empty;
            var version = (string?)key.GetValue("DisplayVersion") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(installFolder)) return null;

            return new InstalledProductInfo(installFolder, version);
        }
        catch
        {
            return null;
        }
    }

    public static void Uninstall(IProgress<string>? progress = null)
    {
        var info = GetInstalledVersion();
        if (info is null)
        {
            progress?.Report("No existing installation found in registry.");
            return;
        }

        var installFolder = info.InstallFolder;

        // Remove installed app files.
        progress?.Report($"Removing application files from {installFolder}...");
        RemoveDirectory(installFolder, progress);

        // Remove Start Menu shortcut folder.
        var startMenuFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            "Programs", "Virtual DOF Matrix");
        if (Directory.Exists(startMenuFolder))
        {
            progress?.Report("Removing Start Menu shortcut...");
            try { Directory.Delete(startMenuFolder, recursive: true); }
            catch (Exception ex) { progress?.Report($"Warning: could not remove Start Menu folder: {ex.Message}"); }
        }

        // Remove ARP registry entry.
        progress?.Report("Removing Add/Remove Programs entry...");
        RemoveRegistryKey(Registry.LocalMachine, ArpKeyPath, progress);

        // Remove HKLM installer hint keys.
        RemoveRegistryKey(Registry.LocalMachine, HklmHintsKeyPath, progress);

        // Remove HKCU shortcut key.
        RemoveRegistryKey(Registry.CurrentUser, HkcuShortcutKeyPath, progress);

        progress?.Report("Uninstall complete.");
    }

    private static void RemoveDirectory(string path, IProgress<string>? progress)
    {
        if (!Directory.Exists(path)) return;
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            progress?.Report($"Warning: could not fully remove {path}: {ex.Message}");
            // Best-effort: try removing individual files.
            foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
            {
                try { File.Delete(file); } catch { }
            }
        }
    }

    private static void RemoveRegistryKey(RegistryKey hive, string keyPath, IProgress<string>? progress)
    {
        try { hive.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false); }
        catch (Exception ex) { progress?.Report($"Warning: could not remove registry key {keyPath}: {ex.Message}"); }
    }
}
