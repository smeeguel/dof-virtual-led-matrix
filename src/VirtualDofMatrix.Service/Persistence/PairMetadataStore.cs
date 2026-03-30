using System.Text.Json;
using VirtualDofMatrix.Service.Api;

namespace VirtualDofMatrix.Service.Persistence;

public sealed class PairMetadataStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "VirtualDofMatrix");

    private static readonly string FilePath = Path.Combine(DirectoryPath, "pairs.json");

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<PairMetadata>> GetPairsAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadUnsafeAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertPairAsync(PairMetadata pair, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var pairs = await ReadUnsafeAsync(cancellationToken);
            var updated = pairs.Where(x => !SamePair(x, pair.TxPort, pair.RxPort)).ToList();
            updated.Add(pair);
            await WriteUnsafeAsync(updated, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeletePairAsync(string txPort, string rxPort, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var pairs = await ReadUnsafeAsync(cancellationToken);
            var updated = pairs.Where(x => !SamePair(x, txPort, rxPort)).ToList();
            await WriteUnsafeAsync(updated, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<List<PairMetadata>> ReadUnsafeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(FilePath))
        {
            return new List<PairMetadata>();
        }

        await using var stream = File.OpenRead(FilePath);
        var pairs = await JsonSerializer.DeserializeAsync<List<PairMetadata>>(stream, JsonOptions, cancellationToken);
        return pairs ?? new List<PairMetadata>();
    }

    private static async Task WriteUnsafeAsync(List<PairMetadata> pairs, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(DirectoryPath);

        await using var stream = File.Create(FilePath);
        await JsonSerializer.SerializeAsync(stream, pairs, JsonOptions, cancellationToken);
    }

    private static bool SamePair(PairMetadata pair, string txPort, string rxPort)
        => pair.TxPort.Equals(txPort, StringComparison.OrdinalIgnoreCase)
           && pair.RxPort.Equals(rxPort, StringComparison.OrdinalIgnoreCase);
}
