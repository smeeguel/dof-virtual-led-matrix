namespace VirtualDofMatrix.App.Transport;

internal enum FrameTransportParseStatus
{
    NeedMoreData,
    ParsedFrame,
    DroppedInvalidData,
}

internal readonly record struct ParsedPipeFrame(byte MessageType, ulong RawSequence, byte[] Payload);

internal readonly record struct FrameTransportParseResult(
    FrameTransportParseStatus Status,
    ParsedPipeFrame Frame,
    string? Warning);

// Overview: stateful parser for VDMF byte streams that can recover from noise and malformed headers.
internal sealed class FrameTransportStreamParser
{
    private const int HeaderLength = 13;
    private const int MaxPayloadLength = 8 * 1100 * 3;
    private static readonly byte[] Magic = [(byte)'V', (byte)'D', (byte)'M', (byte)'F'];
    private readonly List<byte> _receiveBuffer = new();

    public void Append(ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            _receiveBuffer.Add(value);
        }
    }

    public FrameTransportParseResult ParseNext()
    {
        // Note: we keep scanning the same rolling buffer so split headers/payloads can complete on later reads.
        var magicIndex = IndexOfMagic(_receiveBuffer, startIndex: 0);
        if (magicIndex < 0)
        {
            TrimGarbageWithoutMagic();
            return new FrameTransportParseResult(FrameTransportParseStatus.NeedMoreData, default, null);
        }

        if (magicIndex > 0)
        {
            _receiveBuffer.RemoveRange(0, magicIndex);
        }

        if (_receiveBuffer.Count < HeaderLength)
        {
            return new FrameTransportParseResult(FrameTransportParseStatus.NeedMoreData, default, null);
        }

        var messageType = _receiveBuffer[4];
        var payloadLength = ReadInt32LittleEndian(_receiveBuffer, 9);
        if ((messageType != 1 && messageType != 2) || payloadLength < 0 || payloadLength > MaxPayloadLength)
        {
            // Note: malformed headers should not drop the whole client; shift forward and hunt for the next magic.
            var warning = $"Discarded invalid frame header: messageType={messageType}, payloadLength={payloadLength}.";
            ResynchronizeAfterInvalidHeader();
            return new FrameTransportParseResult(FrameTransportParseStatus.DroppedInvalidData, default, warning);
        }

        var frameLength = HeaderLength + payloadLength;
        if (_receiveBuffer.Count < frameLength)
        {
            return new FrameTransportParseResult(FrameTransportParseStatus.NeedMoreData, default, null);
        }

        var rawSequence = unchecked((uint)ReadInt32LittleEndian(_receiveBuffer, 5));
        var payload = payloadLength == 0 ? Array.Empty<byte>() : _receiveBuffer.GetRange(HeaderLength, payloadLength).ToArray();
        _receiveBuffer.RemoveRange(0, frameLength);

        var frame = new ParsedPipeFrame(messageType, rawSequence, payload);
        return new FrameTransportParseResult(FrameTransportParseStatus.ParsedFrame, frame, null);
    }

    private void TrimGarbageWithoutMagic()
    {
        if (_receiveBuffer.Count <= Magic.Length - 1)
        {
            return;
        }

        var keep = Magic.Length - 1;
        var remove = _receiveBuffer.Count - keep;
        _receiveBuffer.RemoveRange(0, remove);
    }

    private void ResynchronizeAfterInvalidHeader()
    {
        if (_receiveBuffer.Count == 0)
        {
            return;
        }

        var nextMagic = IndexOfMagic(_receiveBuffer, startIndex: 1);
        if (nextMagic > 0)
        {
            _receiveBuffer.RemoveRange(0, nextMagic);
            return;
        }

        _receiveBuffer.RemoveAt(0);
    }

    private static int IndexOfMagic(List<byte> buffer, int startIndex)
    {
        for (var i = Math.Max(0, startIndex); i <= buffer.Count - Magic.Length; i++)
        {
            if (buffer[i] == Magic[0]
                && buffer[i + 1] == Magic[1]
                && buffer[i + 2] == Magic[2]
                && buffer[i + 3] == Magic[3])
            {
                return i;
            }
        }

        return -1;
    }

    private static int ReadInt32LittleEndian(IReadOnlyList<byte> buffer, int offset)
    {
        return buffer[offset]
            | (buffer[offset + 1] << 8)
            | (buffer[offset + 2] << 16)
            | (buffer[offset + 3] << 24);
    }
}
