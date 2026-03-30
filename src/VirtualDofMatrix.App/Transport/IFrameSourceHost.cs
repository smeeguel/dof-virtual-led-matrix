using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Transport;

public interface IFrameSourceHost
{
    event EventHandler<FramePresentation>? FramePresented;

    Task StartAsync();

    Task StopAsync();
}
