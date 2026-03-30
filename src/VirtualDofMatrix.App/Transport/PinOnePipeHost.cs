using System.IO.Pipes;
using System.Text;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Transport;

public sealed class PinOnePipeHost : IFrameSourceHost
{
    private readonly AppConfig _config;
    private readonly byte[] _outputs;
    private ulong _outputSequence;
    private CancellationTokenSource? _cts;
    private Task? _runTask;

    public PinOnePipeHost(AppConfig config)
    {
        _config = config;
        _outputs = new byte[Math.Max(1, config.PinOnePipe.OutputCount)];
    }

    public event EventHandler<FramePresentation>? FramePresented;

    public Task StartAsync()
    {
        if (_runTask is not null)
        {
            return Task.CompletedTask;
        }

        _cts = new CancellationTokenSource();
        _runTask = Task.Run(() => RunServerLoopAsync(_cts.Token), _cts.Token);
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
            // Expected shutdown behavior.
        }

        _runTask = null;
        _cts.Dispose();
        _cts = null;
    }

    private async Task RunServerLoopAsync(CancellationToken cancellationToken)
    {
        var pipeName = _config.PinOnePipe.PipeName;
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var serverStream = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            await serverStream.WaitForConnectionAsync(cancellationToken);
            await HandleClientAsync(serverStream, cancellationToken);
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream stream, CancellationToken cancellationToken)
    {
        var requestBuffer = new byte[2048];
        while (stream.IsConnected && !cancellationToken.IsCancellationRequested)
        {
            var bytesRead = await stream.ReadAsync(requestBuffer.AsMemory(0, requestBuffer.Length), cancellationToken);
            if (bytesRead <= 0)
            {
                break;
            }

            var request = Encoding.UTF8.GetString(requestBuffer, 0, bytesRead).Trim('\0', '\r', '\n', ' ');
            if (request.StartsWith("WRITE ", StringComparison.OrdinalIgnoreCase))
            {
                var payload = request.Substring("WRITE ".Length);
                try
                {
                    var message = Convert.FromBase64String(payload);
                    ProcessWriteMessage(message);
                    await WriteResponseAsync(stream, "OK", cancellationToken);
                }
                catch
                {
                    await WriteResponseAsync(stream, "ERR", cancellationToken);
                }
            }
            else if (request.StartsWith("CHECK", StringComparison.OrdinalIgnoreCase))
            {
                await WriteResponseAsync(stream, "TRUE", cancellationToken);
            }
            else if (request.StartsWith("COMPORT", StringComparison.OrdinalIgnoreCase))
            {
                await WriteResponseAsync(stream, _config.PinOnePipe.ComPortLabel, cancellationToken);
            }
            else if (request.StartsWith("DISCONNECT", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            else if (request.StartsWith("CONNECT", StringComparison.OrdinalIgnoreCase))
            {
                await WriteResponseAsync(stream, "OK", cancellationToken);
            }
            else
            {
                await WriteResponseAsync(stream, "OK", cancellationToken);
            }
        }
    }

    private static Task WriteResponseAsync(NamedPipeServerStream stream, string response, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(response);
        return stream.WriteAsync(bytes.AsMemory(0, bytes.Length), cancellationToken).AsTask();
    }

    private void ProcessWriteMessage(byte[] message)
    {
        if (message.Length < 3 || message[0] != 0)
        {
            return;
        }

        var bank = message[1] - 200;
        if (bank is < 0 or > 10)
        {
            return;
        }

        for (var i = 2; i < message.Length; i++)
        {
            var outputIndex = (bank * 7) + (i - 2);
            if (outputIndex < 0 || outputIndex >= _outputs.Length)
            {
                continue;
            }

            _outputs[outputIndex] = message[i];
        }

        var frame = BuildPresentation();
        FramePresented?.Invoke(this, frame);
    }

    private FramePresentation BuildPresentation()
    {
        var totalLeds = Math.Max(1, _config.Matrix.Width * _config.Matrix.Height);
        var rgb = new byte[totalLeds * 3];
        for (var i = 0; i < _outputs.Length && i < totalLeds; i++)
        {
            var value = _outputs[i];
            var offset = i * 3;
            rgb[offset] = value;
            rgb[offset + 1] = value;
            rgb[offset + 2] = value;
        }

        _outputSequence++;
        return new FramePresentation(
            rgb,
            HighestLedWritten: Math.Min(_outputs.Length, totalLeds),
            LedsPerChannel: Math.Min(_outputs.Length, totalLeds),
            OutputSequence: _outputSequence,
            PresentedAtUtc: DateTimeOffset.UtcNow);
    }
}
