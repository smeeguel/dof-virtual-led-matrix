using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using VirtualDofMatrix.App.Logging;
using VirtualDofMatrix.Core;
using VirtualDofMatrix.Core.Toys;

namespace VirtualDofMatrix.App.Transport;

// Overview: multi-client named-pipe broadcaster for toy frames with versioned handshake and per-client backpressure.
public sealed class NamedPipeBroadcastAdapter : IOutputAdapter, IDisposable
{
    private const int ProtocolVersion = 1;
    private const int MinSupportedVersion = 1;

    private readonly string _pipeName;
    private readonly int _maxQueuePerClient;
    private readonly bool _logProtocol;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<Guid, ClientState> _clients = new();
    private readonly Task _acceptLoopTask;

    public NamedPipeBroadcastAdapter(AppConfig config)
    {
        _pipeName = string.IsNullOrWhiteSpace(config.Transport.BroadcastPipeName)
            ? "VirtualDofMatrix.Broadcast"
            : config.Transport.BroadcastPipeName;
        _maxQueuePerClient = Math.Max(1, config.Transport.BroadcastMaxQueuePerClient);
        _logProtocol = config.Debug.LogProtocol;

        _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public string Name => "pipe-broadcast";

    public void Write(ToyFrame frame, RoutingFrameContext context)
    {
        var envelope = new BroadcastFrameEnvelope(
            Type: "frame",
            ProtocolVersion,
            frame.ToyId,
            frame.FrameId,
            frame.TimestampUtc,
            frame.Width,
            frame.Height,
            Convert.ToBase64String(frame.PayloadBytes ?? EncodePixels(frame.Pixels ?? Array.Empty<Rgb24>())),
            context.InputSequence,
            context.SchemaVersion,
            FeatureFlags: ["transport-agnostic-envelope"]);

        var serialized = JsonSerializer.Serialize(envelope);

        foreach (var kvp in _clients)
        {
            var client = kvp.Value;
            if (!client.AcceptsToy(frame.ToyId))
            {
                continue;
            }

            client.EnqueueLatestWins(serialized);
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var server = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            try
            {
                await server.WaitForConnectionAsync(cancellationToken);
                _ = Task.Run(() => HandleClientAsync(server, cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                server.Dispose();
                break;
            }
            catch (Exception ex)
            {
                server.Dispose();
                if (_logProtocol)
                {
                    AppLogger.Warn($"[broadcast] accept loop warning: {ex.Message}");
                }
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream stream, CancellationToken cancellationToken)
    {
        var clientId = Guid.NewGuid();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = linkedCts.Token;

        try
        {
            var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

            var helloLine = await reader.ReadLineAsync(token);
            if (string.IsNullOrWhiteSpace(helloLine))
            {
                return;
            }

            var clientHello = JsonSerializer.Deserialize<ClientHelloEnvelope>(helloLine);
            if (clientHello is null || !IsVersionCompatible(clientHello.MinSupportedVersion, clientHello.ProtocolVersion))
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(new ServerHelloEnvelope(
                    Type: "hello-ack",
                    Accepted: false,
                    ProtocolVersion,
                    MinSupportedVersion,
                    FeatureFlags: ["toy-filters", "latest-wins-queue"],
                    Message: "Version mismatch.")));
                return;
            }

            var state = new ClientState(clientId, stream, writer, _maxQueuePerClient, clientHello.ToyFilters);
            _clients[clientId] = state;

            await writer.WriteLineAsync(JsonSerializer.Serialize(new ServerHelloEnvelope(
                Type: "hello-ack",
                Accepted: true,
                ProtocolVersion,
                MinSupportedVersion,
                FeatureFlags: ["toy-filters", "latest-wins-queue", "transport-agnostic-envelope"],
                Message: "Connected.")));

            if (_logProtocol)
            {
                AppLogger.Info($"[broadcast] client {clientId} connected on '{_pipeName}' filters={string.Join(',', clientHello.ToyFilters ?? Array.Empty<string>())}");
            }

            await state.RunWriterLoopAsync(token);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown/disconnect.
        }
        catch (Exception ex)
        {
            if (_logProtocol)
            {
                AppLogger.Warn($"[broadcast] client {clientId} disconnected: {ex.Message}");
            }
        }
        finally
        {
            _clients.TryRemove(clientId, out _);
            stream.Dispose();
        }
    }

    private static bool IsVersionCompatible(int clientMin, int clientMax)
    {
        var min = Math.Max(clientMin, MinSupportedVersion);
        var max = Math.Min(clientMax, ProtocolVersion);
        return min <= max;
    }

    private static byte[] EncodePixels(IReadOnlyList<Rgb24> pixels)
    {
        var bytes = ArrayPool<byte>.Shared.Rent(pixels.Count * 3);
        var targetLength = pixels.Count * 3;

        for (var i = 0; i < pixels.Count; i++)
        {
            var offset = i * 3;
            bytes[offset] = pixels[i].R;
            bytes[offset + 1] = pixels[i].G;
            bytes[offset + 2] = pixels[i].B;
        }

        var copy = new byte[targetLength];
        Buffer.BlockCopy(bytes, 0, copy, 0, targetLength);
        ArrayPool<byte>.Shared.Return(bytes);
        return copy;
    }

    public void Dispose()
    {
        _cts.Cancel();

        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }

        try
        {
            _acceptLoopTask.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Ignore shutdown races.
        }

        _cts.Dispose();
    }

    private sealed class ClientState : IDisposable
    {
        private readonly Guid _clientId;
        private readonly NamedPipeServerStream _stream;
        private readonly StreamWriter _writer;
        private readonly int _maxQueue;
        private readonly HashSet<string> _toyFilters;
        private readonly Queue<string> _queue = new();
        private readonly SemaphoreSlim _signal = new(0);
        private readonly object _gate = new();
        private bool _disposed;

        public ClientState(
            Guid clientId,
            NamedPipeServerStream stream,
            StreamWriter writer,
            int maxQueue,
            IReadOnlyList<string>? toyFilters)
        {
            _clientId = clientId;
            _stream = stream;
            _writer = writer;
            _maxQueue = maxQueue;
            _toyFilters = new HashSet<string>(toyFilters ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        }

        public bool AcceptsToy(string toyId)
            => _toyFilters.Count == 0 || _toyFilters.Contains(toyId);

        public void EnqueueLatestWins(string payload)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                // Note: latest-wins backpressure means we evict oldest entries when full.
                while (_queue.Count >= _maxQueue)
                {
                    _queue.Dequeue();
                }

                _queue.Enqueue(payload);
                _signal.Release();
            }
        }

        public async Task RunWriterLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && _stream.IsConnected)
            {
                await _signal.WaitAsync(cancellationToken);

                string? payload = null;
                lock (_gate)
                {
                    if (_queue.Count > 0)
                    {
                        payload = _queue.Dequeue();
                    }
                }

                if (payload is null)
                {
                    continue;
                }

                await _writer.WriteLineAsync(payload);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            _signal.Dispose();
            _writer.Dispose();
            _stream.Dispose();
        }
    }

    private sealed record ClientHelloEnvelope(
        string Type,
        int ProtocolVersion,
        int MinSupportedVersion,
        string[] FeatureFlags,
        string[] ToyFilters);

    private sealed record ServerHelloEnvelope(
        string Type,
        bool Accepted,
        int ProtocolVersion,
        int MinSupportedVersion,
        string[] FeatureFlags,
        string Message);

    private sealed record BroadcastFrameEnvelope(
        string Type,
        int ProtocolVersion,
        string ToyId,
        ulong FrameId,
        DateTimeOffset TimestampUtc,
        int Width,
        int Height,
        string PayloadBase64,
        ulong InputSequence,
        string SchemaVersion,
        string[] FeatureFlags);
}
