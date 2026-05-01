using System.Windows;
using VirtualDofMatrix.Installer.Services;
using VirtualDofMatrix.Installer.ViewModels;

namespace VirtualDofMatrix.Installer;

public partial class App : Application
{
    internal static InstallerState State { get; } = new InstallerState();

    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            base.OnStartup(e);

            var args = e.Args;

            if (ContainsFlag(args, "--uninstall"))
            {
                if (ContainsFlag(args, "--silent"))
                    RunSilentUninstall();
                else
                    ShowUninstallConfirmation();
                return;
            }

            ParseInstallArgs(args);

            if (ContainsFlag(args, "--silent"))
            {
                RunSilentInstall();
                return;
            }

            // Check for existing install and pre-populate state.
            var existing = UninstallService.GetInstalledVersion();
            if (existing is not null)
            {
                State.InstallFolder = existing.InstallFolder.TrimEnd('\\', '/');
                if (existing.DisplayVersion == State.ProductVersion)
                    State.IsMaintenanceMode = true;
                else
                    State.IsUpgrade = true;
            }

            // Pre-run DOF detection so wizard pages start with valid default values.
            var detection = DofDetectionService.Detect(
                string.IsNullOrWhiteSpace(State.DofRootPath) ? null : State.DofRootPath);
            State.DofDetected = detection.Detected;
            State.DofRootPath = detection.RootPath;
            State.DofConfigPath = detection.ConfigPath;
            if (string.IsNullOrWhiteSpace(State.BackupPath))
                State.BackupPath = detection.SuggestedBackupPath;

            var window = new MainWindow();
            window.Show();
        }
        catch (Exception ex)
        {
            ShowFatalStartupError(ex);
        }
    }

    private static void ParseInstallArgs(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--install-folder":
                    State.InstallFolder = args[i + 1];
                    break;
                case "--dof-config-path":
                    State.DofConfigPath = args[i + 1];
                    State.DofRootPath = System.IO.Path.GetDirectoryName(args[i + 1]) ?? State.DofRootPath;
                    break;
                case "--template":
                    State.ToyTemplate = args[i + 1];
                    break;
                case "--backup-path":
                    State.BackupPath = args[i + 1];
                    break;
            }
        }
        if (ContainsFlag(args, "--no-backup"))
            State.BackupEnabled = false;
    }

    private static void RunSilentInstall()
    {
        try
        {
            Console.WriteLine($"VirtualDofMatrix.Installer v{State.ProductVersion} - silent install");

            if (string.IsNullOrWhiteSpace(State.DofConfigPath) || State.DofConfigPath == @"C:\DirectOutput\Config")
            {
                var detected = DofDetectionService.Detect();
                if (detected.Detected)
                {
                    State.DofRootPath = detected.RootPath;
                    State.DofConfigPath = detected.ConfigPath;
                    if (string.IsNullOrWhiteSpace(State.BackupPath))
                        State.BackupPath = detected.SuggestedBackupPath;
                }
            }

            var error = InstallService.ValidateSelections(State);
            if (error is not null)
            {
                Console.Error.WriteLine($"Validation failed: {error}");
                Environment.Exit(2);
            }

            InstallService.Apply(State, new Progress<string>(msg => Console.WriteLine(msg)));
            Console.WriteLine("Silent install completed successfully.");
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Silent install failed: {ex}");
            Environment.Exit(1);
        }
    }

    private static void RunSilentUninstall()
    {
        try
        {
            Console.WriteLine("VirtualDofMatrix.Installer - silent uninstall");
            UninstallService.Uninstall(new Progress<string>(msg => Console.WriteLine(msg)));
            Console.WriteLine("Uninstall completed.");
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Uninstall failed: {ex}");
            Environment.Exit(1);
        }
    }

    private static void ShowUninstallConfirmation()
    {
        var result = MessageBox.Show(
            "This will remove Virtual DOF Matrix from your computer.\n\nYour DOF configuration files will NOT be removed.\n\nContinue?",
            "Uninstall Virtual DOF Matrix",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            Environment.Exit(0);
            return;
        }

        try
        {
            UninstallService.Uninstall();
            MessageBox.Show("Virtual DOF Matrix has been removed.", "Uninstall Complete",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Uninstall failed:\n\n{ex.Message}", "Uninstall Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }

        Environment.Exit(0);
    }

    private static bool ContainsFlag(string[] args, string flag) =>
        Array.Exists(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    private static void ShowFatalStartupError(Exception ex)
    {
        try
        {
            MessageBox.Show(
                "Virtual DOF Matrix Setup could not start.\n\n" + ex,
                "Virtual DOF Matrix Setup",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            Environment.Exit(1);
        }
    }
}
