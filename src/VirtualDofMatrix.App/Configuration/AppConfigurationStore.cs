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
        var createdIni = EnsureToyIniExists(iniPath);
        var iniApplied = ToyIniConfiguration.ApplyFromIni(loaded, iniPath);
        var (normalized, shouldPersist) = ApplyLegacyDefaults(loaded);
        shouldPersist |= createdIni;
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

        var iniPath = ResolveToyIniPath(filePath, config.Routing?.ToyConfigIniPath);
        ToyIniConfiguration.SaveToIni(config, iniPath);
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

        if (string.IsNullOrWhiteSpace(config.Settings.DofConfigFolderPath))
        {
            // Keep first-run behavior predictable by defaulting to the canonical DOF config location.
            config.Settings.DofConfigFolderPath = ConfigFolderBootstrapService.DefaultConfigFolderPath;
            shouldPersist = true;
        }

        if (string.IsNullOrWhiteSpace(config.Settings.CabinetXmlPath))
        {
            config.Settings.CabinetXmlPath = Path.Combine(config.Settings.DofConfigFolderPath, "Cabinet.xml");
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
        shouldPersist |= ApplyPrimaryToyCompatFields(config);

        return (config, shouldPersist);
    }

    private static bool ApplyPrimaryToyCompatFields(AppConfig config)
    {
        // Conversational note: until the full app stack is toy-native, we mirror primary toy values into
        // legacy matrix/window fields so runtime rendering paths effectively read from toys.ini.
        var primaryToy = config.Routing.Toys.FirstOrDefault(t => t.Enabled);
        if (primaryToy is null)
        {
            return false;
        }

        var modified = false;

        modified |= SetIntIfDifferent(primaryToy.Mapping.Width, value => config.Matrix.Width = value, config.Matrix.Width);
        modified |= SetIntIfDifferent(primaryToy.Mapping.Height, value => config.Matrix.Height = value, config.Matrix.Height);
        modified |= SetStringIfDifferent(primaryToy.Mapping.Mode, value => config.Matrix.Mapping = value, config.Matrix.Mapping);

        modified |= SetStringIfDifferent(primaryToy.Render.DotShape, value => config.Matrix.DotShape = value, config.Matrix.DotShape);
        modified |= SetIntIfDifferent(primaryToy.Render.MinDotSpacing, value => config.Matrix.MinDotSpacing = value, config.Matrix.MinDotSpacing);
        modified |= SetBoolIfDifferent(primaryToy.Render.FillGapEnabled, value => config.Matrix.FillGapEnabled = value, config.Matrix.FillGapEnabled);
        modified |= SetDoubleIfDifferent(primaryToy.Render.Brightness, value => config.Matrix.Brightness = value, config.Matrix.Brightness);
        modified |= SetDoubleIfDifferent(primaryToy.Render.Gamma, value => config.Matrix.Gamma = value, config.Matrix.Gamma);

        modified |= SetBoolIfDifferent(primaryToy.Bloom.Enabled, value => config.Matrix.Bloom.Enabled = value, config.Matrix.Bloom.Enabled);
        modified |= SetDoubleIfDifferent(primaryToy.Bloom.Threshold, value => config.Matrix.Bloom.Threshold = value, config.Matrix.Bloom.Threshold);
        modified |= SetDoubleIfDifferent(primaryToy.Bloom.SoftKnee, value => config.Matrix.Bloom.SoftKnee = value, config.Matrix.Bloom.SoftKnee);
        modified |= SetIntIfDifferent(primaryToy.Bloom.NearRadiusPx, value => config.Matrix.Bloom.NearRadiusPx = value, config.Matrix.Bloom.NearRadiusPx);
        modified |= SetIntIfDifferent(primaryToy.Bloom.FarRadiusPx, value => config.Matrix.Bloom.FarRadiusPx = value, config.Matrix.Bloom.FarRadiusPx);
        modified |= SetDoubleIfDifferent(primaryToy.Bloom.NearStrength, value => config.Matrix.Bloom.NearStrength = value, config.Matrix.Bloom.NearStrength);
        modified |= SetDoubleIfDifferent(primaryToy.Bloom.FarStrength, value => config.Matrix.Bloom.FarStrength = value, config.Matrix.Bloom.FarStrength);

        if (primaryToy.Window.Left.HasValue)
        {
            modified |= SetDoubleIfDifferent(primaryToy.Window.Left.Value, value => config.Window.Left = value, config.Window.Left);
        }

        if (primaryToy.Window.Top.HasValue)
        {
            modified |= SetDoubleIfDifferent(primaryToy.Window.Top.Value, value => config.Window.Top = value, config.Window.Top);
        }

        if (primaryToy.Window.Width.HasValue)
        {
            modified |= SetDoubleIfDifferent(primaryToy.Window.Width.Value, value => config.Window.Width = value, config.Window.Width);
        }

        if (primaryToy.Window.Height.HasValue)
        {
            modified |= SetDoubleIfDifferent(primaryToy.Window.Height.Value, value => config.Window.Height = value, config.Window.Height);
        }

        modified |= SetBoolIfDifferent(primaryToy.Window.AlwaysOnTop, value => config.Window.AlwaysOnTop = value, config.Window.AlwaysOnTop);
        modified |= SetBoolIfDifferent(primaryToy.Window.Borderless, value => config.Window.Borderless = value, config.Window.Borderless);
        modified |= SetBoolIfDifferent(primaryToy.Window.LockAspectRatio, value => config.Window.LockAspectRatio = value, config.Window.LockAspectRatio);

        return modified;
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

        modified |= EnsureUniqueToyNames(config.Routing.Toys);

        return modified;
    }

    private static bool EnsureUniqueToyNames(IReadOnlyList<ToyRouteConfig> toys)
    {
        var modified = false;
        var nextMatrixIndex = 1;
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var toy in toys)
        {
            if (!string.IsNullOrWhiteSpace(toy.Name))
            {
                usedNames.Add(toy.Name.Trim());
            }
        }

        foreach (var toy in toys)
        {
            if (!string.IsNullOrWhiteSpace(toy.Name))
            {
                continue;
            }

            while (usedNames.Contains($"Matrix{nextMatrixIndex}"))
            {
                nextMatrixIndex++;
            }

            toy.Name = $"Matrix{nextMatrixIndex}";
            usedNames.Add(toy.Name);
            nextMatrixIndex++;
            modified = true;
        }

        // Conversational note: if users duplicate names manually, normalize collisions deterministically into MatrixN names.
        usedNames.Clear();
        foreach (var toy in toys)
        {
            var candidate = toy.Name.Trim();
            if (usedNames.Add(candidate))
            {
                continue;
            }

            while (usedNames.Contains($"Matrix{nextMatrixIndex}"))
            {
                nextMatrixIndex++;
            }

            toy.Name = $"Matrix{nextMatrixIndex}";
            usedNames.Add(toy.Name);
            nextMatrixIndex++;
            modified = true;
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
            Name = "Matrix1",
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
                LockAspectRatio = config.Window.LockAspectRatio,
                Left = config.Window.Left,
                Top = config.Window.Top,
                Width = config.Window.Width,
                Height = config.Window.Height,
            },
            Render = new ToyRenderOptionsConfig
            {
                DotShape = string.IsNullOrWhiteSpace(config.Matrix.DotShape) ? "circle" : config.Matrix.DotShape,
                MinDotSpacing = config.Matrix.MinDotSpacing,
                FillGapEnabled = config.Matrix.FillGapEnabled,
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

    private static bool SetStringIfDifferent(string next, Action<string> set, string current)
    {
        if (string.Equals(next, current, StringComparison.Ordinal))
        {
            return false;
        }

        set(next);
        return true;
    }

    private static bool SetIntIfDifferent(int next, Action<int> set, int current)
    {
        if (next == current)
        {
            return false;
        }

        set(next);
        return true;
    }

    private static bool SetBoolIfDifferent(bool next, Action<bool> set, bool current)
    {
        if (next == current)
        {
            return false;
        }

        set(next);
        return true;
    }

    private static bool SetDoubleIfDifferent(double next, Action<double> set, double current)
    {
        if (Math.Abs(next - current) <= double.Epsilon)
        {
            return false;
        }

        set(next);
        return true;
    }

    private static bool EnsureToyIniExists(string iniPath)
    {
        if (File.Exists(iniPath))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(iniPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Conversational note: we drop a starter file next to the executable so first-run setup is obvious.
        File.WriteAllText(iniPath, BuildDefaultToyIniTemplate());
        return true;
    }

    private static string BuildDefaultToyIniTemplate()
    {
        return """
; Auto-generated starter toys.ini
; Edit toy settings here. Keep this file next to settings.json / app executable.

[policy]
; onMissingData options: drop | partial-black-fill | hold-last
onMissingData = partial-black-fill
; onOversizeRange options: reject-config | clamp
onOversizeRange = clamp
; onFrameRateSpike options: latest-wins | drop-oldest
onFrameRateSpike = latest-wins
; defaultStripLength options: positive integer (example: 1100)
defaultStripLength = 1100

[toy:backglass-main]
; name options: unique user-facing toy name (example: Matrix1, Matrix2, ...)
name = Matrix1
; enabled options: true | false
enabled = true
; kind options: matrix | topper | <custom-name>
kind = matrix
; width options: positive integer (legacy default: 128)
width = 128
; height options: positive integer (legacy default: 32)
height = 32
; mapping options: TopDownAlternateRightLeft | RowMajor | ColumnMajor
mapping = TopDownAlternateRightLeft
; sourceCanonicalStart options: integer >= 0
sourceCanonicalStart = 0
; sourceLength options: integer > 0 (legacy default 128*32 = 4096)
sourceLength = 4096
; sourceStripIndex/sourceStripOffset options: integer >= 0 (optional)
sourceStripIndex = 0
sourceStripOffset = 0

; windowLeft/windowTop options: number (pixels)
windowLeft = 10
windowTop = 6
; windowWidth/windowHeight options: number > 0 (pixels)
windowWidth = 1412
windowHeight = 353
; windowAlwaysOnTop/windowBorderless options: true | false
windowAlwaysOnTop = true
windowBorderless = true
; windowLockAspectRatio options: true | false
windowLockAspectRatio = true

; renderDotShape options: circle | square
renderDotShape = circle
; renderMinDotSpacing options: integer >= 0
renderMinDotSpacing = 2
; fillGapEnabled options: true | false (true biases pitch to dominant axis and stretches dot canvas to fill viewport space minus spacing)
fillGapEnabled = false
; renderBrightness options: number 0.0..1.0
renderBrightness = 1.0
; renderGamma options: number > 0
renderGamma = 0.8

; bloomEnabled options: true | false
bloomEnabled = true
; bloomThreshold/bloomSoftKnee options: number 0.0..1.0
bloomThreshold = 0.72
bloomSoftKnee = 0.18
; bloomNearRadiusPx/bloomFarRadiusPx options: integer >= 0
bloomNearRadiusPx = 2
bloomFarRadiusPx = 10
; bloomNearStrength/bloomFarStrength options: number >= 0
bloomNearStrength = 1.0
bloomFarStrength = 0.2

; outputTargets options: comma list of adapter names (example: viewer,pipe-broadcast)
outputTargets = viewer,pipe-broadcast
""";
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
