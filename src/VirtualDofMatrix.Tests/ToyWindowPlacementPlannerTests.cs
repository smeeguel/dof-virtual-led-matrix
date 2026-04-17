using VirtualDofMatrix.App.Configuration;
using VirtualDofMatrix.Core;
using Xunit;

namespace VirtualDofMatrix.Tests;

public sealed class ToyWindowPlacementPlannerTests
{
    [Fact]
    public void AssignMissingWindowPositions_HorizontalMajorToysStackTopWithStableLeft()
    {
        var toys = new List<ToyRouteConfig>
        {
            BuildToy("h1", width: 64, height: 16),
            BuildToy("h2", width: 32, height: 8),
            BuildToy("h3", width: 128, height: 32),
        };

        ToyWindowPlacementPlanner.AssignMissingWindowPositions(toys, BuildGlobalWindow(), placementGap: 10);

        Assert.All(toys, toy => Assert.Equal(100, toy.Window.Left));
        Assert.Equal(200, toys[0].Window.Top);
        Assert.Equal(260, toys[1].Window.Top); // 200 + 50 + 10
        Assert.Equal(320, toys[2].Window.Top); // 260 + 50 + 10
    }

    [Fact]
    public void AssignMissingWindowPositions_VerticalMajorToysStackLeftWithStableTop()
    {
        var toys = new List<ToyRouteConfig>
        {
            BuildToy("v1", width: 8, height: 32),
            BuildToy("v2", width: 4, height: 16),
            BuildToy("v3", width: 1, height: 10),
        };

        ToyWindowPlacementPlanner.AssignMissingWindowPositions(toys, BuildGlobalWindow(), placementGap: 10);

        Assert.All(toys, toy => Assert.Equal(200, toy.Window.Top));
        Assert.Equal(100, toys[0].Window.Left);
        Assert.Equal(190, toys[1].Window.Left); // 100 + 80 + 10
        Assert.Equal(280, toys[2].Window.Left); // 190 + 80 + 10
    }

    [Fact]
    public void AssignMissingWindowPositions_DoesNotOverwriteFullyExplicitCoordinates()
    {
        var explicitToy = BuildToy("explicit", width: 32, height: 8);
        explicitToy.Window.Left = 640;
        explicitToy.Window.Top = 360;

        var unpositionedToy = BuildToy("auto", width: 32, height: 8);
        var toys = new List<ToyRouteConfig> { explicitToy, unpositionedToy };

        ToyWindowPlacementPlanner.AssignMissingWindowPositions(toys, BuildGlobalWindow(), placementGap: 12);

        Assert.Equal(640, explicitToy.Window.Left);
        Assert.Equal(360, explicitToy.Window.Top);
        Assert.Equal(100, unpositionedToy.Window.Left);
        Assert.Equal(200, unpositionedToy.Window.Top);
    }

    [Fact]
    public void AssignMissingWindowPositions_IsDeterministicForSameInputOrder()
    {
        var firstRun = new List<ToyRouteConfig>
        {
            BuildToy("a", width: 32, height: 8),
            BuildToy("b", width: 8, height: 32),
            BuildToy("c", width: 64, height: 16),
        };

        var secondRun = firstRun.Select(CloneToy).ToList();

        ToyWindowPlacementPlanner.AssignMissingWindowPositions(firstRun, BuildGlobalWindow(), placementGap: 20);
        ToyWindowPlacementPlanner.AssignMissingWindowPositions(secondRun, BuildGlobalWindow(), placementGap: 20);

        for (var i = 0; i < firstRun.Count; i++)
        {
            Assert.Equal(firstRun[i].Window.Left, secondRun[i].Window.Left);
            Assert.Equal(firstRun[i].Window.Top, secondRun[i].Window.Top);
        }
    }

    private static ToyRouteConfig BuildToy(string id, int width, int height)
    {
        return new ToyRouteConfig
        {
            Id = id,
            Name = id,
            Mapping = new ToyMappingConfig { Width = width, Height = height, Mode = "TopDownAlternateRightLeft" },
            Window = new ToyWindowOptionsConfig { Width = 80, Height = 50 },
            Source = new ToySourceConfig { Length = Math.Max(1, width * height) },
        };
    }

    private static WindowConfig BuildGlobalWindow() => new()
    {
        Left = 100,
        Top = 200,
        Width = 320,
        Height = 120,
    };

    private static ToyRouteConfig CloneToy(ToyRouteConfig toy)
    {
        return new ToyRouteConfig
        {
            Id = toy.Id,
            Name = toy.Name,
            Kind = toy.Kind,
            Mapping = new ToyMappingConfig
            {
                Width = toy.Mapping.Width,
                Height = toy.Mapping.Height,
                Mode = toy.Mapping.Mode,
            },
            Source = new ToySourceConfig
            {
                CanonicalStart = toy.Source.CanonicalStart,
                Length = toy.Source.Length,
                StripIndex = toy.Source.StripIndex,
                StripOffset = toy.Source.StripOffset,
            },
            Window = new ToyWindowOptionsConfig
            {
                Left = toy.Window.Left,
                Top = toy.Window.Top,
                Width = toy.Window.Width,
                Height = toy.Window.Height,
                AlwaysOnTop = toy.Window.AlwaysOnTop,
                BackgroundColor = toy.Window.BackgroundColor,
                BackgroundVisible = toy.Window.BackgroundVisible,
                Borderless = toy.Window.Borderless,
                LockAspectRatio = toy.Window.LockAspectRatio,
                UseGlobalWindow = toy.Window.UseGlobalWindow,
            },
        };
    }
}
