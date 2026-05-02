using System.Windows.Controls;
using VirtualDofMatrix.Installer.ViewModels;

namespace VirtualDofMatrix.Installer.Pages;

public partial class FinishPage : UserControl, IWizardPage
{
    public FinishPage() => InitializeComponent();

    public string PageTitle => "Finished";
    public bool NextEnabled => true;

    public void OnActivated(InstallerState state, MainWindow host)
    {
        LaunchCheckBox.IsChecked = state.LaunchAfterInstall;
        OpenConfigToolCheckBox.IsChecked = state.OpenConfigToolAfterInstall;
        OpenInstructionsCheckBox.IsChecked = state.OpenInstructionsAfterInstall;
    }

    public string? Validate(InstallerState state) => null;

    private void LaunchCheckBox_Changed(object sender, System.Windows.RoutedEventArgs e)
    {
        App.State.LaunchAfterInstall = LaunchCheckBox.IsChecked == true;
    }

    private void OpenConfigToolCheckBox_Changed(object sender, System.Windows.RoutedEventArgs e)
    {
        App.State.OpenConfigToolAfterInstall = OpenConfigToolCheckBox.IsChecked == true;
    }

    private void OpenInstructionsCheckBox_Changed(object sender, System.Windows.RoutedEventArgs e)
    {
        App.State.OpenInstructionsAfterInstall = OpenInstructionsCheckBox.IsChecked == true;
    }
}
