using System.IO;
using System.IO.Pipes;
using VirtualDofMatrix.App.Logging;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Transport;

// Overview: FrameTransportHost listens for serialized frame presentations and republishes them as app-level events.
public sealed class FrameTransportHost
{
    private readonly AppConfig _config;
    private volatile bool _isActive = true;

    private CancellationTokenSource? _cts;
    private Task? _runTask;

    public FrameTransportHost(AppConfig config)
    {
        _config = config;
    }

    public event EventHandler<FramePresentation>? FramePresented;

    public void SetActive(bool active)
    {
        _isActive = active;
    }

    public Task StartAsync()
    {
        // Fire-and-forget loop: callers just need idempotent startup semantics.
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
        await RunNamedPipeLoopAsync(cancellationToken);
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
                AppLogger.Info($"Waiting for named pipe client on '{pipeName}'...");
            }

            await pipe.WaitForConnectionAsync(cancellationToken);

            if (_config.Debug.LogProtocol)
            {
                AppLogger.Info($"Named pipe client connected on '{pipeName}'.");
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
                        AppLogger.Info($"Pipe frame seq={sequence}, payload={payloadLength} bytes.");
                    }

                    if (!_isActive)
                    {
                        continue;
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
                    AppLogger.Warn($"Named pipe IO warning: {ioEx.Message}");
                }
            }
            catch (Exception ex)
            {
                if (_config.Debug.LogProtocol)
                {
                    AppLogger.Error($"Named pipe error: {ex.Message}");
                }
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
