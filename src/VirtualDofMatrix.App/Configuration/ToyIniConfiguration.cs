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
            $"onMissingData = {config.Routing.Policy.OnMissingData}",
            $"onOversizeRange = {config.Routing.Policy.OnOversizeRange}",
            $"onFrameRateSpike = {config.Routing.Policy.OnFrameRateSpike}",
            $"defaultStripLength = {config.Routing.Policy.DefaultStripLength}",
        };

        foreach (var toy in config.Routing.Toys)
        {
            lines.Add(string.Empty);
            lines.Add($"[toy:{toy.Id}]");
            lines.Add($"enabled = {toy.Enabled.ToString().ToLowerInvariant()}");
            lines.Add($"kind = {toy.Kind}");
            lines.Add($"width = {toy.Mapping.Width}");
            lines.Add($"height = {toy.Mapping.Height}");
            lines.Add($"mapping = {toy.Mapping.Mode}");
            lines.Add($"sourceCanonicalStart = {toy.Source.CanonicalStart ?? 0}");
            lines.Add($"sourceLength = {toy.Source.Length}");
            if (toy.Source.StripIndex.HasValue)
            {
                lines.Add($"sourceStripIndex = {toy.Source.StripIndex.Value}");
            }

            if (toy.Source.StripOffset.HasValue)
            {
                lines.Add($"sourceStripOffset = {toy.Source.StripOffset.Value}");
            }

            if (toy.Window.Left.HasValue)
            {
                lines.Add($"windowLeft = {toy.Window.Left.Value.ToString(CultureInfo.InvariantCulture)}");
            }

            if (toy.Window.Top.HasValue)
            {
                lines.Add($"windowTop = {toy.Window.Top.Value.ToString(CultureInfo.InvariantCulture)}");
            }

            if (toy.Window.Width.HasValue)
            {
                lines.Add($"windowWidth = {toy.Window.Width.Value.ToString(CultureInfo.InvariantCulture)}");
            }

            if (toy.Window.Height.HasValue)
            {
                lines.Add($"windowHeight = {toy.Window.Height.Value.ToString(CultureInfo.InvariantCulture)}");
            }

            lines.Add($"windowAlwaysOnTop = {toy.Window.AlwaysOnTop.ToString().ToLowerInvariant()}");
            lines.Add($"windowBorderless = {toy.Window.Borderless.ToString().ToLowerInvariant()}");
            lines.Add($"renderDotShape = {toy.Render.DotShape}");
            lines.Add($"renderMinDotSpacing = {toy.Render.MinDotSpacing}");
            lines.Add($"renderBrightness = {toy.Render.Brightness.ToString(CultureInfo.InvariantCulture)}");
            lines.Add($"renderGamma = {toy.Render.Gamma.ToString(CultureInfo.InvariantCulture)}");
            lines.Add($"bloomEnabled = {toy.Bloom.Enabled.ToString().ToLowerInvariant()}");
            lines.Add($"bloomThreshold = {toy.Bloom.Threshold.ToString(CultureInfo.InvariantCulture)}");
            lines.Add($"bloomSoftKnee = {toy.Bloom.SoftKnee.ToString(CultureInfo.InvariantCulture)}");
            lines.Add($"bloomNearRadiusPx = {toy.Bloom.NearRadiusPx}");
            lines.Add($"bloomFarRadiusPx = {toy.Bloom.FarRadiusPx}");
            lines.Add($"bloomNearStrength = {toy.Bloom.NearStrength.ToString(CultureInfo.InvariantCulture)}");
            lines.Add($"bloomFarStrength = {toy.Bloom.FarStrength.ToString(CultureInfo.InvariantCulture)}");

            var outputs = toy.OutputTargets
                .Where(target => target.Enabled && !string.IsNullOrWhiteSpace(target.Adapter))
                .Select(target => target.Adapter)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
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
