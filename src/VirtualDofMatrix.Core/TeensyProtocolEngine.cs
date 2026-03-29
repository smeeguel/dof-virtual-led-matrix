using System.Text;

namespace VirtualDofMatrix.Core;

public sealed class TeensyProtocolEngine
{
    private const byte Ack = (byte)'A';

    private readonly SerialEmulatorSettings _settings;
    private readonly FrameBuffer _frameBuffer;
    private readonly List<byte> _rxBuffer = new();

    public TeensyProtocolEngine(SerialEmulatorSettings settings, FrameBuffer frameBuffer)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _frameBuffer = frameBuffer ?? throw new ArgumentNullException(nameof(frameBuffer));
    }

    public FrameBuffer FrameBuffer => _frameBuffer;

    public ProtocolProcessResult ProcessIncoming(ReadOnlySpan<byte> incoming)
    {
        if (!incoming.IsEmpty)
        {
            _rxBuffer.AddRange(incoming.ToArray());
        }

        var logs = new List<ProtocolLogEntry>();
        var tx = new List<byte>();
        var consumed = 0;

        while (consumed < _rxBuffer.Count)
        {
            var cmd = _rxBuffer[consumed];

            if (cmd == 0x00)
            {
                tx.Add(Ack);
                logs.Add(Log($"RX 00 (command-mode probe) -> TX A"));
                consumed += 1;
                continue;
            }

            if (cmd == (byte)'M')
            {
                var max = _settings.MaxLedsPerChannel;
                tx.Add((byte)((max >> 8) & 0xFF));
                tx.Add((byte)(max & 0xFF));
                tx.Add(Ack);
                logs.Add(Log($"RX M -> TX max={max} and A"));
                consumed += 1;
                continue;
            }

            if (cmd == (byte)'L')
            {
                if (!HasBytes(consumed, 3))
                {
                    break;
                }

                var ledsPerChannel = (_rxBuffer[consumed + 1] << 8) | _rxBuffer[consumed + 2];
                _frameBuffer.SetLedsPerChannel(ledsPerChannel);
                tx.Add(Ack);
                logs.Add(Log($"RX L ledsPerChannel={ledsPerChannel} -> TX A"));
                consumed += 3;
                continue;
            }

            if (cmd == (byte)'C')
            {
                _frameBuffer.Clear();
                tx.Add(Ack);
                logs.Add(Log("RX C (clear) -> TX A"));
                consumed += 1;
                continue;
            }

            if (cmd == (byte)'O')
            {
                _frameBuffer.MarkOutput();
                tx.Add(Ack);
                logs.Add(Log($"RX O (output) -> TX A [outputSequence={_frameBuffer.OutputSequence}]"));
                consumed += 1;
                continue;
            }

            if (cmd == (byte)'R')
            {
                if (!HasBytes(consumed, 5))
                {
                    break;
                }

                var target = (_rxBuffer[consumed + 1] << 8) | _rxBuffer[consumed + 2];
                var ledCount = (_rxBuffer[consumed + 3] << 8) | _rxBuffer[consumed + 4];
                var rgbBytes = checked(ledCount * 3);

                if (!HasBytes(consumed, 5 + rgbBytes))
                {
                    break;
                }

                var payload = _rxBuffer.Skip(consumed + 5).Take(rgbBytes).ToArray();
                var maxTotalLeds = _settings.MaxTotalLeds;
                var requestedEndLed = target + ledCount;

                if (requestedEndLed > maxTotalLeds)
                {
                    logs.Add(Log($"RX R target={target} ledCount={ledCount} exceeds configured maxTotalLeds={maxTotalLeds}; applying with auto-resize to preserve compatibility."));
                }

                _frameBuffer.ApplySegment(target, payload, ledCount);
                tx.Add(Ack);
                logs.Add(Log($"RX R target={target} ledCount={ledCount} rgbBytes={rgbBytes} -> TX A"));
                consumed += 5 + rgbBytes;
                continue;
            }

            logs.Add(Log($"RX unknown byte 0x{cmd:X2}; ignored"));
            consumed += 1;
        }

        if (consumed > 0)
        {
            _rxBuffer.RemoveRange(0, consumed);
        }

        if (tx.Count > 0)
        {
            logs.Add(Log($"TX HEX: {ToHex(tx)}"));
        }

        return new ProtocolProcessResult
        {
            ResponseBytes = tx.ToArray(),
            Logs = logs,
        };
    }

    private static string ToHex(IEnumerable<byte> bytes)
    {
        var sb = new StringBuilder();
        foreach (var b in bytes)
        {
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.AppendFormat("{0:X2}", b);
        }

        return sb.ToString();
    }

    private bool HasBytes(int offset, int length) => (_rxBuffer.Count - offset) >= length;

    private static ProtocolLogEntry Log(string message) => new(DateTimeOffset.UtcNow, message);
}
