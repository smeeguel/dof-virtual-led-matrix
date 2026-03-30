namespace VirtualDofMatrix.App.Serial;

public sealed class ServiceManagedVirtualComPairBackend : IVirtualComPairBackend
{
    private readonly IVirtualComProvisioningClient _provisioningClient;

    public ServiceManagedVirtualComPairBackend(IVirtualComProvisioningClient provisioningClient)
    {
        _provisioningClient = provisioningClient;
    }

    public Task CreatePairAsync(string txPort, string rxPort, CancellationToken cancellationToken = default)
        => _provisioningClient.CreatePairAsync(txPort, rxPort, cancellationToken);

    public Task DeletePairAsync(string txPort, string rxPort, CancellationToken cancellationToken = default)
        => _provisioningClient.DeletePairAsync(txPort, rxPort, cancellationToken);

    public Task<IReadOnlyList<VirtualComPairInfo>> ListPairsAsync(CancellationToken cancellationToken = default)
        => _provisioningClient.ListPairsAsync(cancellationToken);

    public Task<VirtualComHealth> GetHealthAsync(CancellationToken cancellationToken = default)
        => _provisioningClient.GetHealthAsync(cancellationToken);
}
