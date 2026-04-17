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
        if (scopeOverride?.ToyOverrides is not null
            && scopeOverride.ToyOverrides.TryGetValue(toy.Id, out var scopedOverride))
        {
            var mergedOverride = MergeOverrides(baseOverride: null, scopedOverride);
            if (mergedOverride.Enabled.HasValue)
            {
                return mergedOverride.Enabled.Value;
            }
        }

        return toy.Enabled;
    }

    public static TableToyOverrideConfig MergeOverrides(TableToyOverrideConfig? baseOverride, TableToyOverrideConfig? scopedOverride)
    {
        // Note: field-wise nullable merge keeps the schema additive, so future keys can be introduced
        // without changing callers that only care about today's enabled bit.
        var merged = CloneOverride(baseOverride) ?? new TableToyOverrideConfig();
        if (scopedOverride is null)
        {
            return merged;
        }

        if (scopedOverride.Enabled.HasValue)
        {
            merged.Enabled = scopedOverride.Enabled.Value;
        }

        if (scopedOverride.Window?.Left.HasValue == true)
        {
            merged.Window.Left = scopedOverride.Window.Left.Value;
        }

        if (scopedOverride.Window?.Top.HasValue == true)
        {
            merged.Window.Top = scopedOverride.Window.Top.Value;
        }

        if (scopedOverride.Window?.Width.HasValue == true)
        {
            merged.Window.Width = scopedOverride.Window.Width.Value;
        }

        if (scopedOverride.Window?.Height.HasValue == true)
        {
            merged.Window.Height = scopedOverride.Window.Height.Value;
        }

        return merged;
    }

    private static TableToyOverrideConfig? CloneOverride(TableToyOverrideConfig? source)
    {
        if (source is null)
        {
            return null;
        }

        return new TableToyOverrideConfig
        {
            Enabled = source.Enabled,
            Window = new TableToyWindowOverrideConfig
            {
                Left = source.Window?.Left,
                Top = source.Window?.Top,
                Width = source.Window?.Width,
                Height = source.Window?.Height,
            },
        };
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
