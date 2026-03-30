using System.IO;
using System.IO.Ports;
using System.IO.Pipes;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Serial;

public sealed class SerialEmulatorHost
{
    private readonly AppConfig _config;
    private readonly TeensyProtocolEngine _engine;

    private CancellationTokenSource? _cts;
    private Task? _runTask;

    public SerialEmulatorHost(AppConfig config)
    {
        _config = config;
        _engine = new TeensyProtocolEngine(
            new SerialEmulatorSettings
            {
                MaxLedsPerChannel = config.Serial.MaxLedsPerChannel,
                MaxStrips = config.Serial.MaxStrips,
            },
            new FrameBuffer());
    }

    public event EventHandler<FramePresentation>? FramePresented;

    public Task StartAsync()
    {
        if (_runTask is not null)
        {
            return Task.CompletedTask;
        }

        _cts = new CancellationTokenSource();
        _runTask = Task.Run(() => RunLoopAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_cts is null || _runTask is null)
        {
            return;
        }

        _cts.Cancel();

        try
        {
            await _runTask;
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }

        _runTask = null;
        _cts.Dispose();
        _cts = null;
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        // Branch task note: allow runtime transport switch without duplicating the
        // UI/presentation pipeline.
        if (string.Equals(_config.Transport.Mode, "namedPipe", StringComparison.OrdinalIgnoreCase))
        {
            await RunNamedPipeLoopAsync(cancellationToken);
            return;
        }

        RunSerialLoop(cancellationToken);
    }

    private void RunSerialLoop(CancellationToken cancellationToken)
    {
        var serialConfig = _config.Serial;

        using var serialPort = new SerialPort(serialConfig.PortName, serialConfig.BaudRate)
        {
            ReadTimeout = serialConfig.ReadTimeoutMs,
            WriteTimeout = serialConfig.WriteTimeoutMs,
            DtrEnable = serialConfig.DtrEnable,
        };

        serialPort.Open();

        var readBuffer = new byte[8192];
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var read = serialPort.Read(readBuffer, 0, readBuffer.Length);
                if (read <= 0)
                {
                    continue;
                }

                var result = _engine.ProcessIncoming(readBuffer.AsSpan(0, read));

                if (_config.Debug.LogProtocol)
                {
                    foreach (var log in result.Logs)
                    {
                        Console.WriteLine($"[{log.TimestampUtc:O}] {log.Message}");
                    }
                }

                if (result.ResponseBytes.Length > 0)
                {
                    serialPort.Write(result.ResponseBytes, 0, result.ResponseBytes.Length);
                }

                foreach (var frame in result.PresentedFrames)
                {
                    OnFramePresented(frame);
                }
            }
            catch (TimeoutException)
            {
                // Normal idle behavior.
            }
        }
    }

    private async Task RunNamedPipeLoopAsync(CancellationToken cancellationToken)
    {
        // Branch task note: viewer hosts a named-pipe server; DOF custom controller
        // (VirtualLEDStripController) connects as a client.
        var pipeName = string.IsNullOrWhiteSpace(_config.Transport.PipeName)
            ? "VirtualDofMatrix"
            : _config.Transport.PipeName;

        while (!cancellationToken.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.In,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            if (_config.Debug.LogProtocol)
            {
                Console.WriteLine($"[{DateTimeOffset.UtcNow:O}] Waiting for named pipe client on '{pipeName}'...");
            }

            await pipe.WaitForConnectionAsync(cancellationToken);

            if (_config.Debug.LogProtocol)
            {
                Console.WriteLine($"[{DateTimeOffset.UtcNow:O}] Named pipe client connected on '{pipeName}'.");
            }

            try
            {
                while (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
                {
                    var header = await ReadExactlyAsync(pipe, 13, cancellationToken);
                    if (header.Length == 0)
                    {
                        break;
                    }

                    if (header[0] != (byte)'V' || header[1] != (byte)'D' || header[2] != (byte)'M' || header[3] != (byte)'F')
                    {
                        throw new InvalidDataException("Invalid named-pipe frame magic. Expected 'VDMF'.");
                    }

                    // Branch task note: envelope currently uses version 1 framing.
                    var version = header[4];
                    if (version != 1)
                    {
                        throw new InvalidDataException($"Unsupported named-pipe frame version {version}.");
                    }

                    var sequence = BitConverter.ToInt32(header, 5);
                    var payloadLength = BitConverter.ToInt32(header, 9);
                    if (payloadLength < 0 || payloadLength > 8 * 1100 * 3)
                    {
                        throw new InvalidDataException($"Invalid payload length {payloadLength}.");
                    }

                    var payload = payloadLength == 0
                        ? Array.Empty<byte>()
                        : await ReadExactlyAsync(pipe, payloadLength, cancellationToken);

                    if (_config.Debug.LogProtocol)
                    {
                        Console.WriteLine($"[{DateTimeOffset.UtcNow:O}] Pipe frame seq={sequence}, payload={payloadLength} bytes.");
                    }

                    var frame = new FramePresentation(
                        payload,
                        payload.Length / 3,
                        payload.Length / 3,
                        unchecked((ulong)sequence),
                        DateTimeOffset.UtcNow);

                    OnFramePresented(frame);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
            catch (EndOfStreamException)
            {
                // Client disconnected.
            }
            catch (IOException ioEx)
            {
                if (_config.Debug.LogProtocol)
                {
                    Console.WriteLine($"[{DateTimeOffset.UtcNow:O}] Named pipe IO warning: {ioEx.Message}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTimeOffset.UtcNow:O}] Named pipe error: {ex.Message}");
            }
        }
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int length, CancellationToken cancellationToken)
    {
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("Named pipe disconnected while reading frame.");
            }

            offset += read;
        }

        return buffer;
    }

    private void OnFramePresented(FramePresentation frame)
    {
        FramePresented?.Invoke(this, frame);
    }
}
