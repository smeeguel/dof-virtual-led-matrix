using VirtualDofMatrix.App.Presentation;
using VirtualDofMatrix.Core;
using Xunit;

namespace VirtualDofMatrix.Tests;

public sealed class WpfWindowOutputAdapterTests
{
    [Fact]
    public void ResolveMainHostToyId_ReturnsFirstEnabledToyForViewerAdapter()
    {
        var toys = new List<ToyRouteConfig>
        {
            BuildToy("matrix-main", enabled: true, adapter: "viewer", targetEnabled: true),
            BuildToy("strip-topper", enabled: true, adapter: "viewer", targetEnabled: true),
        };

        var hostToyId = WpfWindowOutputAdapter.ResolveMainHostToyId(toys, "viewer");

        Assert.Equal("matrix-main", hostToyId);
    }

    [Fact]
    public void ResolveMainHostToyId_SkipsDisabledToyAndPromotesNextEnabledViewerToy()
    {
        var toys = new List<ToyRouteConfig>
        {
            BuildToy("matrix-main", enabled: false, adapter: "viewer", targetEnabled: true),
            BuildToy("strip-topper", enabled: true, adapter: "viewer", targetEnabled: true),
        };

        var hostToyId = WpfWindowOutputAdapter.ResolveMainHostToyId(toys, "viewer");

        Assert.Equal("strip-topper", hostToyId);
    }

    [Fact]
    public void ResolveMainHostToyId_ReturnsNullWhenNoToyEnabledForAdapter()
    {
        var toys = new List<ToyRouteConfig>
        {
            BuildToy("matrix-main", enabled: false, adapter: "viewer", targetEnabled: true),
            BuildToy("strip-topper", enabled: true, adapter: "broadcast", targetEnabled: true),
        };

        var hostToyId = WpfWindowOutputAdapter.ResolveMainHostToyId(toys, "viewer");

        Assert.Null(hostToyId);
    }

    private static ToyRouteConfig BuildToy(string id, bool enabled, string adapter, bool targetEnabled)
    {
        return new ToyRouteConfig
        {
            Id = id,
            Name = id,
            Enabled = enabled,
            OutputTargets =
            [
                new ToyAdapterTargetConfig
                {
                    Adapter = adapter,
                    Enabled = targetEnabled,
                },
            ],
        };
    }
}
