namespace VirtualDofMatrix.Core;

public sealed class ProtocolProcessResult
{
    public required byte[] ResponseBytes { get; init; }

    public required IReadOnlyList<ProtocolLogEntry> Logs { get; init; }
}
