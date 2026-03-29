using System.Windows;
using VirtualDofMatrix.App.Configuration;
using VirtualDofMatrix.App.Presentation;
using VirtualDofMatrix.App.Serial;

namespace VirtualDofMatrix.App;

public partial class App : Application
{
    private const string ConfigFilePath = "settings.json";

    private readonly AppConfigurationStore _configurationStore = new();
    private SerialEmulatorHost? _serialHost;
    private FramePresentationDispatcher? _presentationDispatcher;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var config = _configurationStore.Load(ConfigFilePath);
        _configurationStore.Save(ConfigFilePath, config);

        var window = new MainWindow(config)
        {
            DataContext = config,
        };

        _serialHost = new SerialEmulatorHost(config);
        _presentationDispatcher = new FramePresentationDispatcher(Dispatcher);
        _presentationDispatcher.Attach(_serialHost);
        _presentationDispatcher.FramePresentedOnUiThread += (_, frame) => window.ApplyPresentation(frame);

        await _serialHost.StartAsync();

        MainWindow = window;
        window.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_presentationDispatcher is not null)
        {
            _presentationDispatcher.Dispose();
            _presentationDispatcher = null;
        }

        if (_serialHost is not null)
        {
            await _serialHost.StopAsync();
        }

        base.OnExit(e);
    }
}
