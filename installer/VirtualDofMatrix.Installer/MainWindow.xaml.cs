using System.IO;
using System.Windows;
using System.Windows.Controls;
using VirtualDofMatrix.Installer.Pages;
using VirtualDofMatrix.Installer.ViewModels;

namespace VirtualDofMatrix.Installer;

public partial class MainWindow : Window
{
    private readonly IWizardPage[] _pages;
    private int _pageIndex;
    private bool _installStarted;

    public MainWindow()
    {
        InitializeComponent();
        _pages = new IWizardPage[]
        {
            new MaintenancePage(),     // [0] same-version: Modify / Repair / Remove
            new WelcomePage(),         // [1]
            new DofDetectionPage(),    // [2]
            new InstallLocationPage(), // [3]
            new DofConfigPage(),       // [4]
            new TemplateSelectPage(),  // [5]
            new SummaryPage(),         // [6]
            new ProgressPage(),        // [7]
            new FinishPage(),          // [8]
        };

        if (App.State.IsMaintenanceMode)
            NavigateTo(0);
        else if (App.State.IsUpgrade)
            NavigateTo(2);
        else
            NavigateTo(1);
    }

    public void UpdateButtons() => RefreshNavigation();

    // Called by ProgressPage when install finishes.
    public void OnInstallComplete(bool succeeded)
    {
        _installStarted = false;
        Dispatcher.Invoke(() =>
        {
            NavigateTo(_pages.Length - 1); // FinishPage
        });
    }

    public void SetProgressActive(bool active)
    {
        Dispatcher.Invoke(() =>
        {
            BackButton.IsEnabled = !active;
            CancelButton.IsEnabled = !active;
            NextButton.IsEnabled = !active;
        });
    }

    private void NavigateTo(int index)
    {
        _pageIndex = Math.Clamp(index, 0, _pages.Length - 1);
        var page = _pages[_pageIndex];
        OuterScrollViewer.VerticalScrollBarVisibility =
            page.NeedsOuterScroll ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        PageContent.Content = page;
        page.OnActivated(App.State, this);
        RefreshHeader();
        RefreshNavigation();
    }

    private void RefreshHeader()
    {
        var page = _pages[_pageIndex];
        PageSubtitleText.Text = page.PageTitle;
        // Pages 1-6 (Welcome … Summary) are numbered steps; page 0 is MaintenancePage.
        const int firstStep = 1;
        const int lastStep  = 6;
        if (_pageIndex >= firstStep && _pageIndex <= lastStep)
            StepIndicatorText.Text = $"Step {_pageIndex} of {lastStep}";
        else
            StepIndicatorText.Text = string.Empty;
    }

    private void RefreshNavigation()
    {
        var page = _pages[_pageIndex];
        var isFinish = page is FinishPage;
        var isProgress = page is ProgressPage;

        BackButton.IsEnabled = _pageIndex > 0 && !isFinish && !isProgress;
        BackButton.Visibility = isFinish ? Visibility.Collapsed : Visibility.Visible;

        CancelButton.IsEnabled = !isProgress && !isFinish;
        CancelButton.Visibility = isFinish ? Visibility.Collapsed : Visibility.Visible;

        if (isFinish)
        {
            NextButton.Content = "Finish";
            NextButton.IsEnabled = true;
        }
        else if (isProgress)
        {
            NextButton.Content = "Installing…";
            NextButton.IsEnabled = false;
        }
        else
        {
            NextButton.Content = "Next →";
            NextButton.IsEnabled = page.NextEnabled;
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pageIndex > 0)
            NavigateTo(_pageIndex - 1);
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pages[_pageIndex] is FinishPage)
        {
            if (App.State.LaunchAfterInstall)
                TryLaunchApp();
            if (App.State.OpenConfigToolAfterInstall)
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("https://configtool.vpuniverse.com/")
                    { UseShellExecute = true });
            Close();
            return;
        }

        var error = _pages[_pageIndex].Validate(App.State);
        if (error is not null)
        {
            if (error.Length > 0) // empty string = block navigation silently (e.g. Remove cancel)
                MessageBox.Show(error, "Virtual DOF Matrix Setup",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_pageIndex < _pages.Length - 1)
        {
            var next = _pages[_pageIndex].GetNextPageIndex(_pageIndex) ?? (_pageIndex + 1);
            NavigateTo(next);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_installStarted) return;
        if (MessageBox.Show(
                "Are you sure you want to cancel the installation?",
                "Cancel Setup",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes)
            Close();
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_installStarted)
            e.Cancel = true;
    }

    private static void TryLaunchApp()
    {
        var appExe = Path.Combine(App.State.InstallFolder, "VirtualDofMatrix.App.exe");
        if (!File.Exists(appExe)) return;
        try
        {
            LaunchInstalledAppUnelevated(appExe);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Virtual DOF Matrix was installed, but automatic launch failed:\n\n{ex.Message}\n\nLaunch it from the Start Menu or install folder.",
                "Virtual DOF Matrix Setup",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private static void LaunchInstalledAppUnelevated(string appExe)
    {
        // The installer runs elevated. Starting the app directly would inherit that elevated token,
        // which can prevent normal-user DOF/VPX processes from talking to the first-run app instance.
        var explorerExe = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "explorer.exe");
        if (!File.Exists(explorerExe))
            throw new FileNotFoundException("Windows Explorer could not be found.", explorerExe);

        System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo(explorerExe)
            {
                UseShellExecute = false,
                Arguments = QuoteArgument(appExe),
            });
    }

    private static string QuoteArgument(string value) =>
        "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
