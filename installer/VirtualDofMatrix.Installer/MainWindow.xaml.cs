using System.IO;
using System.Windows;
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
            new WelcomePage(),
            new DofDetectionPage(),
            new InstallLocationPage(),
            new DofConfigPage(),
            new TemplateSelectPage(),
            new SummaryPage(),
            new ProgressPage(),
            new FinishPage(),
        };

        NavigateTo(App.State.IsUpgrade ? 1 : 0);
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
        PageContent.Content = page;
        page.OnActivated(App.State, this);
        RefreshHeader();
        RefreshNavigation();
    }

    private void RefreshHeader()
    {
        var page = _pages[_pageIndex];
        PageSubtitleText.Text = page.PageTitle;
        const int namedSteps = 6; // Welcome…Summary
        if (_pageIndex < namedSteps)
            StepIndicatorText.Text = $"Step {_pageIndex + 1} of {namedSteps}";
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
            Close();
            return;
        }

        var error = _pages[_pageIndex].Validate(App.State);
        if (error is not null)
        {
            MessageBox.Show(error, "Virtual DOF Matrix Setup",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_pageIndex < _pages.Length - 1)
            NavigateTo(_pageIndex + 1);
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
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(appExe) { UseShellExecute = true });
        }
        catch { }
    }
}
