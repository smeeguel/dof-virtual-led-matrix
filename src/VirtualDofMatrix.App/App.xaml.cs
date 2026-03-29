using System.Windows;
using VirtualDofMatrix.App.Configuration;
using VirtualDofMatrix.App.Serial;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App;

public partial class App : Application
{
    private const string ConfigFilePath = "settings.json";

    private readonly AppConfigurationStore _configurationStore = new();
    private SerialEmulatorHost? _serialHost;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var config = _configurationStore.Load(ConfigFilePath);
        _configurationStore.Save(ConfigFilePath, config);

        _serialHost = new SerialEmulatorHost(config);
        await _serialHost.StartAsync();

        var window = new MainWindow(config)
        {
            DataContext = config,
        };

        MainWindow = window;
        window.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_serialHost is not null)
        {
            await _serialHost.StopAsync();
        }

        base.OnExit(e);
    }
}
