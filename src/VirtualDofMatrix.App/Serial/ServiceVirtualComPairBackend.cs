using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Serial;

public sealed class ServiceVirtualComPairBackend : IVirtualComPairBackend
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly VirtualComProvisioningConfig _config;

    public ServiceVirtualComPairBackend(VirtualComProvisioningConfig config)
    {
        _config = config;
    }

    public async Task CreatePairAsync(string txPort, string rxPort, CancellationToken cancellationToken = default)
    {
        var response = await SendRequestAsync(new PipeRequest("create", txPort, rxPort), cancellationToken);
        EnsureSuccess(response, "create pair");
    }

    public async Task DeletePairAsync(string txPort, string rxPort, CancellationToken cancellationToken = default)
    {
        var response = await SendRequestAsync(new PipeRequest("delete", txPort, rxPort), cancellationToken);
        EnsureSuccess(response, "delete pair");
    }

    public async Task<IReadOnlyList<VirtualComPairInfo>> ListPairsAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendRequestAsync(new PipeRequest("list", null, null), cancellationToken);
        EnsureSuccess(response, "list pairs");

        if (response.Data.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return new List<VirtualComPairInfo>();
        }

        var pairs = response.Data.Deserialize<List<ServicePairMetadata>>(JsonOptions) ?? new List<ServicePairMetadata>();
        return pairs
            .Select(p => new VirtualComPairInfo(p.TxPort, p.RxPort, Driver: "service", Status: "active"))
            .ToList();
    }

    public async Task<VirtualComHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendRequestAsync(new PipeRequest("health", null, null), cancellationToken);
        if (!response.Success)
        {
            return new VirtualComHealth(false, $"{response.ErrorCode}: {response.Message}");
        }

        var health = response.Data.Deserialize<ServiceHealthInfo>(JsonOptions);
        return new VirtualComHealth(health?.DriverReachable == true, health is null ? "missing health payload" : $"service {health.Version}");
    }

    private async Task<ServiceResultEnvelope> SendRequestAsync(PipeRequest request, CancellationToken cancellationToken)
    {
        using var client = new NamedPipeClientStream(
            serverName: ".",
            pipeName: _config.ServicePipeName,
            direction: PipeDirection.InOut,
            options: PipeOptions.Asynchronous);

        await client.ConnectAsync(_config.ServiceConnectTimeoutMs, cancellationToken);

        using var writer = new StreamWriter(client, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);

        var payload = JsonSerializer.Serialize(request, JsonOptions);
        await writer.WriteLineAsync(payload);

        var line = await reader.ReadLineAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(line))
        {
            throw new InvalidOperationException("Service returned an empty response.");
        }

        return JsonSerializer.Deserialize<ServiceResultEnvelope>(line, JsonOptions)
               ?? throw new InvalidOperationException("Service returned an invalid response payload.");
    }

    private static void EnsureSuccess(ServiceResultEnvelope response, string operation)
    {
        if (response.Success)
        {
            return;
        }

        throw new InvalidOperationException($"Service failed to {operation}: {response.ErrorCode} - {response.Message}");
    }

    private sealed record PipeRequest(string Command, string? TxPort, string? RxPort);

    private sealed record ServicePairMetadata(string TxPort, string RxPort, DateTimeOffset CreatedUtc);

    private sealed record ServiceHealthInfo(string Version, bool DriverReachable, int ActivePairCount, DateTimeOffset UtcNow);

    private sealed record ServiceResultEnvelope(bool Success, JsonElement Data, JsonElement ErrorCode, string Message);
}
