namespace VirtualDofMatrix.App.Serial;

public interface IVirtualComPairBackend
{
    Task<bool> TryActivatePairAsync(
        string transmitterPortName,
        string listenerPortName,
        CancellationToken cancellationToken);

    Task DeactivatePairAsync(
        string transmitterPortName,
        string listenerPortName,
        CancellationToken cancellationToken);
}
