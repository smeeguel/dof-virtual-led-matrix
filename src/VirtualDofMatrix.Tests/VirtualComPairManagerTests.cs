using Xunit;
using VirtualDofMatrix.App.Serial;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.Tests;

public sealed class VirtualComPairManagerTests
{
    [Fact]
    public async Task EnsureConfiguredPairAsync_CreatesPair_WhenHealthyAndMissing()
    {
        var config = CreateConfig("service", disableFallback: true);
        var service = new FakeBackend();
        var manager = new VirtualComPairManager(config, service, new FakeBackend());

        await manager.EnsureConfiguredPairAsync();

        Assert.Equal(1, service.CreateCount);
        Assert.Equal(("COM1", "COM2"), service.Created.Single());
    }

    [Fact]
    public async Task EnsureConfiguredPairAsync_ReusesPersistedPair_OnRestart()
    {
        var config = CreateConfig("service", disableFallback: true);
        var service = new FakeBackend
        {
            Pairs = new List<VirtualComPairInfo> { new("COM1", "COM2") },
        };

        var manager = new VirtualComPairManager(config, service, new FakeBackend());
        await manager.EnsureConfiguredPairAsync();

        Assert.Equal(0, service.CreateCount);
    }

    [Fact]
    public async Task EnsureConfiguredPairAsync_FallsBackToProcessBackend_WhenServiceUnavailableAndFallbackEnabled()
    {
        var config = CreateConfig("service", disableFallback: false);
        var service = new FakeBackend { ThrowOnCreate = new InvalidOperationException("service unavailable") };
        var process = new FakeBackend();

        var manager = new VirtualComPairManager(config, service, process);
        await manager.EnsureConfiguredPairAsync();

        Assert.Equal(1, process.CreateCount);
    }

    [Fact]
    public async Task EnsureConfiguredPairAsync_Throws_WhenServiceUnavailableAndFallbackDisabled()
    {
        var config = CreateConfig("service", disableFallback: true);
        var service = new FakeBackend { ThrowOnCreate = new InvalidOperationException("service unavailable") };
        var manager = new VirtualComPairManager(config, service, new FakeBackend());

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.EnsureConfiguredPairAsync());
    }

    [Theory]
    [InlineData("driver missing")]
    [InlineData("signature blocked")]
    public async Task EnsureConfiguredPairAsync_Throws_WhenBackendHealthIsUnhealthy(string message)
    {
        var config = CreateConfig("service", disableFallback: true);
        var service = new FakeBackend
        {
            Health = new VirtualComHealth(false, message),
        };

        var manager = new VirtualComPairManager(config, service, new FakeBackend());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.EnsureConfiguredPairAsync());
        Assert.Contains(message, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static AppConfig CreateConfig(string mode, bool disableFallback)
        => new()
        {
            Serial =
            {
                VirtualProviderMode = mode,
            },
            VirtualCom =
            {
                Enabled = true,
                TxPortName = "COM1",
                RxPortName = "COM2",
                DisableFallbackToProcessCommand = disableFallback,
            },
        };

    private sealed class FakeBackend : IVirtualComPairBackend
    {
        public int CreateCount { get; private set; }
        public List<(string Tx, string Rx)> Created { get; } = new();
        public IReadOnlyList<VirtualComPairInfo> Pairs { get; init; } = new List<VirtualComPairInfo>();
        public VirtualComHealth Health { get; init; } = new(true, "ok");
        public Exception? ThrowOnCreate { get; init; }

        public Task CreatePairAsync(string txPort, string rxPort, CancellationToken cancellationToken = default)
        {
            if (ThrowOnCreate is not null)
            {
                throw ThrowOnCreate;
            }
            CreateCount++;
            Created.Add((txPort, rxPort));
            return Task.CompletedTask;
        }

        public Task DeletePairAsync(string txPort, string rxPort, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<VirtualComPairInfo>> ListPairsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Pairs);

        public Task<VirtualComHealth> GetHealthAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Health);
    }
}
