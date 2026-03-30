using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Serial;

public sealed class VirtualComPairManager
{
    private readonly AppConfig _config;
    private readonly IVirtualComPairBackend _serviceBackend;
    private readonly IVirtualComPairBackend _processBackend;

    public VirtualComPairManager(AppConfig config)
        : this(config, serviceBackend: null, processBackend: null)
    {
    }

    public VirtualComPairManager(
        AppConfig config,
        IVirtualComPairBackend? serviceBackend,
        IVirtualComPairBackend? processBackend)
    {
        _config = config;
        _serviceBackend = serviceBackend ?? new ServiceVirtualComPairBackend(_config.VirtualCom);
        _processBackend = processBackend ?? new LegacyProcessVirtualComPairBackend(_config.VirtualCom);
    }

    public async Task<VirtualComHealth> GetHealthAsync(CancellationToken cancellationToken = default)
        => await SelectPrimaryBackend().GetHealthAsync(cancellationToken);

    public async Task<IReadOnlyList<VirtualComPairInfo>> ListPairsAsync(CancellationToken cancellationToken = default)
        => await SelectPrimaryBackend().ListPairsAsync(cancellationToken);

    public async Task EnsureConfiguredPairAsync(CancellationToken cancellationToken = default)
    {
        if (!_config.VirtualCom.Enabled || IsMode("disabled"))
        {
            return;
        }

        var primary = SelectPrimaryBackend();
        var health = await primary.GetHealthAsync(cancellationToken);
        if (!health.IsHealthy)
        {
            throw new InvalidOperationException($"Virtual COM backend is unhealthy: {health.Message}");
        }

        var txPort = _config.VirtualCom.TxPortName;
        var rxPort = _config.VirtualCom.RxPortName;

        var existing = await primary.ListPairsAsync(cancellationToken);
        if (existing.Any(p => SamePair(p, txPort, rxPort)))
        {
            return;
        }

        try
        {
            await primary.CreatePairAsync(txPort, rxPort, cancellationToken);
        }
        catch when (IsMode("service") && !_config.VirtualCom.DisableFallbackToProcessCommand)
        {
            await _processBackend.CreatePairAsync(txPort, rxPort, cancellationToken);
        }
    }

    public async Task CleanupConfiguredPairAsync(CancellationToken cancellationToken = default)
    {
        if (!_config.VirtualCom.Enabled || !_config.VirtualCom.AutoDeletePairOnShutdown || IsMode("disabled"))
        {
            return;
        }

        try
        {
            await SelectPrimaryBackend().DeletePairAsync(_config.VirtualCom.TxPortName, _config.VirtualCom.RxPortName, cancellationToken);
        }
        catch when (IsMode("service") && !_config.VirtualCom.DisableFallbackToProcessCommand)
        {
            await _processBackend.DeletePairAsync(_config.VirtualCom.TxPortName, _config.VirtualCom.RxPortName, cancellationToken);
        }
    }

    private IVirtualComPairBackend SelectPrimaryBackend()
        => IsMode("processcommand") ? _processBackend : _serviceBackend;

    private bool IsMode(string mode)
        => string.Equals(_config.Serial.VirtualProviderMode, mode, StringComparison.OrdinalIgnoreCase);

    private static bool SamePair(VirtualComPairInfo pair, string txPort, string rxPort)
        => string.Equals(pair.TxPort, txPort, StringComparison.OrdinalIgnoreCase)
           && string.Equals(pair.RxPort, rxPort, StringComparison.OrdinalIgnoreCase);
}
