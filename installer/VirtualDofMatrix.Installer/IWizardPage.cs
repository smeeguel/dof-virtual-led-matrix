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

    // Override the Next button label. Null means use "Next →".
    string? NextButtonLabel => null;

    // Called when the Next button is clicked. Return true to proceed with normal navigation, false to stay.
    bool HandleNextClick(InstallerState state, MainWindow host) => true;

    // Return an explicit target page index to override default (currentIndex + 1), or null for default.
    int? GetNextPageIndex(int currentIndex) => null;

    // False for pages that manage their own internal scroll (e.g. EULA); the outer ScrollViewer is disabled.
    bool NeedsOuterScroll => true;
}
