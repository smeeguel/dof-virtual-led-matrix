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
        var loaded = JsonSerializer.Deserialize<AppConfig>(json, SerializerOptions) ?? new AppConfig();

        var iniPath = ResolveToyIniPath(filePath, loaded.Routing?.ToyConfigIniPath);
        var iniApplied = ToyIniConfiguration.ApplyFromIni(loaded, iniPath);
        var (normalized, shouldPersist) = ApplyLegacyDefaults(loaded);
        shouldPersist |= iniApplied;

        if (shouldPersist)
        {
            // Conversational note: write migrated/normalized config back so future launches skip one-time compatibility work.
            Save(filePath, normalized);
        }

        return normalized;
    }

    public void Save(string filePath, AppConfig config)
    {
        var json = JsonSerializer.Serialize(config, SerializerOptions);
        File.WriteAllText(filePath, json);
    }

    private static (AppConfig Config, bool ShouldPersist) ApplyLegacyDefaults(AppConfig config)
    {
        var shouldPersist = false;

        // Backfill nested objects so older config files can still load without null-check noise everywhere else.
        if (config.Settings is null)
        {
            config.Settings = new SettingsConfig();
            shouldPersist = true;
        }

        if (string.IsNullOrWhiteSpace(config.Settings.CabinetToyName))
        {
            config.Settings.CabinetToyName = "Matrix1";
            shouldPersist = true;
        }

        if (string.IsNullOrWhiteSpace(config.Settings.VisualQuality))
        {
            config.Settings.VisualQuality = "High";
            shouldPersist = true;
        }

        if (config.Matrix.MinDotSpacing < 2)
        {
            config.Matrix.MinDotSpacing = 2;
            shouldPersist = true;
        }

        var snappedBrightness = Math.Round(Math.Clamp(config.Matrix.Brightness, 0.0, 1.0) / 0.05, MidpointRounding.AwayFromZero) * 0.05;
        if (Math.Abs(snappedBrightness - config.Matrix.Brightness) > double.Epsilon)
        {
            config.Matrix.Brightness = snappedBrightness;
            shouldPersist = true;
        }

        if (config.Matrix.ToneMapping is null)
        {
            config.Matrix.ToneMapping = new ToneMappingConfig();
            shouldPersist = true;
        }

        if (config.Matrix.TemporalSmoothing is null)
        {
            config.Matrix.TemporalSmoothing = new TemporalSmoothingConfig();
            shouldPersist = true;
        }

        if (config.Matrix.Visual is null)
        {
            config.Matrix.Visual = new MatrixVisualConfig();
            shouldPersist = true;
        }

        var clampedRadius = Math.Clamp(config.Matrix.Visual.FullBrightnessRadiusMinPct, 0.0, 1.0);
        if (Math.Abs(clampedRadius - config.Matrix.Visual.FullBrightnessRadiusMinPct) > double.Epsilon)
        {
            config.Matrix.Visual.FullBrightnessRadiusMinPct = clampedRadius;
            shouldPersist = true;
        }

        if (config.Matrix.Bloom is null)
        {
            config.Matrix.Bloom = new BloomConfig();
            shouldPersist = true;
        }

        shouldPersist |= NormalizeRoutingConfig(config);

        return (config, shouldPersist);
    }

    private static bool NormalizeRoutingConfig(AppConfig config)
    {
        var modified = false;

        if (config.Routing is null)
        {
            config.Routing = new RoutingConfig();
            modified = true;
        }

        if (config.Routing.Policy is null)
        {
            config.Routing.Policy = new RoutingPolicyConfig();
            modified = true;
        }

        if (config.Routing.Toys is null)
        {
            config.Routing.Toys = [];
            modified = true;
        }

        if (string.IsNullOrWhiteSpace(config.Routing.RoutingSchemaVersion))
        {
            Warn("routing.routingSchemaVersion is missing; defaulting to '1'.");
            config.Routing.RoutingSchemaVersion = "1";
            modified = true;
        }

        if (config.Routing.Policy.DefaultStripLength <= 0)
        {
            Warn($"routing.policy.defaultStripLength={config.Routing.Policy.DefaultStripLength} is invalid; defaulting to 1100.");
            config.Routing.Policy.DefaultStripLength = 1100;
            modified = true;
        }

        NormalizePolicyValue(
            policyName: "routing.policy.onMissingData",
            currentValue: config.Routing.Policy.OnMissingData,
            supportedValues: ["drop", "partial-black-fill", "hold-last"],
            defaultValue: "partial-black-fill",
            assign: value => config.Routing.Policy.OnMissingData = value,
            ref modified);

        NormalizePolicyValue(
            policyName: "routing.policy.onOversizeRange",
            currentValue: config.Routing.Policy.OnOversizeRange,
            supportedValues: ["reject-config", "clamp"],
            defaultValue: "clamp",
            assign: value => config.Routing.Policy.OnOversizeRange = value,
            ref modified);

        NormalizePolicyValue(
            policyName: "routing.policy.onFrameRateSpike",
            currentValue: config.Routing.Policy.OnFrameRateSpike,
            supportedValues: ["latest-wins", "drop-oldest"],
            defaultValue: "latest-wins",
            assign: value => config.Routing.Policy.OnFrameRateSpike = value,
            ref modified);

        if (config.Routing.Toys.Count == 0)
        {
            MigrateLegacyMatrixToDefaultToy(config);
            config.Routing.RoutingSchemaVersion = "1";
            modified = true;
        }

        var nextFallbackCanonicalStart = 0;
        for (var i = 0; i < config.Routing.Toys.Count; i++)
        {
            var toy = config.Routing.Toys[i] ?? new ToyRouteConfig();
            if (!ReferenceEquals(toy, config.Routing.Toys[i]))
            {
                config.Routing.Toys[i] = toy;
                modified = true;
            }

            var entryPrefix = $"routing.toys[{i}]";

            if (string.IsNullOrWhiteSpace(toy.Id))
            {
                toy.Id = $"toy-{i + 1}";
                Warn($"{entryPrefix}.id is missing; generated '{toy.Id}'.");
                modified = true;
            }

            if (string.IsNullOrWhiteSpace(toy.Kind))
            {
                toy.Kind = "matrix";
                Warn($"{entryPrefix}.kind is missing; defaulting to 'matrix'.");
                modified = true;
            }

            if (toy.Mapping is null)
            {
                toy.Mapping = new ToyMappingConfig();
                modified = true;
            }

            if (toy.Mapping.Width <= 0 || toy.Mapping.Height <= 0)
            {
                Warn($"{entryPrefix}.mapping width/height are invalid; using global matrix size {config.Matrix.Width}x{config.Matrix.Height}.");
                toy.Mapping.Width = Math.Max(1, config.Matrix.Width);
                toy.Mapping.Height = Math.Max(1, config.Matrix.Height);
                modified = true;
            }

            if (string.IsNullOrWhiteSpace(toy.Mapping.Mode))
            {
                var fallbackMapping = config.Matrix.Mapping;
                toy.Mapping.Mode = fallbackMapping;
                Warn($"{entryPrefix}.mapping.mode is missing; defaulting to matrix mapping '{fallbackMapping}'.");
                modified = true;
            }

            if (toy.Source is null)
            {
                toy.Source = new ToySourceConfig();
                modified = true;
            }

            var expectedLength = toy.Mapping.Width * toy.Mapping.Height;
            if (toy.Source.Length <= 0)
            {
                toy.Source.Length = expectedLength;
                Warn($"{entryPrefix}.source.length is missing/invalid; defaulting to mapping area {expectedLength}.");
                modified = true;
            }

            modified |= NormalizeToyCanonicalRange(config, toy, entryPrefix, ref nextFallbackCanonicalStart);

            if (toy.Window is null)
            {
                toy.Window = new ToyWindowOptionsConfig();
                modified = true;
            }

            if (toy.Render is null)
            {
                toy.Render = new ToyRenderOptionsConfig();
                modified = true;
            }

            if (toy.Bloom is null)
            {
                toy.Bloom = new ToyBloomOptionsConfig();
                modified = true;
            }

            if (toy.OutputTargets is null)
            {
                toy.OutputTargets = [];
                modified = true;
            }

            if (toy.OutputTargets.Count == 0)
            {
                toy.OutputTargets.Add(new ToyAdapterTargetConfig { Adapter = "viewer", Enabled = true });
                Warn($"{entryPrefix}.outputTargets is empty; added default enabled viewer adapter target.");
                modified = true;
            }

            for (var targetIndex = 0; targetIndex < toy.OutputTargets.Count; targetIndex++)
            {
                var target = toy.OutputTargets[targetIndex] ?? new ToyAdapterTargetConfig();
                if (!ReferenceEquals(target, toy.OutputTargets[targetIndex]))
                {
                    toy.OutputTargets[targetIndex] = target;
                    modified = true;
                }

                if (string.IsNullOrWhiteSpace(target.Adapter))
                {
                    target.Adapter = "viewer";
                    Warn($"{entryPrefix}.outputTargets[{targetIndex}].adapter is missing; defaulting to 'viewer'.");
                    modified = true;
                }

                if (target.Options is null)
                {
                    target.Options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    modified = true;
                }
            }
        }

        return modified;
    }

    private static void NormalizePolicyValue(
        string policyName,
        string? currentValue,
        string[] supportedValues,
        string defaultValue,
        Action<string> assign,
        ref bool modified)
    {
        if (string.IsNullOrWhiteSpace(currentValue))
        {
            Warn($"{policyName} is missing; defaulting to '{defaultValue}'.");
            assign(defaultValue);
            modified = true;
            return;
        }

        var normalized = currentValue.Trim().ToLowerInvariant();
        if (!supportedValues.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            var allowed = string.Join(", ", supportedValues);
            Warn($"{policyName}='{currentValue}' is invalid; expected one of [{allowed}]. Defaulting to '{defaultValue}'.");
            assign(defaultValue);
            modified = true;
            return;
        }

        if (!string.Equals(currentValue, normalized, StringComparison.Ordinal))
        {
            assign(normalized);
            modified = true;
        }
    }

    private static void MigrateLegacyMatrixToDefaultToy(AppConfig config)
    {
        var defaultToy = new ToyRouteConfig
        {
            Id = "backglass-main",
            Enabled = true,
            Kind = "matrix",
            Source = new ToySourceConfig
            {
                CanonicalStart = 0,
                Length = Math.Max(1, config.Matrix.Width * config.Matrix.Height),
                StripIndex = 0,
                StripOffset = 0,
            },
            Mapping = new ToyMappingConfig
            {
                Width = Math.Max(1, config.Matrix.Width),
                Height = Math.Max(1, config.Matrix.Height),
                Mode = string.IsNullOrWhiteSpace(config.Matrix.Mapping)
                    ? "TopDownAlternateRightLeft"
                    : config.Matrix.Mapping,
            },
            Window = new ToyWindowOptionsConfig
            {
                UseGlobalWindow = true,
                AlwaysOnTop = config.Window.AlwaysOnTop,
                Borderless = config.Window.Borderless,
                Left = config.Window.Left,
                Top = config.Window.Top,
                Width = config.Window.Width,
                Height = config.Window.Height,
            },
            Render = new ToyRenderOptionsConfig
            {
                DotShape = string.IsNullOrWhiteSpace(config.Matrix.DotShape) ? "circle" : config.Matrix.DotShape,
                MinDotSpacing = config.Matrix.MinDotSpacing,
                Brightness = config.Matrix.Brightness,
                Gamma = config.Matrix.Gamma,
            },
            Bloom = new ToyBloomOptionsConfig
            {
                Enabled = config.Matrix.Bloom.Enabled,
                Threshold = config.Matrix.Bloom.Threshold,
                SoftKnee = config.Matrix.Bloom.SoftKnee,
                NearRadiusPx = config.Matrix.Bloom.NearRadiusPx,
                FarRadiusPx = config.Matrix.Bloom.FarRadiusPx,
                NearStrength = config.Matrix.Bloom.NearStrength,
                FarStrength = config.Matrix.Bloom.FarStrength,
            },
            OutputTargets =
            [
                new ToyAdapterTargetConfig
                {
                    Adapter = "viewer",
                    Enabled = true,
                },
            ],
        };

        config.Routing.Toys.Add(defaultToy);
        Warn("routing.toys was empty; migrated legacy matrix/window settings into routing.toys[0] as 'backglass-main'.");
    }

    private static bool NormalizeToyCanonicalRange(AppConfig config, ToyRouteConfig toy, string entryPrefix, ref int nextFallbackCanonicalStart)
    {
        var modified = false;
        var source = toy.Source;
        var defaultStripLength = config.Routing.Policy.DefaultStripLength;

        if (source.CanonicalStart is not null && source.CanonicalStart.Value >= 0)
        {
            nextFallbackCanonicalStart = Math.Max(nextFallbackCanonicalStart, source.CanonicalStart.Value + source.Length);
            return modified;
        }

        if (source.StripIndex is not null && source.StripOffset is not null)
        {
            var stripIndex = Math.Max(0, source.StripIndex.Value);
            var stripOffset = Math.Max(0, source.StripOffset.Value);
            source.CanonicalStart = (stripIndex * defaultStripLength) + stripOffset;
            Warn($"{entryPrefix}.source normalized strip+offset ({stripIndex},{stripOffset}) to canonicalStart={source.CanonicalStart.Value} using defaultStripLength={defaultStripLength}.");
            nextFallbackCanonicalStart = Math.Max(nextFallbackCanonicalStart, source.CanonicalStart.Value + source.Length);
            modified = true;
            return modified;
        }

        source.CanonicalStart = nextFallbackCanonicalStart;
        Warn($"{entryPrefix}.source missing canonicalStart and strip+offset; assigned canonicalStart={source.CanonicalStart.Value}. Consider setting source.canonicalStart explicitly.");
        nextFallbackCanonicalStart += source.Length;
        modified = true;

        return modified;
    }

    private static string ResolveToyIniPath(string settingsJsonPath, string? configuredPath)
    {
        var iniFileName = string.IsNullOrWhiteSpace(configuredPath) ? "toys.ini" : configuredPath;
        if (Path.IsPathRooted(iniFileName))
        {
            return iniFileName;
        }

        var settingsDirectory = Path.GetDirectoryName(settingsJsonPath);
        return string.IsNullOrWhiteSpace(settingsDirectory)
            ? iniFileName
            : Path.Combine(settingsDirectory, iniFileName);
    }

    private static void Warn(string message)
    {
        Console.Error.WriteLine($"[config] WARN {message}");
        AppLogger.Warn($"[config] {message}");
    }
}
