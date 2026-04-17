using System.IO;
using System.Text.Json;
using VirtualDofMatrix.App.Logging;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Configuration;

// Overview: this store keeps settings.json compatible across schema tweaks by applying defaults after deserialize.
public sealed class AppConfigurationStore
{
    // Note: keep routing normalization aligned with the documented Teensy compatibility target.
    private const int MaxCompatibleStripCount = 8;
    private readonly CabinetXmlService _cabinetXmlService = new();

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public AppConfig Load(string filePath)
    {
        var loaded = new AppConfig();
        if (File.Exists(filePath))
        {
            var json = File.ReadAllText(filePath);
            loaded = JsonSerializer.Deserialize<AppConfig>(json, SerializerOptions) ?? new AppConfig();
        }

        var iniPath = ResolveToyIniPath(filePath, loaded.Routing?.ToyConfigIniPath);
        var createdIni = EnsureToyIniExists(iniPath, loaded);
        var iniApplied = ToyIniConfiguration.ApplyFromIni(loaded, iniPath);
        var tableOverrideIniPath = ResolveTableOverrideIniPath(filePath, loaded.Routing?.TableOverrideIniPath);
        var tableOverridesApplied = TableToyOverrideIniConfiguration.ApplyFromIni(loaded, tableOverrideIniPath);
        var iniResynced = TryResyncToyIniFromCabinet(loaded, iniPath);
        var (normalized, shouldPersist) = ApplyLegacyDefaults(loaded);
        AppLogger.Info($"[config] settingsPath={filePath}");
        AppLogger.Info($"[config] toysIniPath={iniPath}");
        AppLogger.Info($"[config] tableOverrideIniPath={tableOverrideIniPath}");
        AppLogger.Info($"[config] toysIniCreated={createdIni} toysIniApplied={iniApplied} toysIniResynced={iniResynced} routingToyCount={normalized.Routing.Toys.Count}");
        shouldPersist |= createdIni;
        shouldPersist |= iniApplied;
        shouldPersist |= tableOverridesApplied;
        shouldPersist |= iniResynced;

        if (shouldPersist)
        {
            // Note: write migrated/normalized config back so future launches skip one-time compatibility work.
            Save(filePath, normalized);
        }

        return normalized;
    }

    public void Save(string filePath, AppConfig config)
    {
        // Note: callers may save a brand-new AppConfig before the first Load() normalization pass runs.
        // Keep persisted settings.json and toys.ini first-run safe by applying the same legacy/default
        // backfill used during Load(), including the canonical backglass Matrix1 toy migration.
        var (normalized, _) = ApplyLegacyDefaults(config);

        // Note: toys.ini is authoritative for per-toy settings; keep settings.json focused on app/global settings
        // by omitting inline toy definitions from persisted JSON.
        var settingsSnapshot = JsonSerializer.Deserialize<AppConfig>(
            JsonSerializer.Serialize(normalized, SerializerOptions),
            SerializerOptions) ?? new AppConfig();
        settingsSnapshot.Routing ??= new RoutingConfig();
        settingsSnapshot.Routing.Toys = [];
        settingsSnapshot.Routing.TableToyVisibilityOverrides = [];

        var json = JsonSerializer.Serialize(settingsSnapshot, SerializerOptions);
        File.WriteAllText(filePath, json);

        var iniPath = ResolveToyIniPath(filePath, normalized.Routing?.ToyConfigIniPath);
        ToyIniConfiguration.SaveToIni(normalized, iniPath);
        var tableOverrideIniPath = ResolveTableOverrideIniPath(filePath, normalized.Routing?.TableOverrideIniPath);
        TableToyOverrideIniConfiguration.SaveToIni(normalized, tableOverrideIniPath);
        AppLogger.Info($"[config] saved settingsPath={filePath}");
        // Note: guard nullability in logging so analyzers stay green even if routing defaults change later.
        var routingToyCount = normalized.Routing?.Toys.Count ?? 0;
        AppLogger.Info($"[config] saved toysIniPath={iniPath} routingToyCount={routingToyCount}");
        AppLogger.Info($"[config] saved tableOverrideIniPath={tableOverrideIniPath}");
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

        if (config.Settings.DefaultStripBulbSize <= 0)
        {
            config.Settings.DefaultStripBulbSize = 32;
            shouldPersist = true;
        }

        if (config.Routing is null)
        {
            config.Routing = new RoutingConfig();
            shouldPersist = true;
        }

        if (string.IsNullOrWhiteSpace(config.Routing.TableOverrideIniPath))
        {
            config.Routing.TableOverrideIniPath = "table-toy-overrides.ini";
            shouldPersist = true;
        }

        if (config.Matrix.MinDotSpacing < 2)
        {
            config.Matrix.MinDotSpacing = 2;
            shouldPersist = true;
        }

        // Note: when only legacy scalar spacing is present, infer both axes from that scalar value.
        if (config.Matrix.MinDotSpacingX == 2 && config.Matrix.MinDotSpacingY == 2 && config.Matrix.MinDotSpacing > 2)
        {
            config.Matrix.MinDotSpacingX = config.Matrix.MinDotSpacing;
            config.Matrix.MinDotSpacingY = config.Matrix.MinDotSpacing;
            shouldPersist = true;
        }

        // Note: per-axis spacing defaults to the legacy scalar spacing so older configs preserve intent.
        if (config.Matrix.MinDotSpacingX < 2)
        {
            config.Matrix.MinDotSpacingX = Math.Max(2, config.Matrix.MinDotSpacing);
            shouldPersist = true;
        }

        if (config.Matrix.MinDotSpacingY < 2)
        {
            config.Matrix.MinDotSpacingY = Math.Max(2, config.Matrix.MinDotSpacing);
            shouldPersist = true;
        }

        // Keep legacy scalar in sync so older UI/edit paths that still read MinDotSpacing remain predictable.
        var normalizedScalarSpacing = Math.Max(config.Matrix.MinDotSpacingX, config.Matrix.MinDotSpacingY);
        if (config.Matrix.MinDotSpacing != normalizedScalarSpacing)
        {
            config.Matrix.MinDotSpacing = normalizedScalarSpacing;
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

        if (config.Routing.TableToyVisibilityOverrides is null)
        {
            config.Routing.TableToyVisibilityOverrides = [];
            modified = true;
        }

        for (var i = 0; i < config.Routing.TableToyVisibilityOverrides.Count; i++)
        {
            var overrideEntry = config.Routing.TableToyVisibilityOverrides[i] ?? new TableToyVisibilityOverrideConfig();
            if (!ReferenceEquals(overrideEntry, config.Routing.TableToyVisibilityOverrides[i]))
            {
                config.Routing.TableToyVisibilityOverrides[i] = overrideEntry;
                modified = true;
            }

            if (overrideEntry.ToyEnabledOverrides is null)
            {
                overrideEntry.ToyEnabledOverrides = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                modified = true;
            }
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
            else
            {
                // Note: when only legacy scalar toy spacing exists, mirror it into both axis fields.
                if (toy.Render.MinDotSpacingX == 2 && toy.Render.MinDotSpacingY == 2 && toy.Render.MinDotSpacing != 2)
                {
                    var fallbackToySpacing = Math.Max(0, toy.Render.MinDotSpacing);
                    toy.Render.MinDotSpacingX = fallbackToySpacing;
                    toy.Render.MinDotSpacingY = fallbackToySpacing;
                    modified = true;
                }

                // Note: per-axis toy spacing defaults to the legacy scalar field for backward compatibility.
                if (toy.Render.MinDotSpacingX < 0)
                {
                    toy.Render.MinDotSpacingX = Math.Max(0, toy.Render.MinDotSpacing);
                    modified = true;
                }

                if (toy.Render.MinDotSpacingY < 0)
                {
                    toy.Render.MinDotSpacingY = Math.Max(0, toy.Render.MinDotSpacing);
                    modified = true;
                }

                var normalizedToyScalarSpacing = Math.Max(Math.Max(0, toy.Render.MinDotSpacingX), Math.Max(0, toy.Render.MinDotSpacingY));
                if (toy.Render.MinDotSpacing != normalizedToyScalarSpacing)
                {
                    toy.Render.MinDotSpacing = normalizedToyScalarSpacing;
                    modified = true;
                }
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

    private bool TryResyncToyIniFromCabinet(AppConfig config, string iniPath)
    {
        var cabinetPath = _cabinetXmlService.ResolveCabinetXmlPath(config.Settings.CabinetXmlPath);
        if (string.IsNullOrWhiteSpace(cabinetPath))
        {
            return false;
        }

        IReadOnlyList<CabinetVirtualLedStripToy> virtualToys;
        try
        {
            // Note: this read gates resync so we do not mutate toys.ini when Cabinet.xml cannot be parsed.
            virtualToys = _cabinetXmlService.GetVirtualLedStripToys(cabinetPath);
        }
        catch (Exception ex)
        {
            Warn($"Skipping toys.ini resync because Cabinet.xml could not be parsed ({ex.Message}).");
            return false;
        }

        if (virtualToys.Count == 0)
        {
            return false;
        }

        var desiredToys = BuildRoutingToysFromCabinet(config);
        if (!RequiresToyIniResync(config.Routing.Toys, desiredToys))
        {
            return false;
        }

        var backupPath = BackupToyIniFile(iniPath);
        var merged = MergeToyIniVisualState(existingToys: config.Routing.Toys, desiredToys: desiredToys);
        config.Routing.Toys = merged;

        AppLogger.Info($"[config] toys.ini resync applied using Cabinet.xml toy inventory. backupPath={(string.IsNullOrWhiteSpace(backupPath) ? "(none)" : backupPath)}");
        return true;
    }

    private static bool RequiresToyIniResync(IReadOnlyList<ToyRouteConfig> existingToys, IReadOnlyList<ToyRouteConfig> desiredToys)
    {
        if (existingToys.Count != desiredToys.Count)
        {
            return true;
        }

        var existingByKey = existingToys
            .Where(toy => !string.IsNullOrWhiteSpace(ToToyMatchKey(toy)))
            .GroupBy(ToToyMatchKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

        foreach (var desired in desiredToys)
        {
            var key = ToToyMatchKey(desired);
            if (!existingByKey.TryGetValue(key, out var existing))
            {
                return true;
            }

            if (existing.Mapping.Width != desired.Mapping.Width
                || existing.Mapping.Height != desired.Mapping.Height
                || !string.Equals(existing.Mapping.Mode, desired.Mapping.Mode, StringComparison.OrdinalIgnoreCase)
                || existing.Source.CanonicalStart != desired.Source.CanonicalStart
                || existing.Source.Length != desired.Source.Length
                || !string.Equals(existing.Kind, desired.Kind, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static List<ToyRouteConfig> MergeToyIniVisualState(
        IReadOnlyList<ToyRouteConfig> existingToys,
        IReadOnlyList<ToyRouteConfig> desiredToys)
    {
        var existingByKey = existingToys
            .Where(toy => !string.IsNullOrWhiteSpace(ToToyMatchKey(toy)))
            .GroupBy(ToToyMatchKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

        var merged = new List<ToyRouteConfig>(desiredToys.Count);
        foreach (var desired in desiredToys)
        {
            var key = ToToyMatchKey(desired);
            if (!existingByKey.TryGetValue(key, out var existing))
            {
                merged.Add(desired);
                continue;
            }

            // Note: Cabinet.xml remains source-of-truth for toy shape/range; we only preserve user-facing visual/window preferences.
            desired.Enabled = existing.Enabled;
            desired.Window = CloneWithJson(existing.Window) ?? new ToyWindowOptionsConfig();
            desired.Render = CloneWithJson(existing.Render) ?? new ToyRenderOptionsConfig();
            desired.Bloom = CloneWithJson(existing.Bloom) ?? new ToyBloomOptionsConfig();
            desired.OutputTargets = CloneWithJson(existing.OutputTargets) ?? [];
            merged.Add(desired);
        }

        return merged;
    }

    private static T? CloneWithJson<T>(T source)
    {
        if (source is null)
        {
            return default;
        }

        var json = JsonSerializer.Serialize(source, SerializerOptions);
        return JsonSerializer.Deserialize<T>(json, SerializerOptions);
    }

    private static string ToToyMatchKey(ToyRouteConfig toy)
    {
        var name = string.IsNullOrWhiteSpace(toy.Name) ? toy.Id : toy.Name;
        return name?.Trim() ?? string.Empty;
    }

    private static string? BackupToyIniFile(string iniPath)
    {
        if (!File.Exists(iniPath))
        {
            return null;
        }

        var folder = Path.GetDirectoryName(iniPath);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return null;
        }

        var backupPath = Path.Combine(folder, $"toys.ini.backup_{DateTime.Now:yyyyMMdd_HHmmss}");
        var suffix = 1;
        while (File.Exists(backupPath))
        {
            backupPath = Path.Combine(folder, $"toys.ini.backup_{DateTime.Now:yyyyMMdd_HHmmss}_{suffix++}");
        }

        File.Copy(iniPath, backupPath);
        return backupPath;
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

        // Note: if users duplicate names manually, normalize collisions deterministically into MatrixN names.
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
                BackgroundVisible = config.Window.BackgroundVisible,
                BackgroundColor = config.Window.BackgroundColor,
                Left = config.Window.Left,
                Top = config.Window.Top,
                Width = config.Window.Width,
                Height = config.Window.Height,
            },
            Render = new ToyRenderOptionsConfig
            {
                DotShape = string.IsNullOrWhiteSpace(config.Matrix.DotShape) ? "circle" : config.Matrix.DotShape,
                MinDotSpacing = config.Matrix.MinDotSpacing,
                MinDotSpacingX = config.Matrix.MinDotSpacingX,
                MinDotSpacingY = config.Matrix.MinDotSpacingY,
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

        if (string.Equals(toy.Kind, "strip", StringComparison.OrdinalIgnoreCase)
            && source.StripIndex is null
            && source.StripOffset is null
            && source.CanonicalStart is not null
            && source.CanonicalStart.Value != 0)
        {
            // Note: strip toys without explicit strip index/offset currently route from strip-local start.
            // Force canonicalStart=0 so older non-zero values from list resequencing do not black out strip lights.
            Warn($"{entryPrefix}.source.canonicalStart={source.CanonicalStart.Value} reset to 0 for strip toy without sourceStripIndex/sourceStripOffset.");
            source.CanonicalStart = 0;
            modified = true;
        }

        if (source.CanonicalStart is not null && source.CanonicalStart.Value >= 0)
        {
            nextFallbackCanonicalStart = Math.Max(nextFallbackCanonicalStart, source.CanonicalStart.Value + source.Length);
            return modified;
        }

        if (source.StripIndex is not null && source.StripOffset is not null)
        {
            // Note: clamping strip indices here keeps source math deterministic and avoids silent out-of-range toy mappings.
            var stripIndex = source.StripIndex.Value;
            if (stripIndex < 0)
            {
                Warn($"{entryPrefix}.source.stripIndex={stripIndex} is invalid; clamped to 0.");
                stripIndex = 0;
                source.StripIndex = stripIndex;
                modified = true;
            }
            else if (stripIndex >= MaxCompatibleStripCount)
            {
                var clampedStripIndex = MaxCompatibleStripCount - 1;
                Warn($"{entryPrefix}.source.stripIndex={stripIndex} exceeds safe compatibility range [0..{clampedStripIndex}]; clamped to {clampedStripIndex}.");
                stripIndex = clampedStripIndex;
                source.StripIndex = stripIndex;
                modified = true;
            }

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

    private bool EnsureToyIniExists(string iniPath, AppConfig config)
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

        // Note: first-run bootstraps from Cabinet.xml virtual toys when available so existing DOF users
        // start with matching toy windows/ranges instead of an empty one-toy template.
        var bootstrapConfig = BuildToyIniBootstrapConfig(config);
        ToyIniConfiguration.SaveToIni(bootstrapConfig, iniPath);
        return true;
    }

    private AppConfig BuildToyIniBootstrapConfig(AppConfig sourceConfig)
    {
        // Note: SaveToIni strips routing toys from persisted settings.json, so clone current config
        // and populate only the first-run toys.ini payload here.
        var bootstrap = JsonSerializer.Deserialize<AppConfig>(
            JsonSerializer.Serialize(sourceConfig, SerializerOptions),
            SerializerOptions) ?? new AppConfig();
        bootstrap.Routing ??= new RoutingConfig();
        bootstrap.Routing.Toys = BuildRoutingToysFromCabinet(bootstrap);
        return bootstrap;
    }

    private List<ToyRouteConfig> BuildRoutingToysFromCabinet(AppConfig config)
    {
        var cabinetPath = _cabinetXmlService.ResolveCabinetXmlPath(config.Settings.CabinetXmlPath);
        if (string.IsNullOrWhiteSpace(cabinetPath))
        {
            return [BuildDefaultBackglassToy()];
        }

        IReadOnlyList<CabinetVirtualLedStripToy> virtualToys;
        try
        {
            virtualToys = _cabinetXmlService.GetVirtualLedStripToys(cabinetPath);
        }
        catch (Exception ex)
        {
            Warn($"Unable to parse Cabinet.xml for first-run toys.ini bootstrap ({ex.Message}); falling back to default toy.");
            return [BuildDefaultBackglassToy()];
        }

        if (virtualToys.Count == 0)
        {
            return [BuildDefaultBackglassToy()];
        }

        var slugUseCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var toys = new List<ToyRouteConfig>(virtualToys.Count);
        var defaultStripBulbSize = config.Settings.DefaultStripBulbSize > 0 ? config.Settings.DefaultStripBulbSize : 32;

        foreach (var toy in virtualToys)
        {
            var isMatrix = toy.Width > 1 && toy.Height > 1;
            var kind = isMatrix ? "matrix" : "strip";
            var mapping = ResolveToyMapping(toy);
            var firstLedNumber = toy.FirstLedNumber.GetValueOrDefault(1);
            if (firstLedNumber < 1)
            {
                firstLedNumber = 1;
            }

            var canonicalStart = firstLedNumber - 1;
            var sourceLength = toy.LedCount > 0 ? toy.LedCount : Math.Max(1, toy.Width * toy.Height);
            var source = new ToySourceConfig
            {
                CanonicalStart = canonicalStart,
                Length = sourceLength,
            };

            if (string.Equals(kind, "strip", StringComparison.OrdinalIgnoreCase))
            {
                // Note: current strip runtime behavior consumes strip-local payloads; keeping strip
                // canonicalStart at 0 avoids dark strips when non-zero canonical offsets are imported.
                source.CanonicalStart = 0;
                source.StripIndex = null;
                source.StripOffset = null;
            }
            else if (sourceLength > 0)
            {
                source.StripIndex = Math.Clamp(canonicalStart / config.Routing.Policy.DefaultStripLength, 0, MaxCompatibleStripCount - 1);
                source.StripOffset = canonicalStart % config.Routing.Policy.DefaultStripLength;
            }

            toys.Add(new ToyRouteConfig
            {
                Id = BuildUniqueToyId(toy.Name, slugUseCount, toys.Count == 0 && isMatrix),
                Name = toy.Name,
                Enabled = true,
                Kind = kind,
                Source = source,
                Mapping = new ToyMappingConfig
                {
                    Width = toy.Width,
                    Height = toy.Height,
                    Mode = mapping,
                },
                // Note: mirror Toy Wizard type defaults so imported toys match first-class create flow.
                Window = BuildBootstrapWindowOptions(kind, mapping, sourceLength, defaultStripBulbSize),
                OutputTargets =
                [
                    new ToyAdapterTargetConfig
                    {
                        Adapter = "viewer",
                        Enabled = true,
                    },
                ],
            });
        }

        if (toys.Count == 0)
        {
            toys.Add(BuildDefaultBackglassToy());
        }

        return toys;
    }

    private static string ResolveToyMapping(CabinetVirtualLedStripToy toy)
    {
        if (toy.Width == 1 && toy.Height > 1)
        {
            return "ColumnMajor";
        }

        if (toy.Height == 1 && toy.Width > 1)
        {
            return "RowMajor";
        }

        return string.IsNullOrWhiteSpace(toy.LedStripArrangement)
            ? "TopDownAlternateRightLeft"
            : toy.LedStripArrangement!;
    }

    private static string BuildUniqueToyId(string toyName, Dictionary<string, int> slugUseCount, bool preferBackglassAlias)
    {
        if (preferBackglassAlias)
        {
            slugUseCount["backglass-main"] = 1;
            return "backglass-main";
        }

        var slug = SlugifyToyId(toyName);
        if (!slugUseCount.TryGetValue(slug, out var count))
        {
            slugUseCount[slug] = 1;
            return slug;
        }

        count++;
        slugUseCount[slug] = count;
        return $"{slug}-{count}";
    }

    private static string SlugifyToyId(string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return $"toy-{Guid.NewGuid():N}".Substring(0, 12);
        }

        var normalized = new string(rawName
            .Trim()
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray());

        normalized = normalized.Trim('-');
        while (normalized.Contains("--", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("--", "-", StringComparison.Ordinal);
        }

        return string.IsNullOrWhiteSpace(normalized) ? "toy" : normalized;
    }

    private static ToyWindowOptionsConfig BuildBootstrapWindowOptions(
        string kind,
        string mapping,
        int sourceLength,
        int defaultStripBulbSize)
    {
        var isStrip = string.Equals(kind, "strip", StringComparison.OrdinalIgnoreCase);
        var options = new ToyWindowOptionsConfig
        {
            UseGlobalWindow = true,
            AlwaysOnTop = true,
            Borderless = true,
            LockAspectRatio = !isStrip,
            BackgroundVisible = !isStrip,
            BackgroundColor = "#000000",
        };

        if (!isStrip)
        {
            return options;
        }

        // Note: keep initial strip window dimensions aligned with Toy Wizard sizing logic so
        // imported strip windows open at a practical one-dimensional footprint.
        const int defaultSpacing = 2;
        const int defaultBloomFarRadiusPx = 10;
        var length = Math.Max(1, sourceLength);
        var minorAxisPadding = defaultBloomFarRadiusPx * 2;
        var isVertical = mapping.Equals("ColumnMajor", StringComparison.OrdinalIgnoreCase);
        options.Width = isVertical
            ? defaultStripBulbSize + minorAxisPadding
            : (defaultStripBulbSize + defaultSpacing) * length;
        options.Height = isVertical
            ? (defaultStripBulbSize + defaultSpacing) * length
            : defaultStripBulbSize + minorAxisPadding;

        return options;
    }

    private static ToyRouteConfig BuildDefaultBackglassToy()
    {
        return new ToyRouteConfig
        {
            Id = "backglass-main",
            Name = "Matrix1",
            Enabled = true,
            Kind = "matrix",
            Source = new ToySourceConfig
            {
                CanonicalStart = 0,
                Length = 4096,
                StripIndex = 0,
                StripOffset = 0,
            },
            Mapping = new ToyMappingConfig
            {
                Width = 128,
                Height = 32,
                Mode = "TopDownAlternateRightLeft",
            },
            OutputTargets =
            [
                new ToyAdapterTargetConfig
                {
                    Adapter = "viewer",
                    Enabled = true,
                },
                new ToyAdapterTargetConfig
                {
                    Adapter = "pipe-broadcast",
                    Enabled = true,
                },
            ],
        };
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

    private static string ResolveTableOverrideIniPath(string settingsJsonPath, string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var expandedPath = Environment.ExpandEnvironmentVariables(configuredPath);
            if (Path.IsPathRooted(expandedPath))
            {
                return expandedPath;
            }

            var settingsDirectory = Path.GetDirectoryName(settingsJsonPath);
            return string.IsNullOrWhiteSpace(settingsDirectory)
                ? expandedPath
                : Path.Combine(settingsDirectory, expandedPath);
        }

        var settingsDirectory = Path.GetDirectoryName(settingsJsonPath);
        return string.IsNullOrWhiteSpace(settingsDirectory)
            ? "table-toy-overrides.ini"
            : Path.Combine(settingsDirectory, "table-toy-overrides.ini");
    }

    private static void Warn(string message)
    {
        Console.Error.WriteLine($"[config] WARN {message}");
        AppLogger.Warn($"[config] {message}");
    }
}
