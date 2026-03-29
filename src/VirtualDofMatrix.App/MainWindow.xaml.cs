using System.Windows;
using System.Windows.Input;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App;

public partial class MainWindow : Window
{
    public MainWindow(AppConfig config)
    {
        InitializeComponent();

        Topmost = config.Window.AlwaysOnTop;
        Left = config.Window.Left;
        Top = config.Window.Top;
        Width = config.Window.Width;
        Height = config.Window.Height;
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
