using System.IO;
using System.Windows;
using System.Windows.Controls;
using Markdig;
using Markdig.Wpf;
using VirtualDofMatrix.Installer.ViewModels;

namespace VirtualDofMatrix.Installer.Pages;

public partial class WelcomePage : UserControl, IWizardPage
{
    private MainWindow? _host;

    public WelcomePage() => InitializeComponent();

    public string PageTitle => "License Agreement";
    public bool NextEnabled => AcceptCheckBox.IsChecked == true;
    public bool NeedsOuterScroll => false;

    public void OnActivated(InstallerState state, MainWindow host)
    {
        _host = host;
        TitleText.Text = state.IsUpgrade
            ? $"Upgrade Virtual DOF Matrix to v{state.ProductVersion}"
            : "Welcome to Virtual DOF Matrix Setup";

        if (EulaBox.Document.Blocks.Count <= 1)
            LoadEula();
    }

    public string? Validate(InstallerState state) =>
        AcceptCheckBox.IsChecked == true
            ? null
            : "You must accept the license agreement to continue.";

    private void LoadEula()
    {
        try
        {
            var streamInfo = Application.GetResourceStream(new Uri("Assets/Eula.md", UriKind.Relative));
            if (streamInfo is null) return;
            using var reader = new StreamReader(streamInfo.Stream);
            var markdown = reader.ReadToEnd();
            var pipeline = new MarkdownPipelineBuilder().UseSupportedExtensions().Build();
            EulaBox.Document = Markdig.Wpf.Markdown.ToFlowDocument(markdown, pipeline);
        }
        catch { }
    }

    private void AcceptCheckBox_Changed(object sender, RoutedEventArgs e) =>
        _host?.UpdateButtons();
}
