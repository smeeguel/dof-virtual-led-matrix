using System.IO;
using System.Globalization;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Configuration;

// Overview: parses optional per-table toy overrides from a dedicated sidecar INI.
// The sidecar schema uses "toy:<id>.<field>" keys so future nullable fields can be added without breaking.
internal static class TableToyOverrideIniConfiguration
{
    public static bool ApplyFromIni(AppConfig config, string iniPath)
    {
        if (!File.Exists(iniPath))
        {
            return false;
        }

        var sections = ParseSections(ReadIniLines(iniPath));
        var parsedOverrides = new List<TableToyVisibilityOverrideConfig>();
        foreach (var section in sections)
        {
            if (!section.Key.StartsWith("table:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var tableKey = section.Key[6..].Trim();
            if (string.IsNullOrWhiteSpace(tableKey))
            {
                continue;
            }

            var toyOverrides = new Dictionary<string, TableToyOverrideConfig>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in section.Value)
            {
                if (!TryParseToyFieldKey(pair.Key, out var toyId, out var fieldName))
                {
                    continue;
                }

                if (!toyOverrides.TryGetValue(toyId, out var toyOverride))
                {
                    toyOverride = new TableToyOverrideConfig();
                    toyOverrides[toyId] = toyOverride;
                }
                toyOverride.Window ??= new TableToyWindowOverrideConfig();

                // Note: parser intentionally ignores unknown/future keys so current builds stay forward-compatible.
                if (fieldName.Equals("enabled", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryParseBool(pair.Value, out var enabled))
                    {
                        toyOverride.Enabled = enabled;
                    }
                }
                else if (fieldName.Equals("window.left", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryParseNullableDouble(pair.Value, out var left))
                    {
                        toyOverride.Window.Left = left;
                    }
                }
                else if (fieldName.Equals("window.top", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryParseNullableDouble(pair.Value, out var top))
                    {
                        toyOverride.Window.Top = top;
                    }
                }
                else if (fieldName.Equals("window.width", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryParseNullableDouble(pair.Value, out var width))
                    {
                        toyOverride.Window.Width = width;
                    }
                }
                else if (fieldName.Equals("window.height", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryParseNullableDouble(pair.Value, out var height))
                    {
                        toyOverride.Window.Height = height;
                    }
                }
            }

            parsedOverrides.Add(new TableToyVisibilityOverrideConfig
            {
                TableKey = tableKey,
                ToyOverrides = toyOverrides,
            });
        }

        config.Routing.TableToyVisibilityOverrides = parsedOverrides
            .OrderBy(entry => entry.TableKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return true;
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
            "; Managed by Virtual DOF Matrix. Per-table toy overrides live here.",
            "; Section format: [table:<table-id>]",
            "; Entry format: toy:<toy-id>.<field> = <value>",
            "; MVP field: toy:<toy-id>.enabled = true|false",
            "; Reserved future fields: toy:<toy-id>.window.left/top/width/height = <double>",
        };

        var sortedTables = (config.Routing.TableToyVisibilityOverrides ?? [])
            .Where(entry => !string.IsNullOrWhiteSpace(entry.TableKey))
            .OrderBy(entry => entry.TableKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var tableOverride in sortedTables)
        {
            lines.Add(string.Empty);
            lines.Add($"[table:{tableOverride.TableKey}]");

            var sortedToys = (tableOverride.ToyOverrides ?? new Dictionary<string, TableToyOverrideConfig>(StringComparer.OrdinalIgnoreCase))
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var toyEntry in sortedToys)
            {
                var toyId = toyEntry.Key;
                var toyOverride = toyEntry.Value ?? new TableToyOverrideConfig();
                var windowOverride = toyOverride.Window ?? new TableToyWindowOverrideConfig();
                if (toyOverride.Enabled.HasValue)
                {
                    lines.Add($"toy:{toyId}.enabled = {toyOverride.Enabled.Value.ToString().ToLowerInvariant()}");
                }

                // Note: these reserved geometry keys are persisted when present, but ignored at runtime in MVP.
                if (windowOverride.Left.HasValue)
                {
                    lines.Add($"toy:{toyId}.window.left = {windowOverride.Left.Value.ToString(CultureInfo.InvariantCulture)}");
                }

                if (windowOverride.Top.HasValue)
                {
                    lines.Add($"toy:{toyId}.window.top = {windowOverride.Top.Value.ToString(CultureInfo.InvariantCulture)}");
                }

                if (windowOverride.Width.HasValue)
                {
                    lines.Add($"toy:{toyId}.window.width = {windowOverride.Width.Value.ToString(CultureInfo.InvariantCulture)}");
                }

                if (windowOverride.Height.HasValue)
                {
                    lines.Add($"toy:{toyId}.window.height = {windowOverride.Height.Value.ToString(CultureInfo.InvariantCulture)}");
                }
            }
        }

        File.WriteAllLines(iniPath, lines);
    }

    private static Dictionary<string, Dictionary<string, string>> ParseSections(IEnumerable<string> lines)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? currentSection = null;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith(";") || line.StartsWith("#"))
            {
                continue;
            }

            if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
            {
                var sectionName = line[1..^1].Trim();
                currentSection = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                sections[sectionName] = currentSection;
                continue;
            }

            if (currentSection is null)
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
            currentSection[key] = value;
        }

        return sections;
    }

    private static IEnumerable<string> ReadIniLines(string iniPath)
    {
        var raw = File.ReadAllText(iniPath);
        if (!raw.Contains('\n') && raw.Contains("\\n", StringComparison.Ordinal))
        {
            raw = raw
                .Replace("\\r\\n", "\n", StringComparison.Ordinal)
                .Replace("\\n", "\n", StringComparison.Ordinal);
        }

        return raw
            .Split('\n')
            .Select(line => line.TrimEnd('\r'));
    }

    private static bool TryParseBool(string raw, out bool value)
    {
        if (bool.TryParse(raw, out value))
        {
            return true;
        }

        switch (raw.Trim().ToLowerInvariant())
        {
            case "1":
                value = true;
                return true;
            case "0":
                value = false;
                return true;
            default:
                value = false;
                return false;
        }
    }

    private static bool TryParseToyFieldKey(string key, out string toyId, out string fieldName)
    {
        toyId = string.Empty;
        fieldName = string.Empty;
        if (!key.StartsWith("toy:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var normalized = key[4..].Trim();
        var dotIndex = normalized.IndexOf('.');
        if (dotIndex <= 0 || dotIndex >= normalized.Length - 1)
        {
            return false;
        }

        toyId = normalized[..dotIndex].Trim();
        fieldName = normalized[(dotIndex + 1)..].Trim();
        return !string.IsNullOrWhiteSpace(toyId) && !string.IsNullOrWhiteSpace(fieldName);
    }

    private static bool TryParseNullableDouble(string raw, out double? value)
    {
        value = null;
        var normalized = raw.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return true;
        }

        if (double.TryParse(normalized, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }
}
