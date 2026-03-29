namespace VirtualDofMatrix.App.Serial;

public sealed record ComPairReservation(
    string TransmitterPortName,
    string ListenerPortName,
    DateTimeOffset ActivatedAtUtc,
    bool ReusedPersistedPair,
    bool FallbackPairAllocated);
