using System.IO;
using System.Text.Json;
using VirtualDofMatrix.App.Logging;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Configuration;

// Overview: this store keeps settings.json compatible across schema tweaks by applying defaults after deserialize.
public sealed class AppConfigurationStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public AppConfig Load(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return new AppConfig();
        }

        var json = File.ReadAllText(filePath);
        var loaded = JsonSerializer.Deserialize<AppConfig>(json, SerializerOptions);
        return ApplyLegacyDefaults(loaded ?? new AppConfig());
    }

    public void Save(string filePath, AppConfig config)
    {
        var json = JsonSerializer.Serialize(config, SerializerOptions);
        File.WriteAllText(filePath, json);
    }

    private static AppConfig ApplyLegacyDefaults(AppConfig config)
    {
        // Backfill nested objects so older config files can still load without null-check noise everywhere else.
        if (config.Settings is null)
        {
            config.Settings = new SettingsConfig();
        }

        if (string.IsNullOrWhiteSpace(config.Settings.CabinetToyName))
        {
            config.Settings.CabinetToyName = "Matrix1";
        }

        if (string.IsNullOrWhiteSpace(config.Settings.VisualQuality))
        {
            config.Settings.VisualQuality = "High";
        }

        if (config.Matrix.MinDotSpacing < 2)
        {
            config.Matrix.MinDotSpacing = 2;
        }

        config.Matrix.Brightness = Math.Round(Math.Clamp(config.Matrix.Brightness, 0.0, 1.0) / 0.05, MidpointRounding.AwayFromZero) * 0.05;

        if (config.Matrix.ToneMapping is null)
        {
            config.Matrix.ToneMapping = new ToneMappingConfig();
        }

        if (config.Matrix.TemporalSmoothing is null)
        {
            config.Matrix.TemporalSmoothing = new TemporalSmoothingConfig();
        }

        if (config.Matrix.Visual is null)
        {
            config.Matrix.Visual = new MatrixVisualConfig();
        }

        config.Matrix.Visual.FullBrightnessRadiusMinPct = Math.Clamp(config.Matrix.Visual.FullBrightnessRadiusMinPct, 0.0, 1.0);

        if (config.Matrix.Bloom is null)
        {
            config.Matrix.Bloom = new BloomConfig();
        }

        NormalizeRoutingConfig(config);

        return config;
    }

    private static void NormalizeRoutingConfig(AppConfig config)
    {
        config.Routing ??= new RoutingConfig();
        config.Routing.Policy ??= new RoutingPolicyConfig();
        config.Routing.Toys ??= [];

        if (string.IsNullOrWhiteSpace(config.Routing.RoutingSchemaVersion))
        {
            Warn("routing.routingSchemaVersion is missing; defaulting to '1.0'.");
            config.Routing.RoutingSchemaVersion = "1.0";
        }

        if (config.Routing.Policy.DefaultStripLength <= 0)
        {
            Warn($"routing.policy.defaultStripLength={config.Routing.Policy.DefaultStripLength} is invalid; defaulting to 1100.");
            config.Routing.Policy.DefaultStripLength = 1100;
        }

        var nextFallbackCanonicalStart = 0;

        for (var i = 0; i < config.Routing.Toys.Count; i++)
        {
            var toy = config.Routing.Toys[i] ?? new ToyRouteConfig();
            config.Routing.Toys[i] = toy;

            var entryPrefix = $"routing.toys[{i}]";

            if (string.IsNullOrWhiteSpace(toy.Id))
            {
                toy.Id = $"toy-{i + 1}";
                Warn($"{entryPrefix}.id is missing; generated '{toy.Id}'.");
            }

            if (string.IsNullOrWhiteSpace(toy.Kind))
            {
                toy.Kind = "matrix";
                Warn($"{entryPrefix}.kind is missing; defaulting to 'matrix'.");
            }

            toy.Mapping ??= new ToyMappingConfig();
            if (toy.Mapping.Width <= 0 || toy.Mapping.Height <= 0)
            {
                Warn($"{entryPrefix}.mapping width/height are invalid; using global matrix size {config.Matrix.Width}x{config.Matrix.Height}.");
                toy.Mapping.Width = Math.Max(1, config.Matrix.Width);
                toy.Mapping.Height = Math.Max(1, config.Matrix.Height);
            }

            if (string.IsNullOrWhiteSpace(toy.Mapping.Mode))
            {
                var fallbackMapping = config.Matrix.Mapping;
                toy.Mapping.Mode = fallbackMapping;
                Warn($"{entryPrefix}.mapping.mode is missing; defaulting to matrix mapping '{fallbackMapping}'.");
            }

            toy.Source ??= new ToySourceConfig();
            var expectedLength = toy.Mapping.Width * toy.Mapping.Height;
            if (toy.Source.Length <= 0)
            {
                toy.Source.Length = expectedLength;
                Warn($"{entryPrefix}.source.length is missing/invalid; defaulting to mapping area {expectedLength}.");
            }

            NormalizeToyCanonicalRange(config, toy, entryPrefix, ref nextFallbackCanonicalStart);

            toy.Window ??= new ToyWindowOptionsConfig();
            toy.Render ??= new ToyRenderOptionsConfig();
            toy.OutputTargets ??= [];

            if (toy.OutputTargets.Count == 0)
            {
                toy.OutputTargets.Add(new ToyAdapterTargetConfig { Adapter = "viewer", Enabled = true });
                Warn($"{entryPrefix}.outputTargets is empty; added default enabled viewer adapter target.");
            }

            for (var targetIndex = 0; targetIndex < toy.OutputTargets.Count; targetIndex++)
            {
                var target = toy.OutputTargets[targetIndex] ?? new ToyAdapterTargetConfig();
                toy.OutputTargets[targetIndex] = target;

                if (string.IsNullOrWhiteSpace(target.Adapter))
                {
                    target.Adapter = "viewer";
                    Warn($"{entryPrefix}.outputTargets[{targetIndex}].adapter is missing; defaulting to 'viewer'.");
                }

                target.Options ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    private static void NormalizeToyCanonicalRange(AppConfig config, ToyRouteConfig toy, string entryPrefix, ref int nextFallbackCanonicalStart)
    {
        var source = toy.Source;
        var defaultStripLength = config.Routing.Policy.DefaultStripLength;

        if (source.CanonicalStart is not null && source.CanonicalStart.Value >= 0)
        {
            nextFallbackCanonicalStart = Math.Max(nextFallbackCanonicalStart, source.CanonicalStart.Value + source.Length);
            return;
        }

        if (source.StripIndex is not null && source.StripOffset is not null)
        {
            var stripIndex = Math.Max(0, source.StripIndex.Value);
            var stripOffset = Math.Max(0, source.StripOffset.Value);
            source.CanonicalStart = (stripIndex * defaultStripLength) + stripOffset;
            Warn($"{entryPrefix}.source normalized strip+offset ({stripIndex},{stripOffset}) to canonicalStart={source.CanonicalStart.Value} using defaultStripLength={defaultStripLength}.");
            nextFallbackCanonicalStart = Math.Max(nextFallbackCanonicalStart, source.CanonicalStart.Value + source.Length);
            return;
        }

        source.CanonicalStart = nextFallbackCanonicalStart;
        Warn($"{entryPrefix}.source missing canonicalStart and strip+offset; assigned canonicalStart={source.CanonicalStart.Value}. Consider setting source.canonicalStart explicitly.");
        nextFallbackCanonicalStart += source.Length;
    }

    private static void Warn(string message)
    {
        Console.Error.WriteLine($"[config] WARN {message}");
        AppLogger.Warn($"[config] {message}");
    }
}
