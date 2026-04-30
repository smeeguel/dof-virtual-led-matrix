using System.Windows.Controls;
using VirtualDofMatrix.Installer.Services;
using VirtualDofMatrix.Installer.ViewModels;

namespace VirtualDofMatrix.Installer.Pages;

public partial class SummaryPage : UserControl, IWizardPage
{
    public SummaryPage() => InitializeComponent();

    public string PageTitle => "Summary";
    public bool NextEnabled => true;

    public void OnActivated(InstallerState state, MainWindow host)
    {
        SummaryInstallFolder.Text = state.InstallFolder;
        SummaryDofConfig.Text = state.DofConfigPath;
        SummaryTemplate.Text = state.ToyTemplate switch
        {
            "matrix_plus_3_strips" => "Matrix and 3 LED Strips",
            _ => "Single Matrix"
        };
        SummaryBackup.Text = state.BackupEnabled ? "Enabled" : "Disabled";
        SummaryBackupPath.Text = state.BackupEnabled
            ? (string.IsNullOrWhiteSpace(state.BackupPath) ? "(auto-generated)" : state.BackupPath)
            : "—";
    }

    public string? Validate(InstallerState state) => InstallService.ValidateSelections(state);
}
