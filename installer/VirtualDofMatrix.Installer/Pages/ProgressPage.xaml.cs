using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using VirtualDofMatrix.Installer.Services;
using VirtualDofMatrix.Installer.ViewModels;

namespace VirtualDofMatrix.Installer.Pages;

public partial class ProgressPage : UserControl, IWizardPage
{
    private readonly ObservableCollection<string> _log = new();
    private MainWindow? _host;

    public ProgressPage()
    {
        InitializeComponent();
        LogList.ItemsSource = _log;
    }

    public string PageTitle => "Installing";
    public bool NextEnabled => false;

    public void OnActivated(InstallerState state, MainWindow host)
    {
        _host = host;
        _log.Clear();
        ErrorPanel.Visibility = Visibility.Collapsed;
        StatusHeading.Text = "Installing…";
        CurrentStepText.Text = "Preparing…";
        InstallProgressBar.IsIndeterminate = true;
        host.SetProgressActive(true);

        // Kick off install on a background thread; do NOT await here (async void is intentional for WPF event-like usage).
        _ = RunInstallAsync(state);
    }

    public string? Validate(InstallerState state) => null;

    private async System.Threading.Tasks.Task RunInstallAsync(InstallerState state)
    {
        var progress = new Progress<string>(msg =>
        {
            Dispatcher.Invoke(() =>
            {
                _log.Add(msg);
                CurrentStepText.Text = msg;
                LogList.ScrollIntoView(msg);
            });
        });

        Exception? failure = null;
        try
        {
            await System.Threading.Tasks.Task.Run(() => InstallService.Apply(state, progress));
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        Dispatcher.Invoke(() =>
        {
            InstallProgressBar.IsIndeterminate = false;
            _host?.SetProgressActive(false);

            if (failure is not null)
            {
                InstallProgressBar.Value = 0;
                StatusHeading.Text = "Installation failed";
                CurrentStepText.Text = "An error occurred.";
                ErrorPanel.Visibility = Visibility.Visible;
                ErrorText.Text = failure.Message;
                // Leave user on this page so they can read the log.
            }
            else
            {
                InstallProgressBar.Value = 100;
                InstallProgressBar.IsIndeterminate = false;
                StatusHeading.Text = "Installation complete";
                CurrentStepText.Text = "Done.";
                _host?.OnInstallComplete(true);
            }
        });
    }
}
