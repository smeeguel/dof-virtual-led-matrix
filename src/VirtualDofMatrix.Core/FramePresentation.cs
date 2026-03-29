namespace VirtualDofMatrix.Core;

public sealed record FramePresentation(
    byte[] RgbBytes,
    int LowestLedWritten,
    int HighestLedWritten,
    int LedsPerChannel,
    ulong OutputSequence,
    DateTimeOffset PresentedAtUtc)
{
    public ReadOnlyMemory<byte> RgbMemory => RgbBytes;
}
