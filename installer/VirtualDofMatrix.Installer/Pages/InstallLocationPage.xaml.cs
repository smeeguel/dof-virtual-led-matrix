using System.Windows;
using System.Windows.Controls;
using VirtualDofMatrix.Installer.ViewModels;

namespace VirtualDofMatrix.Installer.Pages;

public partial class InstallLocationPage : UserControl, IWizardPage
{
    private InstallerState? _state;
    private bool _suppressTextChanged;

    public InstallLocationPage() => InitializeComponent();

    public string PageTitle => "Install Location";
    public bool NextEnabled => true;

    public void OnActivated(InstallerState state, MainWindow host)
    {
        _state = state;
        _suppressTextChanged = true;
        InstallFolderBox.Text = state.InstallFolder;
        _suppressTextChanged = false;

        if (state.IsUpgrade)
        {
            UpgradeBanner.Visibility = Visibility.Visible;
            UpgradeText.Text = $"Upgrading to v{state.ProductVersion}. The existing installation will be replaced.";
        }
        else
        {
            UpgradeBanner.Visibility = Visibility.Collapsed;
        }
    }

    public string? Validate(InstallerState state)
    {
        if (string.IsNullOrWhiteSpace(state.InstallFolder))
            return "Please specify an install folder.";
        if (!System.IO.Path.IsPathRooted(state.InstallFolder))
            return "Install folder must be an absolute path.";
        return null;
    }

    private void ChangeButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose install location",
            InitialDirectory = InstallFolderBox.Text.Trim()
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        _suppressTextChanged = true;
        InstallFolderBox.Text = dialog.FolderName;
        _suppressTextChanged = false;

        if (_state is not null)
            _state.InstallFolder = dialog.FolderName;
    }

    private void InstallFolderBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTextChanged || _state is null) return;
        _state.InstallFolder = InstallFolderBox.Text.Trim();
    }
}
