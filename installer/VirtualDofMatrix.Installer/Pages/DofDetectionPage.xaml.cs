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
    private bool _suppressTextChanged;

    public DofDetectionPage() => InitializeComponent();

    public string PageTitle => "DOF Detection";
    public bool NextEnabled => true; // always allow advancing; validation is at SummaryPage

    public void OnActivated(InstallerState state, MainWindow host)
    {
        _state = state;
        _suppressTextChanged = true;
        DofRootPathBox.Text = state.DofRootPath;
        _suppressTextChanged = false;
        UpdateStatusDisplay(state.DofDetected, state.DofRootPath);
    }

    public string? Validate(InstallerState state) => null;

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
            StatusDetail.Text = "You can proceed and specify a path manually, or install DOF first.";
            StatusBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFF5F5")!);
            StatusBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F0A0A0")!);
            DownloadPanel.Visibility = Visibility.Visible;
        }
    }

    private void DetectAgainButton_Click(object sender, RoutedEventArgs e)
    {
        if (_state is null) return;
        var hint = DofRootPathBox.Text.Trim();
        var result = DofDetectionService.Detect(string.IsNullOrWhiteSpace(hint) ? null : hint);
        _state.DofDetected = result.Detected;
        _state.DofRootPath = result.RootPath;
        _state.DofConfigPath = result.ConfigPath;
        if (string.IsNullOrWhiteSpace(_state.BackupPath))
            _state.BackupPath = result.SuggestedBackupPath;

        _suppressTextChanged = true;
        DofRootPathBox.Text = result.RootPath;
        _suppressTextChanged = false;

        UpdateStatusDisplay(result.Detected, result.RootPath);
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select DirectOutput root folder",
            InitialDirectory = DofRootPathBox.Text.Trim()
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        var path = dialog.FolderName;
        _suppressTextChanged = true;
        DofRootPathBox.Text = path;
        _suppressTextChanged = false;

        if (_state is null) return;
        _state.DofRootPath = path;
        _state.DofConfigPath = System.IO.Path.Combine(path, "Config");
        var detected = DofDetectionService.IsValidDofRoot(path);
        _state.DofDetected = detected;
        if (string.IsNullOrWhiteSpace(_state.BackupPath))
            _state.BackupPath = System.IO.Path.Combine(path, "Backups", DofDetectionService.BuildTimestampedBackupFolderName());
        UpdateStatusDisplay(detected, path);
    }

    private void DofRootPathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTextChanged || _state is null) return;
        var path = DofRootPathBox.Text.Trim();
        _state.DofRootPath = path;
        _state.DofConfigPath = System.IO.Path.Combine(path, "Config");
    }

    private void DownloadLink_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(DofDownloadUrl) { UseShellExecute = true }); }
        catch { }
    }
}
