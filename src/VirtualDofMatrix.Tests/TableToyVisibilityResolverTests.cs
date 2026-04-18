using VirtualDofMatrix.App.Configuration;
using VirtualDofMatrix.Core;
using Xunit;

namespace VirtualDofMatrix.Tests;

public sealed class TableToyVisibilityResolverTests
{
    [Fact]
    public void ResolveEffectiveToyWindowOptions_UsesScopedGeometryWhenPresent()
    {
        var toy = new ToyRouteConfig
        {
            Id = "matrix-main",
            Window = new ToyWindowOptionsConfig
            {
                Left = 100,
                Top = 200,
                Width = 640,
                Height = 320,
                AlwaysOnTop = true,
                Borderless = true,
            },
        };

        var routing = new RoutingConfig
        {
            ActiveTableOverrideKey = "my-table",
            Toys = [toy],
            TableToyVisibilityOverrides =
            [
                new TableToyVisibilityOverrideConfig
                {
                    TableKey = "my-table",
                    ToyOverrides = new Dictionary<string, TableToyOverrideConfig>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["matrix-main"] = new TableToyOverrideConfig
                        {
                            Window = new TableToyWindowOverrideConfig
                            {
                                Left = 111,
                                Top = 222,
                                Width = 800,
                                Height = 400,
                            },
                        },
                    },
                },
            ],
        };

        var effective = TableToyVisibilityResolver.ResolveEffectiveToyWindowOptions(routing, toy);

        Assert.Equal(111, effective.Left);
        Assert.Equal(222, effective.Top);
        Assert.Equal(800, effective.Width);
        Assert.Equal(400, effective.Height);
        Assert.True(effective.AlwaysOnTop);
        Assert.True(effective.Borderless);
    }

    [Fact]
    public void ResolveEffectiveToyWindowOptions_FallsBackFieldByFieldToGlobalGeometry()
    {
        var toy = new ToyRouteConfig
        {
            Id = "matrix-main",
            Window = new ToyWindowOptionsConfig
            {
                Left = 100,
                Top = 200,
                Width = 640,
                Height = 320,
            },
        };

        var routing = new RoutingConfig
        {
            ActiveTableOverrideKey = "my-table",
            Toys = [toy],
            TableToyVisibilityOverrides =
            [
                new TableToyVisibilityOverrideConfig
                {
                    TableKey = "my-table",
                    ToyOverrides = new Dictionary<string, TableToyOverrideConfig>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["matrix-main"] = new TableToyOverrideConfig
                        {
                            Window = new TableToyWindowOverrideConfig
                            {
                                Left = 333,
                                Height = 444,
                            },
                        },
                    },
                },
            ],
        };

        var effective = TableToyVisibilityResolver.ResolveEffectiveToyWindowOptions(routing, toy);

        Assert.Equal(333, effective.Left);
        Assert.Equal(200, effective.Top);
        Assert.Equal(640, effective.Width);
        Assert.Equal(444, effective.Height);
    }
}
