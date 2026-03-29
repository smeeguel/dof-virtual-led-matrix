using System.IO.Ports;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Serial;

public sealed class SerialEmulatorHost
{
    private readonly AppConfig _config;
    private readonly TeensyProtocolEngine _engine;
    private readonly FrameBuffer _frameBuffer;

    private CancellationTokenSource? _cts;
    private Task? _runTask;

    public SerialEmulatorHost(AppConfig config)
    {
        _config = config;
        _frameBuffer = new FrameBuffer();
        _engine = new TeensyProtocolEngine(
            new SerialEmulatorSettings
            {
                MaxLedsPerChannel = config.Serial.MaxLedsPerChannel,
                MaxStrips = config.Serial.MaxStrips,
            },
            _frameBuffer);
    }

    public Task StartAsync()
    {
        if (_runTask is not null)
        {
            return Task.CompletedTask;
        }

        _cts = new CancellationTokenSource();
        _runTask = Task.Run(() => RunLoop(_cts.Token), _cts.Token);
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

    private void RunLoop(CancellationToken cancellationToken)
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

                if (_config.Matrix.InstantTrigger && result.ResponseBytes.Length > 0)
                {
                    // MS2 planning decision: trigger immediate UI refresh path once renderer is hooked.
                    // Keeping this branch here documents behavior and keeps config contract active.
                }
            }
            catch (TimeoutException)
            {
                // Normal idle behavior.
            }
        }
    }
}
