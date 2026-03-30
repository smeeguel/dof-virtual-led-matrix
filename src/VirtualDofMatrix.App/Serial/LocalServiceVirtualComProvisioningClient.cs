using System.Net.Http;
using System.Net.Http.Json;

namespace VirtualDofMatrix.App.Serial;

public sealed class LocalServiceVirtualComProvisioningClient : IVirtualComProvisioningClient
{
    private readonly HttpClient _httpClient;

    public LocalServiceVirtualComProvisioningClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task CreatePairAsync(string txPort, string rxPort, CancellationToken cancellationToken = default)
    {
        var payload = new VirtualComPairRequest(txPort, rxPort);
        using var response = await _httpClient.PostAsJsonAsync("pairs", payload, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task DeletePairAsync(string txPort, string rxPort, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"pairs/{txPort}/{rxPort}");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<VirtualComPairInfo>> ListPairsAsync(CancellationToken cancellationToken = default)
    {
        var pairs = await _httpClient.GetFromJsonAsync<List<VirtualComPairInfo>>("pairs", cancellationToken);
        return pairs ?? [];
    }

    public async Task<VirtualComHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        var health = await _httpClient.GetFromJsonAsync<VirtualComHealth>("health", cancellationToken);
        return health ?? new VirtualComHealth(false, "Service returned an empty health response.");
    }

    private sealed record VirtualComPairRequest(string TxPort, string RxPort);
}
