namespace VirtualDofMatrix.Core;

public readonly record struct DirtyLedRange(int StartLed, int LedCount)
{
    public int EndLedExclusive => StartLed + LedCount;
}

public sealed class FrameBuffer
{
    private readonly object _sync = new();
    private byte[] _rgbBytes = Array.Empty<byte>();
    private readonly List<DirtyLedRange> _pendingDirtyRanges = new();
    private int _pendingDirtyLedCount;

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
            var clearLedCount = Math.Max(HighestLedWritten, LedsPerChannel);
            if (_rgbBytes.Length > 0)
            {
                for (var led = 0; led < clearLedCount; led++)
                {
                    var offset = led * 3;
                    if ((uint)(offset + 2) >= (uint)_rgbBytes.Length)
                    {
                        break;
                    }

                    if (_rgbBytes[offset] != 0 || _rgbBytes[offset + 1] != 0 || _rgbBytes[offset + 2] != 0)
                    {
                        AddDirtyRangeCore(led, 1);
                    }
                }

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

            var currentDirtyStart = -1;
            var writeSpan = _rgbBytes.AsSpan(targetByteOffset, bytesToWrite);
            for (var ledOffset = 0; ledOffset < ledCount; ledOffset++)
            {
                var payloadOffset = ledOffset * 3;
                var changed =
                    writeSpan[payloadOffset] != payload[payloadOffset] ||
                    writeSpan[payloadOffset + 1] != payload[payloadOffset + 1] ||
                    writeSpan[payloadOffset + 2] != payload[payloadOffset + 2];

                if (changed)
                {
                    writeSpan[payloadOffset] = payload[payloadOffset];
                    writeSpan[payloadOffset + 1] = payload[payloadOffset + 1];
                    writeSpan[payloadOffset + 2] = payload[payloadOffset + 2];

                    if (currentDirtyStart < 0)
                    {
                        currentDirtyStart = ledOffset;
                    }
                }
                else if (currentDirtyStart >= 0)
                {
                    AddDirtyRangeCore(targetPosition + currentDirtyStart, ledOffset - currentDirtyStart);
                    currentDirtyStart = -1;
                }
            }

            if (currentDirtyStart >= 0)
            {
                AddDirtyRangeCore(targetPosition + currentDirtyStart, ledCount - currentDirtyStart);
            }

            HighestLedWritten = Math.Max(HighestLedWritten, targetPosition + ledCount);
        }
    }

    public FramePresentation MarkOutputAndCreatePresentation()
    {
        lock (_sync)
        {
            OutputSequence++;
            LastOutputUtc = DateTimeOffset.UtcNow;

            var dirtyRanges = _pendingDirtyRanges.ToArray();
            var dirtyLedCount = _pendingDirtyLedCount;
            _pendingDirtyRanges.Clear();
            _pendingDirtyLedCount = 0;

            return new FramePresentation(
                RgbBytes: _rgbBytes.ToArray(),
                HighestLedWritten: HighestLedWritten,
                LedsPerChannel: LedsPerChannel,
                OutputSequence: OutputSequence,
                PresentedAtUtc: LastOutputUtc.Value,
                DirtyRanges: dirtyRanges,
                DirtyLedCount: dirtyLedCount);
        }
    }

    private void AddDirtyRangeCore(int startLed, int ledCount)
    {
        if (ledCount <= 0)
        {
            return;
        }

        var newStart = startLed;
        var newEnd = startLed + ledCount;

        for (var i = _pendingDirtyRanges.Count - 1; i >= 0; i--)
        {
            var existing = _pendingDirtyRanges[i];
            if (newEnd < existing.StartLed || newStart > existing.EndLedExclusive)
            {
                continue;
            }

            newStart = Math.Min(newStart, existing.StartLed);
            newEnd = Math.Max(newEnd, existing.EndLedExclusive);
            _pendingDirtyLedCount -= existing.LedCount;
            _pendingDirtyRanges.RemoveAt(i);
        }

        var mergedCount = newEnd - newStart;
        _pendingDirtyRanges.Add(new DirtyLedRange(newStart, mergedCount));
        _pendingDirtyLedCount += mergedCount;
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
