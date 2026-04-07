using System.IO;
using System.Globalization;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Configuration;

// Overview: parses optional toy INI files so toy routing/visual settings can be documented with comments.
internal static class ToyIniConfiguration
{
    public static bool ApplyFromIni(AppConfig config, string iniPath)
    {
        if (!File.Exists(iniPath))
        {
            return false;
        }

        var sections = ParseSections(File.ReadAllLines(iniPath));
        var modified = false;

        if (sections.TryGetValue("policy", out var policyValues))
        {
            modified |= ApplyPolicy(config.Routing.Policy, policyValues);
        }

        foreach (var section in sections)
        {
            if (!section.Key.StartsWith("toy:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var toyId = section.Key[4..].Trim();
            if (string.IsNullOrWhiteSpace(toyId))
            {
                continue;
            }

            modified |= ApplyToySection(config, toyId, section.Value);
        }

        return modified;
    }

    public static void SaveToIni(AppConfig config, string iniPath)
    {
        var directory = Path.GetDirectoryName(iniPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var lines = new List<string>
        {
            "; Managed by Virtual DOF Matrix. Toy/window edits are persisted here.",
            string.Empty,
            "[policy]",
            "; onMissingData options: drop | partial-black-fill | hold-last (example: partial-black-fill)",
            $"onMissingData = {config.Routing.Policy.OnMissingData}",
            "; onOversizeRange options: reject-config | clamp (example: clamp)",
            $"onOversizeRange = {config.Routing.Policy.OnOversizeRange}",
            "; onFrameRateSpike options: latest-wins | drop-oldest (example: latest-wins)",
            $"onFrameRateSpike = {config.Routing.Policy.OnFrameRateSpike}",
            "; defaultStripLength options: positive integer (example: 1100)",
            $"defaultStripLength = {config.Routing.Policy.DefaultStripLength}",
        };

        foreach (var toy in config.Routing.Toys)
        {
            lines.Add(string.Empty);
            lines.Add($"[toy:{toy.Id}]");
            lines.Add("; --- identity ---");
            lines.Add("; enabled options: true | false (example: true)");
            lines.Add($"enabled = {toy.Enabled.ToString().ToLowerInvariant()}");
            lines.Add("; kind options: matrix | topper | flasher | <custom-name> (example: matrix)");
            lines.Add($"kind = {toy.Kind}");
            lines.Add(string.Empty);
            lines.Add("; --- matrix geometry ---");
            lines.Add("; width options: positive integer (example: 128)");
            lines.Add($"width = {toy.Mapping.Width}");
            lines.Add("; height options: positive integer (example: 32)");
            lines.Add($"height = {toy.Mapping.Height}");
            lines.Add("; mapping options: TopDownAlternateRightLeft | RowMajor | ColumnMajor (example: TopDownAlternateRightLeft)");
            lines.Add($"mapping = {toy.Mapping.Mode}");
            lines.Add(string.Empty);
            lines.Add("; --- source routing ---");
            lines.Add("; sourceCanonicalStart options: integer >= 0 (example: 0)");
            lines.Add($"sourceCanonicalStart = {toy.Source.CanonicalStart ?? 0}");
            lines.Add("; sourceLength options: integer > 0 (example: 4096 for 128x32)");
            lines.Add($"sourceLength = {toy.Source.Length}");
            lines.Add("; sourceStripIndex options: integer >= 0 (optional, example: 0)");
            lines.Add(toy.Source.StripIndex.HasValue ? $"sourceStripIndex = {toy.Source.StripIndex.Value}" : "; sourceStripIndex = 0");

            lines.Add("; sourceStripOffset options: integer >= 0 (optional, example: 0)");
            lines.Add(toy.Source.StripOffset.HasValue ? $"sourceStripOffset = {toy.Source.StripOffset.Value}" : "; sourceStripOffset = 0");

            lines.Add(string.Empty);
            lines.Add("; --- window placement ---");
            lines.Add("; windowLeft options: number (pixels, example: 10)");
            lines.Add(toy.Window.Left.HasValue ? $"windowLeft = {toy.Window.Left.Value.ToString(CultureInfo.InvariantCulture)}" : "; windowLeft = 10");

            lines.Add("; windowTop options: number (pixels, example: 6)");
            lines.Add(toy.Window.Top.HasValue ? $"windowTop = {toy.Window.Top.Value.ToString(CultureInfo.InvariantCulture)}" : "; windowTop = 6");

            lines.Add("; windowWidth options: number > 0 (pixels, example: 1412)");
            lines.Add(toy.Window.Width.HasValue ? $"windowWidth = {toy.Window.Width.Value.ToString(CultureInfo.InvariantCulture)}" : "; windowWidth = 1412");

            lines.Add("; windowHeight options: number > 0 (pixels, example: 353)");
            lines.Add(toy.Window.Height.HasValue ? $"windowHeight = {toy.Window.Height.Value.ToString(CultureInfo.InvariantCulture)}" : "; windowHeight = 353");

            lines.Add("; windowAlwaysOnTop options: true | false (example: true)");
            lines.Add($"windowAlwaysOnTop = {toy.Window.AlwaysOnTop.ToString().ToLowerInvariant()}");
            lines.Add("; windowBorderless options: true | false (example: true)");
            lines.Add($"windowBorderless = {toy.Window.Borderless.ToString().ToLowerInvariant()}");
            lines.Add(string.Empty);
            lines.Add("; --- render ---");
            lines.Add("; renderDotShape options: circle | square (example: circle)");
            lines.Add($"renderDotShape = {toy.Render.DotShape}");
            lines.Add("; renderMinDotSpacing options: integer >= 0 (example: 2)");
            lines.Add($"renderMinDotSpacing = {toy.Render.MinDotSpacing}");
            lines.Add("; fillGapEnabled options: true | false (example: false; true stretches dots to fill available viewport space)");
            lines.Add($"fillGapEnabled = {toy.Render.FillGapEnabled.ToString().ToLowerInvariant()}");
            lines.Add("; renderBrightness options: number 0.0..1.0 (example: 1.0)");
            lines.Add($"renderBrightness = {toy.Render.Brightness.ToString(CultureInfo.InvariantCulture)}");
            lines.Add("; renderGamma options: number > 0 (example: 0.8)");
            lines.Add($"renderGamma = {toy.Render.Gamma.ToString(CultureInfo.InvariantCulture)}");
            lines.Add(string.Empty);
            lines.Add("; --- bloom ---");
            lines.Add("; bloomEnabled options: true | false (example: true)");
            lines.Add($"bloomEnabled = {toy.Bloom.Enabled.ToString().ToLowerInvariant()}");
            lines.Add("; bloomThreshold options: number 0.0..1.0 (example: 0.72)");
            lines.Add($"bloomThreshold = {toy.Bloom.Threshold.ToString(CultureInfo.InvariantCulture)}");
            lines.Add("; bloomSoftKnee options: number 0.0..1.0 (example: 0.18)");
            lines.Add($"bloomSoftKnee = {toy.Bloom.SoftKnee.ToString(CultureInfo.InvariantCulture)}");
            lines.Add("; bloomNearRadiusPx options: integer >= 0 (example: 2)");
            lines.Add($"bloomNearRadiusPx = {toy.Bloom.NearRadiusPx}");
            lines.Add("; bloomFarRadiusPx options: integer >= 0 (example: 10)");
            lines.Add($"bloomFarRadiusPx = {toy.Bloom.FarRadiusPx}");
            lines.Add("; bloomNearStrength options: number >= 0 (example: 1.0)");
            lines.Add($"bloomNearStrength = {toy.Bloom.NearStrength.ToString(CultureInfo.InvariantCulture)}");
            lines.Add("; bloomFarStrength options: number >= 0 (example: 0.2)");
            lines.Add($"bloomFarStrength = {toy.Bloom.FarStrength.ToString(CultureInfo.InvariantCulture)}");

            var outputs = toy.OutputTargets
                .Where(target => target.Enabled && !string.IsNullOrWhiteSpace(target.Adapter))
                .Select(target => target.Adapter)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            // Conversational note: we keep outputTargets compact, but document known adapters so generated INI stays self-explanatory.
            lines.Add("; outputTargets options: comma list of adapter names (known: viewer | pipe-broadcast, example: viewer,pipe-broadcast)");
            lines.Add($"outputTargets = {string.Join(',', outputs)}");
        }

        // Conversational note: this write keeps toy/window edits living in toys.ini instead of drifting back into settings.json.
        File.WriteAllLines(iniPath, lines);
    }

    private static Dictionary<string, Dictionary<string, string>> ParseSections(IEnumerable<string> lines)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith(";") || line.StartsWith("#"))
            {
                continue;
            }

            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                var name = line[1..^1].Trim();
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                sections[name] = current;
                continue;
            }

            if (current is null)
            {
                continue;
            }

            var equalsIndex = line.IndexOf('=');
            if (equalsIndex <= 0)
            {
                continue;
            }

            var key = line[..equalsIndex].Trim();
            var value = line[(equalsIndex + 1)..].Trim();
            current[key] = value;
        }

        return sections;
    }

    private static bool ApplyPolicy(RoutingPolicyConfig policy, Dictionary<string, string> values)
    {
        var modified = false;
        modified |= SetIfPresent(values, "onMissingData", value => policy.OnMissingData = value);
        modified |= SetIfPresent(values, "onOversizeRange", value => policy.OnOversizeRange = value);
        modified |= SetIfPresent(values, "onFrameRateSpike", value => policy.OnFrameRateSpike = value);
        modified |= SetIfPresent(values, "defaultStripLength", value => policy.DefaultStripLength = ParseInt(value, policy.DefaultStripLength));
        return modified;
    }

    private static bool ApplyToySection(AppConfig config, string toyId, Dictionary<string, string> values)
    {
        var toy = config.Routing.Toys.FirstOrDefault(t => string.Equals(t.Id, toyId, StringComparison.OrdinalIgnoreCase));
        if (toy is null)
        {
            toy = new ToyRouteConfig { Id = toyId };
            config.Routing.Toys.Add(toy);
        }

        var modified = false;

        modified |= SetIfPresent(values, "enabled", value => toy.Enabled = ParseBool(value, toy.Enabled));
        modified |= SetIfPresent(values, "kind", value => toy.Kind = value);
        modified |= SetIfPresent(values, "width", value => toy.Mapping.Width = ParseInt(value, toy.Mapping.Width));
        modified |= SetIfPresent(values, "height", value => toy.Mapping.Height = ParseInt(value, toy.Mapping.Height));
        modified |= SetIfPresent(values, "mapping", value => toy.Mapping.Mode = value);

        modified |= SetIfPresent(values, "sourceCanonicalStart", value => toy.Source.CanonicalStart = ParseInt(value, toy.Source.CanonicalStart ?? 0));
        modified |= SetIfPresent(values, "sourceLength", value => toy.Source.Length = ParseInt(value, toy.Source.Length));
        modified |= SetIfPresent(values, "sourceStripIndex", value => toy.Source.StripIndex = ParseInt(value, toy.Source.StripIndex ?? 0));
        modified |= SetIfPresent(values, "sourceStripOffset", value => toy.Source.StripOffset = ParseInt(value, toy.Source.StripOffset ?? 0));

        modified |= SetIfPresent(values, "windowLeft", value => toy.Window.Left = ParseDouble(value, toy.Window.Left ?? 0));
        modified |= SetIfPresent(values, "windowTop", value => toy.Window.Top = ParseDouble(value, toy.Window.Top ?? 0));
        modified |= SetIfPresent(values, "windowWidth", value => toy.Window.Width = ParseDouble(value, toy.Window.Width ?? 0));
        modified |= SetIfPresent(values, "windowHeight", value => toy.Window.Height = ParseDouble(value, toy.Window.Height ?? 0));
        modified |= SetIfPresent(values, "windowAlwaysOnTop", value => toy.Window.AlwaysOnTop = ParseBool(value, toy.Window.AlwaysOnTop));
        modified |= SetIfPresent(values, "windowBorderless", value => toy.Window.Borderless = ParseBool(value, toy.Window.Borderless));

        modified |= SetIfPresent(values, "renderDotShape", value => toy.Render.DotShape = value);
        modified |= SetIfPresent(values, "renderMinDotSpacing", value => toy.Render.MinDotSpacing = ParseInt(value, toy.Render.MinDotSpacing));
        modified |= SetIfPresent(values, "fillGapEnabled", value => toy.Render.FillGapEnabled = ParseBool(value, toy.Render.FillGapEnabled));
        modified |= SetIfPresent(values, "renderBrightness", value => toy.Render.Brightness = ParseDouble(value, toy.Render.Brightness));
        modified |= SetIfPresent(values, "renderGamma", value => toy.Render.Gamma = ParseDouble(value, toy.Render.Gamma));

        modified |= SetIfPresent(values, "bloomEnabled", value => toy.Bloom.Enabled = ParseBool(value, toy.Bloom.Enabled));
        modified |= SetIfPresent(values, "bloomThreshold", value => toy.Bloom.Threshold = ParseDouble(value, toy.Bloom.Threshold));
        modified |= SetIfPresent(values, "bloomSoftKnee", value => toy.Bloom.SoftKnee = ParseDouble(value, toy.Bloom.SoftKnee));
        modified |= SetIfPresent(values, "bloomNearRadiusPx", value => toy.Bloom.NearRadiusPx = ParseInt(value, toy.Bloom.NearRadiusPx));
        modified |= SetIfPresent(values, "bloomFarRadiusPx", value => toy.Bloom.FarRadiusPx = ParseInt(value, toy.Bloom.FarRadiusPx));
        modified |= SetIfPresent(values, "bloomNearStrength", value => toy.Bloom.NearStrength = ParseDouble(value, toy.Bloom.NearStrength));
        modified |= SetIfPresent(values, "bloomFarStrength", value => toy.Bloom.FarStrength = ParseDouble(value, toy.Bloom.FarStrength));

        if (values.TryGetValue("outputTargets", out var outputTargetsValue))
        {
            var targets = outputTargetsValue
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (targets.Length > 0)
            {
                toy.OutputTargets = targets
                    .Select(target => new ToyAdapterTargetConfig { Adapter = target, Enabled = true })
                    .ToList();
                modified = true;
            }
        }

        return modified;
    }

    private static bool SetIfPresent(Dictionary<string, string> values, string key, Action<string> apply)
    {
        if (!values.TryGetValue(key, out var value))
        {
            return false;
        }

        apply(value);
        return true;
    }

    private static int ParseInt(string raw, int fallback)
        => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static double ParseDouble(string raw, double fallback)
        => double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static bool ParseBool(string raw, bool fallback)
    {
        if (bool.TryParse(raw, out var parsed))
        {
            return parsed;
        }

        return raw switch
        {
            "1" => true,
            "0" => false,
            _ => fallback,
        };
    }
}
