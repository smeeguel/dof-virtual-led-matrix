using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VirtualDofMatrix.Installer.ViewModels;

namespace VirtualDofMatrix.Installer.Pages;

public partial class TemplateSelectPage : UserControl, IWizardPage
{
    private InstallerState? _state;

    public TemplateSelectPage() => InitializeComponent();

    public string PageTitle => "Virtual Toy Template";
    public bool NextEnabled => true;

    public void OnActivated(InstallerState state, MainWindow host)
    {
        _state = state;
        SingleMatrixRadio.IsChecked = state.ToyTemplate != "matrix_plus_3_strips";
        MatrixStripsRadio.IsChecked = state.ToyTemplate == "matrix_plus_3_strips";
        UpdateBorderHighlights();
    }

    public string? Validate(InstallerState state) => null;

    private void Template_Checked(object sender, RoutedEventArgs e)
    {
        if (_state is null) return;
        _state.ToyTemplate = MatrixStripsRadio.IsChecked == true ? "matrix_plus_3_strips" : "single_matrix";
        UpdateBorderHighlights();
    }

    private void SingleMatrixBorder_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        SingleMatrixRadio.IsChecked = true;
    }

    private void MatrixStripsBorder_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        MatrixStripsRadio.IsChecked = true;
    }

    private void UpdateBorderHighlights()
    {
        var accent = (SolidColorBrush)Application.Current.Resources["AccentBrush"];
        var neutral = (SolidColorBrush)Application.Current.Resources["BorderBrush"];
        SingleMatrixBorder.BorderBrush = SingleMatrixRadio.IsChecked == true ? accent : neutral;
        MatrixStripsBorder.BorderBrush = MatrixStripsRadio.IsChecked == true ? accent : neutral;
    }
}
