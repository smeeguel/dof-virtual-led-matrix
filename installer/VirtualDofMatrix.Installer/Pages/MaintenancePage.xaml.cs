using System.Windows;
using System.Windows.Controls;
using VirtualDofMatrix.Installer.Services;
using VirtualDofMatrix.Installer.ViewModels;

namespace VirtualDofMatrix.Installer.Pages;

public partial class MaintenancePage : UserControl, IWizardPage
{
    private MainWindow? _host;

    public MaintenancePage() => InitializeComponent();

    public string PageTitle => "Maintenance";
    public bool NextEnabled => true;

    public void OnActivated(InstallerState state, MainWindow host)
    {
        _host = host;
        var existing = UninstallService.GetInstalledVersion();
        var ver = existing?.DisplayVersion ?? state.ProductVersion;
        TitleText.Text = $"Virtual DOF Matrix v{ver} is already installed";
        SubtitleText.Text = $"This installer contains v{state.ProductVersion}. Choose what you would like to do:";
    }

    public string? Validate(InstallerState state)
    {
        if (RemoveRadio.IsChecked == true)
        {
            var confirm = MessageBox.Show(
                "This will remove Virtual DOF Matrix from your computer.\n\nYour DOF configuration files will NOT be removed.\n\nContinue?",
                "Remove Virtual DOF Matrix",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes)
                return string.Empty; // block navigation silently — user cancelled

            // Close the app if it is running so its EXE is not locked during removal.
            var installed = UninstallService.GetInstalledVersion();
            if (installed is not null)
            {
                var running = UninstallService.FindRunningAppProcesses(installed.InstallFolder);
                if (running.Count > 0)
                {
                    var closeConfirm = MessageBox.Show(
                        "Virtual DOF Matrix is currently running and must be closed before uninstalling.\n\nClick OK to close the app and continue.",
                        "Close Virtual DOF Matrix",
                        MessageBoxButton.OKCancel,
                        MessageBoxImage.Information);

                    if (closeConfirm != MessageBoxResult.OK)
                        return string.Empty; // user cancelled

                    UninstallService.CloseAppProcesses(running);
                }
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

            Application.Current.Shutdown();
            return string.Empty; // unreachable; guards against deferred shutdown
        }

        return null;
    }

    public int? GetNextPageIndex(int currentIndex)
    {
        if (RepairRadio.IsChecked == true)
            return 6; // SummaryPage — user reviews existing settings then clicks Install
        return 2;     // Modify → DofDetectionPage
    }

    private void Choice_Checked(object sender, RoutedEventArgs e) =>
        _host?.UpdateButtons();
}
