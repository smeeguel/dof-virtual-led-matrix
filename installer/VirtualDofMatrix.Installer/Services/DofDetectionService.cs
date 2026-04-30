using System.Globalization;
using System.IO;
using Microsoft.Win32;

namespace VirtualDofMatrix.Installer.Services;

public sealed record DofDetectionResult(
    bool Detected,
    string RootPath,
    string ConfigPath,
    string SuggestedBackupPath);

public static class DofDetectionService
{
    private const string DefaultDofRootPath = @"C:\DirectOutput";

    public static DofDetectionResult Detect(string? hintRootPath = null)
    {
        var root = string.Empty;

        if (IsValidDofRoot(DefaultDofRootPath))
            root = DefaultDofRootPath;
        else if (!string.IsNullOrWhiteSpace(hintRootPath) && IsValidDofRoot(hintRootPath))
            root = hintRootPath!;
        else
            root = ScanRegistryAndFilesystem();

        if (!string.IsNullOrWhiteSpace(root))
        {
            var configPath = Path.Combine(root, "Config");
            var backupPath = Path.Combine(root, "Backups", BuildTimestampedBackupFolderName());
            return new DofDetectionResult(true, root, configPath, backupPath);
        }

        var fallbackRoot = string.IsNullOrWhiteSpace(hintRootPath) ? DefaultDofRootPath : hintRootPath!;
        var fallbackConfig = Path.Combine(fallbackRoot, "Config");
        var fallbackBackup = Path.Combine(fallbackRoot, "Backups", BuildTimestampedBackupFolderName());
        return new DofDetectionResult(false, fallbackRoot, fallbackConfig, fallbackBackup);
    }

    public static bool IsValidDofRoot(string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) return false;
        var normalized = Normalize(rootPath);
        return Directory.Exists(Path.Combine(normalized, "Config")) &&
               (Directory.Exists(Path.Combine(normalized, "x64")) || Directory.Exists(Path.Combine(normalized, "x86")));
    }

    public static bool IsValidDofRootForInstall(string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) return false;
        return Directory.Exists(Path.Combine(Normalize(rootPath!), "Config"));
    }

    public static string Normalize(string path) =>
        path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public static string BuildTimestampedBackupFolderName() =>
        string.Format(CultureInfo.InvariantCulture, "ConfigBackup-{0}",
            DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));

    private static string ScanRegistryAndFilesystem()
    {
        foreach (var candidate in EnumerateRegistryCandidates())
            if (IsValidDofRoot(candidate)) return candidate;

        foreach (var candidate in EnumerateFilesystemCandidates())
            if (IsValidDofRoot(candidate)) return candidate;

        return string.Empty;
    }

    private static IEnumerable<string> EnumerateRegistryCandidates()
    {
        var basePaths = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (hive, label) in new[] { (Registry.LocalMachine, "HKLM"), (Registry.CurrentUser, "HKCU") })
        {
            foreach (var basePath in basePaths)
            {
                using var uninstallRoot = hive.OpenSubKey(basePath);
                if (uninstallRoot is null) continue;

                foreach (var subName in uninstallRoot.GetSubKeyNames())
                {
                    using var appKey = uninstallRoot.OpenSubKey(subName);
                    if (appKey is null) continue;

                    var displayName = Convert.ToString(appKey.GetValue("DisplayName"), CultureInfo.InvariantCulture) ?? string.Empty;
                    if (!displayName.Contains("DirectOutput", StringComparison.OrdinalIgnoreCase)) continue;

                    var location = Normalize(Convert.ToString(appKey.GetValue("InstallLocation"), CultureInfo.InvariantCulture) ?? string.Empty);
                    if (!string.IsNullOrWhiteSpace(location) && seen.Add(location))
                        yield return location;

                    var icon = DeriveRootFromDisplayIcon(Convert.ToString(appKey.GetValue("DisplayIcon"), CultureInfo.InvariantCulture) ?? string.Empty);
                    if (!string.IsNullOrWhiteSpace(icon) && seen.Add(icon))
                        yield return icon;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateFilesystemCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var drive in Environment.GetLogicalDrives())
        {
            string? candidate = null;
            try
            {
                var info = new DriveInfo(drive);
                if (info.DriveType == DriveType.Fixed && info.IsReady)
                    candidate = Normalize(Path.Combine(info.RootDirectory.FullName, "DirectOutput"));
            }
            catch { }
            if (candidate != null && seen.Add(candidate)) yield return candidate;
        }

        var programFiles = Normalize(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            var candidate = Path.Combine(programFiles, "DirectOutput");
            if (seen.Add(candidate)) yield return candidate;
        }

        var programFilesX86 = Normalize(Environment.GetEnvironmentVariable("ProgramFiles(x86)") ??
                                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            var candidate = Path.Combine(programFilesX86, "DirectOutput");
            if (seen.Add(candidate)) yield return candidate;
        }
    }

    private static string DeriveRootFromDisplayIcon(string displayIcon)
    {
        var value = Normalize(displayIcon);
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var comma = value.IndexOf(',');
        if (comma >= 0) value = value[..comma];
        value = value.Trim().Trim('"');

        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        if (Directory.Exists(value)) return Normalize(value);
        if (File.Exists(value)) return Normalize(Path.GetDirectoryName(value) ?? string.Empty);
        return Normalize(Path.GetDirectoryName(value) ?? string.Empty);
    }
}
