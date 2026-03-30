using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Serial;

public sealed class VirtualComPairManager
{
    private readonly AppConfig _config;
    private readonly IVirtualComPairBackend _backend;

    public VirtualComPairManager(AppConfig config)
    {
        _config = config;

        var mode = (_config.Serial.VirtualProviderMode ?? "service").Trim().ToLowerInvariant();
        _backend = mode switch
        {
            "processcommand" => new LegacyProcessVirtualComPairBackend(_config.VirtualCom),
            "disabled" => new DisabledVirtualComPairBackend(),
            _ => new ServiceVirtualComPairBackend(_config.VirtualCom),
        };
    }

    public Task<VirtualComHealth> GetHealthAsync(CancellationToken cancellationToken = default)
        => _backend.GetHealthAsync(cancellationToken);

    public Task<IReadOnlyList<VirtualComPairInfo>> ListPairsAsync(CancellationToken cancellationToken = default)
        => _backend.ListPairsAsync(cancellationToken);

    public async Task EnsureConfiguredPairAsync(CancellationToken cancellationToken = default)
    {
        if (!_config.VirtualCom.Enabled)
        {
            return;
        }

        await _backend.CreatePairAsync(_config.VirtualCom.TxPortName, _config.VirtualCom.RxPortName, cancellationToken);
    }

    public async Task CleanupConfiguredPairAsync(CancellationToken cancellationToken = default)
    {
        if (!_config.VirtualCom.Enabled || !_config.VirtualCom.AutoDeletePairOnShutdown)
        {
            return;
        }

        await _backend.DeletePairAsync(_config.VirtualCom.TxPortName, _config.VirtualCom.RxPortName, cancellationToken);
    }

    private sealed class DisabledVirtualComPairBackend : IVirtualComPairBackend
    {
        public Task CreatePairAsync(string txPort, string rxPort, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeletePairAsync(string txPort, string rxPort, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<VirtualComPairInfo>> ListPairsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<VirtualComPairInfo>>(new List<VirtualComPairInfo>());

        public Task<VirtualComHealth> GetHealthAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new VirtualComHealth(true, "virtual provider mode disabled"));
    }
}
