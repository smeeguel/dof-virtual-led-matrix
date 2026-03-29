using System.IO.Ports;
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

                foreach (var frame in result.PresentedFrames)
                {
                    if (_config.Matrix.InstantTrigger)
                    {
                        OnFramePresented(frame);
                    }
                }
            }
            catch (TimeoutException)
            {
                // Normal idle behavior.
            }
        }
    }

    private void OnFramePresented(FramePresentation frame)
    {
        FramePresented?.Invoke(this, frame);
    }
}
