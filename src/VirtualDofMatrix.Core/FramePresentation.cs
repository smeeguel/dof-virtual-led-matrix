namespace VirtualDofMatrix.Core;

public sealed record FramePresentation(
    byte[] RgbBytes,
    int HighestLedWritten,
    int LedsPerChannel,
    ulong OutputSequence,
    DateTimeOffset PresentedAtUtc,
    IReadOnlyList<DirtyLedRange>? DirtyRanges = null,
    int DirtyLedCount = 0)
{
    public ReadOnlyMemory<byte> RgbMemory => RgbBytes;

    public IReadOnlyList<DirtyLedRange> DirtyLedRanges => DirtyRanges ?? Array.Empty<DirtyLedRange>();
}
