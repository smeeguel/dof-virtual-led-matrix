using VirtualDofMatrix.Installer.ViewModels;

namespace VirtualDofMatrix.Installer;

public interface IWizardPage
{
    string PageTitle { get; }

    // Called each time the wizard navigates to this page.
    void OnActivated(InstallerState state, MainWindow host);

    // Returns null if OK to proceed, or a user-facing error string if not.
    string? Validate(InstallerState state);

    // Whether the Next button should be enabled on this page.
    bool NextEnabled { get; }
}
