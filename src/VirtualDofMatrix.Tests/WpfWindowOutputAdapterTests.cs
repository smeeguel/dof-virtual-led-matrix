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

        var hostToyId = WpfWindowOutputAdapter.ResolveMainHostToyId(currentHostToyId: null, toys, "viewer");

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

        var hostToyId = WpfWindowOutputAdapter.ResolveMainHostToyId(currentHostToyId: null, toys, "viewer");

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

        var hostToyId = WpfWindowOutputAdapter.ResolveMainHostToyId(currentHostToyId: null, toys, "viewer");

        Assert.Null(hostToyId);
    }

    [Fact]
    public void ResolveMainHostToyId_KeepsCurrentHostWhenStillEnabledForViewer()
    {
        var toys = new List<ToyRouteConfig>
        {
            BuildToy("strip-topper", enabled: true, adapter: "viewer", targetEnabled: true),
            BuildToy("matrix-main", enabled: true, adapter: "viewer", targetEnabled: true),
        };

        var hostToyId = WpfWindowOutputAdapter.ResolveMainHostToyId("matrix-main", toys, "viewer");

        Assert.Equal("matrix-main", hostToyId);
    }

    [Fact]
    public void ResolveMainHostToyId_FailsOverWhenCurrentHostIsDisabled()
    {
        var toys = new List<ToyRouteConfig>
        {
            BuildToy("strip-topper", enabled: true, adapter: "viewer", targetEnabled: true),
            BuildToy("matrix-main", enabled: false, adapter: "viewer", targetEnabled: true),
        };

        var hostToyId = WpfWindowOutputAdapter.ResolveMainHostToyId("matrix-main", toys, "viewer");

        Assert.Equal("matrix-main", hostToyId);
    }

    [Fact]
    public void ResolveMainHostToyId_FailsOverWhenCurrentHostNoLongerSupportsViewerAdapter()
    {
        var toys = new List<ToyRouteConfig>
        {
            BuildToy("strip-topper", enabled: true, adapter: "viewer", targetEnabled: true),
            BuildToy("matrix-main", enabled: false, adapter: "broadcast", targetEnabled: true),
        };

        var hostToyId = WpfWindowOutputAdapter.ResolveMainHostToyId("matrix-main", toys, "viewer");

        Assert.Equal("strip-topper", hostToyId);
    }

    [Fact]
    public void ComputeOverlayState_ShowsNameWhenHoveredWithoutSelecting()
    {
        var state = WpfWindowOutputAdapter.ComputeOverlayState(
            toyId: "matrix-main",
            selectedToyId: null,
            hoveredToyId: "matrix-main",
            isLayoutEditModeEnabled: false);

        Assert.False(state.IsSelected);
        Assert.True(state.IsHovered);
        Assert.True(state.ShowNameOverlay);
    }

    [Fact]
    public void ComputeOverlayState_SelectionRemainsLockedWhenHoveringDifferentToy()
    {
        var selectedToyState = WpfWindowOutputAdapter.ComputeOverlayState(
            toyId: "matrix-main",
            selectedToyId: "matrix-main",
            hoveredToyId: "strip-topper",
            isLayoutEditModeEnabled: true);
        var hoveredToyState = WpfWindowOutputAdapter.ComputeOverlayState(
            toyId: "strip-topper",
            selectedToyId: "matrix-main",
            hoveredToyId: "strip-topper",
            isLayoutEditModeEnabled: true);

        Assert.True(selectedToyState.IsSelected);
        Assert.False(selectedToyState.IsHovered);
        Assert.True(selectedToyState.ShowNameOverlay);
        Assert.False(hoveredToyState.IsSelected);
        Assert.True(hoveredToyState.IsHovered);
        Assert.True(hoveredToyState.ShowNameOverlay);
    }

    [Fact]
    public void ComputeOverlayState_HidesNameWhenSettingsClosedAndToyNotHovered()
    {
        var state = WpfWindowOutputAdapter.ComputeOverlayState(
            toyId: "matrix-main",
            selectedToyId: "matrix-main",
            hoveredToyId: null,
            isLayoutEditModeEnabled: false);

        Assert.True(state.IsSelected);
        Assert.False(state.IsHovered);
        Assert.False(state.ShowNameOverlay);
    }

    [Fact]
    public void ComputeOverlayState_HidesHoverNameWhenDifferentToyIsLockSelected()
    {
        var hoveredToyState = WpfWindowOutputAdapter.ComputeOverlayState(
            toyId: "strip-topper",
            selectedToyId: "matrix-main",
            hoveredToyId: "strip-topper",
            isLayoutEditModeEnabled: false);

        Assert.False(hoveredToyState.IsSelected);
        Assert.True(hoveredToyState.IsHovered);
        Assert.False(hoveredToyState.ShowNameOverlay);
    }

    [Fact]
    public void ComputeSelectionBorderState_ShowsHoverBorderWhenNoToyIsLockSelected()
    {
        var showSelectionBorder = WpfWindowOutputAdapter.ComputeSelectionBorderState(
            isSelected: false,
            isHovered: true,
            selectedToyId: null);

        Assert.True(showSelectionBorder);
    }

    [Fact]
    public void ComputeSelectionBorderState_HidesHoverBorderWhenAnotherToyIsLockSelected()
    {
        var showSelectionBorder = WpfWindowOutputAdapter.ComputeSelectionBorderState(
            isSelected: false,
            isHovered: true,
            selectedToyId: "matrix-main");

        Assert.False(showSelectionBorder);
    }

    [Fact]
    public void ReduceHoverState_MouseLeaveDifferentToy_DoesNotClearHoverOrSelectionInputs()
    {
        var hoveredToyId = WpfWindowOutputAdapter.ReduceHoverState(
            currentHoveredToyId: "matrix-main",
            toyId: "strip-topper",
            isMouseEnter: false);

        Assert.Equal("matrix-main", hoveredToyId);
    }

    [Fact]
    public void ReduceHoverState_MouseEnterAndMatchingLeave_TracksTransientHoverLifecycle()
    {
        var hoveredOnEnter = WpfWindowOutputAdapter.ReduceHoverState(
            currentHoveredToyId: null,
            toyId: "matrix-main",
            isMouseEnter: true);
        var hoveredAfterLeave = WpfWindowOutputAdapter.ReduceHoverState(
            currentHoveredToyId: hoveredOnEnter,
            toyId: "matrix-main",
            isMouseEnter: false);

        Assert.Equal("matrix-main", hoveredOnEnter);
        Assert.Null(hoveredAfterLeave);
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
