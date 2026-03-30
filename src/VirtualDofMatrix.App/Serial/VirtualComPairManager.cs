using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Serial;

public sealed class VirtualComPairManager
{
    private readonly VirtualComProvisioningConfig _config;
    private readonly IVirtualComPairBackend _backend;

    public VirtualComPairManager(VirtualComProvisioningConfig config)
    {
        _config = config;

        if (_config.UseLegacyProcessBackend)
        {
            _backend = new LegacyProcessVirtualComPairBackend(_config);
        }
        else
        {
            var httpClient = new HttpClient
            {
                BaseAddress = new Uri(_config.ServiceBaseUrl, UriKind.Absolute),
            };

            _backend = new ServiceManagedVirtualComPairBackend(new LocalServiceVirtualComProvisioningClient(httpClient));
        }
    }

    public Task<VirtualComHealth> GetHealthAsync(CancellationToken cancellationToken = default)
        => _backend.GetHealthAsync(cancellationToken);

    public Task<IReadOnlyList<VirtualComPairInfo>> ListPairsAsync(CancellationToken cancellationToken = default)
        => _backend.ListPairsAsync(cancellationToken);

    public async Task EnsureConfiguredPairAsync(CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled)
        {
            return;
        }

        await _backend.CreatePairAsync(_config.TxPortName, _config.RxPortName, cancellationToken);
    }

    public async Task CleanupConfiguredPairAsync(CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled || !_config.AutoDeletePairOnShutdown)
        {
            return;
        }

        await _backend.DeletePairAsync(_config.TxPortName, _config.RxPortName, cancellationToken);
    }
}
