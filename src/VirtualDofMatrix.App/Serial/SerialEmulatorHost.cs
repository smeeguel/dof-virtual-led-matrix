using System.IO;
using System.IO.Ports;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Serial;

public sealed class SerialEmulatorHost
{
    private static readonly TimeSpan PortOpenRetryDelay = TimeSpan.FromSeconds(1);

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

    private async Task RunLoop(CancellationToken cancellationToken)
    {
        var readBuffer = new byte[8192];

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var serialPort = CreateSerialPort();
                serialPort.Open();

                Console.WriteLine($"[Serial] Opened {_config.Serial.PortName} at {_config.Serial.BaudRate} baud.");

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
                    catch (IOException ex)
                    {
                        Console.WriteLine($"[Serial] I/O error on {_config.Serial.PortName}: {ex.Message}. Reopening port.");
                        break;
                    }
                    catch (InvalidOperationException ex)
                    {
                        Console.WriteLine($"[Serial] Port {_config.Serial.PortName} became unavailable: {ex.Message}. Reopening port.");
                        break;
                    }
                }
            }
            catch (FileNotFoundException ex)
            {
                Console.WriteLine($"[Serial] Port {_config.Serial.PortName} not found: {ex.Message}. Retrying in {PortOpenRetryDelay.TotalSeconds:0}s.");
                await Task.Delay(PortOpenRetryDelay, cancellationToken);
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.WriteLine($"[Serial] Access denied for {_config.Serial.PortName}: {ex.Message}. Retrying in {PortOpenRetryDelay.TotalSeconds:0}s.");
                await Task.Delay(PortOpenRetryDelay, cancellationToken);
            }
            catch (IOException ex)
            {
                Console.WriteLine($"[Serial] Failed opening {_config.Serial.PortName}: {ex.Message}. Retrying in {PortOpenRetryDelay.TotalSeconds:0}s.");
                await Task.Delay(PortOpenRetryDelay, cancellationToken);
            }
        }
    }

    private SerialPort CreateSerialPort()
    {
        var serialConfig = _config.Serial;

        return new SerialPort(serialConfig.PortName, serialConfig.BaudRate)
        {
            ReadTimeout = serialConfig.ReadTimeoutMs,
            WriteTimeout = serialConfig.WriteTimeoutMs,
            DtrEnable = serialConfig.DtrEnable,
        };
    }

    private void OnFramePresented(FramePresentation frame)
    {
        FramePresented?.Invoke(this, frame);
    }
}
