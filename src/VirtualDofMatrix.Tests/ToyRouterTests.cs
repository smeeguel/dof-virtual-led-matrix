using VirtualDofMatrix.Core;
using VirtualDofMatrix.Core.Toys;
using Xunit;

namespace VirtualDofMatrix.Tests;

public sealed class ToyRouterTests
{
    [Fact]
    public void Route_WhenOnFrameRateSpikeLatestWins_DropsOlderOrEqualSequences()
    {
        var router = new ToyRouter(new RoutingPolicyConfig { OnFrameRateSpike = "latest-wins" });
        var toys = CreateSingleToyDefinition();
        var payload = BuildSolidPayload(4, 0x55);

        _ = router.Route(payload, CreateContext(sequence: 10, payload.Length), toys);
        var stale = router.Route(payload, CreateContext(sequence: 10, payload.Length), toys);

        Assert.Empty(stale.Frames);
        Assert.Contains(stale.Diagnostics, d => d.PolicyAction == ToyRoutingPolicyAction.FrameDroppedLatestWins);
    }

    [Fact]
    public void Route_WhenOnFrameRateSpikeDropOldest_DropsOlderOrEqualSequences()
    {
        var router = new ToyRouter(new RoutingPolicyConfig { OnFrameRateSpike = "drop-oldest" });
        var toys = CreateSingleToyDefinition();
        var payload = BuildSolidPayload(4, 0x77);

        _ = router.Route(payload, CreateContext(sequence: 21, payload.Length), toys);
        var stale = router.Route(payload, CreateContext(sequence: 20, payload.Length), toys);

        Assert.Empty(stale.Frames);
        Assert.Contains(stale.Diagnostics, d => d.PolicyAction == ToyRoutingPolicyAction.FrameDroppedDropOldest);
    }

    private static IReadOnlyList<ToyDefinition> CreateSingleToyDefinition()
    {
        return
        [
            new ToyDefinition(
                Id: "backglass-main",
                Enabled: true,
                Kind: "matrix",
                Width: 2,
                Height: 2,
                Mapping: "RowMajor",
                SourceDescriptor: new ToySourceRange(CanonicalStart: 0, Length: 4, SourceStripIndex: 0, SourceOffset: 0),
                OutputTargets: ["viewer"]),
        ];
    }

    private static RoutingFrameContext CreateContext(ulong sequence, int payloadLength)
        => new(sequence, payloadLength, "rgb24", DateTimeOffset.UtcNow);

    private static byte[] BuildSolidPayload(int ledCount, byte value)
        => Enumerable.Repeat(value, ledCount * 3).ToArray();
}
