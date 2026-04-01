using System.Buffers;
using System.Threading;

namespace VirtualDofMatrix.Core;

public sealed class FramePresentation : IDisposable
{
    private byte[]? _buffer;
    private readonly int _rgbLength;
    private readonly Action<byte[]>? _releaseBuffer;

    public FramePresentation(
        byte[] rgbBytes,
        int HighestLedWritten,
        int LedsPerChannel,
        ulong OutputSequence,
        DateTimeOffset PresentedAtUtc)
        : this(rgbBytes, rgbBytes.Length, HighestLedWritten, LedsPerChannel, OutputSequence, PresentedAtUtc, releaseBuffer: null)
    {
    }

    private FramePresentation(
        byte[] rgbBytes,
        int rgbLength,
        int highestLedWritten,
        int ledsPerChannel,
        ulong outputSequence,
        DateTimeOffset presentedAtUtc,
        Action<byte[]>? releaseBuffer)
    {
        _buffer = rgbBytes ?? throw new ArgumentNullException(nameof(rgbBytes));
        _rgbLength = Math.Clamp(rgbLength, 0, rgbBytes.Length);
        HighestLedWritten = highestLedWritten;
        LedsPerChannel = ledsPerChannel;
        OutputSequence = outputSequence;
        PresentedAtUtc = presentedAtUtc;
        _releaseBuffer = releaseBuffer;
    }

    public int HighestLedWritten { get; }

    public int LedsPerChannel { get; }

    public ulong OutputSequence { get; }

    public DateTimeOffset PresentedAtUtc { get; }

    public ReadOnlyMemory<byte> RgbBytes => RgbMemory;

    public ReadOnlyMemory<byte> RgbMemory
    {
        get
        {
            var buffer = Volatile.Read(ref _buffer);
            return buffer is null ? ReadOnlyMemory<byte>.Empty : new ReadOnlyMemory<byte>(buffer, 0, _rgbLength);
        }
    }

    public static FramePresentation FromPooledBuffer(
        byte[] rgbBytes,
        int rgbLength,
        int highestLedWritten,
        int ledsPerChannel,
        ulong outputSequence,
        DateTimeOffset presentedAtUtc)
    {
        return new FramePresentation(
            rgbBytes,
            rgbLength,
            highestLedWritten,
            ledsPerChannel,
            outputSequence,
            presentedAtUtc,
            releaseBuffer: static buffer => ArrayPool<byte>.Shared.Return(buffer));
    }

    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
        {
            _releaseBuffer?.Invoke(buffer);
        }
    }
}
