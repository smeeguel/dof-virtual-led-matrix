using System.IO;
using System.Windows;
using System.Windows.Controls;
using VirtualDofMatrix.Installer.Services;
using VirtualDofMatrix.Installer.ViewModels;

namespace VirtualDofMatrix.Installer.Pages;

public partial class DofConfigPage : UserControl, IWizardPage
{
    private InstallerState? _state;
    private bool _suppress;

    public DofConfigPage() => InitializeComponent();

    public string PageTitle => "DOF Configuration";
    public bool NextEnabled => true;

    public void OnActivated(InstallerState state, MainWindow host)
    {
        _state = state;
        _suppress = true;
        DofConfigPathBox.Text = state.DofConfigPath;
        BackupCheckBox.IsChecked = state.BackupEnabled;

        if (string.IsNullOrWhiteSpace(state.BackupPath))
            state.BackupPath = DeriveDefaultBackupPath(state.DofRootPath);

        BackupPathBox.Text = state.BackupPath;
        _suppress = false;
        UpdateBackupPathEnabled();
    }

    public string? Validate(InstallerState state) => null;

    private static string DeriveDefaultBackupPath(string dofRootPath) =>
        string.IsNullOrWhiteSpace(dofRootPath)
            ? string.Empty
            : Path.Combine(dofRootPath, "Backups");

    private void UpdateBackupPathEnabled()
    {
        if (BackupPathBox is null || BackupPathLabel is null) return;

        var enabled = BackupCheckBox.IsChecked == true;
        BackupPathBox.IsEnabled = enabled;
        BackupPathLabel.Opacity = enabled ? 1.0 : 0.4;
    }

    private void BrowseConfigButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select DOF Config folder",
            InitialDirectory = Path.GetDirectoryName(DofConfigPathBox.Text.Trim()) ?? DofConfigPathBox.Text.Trim()
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        var path = dialog.FolderName;
        var newRoot = DofDetectionService.Normalize(Path.GetDirectoryName(path) ?? string.Empty);

        _suppress = true;
        DofConfigPathBox.Text = path;
        _suppress = false;

        if (_state is null) return;
        _state.DofConfigPath = path;
        _state.DofRootPath = newRoot;
        RefreshBackupDefault(newRoot);
    }

    private void BrowseBackupButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select backup destination folder",
            InitialDirectory = Path.GetDirectoryName(BackupPathBox.Text.Trim()) ?? string.Empty
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        var path = dialog.FolderName;
        _suppress = true;
        BackupPathBox.Text = path;
        _suppress = false;

        if (_state is not null) _state.BackupPath = path;
    }

    private void DofConfigPathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppress || _state is null) return;
        var path = DofConfigPathBox.Text.Trim();
        var newRoot = DofDetectionService.Normalize(Path.GetDirectoryName(path) ?? string.Empty);
        _state.DofConfigPath = path;
        _state.DofRootPath = newRoot;
        RefreshBackupDefault(newRoot);
    }

    private void RefreshBackupDefault(string newRoot)
    {
        if (_state is null) return;
        var derived = DeriveDefaultBackupPath(newRoot);
        // Only update the backup path if it still looks like a derived default (ends with \Backups)
        var current = _state.BackupPath;
        if (string.IsNullOrWhiteSpace(current) ||
            current.EndsWith(Path.DirectorySeparatorChar + "Backups", StringComparison.OrdinalIgnoreCase))
        {
            _state.BackupPath = derived;
            _suppress = true;
            BackupPathBox.Text = derived;
            _suppress = false;
        }
    }

    private void BackupPathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppress || _state is null) return;
        _state.BackupPath = BackupPathBox.Text.Trim();
    }

    private void BackupCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_state is not null) _state.BackupEnabled = BackupCheckBox.IsChecked == true;
        UpdateBackupPathEnabled();
    }
}
