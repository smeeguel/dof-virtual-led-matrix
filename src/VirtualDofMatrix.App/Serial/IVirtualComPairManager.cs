namespace VirtualDofMatrix.App.Serial;

public interface IVirtualComPairManager
{
    Task<ComPairReservation> EnsureActivePairAsync(CancellationToken cancellationToken);

    Task DeactivateAsync(ComPairReservation reservation, CancellationToken cancellationToken);
}
