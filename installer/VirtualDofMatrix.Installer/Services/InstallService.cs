using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using VirtualDofMatrix.Installer.ViewModels;

namespace VirtualDofMatrix.Installer.Services;

public static class InstallService
{
    private const string DofDownloadUrl = "http://mjrnet.org/pinscape/dll-updates.html#DOF";
    private const string ArpKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\VirtualDofMatrix";
    private const string HklmHintsKeyPath = @"Software\VirtualDofMatrix";

    private static readonly IReadOnlyDictionary<string, string> TemplateToFolder =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "single_matrix", "01-single-matrix" },
            { "matrix_plus_3_strips", "02-matrix-and-flasher-strips" },
        };

    public static string? ValidateSelections(InstallerState state)
    {
        var configPath = DofDetectionService.Normalize(state.DofConfigPath);

        if (string.IsNullOrWhiteSpace(configPath))
            return "DOF config path is required.";

        if (!Path.GetFileName(configPath).Equals("Config", StringComparison.OrdinalIgnoreCase))
            return "DOF config path must end with \\Config (e.g. C:\\DirectOutput\\Config).";

        var root = Path.GetDirectoryName(configPath);
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathRooted(root))
            return "Could not derive DOF root folder from the config path.";

        if (!DofDetectionService.IsValidDofRootForInstall(root))
            return "The selected DOF root must contain a Config sub-folder. Install DirectOutput first.";

        if (!TemplateToFolder.ContainsKey(state.ToyTemplate))
            return $"Unknown template '{state.ToyTemplate}'. Supported: {string.Join(", ", TemplateToFolder.Keys)}.";

        return null;
    }

    public static void Apply(InstallerState state, IProgress<string>? progress = null)
    {
        var configPath = DofDetectionService.Normalize(state.DofConfigPath);
        var rootPath = DofDetectionService.Normalize(
            string.IsNullOrWhiteSpace(state.DofRootPath)
                ? (Path.GetDirectoryName(configPath) ?? string.Empty)
                : state.DofRootPath);
        var installFolder = DofDetectionService.Normalize(state.InstallFolder);
        var templateId = state.ToyTemplate;
        var backupEnabled = state.BackupEnabled;
        var backupPath = DofDetectionService.Normalize(state.BackupPath);

        if (!TemplateToFolder.TryGetValue(templateId, out var templateFolder))
            throw new InvalidOperationException($"Unknown template '{templateId}'.");

        var appPayloadDir = FindAppPayloadDir();
        var dofPayloadDir = FindDofPayloadDir();
        var baselineConfigSrc = Path.Combine(dofPayloadDir, "Config");
        var templateSrc = Path.Combine(dofPayloadDir, "Config", "templates", templateFolder);

        if (!Directory.Exists(appPayloadDir))
            throw new DirectoryNotFoundException($"App payload directory not found at '{appPayloadDir}'.");
        if (!Directory.Exists(templateSrc))
            throw new DirectoryNotFoundException($"Template '{templateId}' payload not found at '{templateSrc}'.");
        if (!Directory.Exists(baselineConfigSrc))
            throw new DirectoryNotFoundException($"Baseline DOF Config not found at '{baselineConfigSrc}'.");

        // 1. Copy app files to install folder.
        progress?.Report($"Copying application files to {installFolder}...");
        Directory.CreateDirectory(installFolder);
        CopyDirectory(appPayloadDir, installFolder, _ => true, progress);

        // 2. Copy installer exe so uninstall works from Programs & Features.
        progress?.Report("Registering uninstaller...");
        CopyInstallerExe(installFolder);

        // 3. Create Start Menu shortcut.
        progress?.Report("Creating Start Menu shortcut...");
        CreateStartMenuShortcut(installFolder);

        // 4. Register in Add/Remove Programs.
        progress?.Report("Registering in Add/Remove Programs...");
        RegisterArpEntry(installFolder, state.ProductVersion);

        // 5. DOF setup: backup, DLL copy, baseline config, template.
        progress?.Report($"Preparing DOF config folder at {configPath}...");
        Directory.CreateDirectory(configPath);
        Directory.CreateDirectory(rootPath);

        if (backupEnabled && Directory.Exists(configPath))
        {
            if (string.IsNullOrWhiteSpace(backupPath))
                backupPath = Path.Combine(rootPath, "Backups", DofDetectionService.BuildTimestampedBackupFolderName());
            progress?.Report($"Backing up existing DOF Config to {backupPath}...");
            CopyDirectory(configPath, backupPath, _ => true, progress);
        }

        progress?.Report("Copying DirectOutput DLLs...");
        CopyDofDll(dofPayloadDir, rootPath, "x64", progress);
        CopyDofDll(dofPayloadDir, rootPath, "x86", progress);

        progress?.Report("Copying baseline DOF config files...");
        CopyDirectory(baselineConfigSrc, configPath,
            src =>
            {
                var rel = src[(baselineConfigSrc.Length)..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var parts = rel.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
                return parts.Length == 0 || !parts[0].Equals("templates", StringComparison.OrdinalIgnoreCase);
            },
            progress);

        progress?.Report($"Applying template '{templateId}'...");
        CopyDirectory(templateSrc, configPath, _ => true, progress);

        // 6. Persist DOF path hints for the app.
        progress?.Report("Writing registry hints...");
        PersistHklmHints(rootPath, configPath);

        // 7. HKCU shortcut key (mirrors old MSI component key path).
        using var hkcuKey = Registry.CurrentUser.CreateSubKey(@"Software\Virtual DOF Matrix\Virtual DOF Matrix");
        hkcuKey?.SetValue("StartMenuShortcutInstalled", 1, RegistryValueKind.DWord);

        progress?.Report("Installation complete.");
    }

    private static void CopyDofDll(string dofPayloadDir, string dofRoot, string arch, IProgress<string>? progress)
    {
        var src = Path.Combine(dofPayloadDir, arch, "DirectOutput.dll");
        if (!File.Exists(src)) return;
        var destDir = Path.Combine(dofRoot, arch);
        Directory.CreateDirectory(destDir);
        var dest = Path.Combine(destDir, "DirectOutput.dll");
        RetryFileCopy(src, dest, arch + " DirectOutput.dll", progress);
    }

    private static void RetryFileCopy(string src, string dest, string description, IProgress<string>? progress)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                File.Copy(src, dest, overwrite: true);
                return;
            }
            catch (IOException ex) when (IsSharingViolation(ex) && attempt < 3)
            {
                progress?.Report($"File locked: {dest}. Close apps using DirectOutput and retry ({attempt}/3)...");
                System.Threading.Thread.Sleep(3000);
            }
        }
        File.Copy(src, dest, overwrite: true);
    }

    private static bool IsSharingViolation(IOException ex)
    {
        var hr = Marshal.GetHRForException(ex) & 0xFFFF;
        return hr == 32 || hr == 33;
    }

    private static void CopyDirectory(string src, string dest, Func<string, bool> include, IProgress<string>? progress)
    {
        var files = Directory.GetFiles(src, "*", SearchOption.AllDirectories);
        foreach (var srcFile in files)
        {
            if (!include(srcFile)) continue;
            var rel = srcFile[src.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var destFile = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
            File.Copy(srcFile, destFile, overwrite: true);
        }
        progress?.Report($"  Copied {files.Length} file(s) from {Path.GetFileName(src)}");
    }

    private static void CopyInstallerExe(string installFolder)
    {
        var exePath = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return;
        var dest = Path.Combine(installFolder, "VirtualDofMatrix.Installer.exe");
        try { File.Copy(exePath, dest, overwrite: true); }
        catch { }
    }

    private static void CreateStartMenuShortcut(string installFolder)
    {
        var shortcutFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            "Programs", "Virtual DOF Matrix");
        Directory.CreateDirectory(shortcutFolder);
        var lnkPath = Path.Combine(shortcutFolder, "Virtual DOF Matrix.lnk");
        var targetExe = Path.Combine(installFolder, "VirtualDofMatrix.App.exe");
        ShortcutHelper.Create(lnkPath, targetExe, installFolder);
    }

    private static void RegisterArpEntry(string installFolder, string version)
    {
        using var key = Registry.LocalMachine.CreateSubKey(ArpKeyPath);
        if (key is null) return;
        var uninstaller = Path.Combine(installFolder, "VirtualDofMatrix.Installer.exe");
        key.SetValue("DisplayName", "Virtual DOF Matrix", RegistryValueKind.String);
        key.SetValue("Publisher", "Virtual DOF Matrix", RegistryValueKind.String);
        key.SetValue("DisplayVersion", version, RegistryValueKind.String);
        key.SetValue("InstallLocation", installFolder + Path.DirectorySeparatorChar, RegistryValueKind.String);
        key.SetValue("UninstallString", $"\"{uninstaller}\" --uninstall", RegistryValueKind.String);
        key.SetValue("QuietUninstallString", $"\"{uninstaller}\" --uninstall --silent", RegistryValueKind.String);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("URLInfoAbout", "https://github.com/virtualledmatrix", RegistryValueKind.String);
        key.SetValue("HelpLink", DofDownloadUrl, RegistryValueKind.String);
    }

    private static void PersistHklmHints(string dofRootPath, string dofConfigPath)
    {
        using var key = Registry.LocalMachine.CreateSubKey(HklmHintsKeyPath);
        if (key is null) return;
        key.SetValue("DofRootPath", dofRootPath, RegistryValueKind.String);
        key.SetValue("DofConfigPath", dofConfigPath, RegistryValueKind.String);
        key.SetValue("GeneratedUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), RegistryValueKind.String);
    }

    private static string FindAppPayloadDir()
    {
        var exeDir = ExeDirectory();
        var devLayout = Path.Combine(exeDir, "payload", "app");
        if (Directory.Exists(devLayout)) return devLayout;
        if (File.Exists(Path.Combine(exeDir, "VirtualDofMatrix.App.exe"))) return exeDir;
        return EmbeddedPayloadService.AppPayloadDirectory;
    }

    private static string FindDofPayloadDir()
    {
        var exeDir = ExeDirectory();
        var devLayout = Path.Combine(exeDir, "payload", "DOF");
        if (Directory.Exists(devLayout)) return devLayout;
        var releaseLayout = Path.Combine(exeDir, "DOF");
        if (Directory.Exists(releaseLayout)) return releaseLayout;
        if (Directory.Exists(EmbeddedPayloadService.DofPayloadDirectory))
            return EmbeddedPayloadService.DofPayloadDirectory;
        throw new DirectoryNotFoundException(
            $"DOF payload directory not found. Expected embedded payload resources, 'payload/DOF/', or 'DOF/' next to the installer at '{exeDir}'.");
    }

    private static string ExeDirectory() =>
        Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName) ?? Directory.GetCurrentDirectory();
}

internal static class ShortcutHelper
{
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkComClass { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint flags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder psz, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string psz);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder psz, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string psz);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder psz, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string psz);
        void GetHotkey(out short pw);
        void SetHotkey(short w);
        void GetShowCmd(out int pi);
        void SetShowCmd(int i);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder psz, int cch, out int pi);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string psz, int i);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string psz, uint dw);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string psz);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010B-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string psz, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string psz, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string psz);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string psz);
    }

    internal static void Create(string lnkPath, string targetPath, string? workingDir = null)
    {
        var lnk = (IShellLinkW)new ShellLinkComClass();
        lnk.SetPath(targetPath);
        lnk.SetWorkingDirectory(workingDir ?? Path.GetDirectoryName(targetPath) ?? string.Empty);
        lnk.SetIconLocation(targetPath, 0);
        ((IPersistFile)lnk).Save(lnkPath, true);
    }
}
