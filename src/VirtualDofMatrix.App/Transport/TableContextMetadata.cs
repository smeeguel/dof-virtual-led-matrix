namespace VirtualDofMatrix.App.Transport;

public sealed record TableContextMetadata(
    string TableName,
    string RomName,
    ulong Sequence,
    DateTimeOffset ReceivedAtUtc);
