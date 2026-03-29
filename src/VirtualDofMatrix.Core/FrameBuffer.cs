namespace VirtualDofMatrix.Core;

public sealed class FrameBuffer
{
    private readonly object _sync = new();
    private byte[] _rgbBytes = Array.Empty<byte>();

    public int LedsPerChannel { get; private set; }

    public int HighestLedWritten { get; private set; }

    public ulong OutputSequence { get; private set; }

    public DateTimeOffset? LastOutputUtc { get; private set; }

    public ReadOnlyMemory<byte> RgbBytes
    {
        get
        {
            lock (_sync)
            {
                return _rgbBytes.ToArray();
            }
        }
    }

    public void SetLedsPerChannel(int ledsPerChannel)
    {
        if (ledsPerChannel < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ledsPerChannel));
        }

        lock (_sync)
        {
            LedsPerChannel = ledsPerChannel;
            EnsureCapacity(ledsPerChannel * 3);
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            if (_rgbBytes.Length > 0)
            {
                Array.Clear(_rgbBytes, 0, _rgbBytes.Length);
            }

            HighestLedWritten = 0;
        }
    }

    public void ApplySegment(int targetPosition, ReadOnlySpan<byte> payload, int ledCount)
    {
        if (targetPosition < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetPosition));
        }

        if (ledCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ledCount));
        }

        var bytesToWrite = ledCount * 3;
        if (payload.Length != bytesToWrite)
        {
            throw new ArgumentException($"Expected payload length {bytesToWrite} but received {payload.Length}.", nameof(payload));
        }

        lock (_sync)
        {
            var targetByteOffset = targetPosition * 3;
            var endOffset = targetByteOffset + bytesToWrite;
            EnsureCapacity(endOffset);
            payload.CopyTo(_rgbBytes.AsSpan(targetByteOffset, bytesToWrite));
            HighestLedWritten = Math.Max(HighestLedWritten, targetPosition + ledCount);
        }
    }

    public FramePresentation MarkOutputAndCreatePresentation()
    {
        lock (_sync)
        {
            OutputSequence++;
            LastOutputUtc = DateTimeOffset.UtcNow;

            return new FramePresentation(
                RgbBytes: _rgbBytes.ToArray(),
                HighestLedWritten: HighestLedWritten,
                LedsPerChannel: LedsPerChannel,
                OutputSequence: OutputSequence,
                PresentedAtUtc: LastOutputUtc.Value);
        }
    }

    private void EnsureCapacity(int requiredBytes)
    {
        if (requiredBytes <= _rgbBytes.Length)
        {
            return;
        }

        Array.Resize(ref _rgbBytes, requiredBytes);
    }
}
