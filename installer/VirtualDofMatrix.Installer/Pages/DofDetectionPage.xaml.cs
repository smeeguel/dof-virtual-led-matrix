using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VirtualDofMatrix.Installer.Services;
using VirtualDofMatrix.Installer.ViewModels;

namespace VirtualDofMatrix.Installer.Pages;

public partial class DofDetectionPage : UserControl, IWizardPage
{
    private const string DofDownloadUrl = "http://mjrnet.org/pinscape/dll-updates.html#DOF";

    private InstallerState? _state;

    public DofDetectionPage() => InitializeComponent();

    public string PageTitle => "DOF Detection";
    public bool NextEnabled => true;
    public string? NextButtonLabel => _state?.DofDetected == true ? null : "Retry Detection";

    public void OnActivated(InstallerState state, MainWindow host)
    {
        _state = state;
        UpdateStatusDisplay(state.DofDetected, state.DofRootPath);
    }

    public string? Validate(InstallerState state) => null;

    public bool HandleNextClick(InstallerState state, MainWindow host)
    {
        if (state.DofDetected) return true;
        RunDetection(state);
        host.UpdateButtons();
        return state.DofDetected;
    }

    private void UpdateStatusDisplay(bool detected, string rootPath)
    {
        if (detected)
        {
            StatusIcon.Text = "✓";
            StatusIcon.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#107C10")!);
            StatusTitle.Text = "DirectOutput Framework detected";
            StatusDetail.Text = rootPath;
            StatusBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F0FFF0")!);
            StatusBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#90D890")!);
            DownloadPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            StatusIcon.Text = "✗";
            StatusIcon.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D13438")!);
            StatusTitle.Text = "DirectOutput Framework not detected";
            StatusDetail.Text = "Install DOF using the link below, then click Retry Detection.";
            StatusBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFF5F5")!);
            StatusBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F0A0A0")!);
            DownloadPanel.Visibility = Visibility.Visible;
        }
    }

    private void RunDetection(InstallerState state)
    {
        var result = DofDetectionService.Detect();
        state.DofDetected = result.Detected;
        state.DofRootPath = result.RootPath;
        state.DofConfigPath = result.ConfigPath;
        if (string.IsNullOrWhiteSpace(state.BackupPath))
            state.BackupPath = result.SuggestedBackupPath;
        UpdateStatusDisplay(result.Detected, result.RootPath);
    }

    private void DownloadLink_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(DofDownloadUrl) { UseShellExecute = true }); }
        catch { }
    }
}
