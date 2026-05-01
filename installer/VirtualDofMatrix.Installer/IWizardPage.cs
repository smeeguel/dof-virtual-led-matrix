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

    // Return an explicit target page index to override default (currentIndex + 1), or null for default.
    int? GetNextPageIndex(int currentIndex) => null;

    // False for pages that manage their own internal scroll (e.g. EULA); the outer ScrollViewer is disabled.
    bool NeedsOuterScroll => true;
}
