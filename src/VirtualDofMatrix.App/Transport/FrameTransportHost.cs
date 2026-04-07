using System.IO;
using System.IO.Pipes;
using VirtualDofMatrix.App.Logging;
using VirtualDofMatrix.Core;
using VirtualDofMatrix.Core.Toys;

namespace VirtualDofMatrix.App.Transport;

// Overview: FrameTransportHost listens for named-pipe payload frames, routes them, then publishes per-toy frames to adapters.
public sealed class FrameTransportHost
{
    private readonly AppConfig _config;
    private readonly IToyRouter _toyRouter;
    private readonly IRoutingPlanProvider _routingPlanProvider;
    private readonly IReadOnlyList<IOutputAdapter> _outputAdapters;
    private readonly HashSet<string> _loggedFirstSuccessfulRoutes = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _isActive = true;
    private ulong _connectionEpoch;

    private CancellationTokenSource? _cts;
    private Task? _runTask;

    public FrameTransportHost(
        AppConfig config,
        IToyRouter toyRouter,
        IRoutingPlanProvider routingPlanProvider,
        IReadOnlyList<IOutputAdapter> outputAdapters)
    {
        _config = config;
        _toyRouter = toyRouter;
        _routingPlanProvider = routingPlanProvider;
        _outputAdapters = outputAdapters;
    }

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
            // Conversational note: DOF table relaunches create a new pipe client and restart frame sequence values.
            // We bump a per-connection epoch so routed InputSequence stays monotonic across reconnects.
            _connectionEpoch++;

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

                    var version = header[4];
                    if (version != 1)
                    {
                        throw new InvalidDataException($"Unsupported named-pipe frame version {version}.");
                    }

                    var rawSequence = unchecked((uint)BitConverter.ToInt32(header, 5));
                    var payloadLength = BitConverter.ToInt32(header, 9);
                    if (payloadLength < 0 || payloadLength > 8 * 1100 * 3)
                    {
                        throw new InvalidDataException($"Invalid payload length {payloadLength}.");
                    }

                    var payload = payloadLength == 0
                        ? Array.Empty<byte>()
                        : await ReadExactlyAsync(pipe, payloadLength, cancellationToken);

                    var sequence = (_connectionEpoch << 32) | rawSequence;
                    if (_config.Debug.LogProtocol && _config.Debug.LogFrames)
                    {
                        AppLogger.Info($"Pipe frame seq={sequence}, payload={payloadLength} bytes.");
                    }

                    if (!_isActive)
                    {
                        continue;
                    }

                    RouteAndPublish(payload, sequence, version);
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

    private void RouteAndPublish(byte[] payload, ulong sequence, byte version)
    {
        var context = new RoutingFrameContext(
            InputSequence: sequence,
            PayloadLength: payload.Length,
            PayloadKind: "NamedPipe.VDMF",
            ReceivedAtUtc: DateTimeOffset.UtcNow,
            PresentedAtUtc: null,
            SourceStripIndex: null,
            SourceOffset: null,
            SchemaVersion: $"vdmf-{version}");

        var plan = _routingPlanProvider.GetActiveToyDefinitions();
        var definitionsById = plan.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        var result = _toyRouter.Route(payload, context, plan);

        foreach (var diagnostic in result.Diagnostics)
        {
            if (_config.Debug.LogProtocol
                && diagnostic.PolicyAction == ToyRoutingPolicyAction.None
                && diagnostic.MappedLedCount > 0
                && diagnostic.MissingBytes == 0
                && _loggedFirstSuccessfulRoutes.Add(diagnostic.ToyId))
            {
                // Conversational note: this keeps route logs actionable by emitting only each toy's first clean success line.
                AppLogger.Info($"[route] toy={diagnostic.ToyId} mapped={diagnostic.MappedLedCount} missingBytes={diagnostic.MissingBytes} action={diagnostic.PolicyAction} msg={diagnostic.Message}");
            }
        }

        foreach (var frame in result.Frames)
        {
            if (!definitionsById.TryGetValue(frame.ToyId, out var toyDefinition))
            {
                continue;
            }

            // Conversational note: adapters are opt-in per toy so one toy can feed viewer while another streams elsewhere.
            foreach (var adapter in _outputAdapters)
            {
                if (!toyDefinition.OutputTargets.Contains(adapter.Name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                adapter.Write(frame, context);
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
}
