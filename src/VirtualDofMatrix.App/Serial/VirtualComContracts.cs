namespace VirtualDofMatrix.App.Serial;

public readonly record struct VirtualComPair(string TxPort, string RxPort);

public sealed record VirtualComPairInfo(string TxPort, string RxPort, string? Driver = null, string? Status = null);

public sealed record VirtualComHealth(bool IsHealthy, string? Message = null);

public interface IVirtualComPairBackend
{
    Task CreatePairAsync(string txPort, string rxPort, CancellationToken cancellationToken = default);

    Task DeletePairAsync(string txPort, string rxPort, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VirtualComPairInfo>> ListPairsAsync(CancellationToken cancellationToken = default);

    Task<VirtualComHealth> GetHealthAsync(CancellationToken cancellationToken = default);
}

public interface IVirtualComProvisioningClient
{
    Task CreatePairAsync(string txPort, string rxPort, CancellationToken cancellationToken = default);

    Task DeletePairAsync(string txPort, string rxPort, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VirtualComPairInfo>> ListPairsAsync(CancellationToken cancellationToken = default);

    Task<VirtualComHealth> GetHealthAsync(CancellationToken cancellationToken = default);
}
