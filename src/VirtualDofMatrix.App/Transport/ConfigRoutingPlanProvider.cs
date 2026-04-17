using VirtualDofMatrix.Core;
using VirtualDofMatrix.Core.Toys;
using VirtualDofMatrix.App.Configuration;

namespace VirtualDofMatrix.App.Transport;

// Overview: converts app configuration routing toys into immutable toy definitions for the router.
public sealed class ConfigRoutingPlanProvider : IRoutingPlanProvider
{
    private readonly AppConfig _config;

    public ConfigRoutingPlanProvider(AppConfig config)
    {
        _config = config;
    }

    public IReadOnlyList<ToyDefinition> GetActiveToyDefinitions()
    {
        var definitions = new List<ToyDefinition>();
        foreach (var toy in _config.Routing.Toys)
        {
            var enabledForScope = TableToyVisibilityResolver.IsToyEnabledForScope(_config.Routing, toy);
            if (!enabledForScope)
            {
                continue;
            }

            var outputTargets = toy.OutputTargets
                .Where(t => t.Enabled && !string.IsNullOrWhiteSpace(t.Adapter))
                .Select(t => t.Adapter)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (outputTargets.Length == 0)
            {
                continue;
            }

            definitions.Add(new ToyDefinition(
                Id: toy.Id,
                Enabled: enabledForScope,
                Kind: toy.Kind,
                Width: toy.Mapping.Width,
                Height: toy.Mapping.Height,
                Mapping: toy.Mapping.Mode,
                SourceDescriptor: new ToySourceRange(
                    CanonicalStart: toy.Source.CanonicalStart ?? 0,
                    Length: toy.Source.Length,
                    SourceStripIndex: toy.Source.StripIndex,
                    SourceOffset: toy.Source.StripOffset),
                OutputTargets: outputTargets));
        }

        return definitions;
    }
}
