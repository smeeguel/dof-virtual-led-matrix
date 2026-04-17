using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Configuration;

// Overview: centralizes global-vs-table override precedence for effective toy visibility.
internal static class TableToyVisibilityResolver
{
    public static bool IsToyEnabledForScope(RoutingConfig routing, ToyRouteConfig toy)
    {
        if (routing is null || toy is null)
        {
            return false;
        }

        var activeScopeKey = routing.ActiveTableOverrideKey;
        if (string.IsNullOrWhiteSpace(activeScopeKey))
        {
            return toy.Enabled;
        }

        var scopeOverride = routing.TableToyVisibilityOverrides?
            .FirstOrDefault(entry => entry.TableKey.Equals(activeScopeKey, StringComparison.OrdinalIgnoreCase));
        if (scopeOverride?.ToyEnabledOverrides is not null
            && scopeOverride.ToyEnabledOverrides.TryGetValue(toy.Id, out var scopedEnabled))
        {
            return scopedEnabled;
        }

        return toy.Enabled;
    }

    public static int CountEnabledToysForActiveScope(RoutingConfig routing)
    {
        if (routing?.Toys is null)
        {
            return 0;
        }

        return routing.Toys.Count(toy => IsToyEnabledForScope(routing, toy));
    }
}
