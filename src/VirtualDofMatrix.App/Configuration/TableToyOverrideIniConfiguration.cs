using System.IO;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Configuration;

// Overview: parses optional per-table toy visibility overrides from a dedicated sidecar INI.
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

            var toyOverrides = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in section.Value)
            {
                if (!pair.Key.StartsWith("toy:", StringComparison.OrdinalIgnoreCase)
                    || !pair.Key.EndsWith(".enabled", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var toyId = pair.Key[4..^8].Trim();
                if (string.IsNullOrWhiteSpace(toyId))
                {
                    continue;
                }

                if (TryParseBool(pair.Value, out var enabled))
                {
                    toyOverrides[toyId] = enabled;
                }
            }

            parsedOverrides.Add(new TableToyVisibilityOverrideConfig
            {
                TableKey = tableKey,
                ToyEnabledOverrides = toyOverrides,
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
            "; Managed by Virtual DOF Matrix. Per-table toy visibility overrides live here.",
            "; Section format: [table:<table-id>]",
            "; Entry format: toy:<toy-id>.enabled = true|false",
        };

        var sortedTables = (config.Routing.TableToyVisibilityOverrides ?? [])
            .Where(entry => !string.IsNullOrWhiteSpace(entry.TableKey))
            .OrderBy(entry => entry.TableKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var tableOverride in sortedTables)
        {
            lines.Add(string.Empty);
            lines.Add($"[table:{tableOverride.TableKey}]");

            var sortedToys = (tableOverride.ToyEnabledOverrides ?? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase))
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var toyEntry in sortedToys)
            {
                lines.Add($"toy:{toyEntry.Key}.enabled = {toyEntry.Value.ToString().ToLowerInvariant()}");
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
}
