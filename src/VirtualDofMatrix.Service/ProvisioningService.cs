using VirtualDofMatrix.Service.Api;
using VirtualDofMatrix.Service.Driver;
using VirtualDofMatrix.Service.Persistence;

namespace VirtualDofMatrix.Service;

public sealed class ProvisioningService
{
    private readonly PairMetadataStore _store;
    private readonly IVirtualComDriverController _driver;

    public ProvisioningService(PairMetadataStore store, IVirtualComDriverController driver)
    {
        _store = store;
        _driver = driver;
    }

    public async Task<ServiceResult<PairMetadata>> CreatePairAsync(string txPort, string rxPort, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(txPort) || string.IsNullOrWhiteSpace(rxPort))
        {
            return ServiceResult<PairMetadata>.Fail(ServiceErrorCode.ValidationFailed, "Both txPort and rxPort are required.");
        }

        var pairs = await _store.GetPairsAsync(cancellationToken);
        if (pairs.Any(x => IsPortConflict(x, txPort, rxPort)))
        {
            return ServiceResult<PairMetadata>.Fail(ServiceErrorCode.PortConflict, "Requested COM port already belongs to an active pair.");
        }

        var driverResult = await _driver.CreateLinkedPairAsync(txPort, rxPort, cancellationToken);
        if (!driverResult.Success)
        {
            return ServiceResult<PairMetadata>.Fail(driverResult.ErrorCode, driverResult.Message);
        }

        var created = new PairMetadata(txPort, rxPort, DateTimeOffset.UtcNow);
        await _store.UpsertPairAsync(created, cancellationToken);

        return ServiceResult<PairMetadata>.Ok(created, "pair created");
    }

    public async Task<ServiceResult<PairMetadata>> DeletePairAsync(string txPort, string rxPort, CancellationToken cancellationToken)
    {
        var existing = (await _store.GetPairsAsync(cancellationToken))
            .FirstOrDefault(x => SamePair(x, txPort, rxPort));

        if (existing is null)
        {
            return ServiceResult<PairMetadata>.Fail(ServiceErrorCode.PairNotFound, "Pair not found.");
        }

        var driverResult = await _driver.DeleteLinkedPairAsync(txPort, rxPort, cancellationToken);
        if (!driverResult.Success)
        {
            return ServiceResult<PairMetadata>.Fail(driverResult.ErrorCode, driverResult.Message);
        }

        await _store.DeletePairAsync(txPort, rxPort, cancellationToken);

        return ServiceResult<PairMetadata>.Ok(existing, "pair deleted");
    }

    public Task<IReadOnlyList<PairMetadata>> ListPairsAsync(CancellationToken cancellationToken)
        => _store.GetPairsAsync(cancellationToken);

    public async Task<HealthInfo> GetHealthAsync(CancellationToken cancellationToken)
    {
        var pairs = await _store.GetPairsAsync(cancellationToken);
        var driverHealth = await _driver.GetDriverHealthAsync(cancellationToken);

        return new HealthInfo(ServiceInfo.ServiceVersion, driverHealth.Success, pairs.Count, DateTimeOffset.UtcNow);
    }

    private static bool IsPortConflict(PairMetadata pair, string txPort, string rxPort)
        => pair.TxPort.Equals(txPort, StringComparison.OrdinalIgnoreCase)
           || pair.RxPort.Equals(rxPort, StringComparison.OrdinalIgnoreCase)
           || pair.TxPort.Equals(rxPort, StringComparison.OrdinalIgnoreCase)
           || pair.RxPort.Equals(txPort, StringComparison.OrdinalIgnoreCase);

    private static bool SamePair(PairMetadata pair, string txPort, string rxPort)
        => pair.TxPort.Equals(txPort, StringComparison.OrdinalIgnoreCase)
           && pair.RxPort.Equals(rxPort, StringComparison.OrdinalIgnoreCase);
}
