using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace VirtualDofMatrix.App.Configuration;

public sealed class TableContextService
{
    private static readonly Regex VpxPathRegex = new("\"(?<quoted>[^\"]+\\.vpx)\"|(?<plain>[^\\s]+\\.vpx)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly Func<IReadOnlyList<string>> _vpxCommandLineProvider;

    private string? _explicitTableName;
    private string? _explicitRomName;
    private string _mappingCachePath = string.Empty;
    private DateTime _mappingCacheWriteUtc = DateTime.MinValue;
    private Dictionary<string, string> _tableToRom = new(StringComparer.OrdinalIgnoreCase);

    public TableContextService()
        : this(ReadVpxCommandLines)
    {
    }

    internal TableContextService(Func<IReadOnlyList<string>> vpxCommandLineProvider)
    {
        _vpxCommandLineProvider = vpxCommandLineProvider;
    }

    // Conversational note: explicit launch/control context is the top-priority source because it is app-supplied intent.
    public void UpdateExplicitContext(string? tableName, string? romName)
    {
        _explicitTableName = NormalizeTableName(tableName);
        _explicitRomName = NormalizeRomName(romName);
    }

    public TableContextSnapshot ResolveCurrentContext(string? dofConfigFolderPath)
    {
        if (!string.IsNullOrWhiteSpace(_explicitTableName) || !string.IsNullOrWhiteSpace(_explicitRomName))
        {
            var mappedRom = !string.IsNullOrWhiteSpace(_explicitRomName)
                ? _explicitRomName
                : MapRomFromTable(_explicitTableName, dofConfigFolderPath);

            return new TableContextSnapshot(
                TableName: _explicitTableName,
                RomName: mappedRom,
                Source: !string.IsNullOrWhiteSpace(mappedRom) && string.IsNullOrWhiteSpace(_explicitRomName) ? "mapping" : "args",
                Confidence: !string.IsNullOrWhiteSpace(_explicitRomName) ? "high" : "medium");
        }

        var vpxTableName = InferTableNameFromRunningVpx();
        if (!string.IsNullOrWhiteSpace(vpxTableName))
        {
            var mappedRom = MapRomFromTable(vpxTableName, dofConfigFolderPath);
            return new TableContextSnapshot(
                TableName: vpxTableName,
                RomName: mappedRom,
                Source: !string.IsNullOrWhiteSpace(mappedRom) ? "mapping" : "vpx-cmdline",
                Confidence: !string.IsNullOrWhiteSpace(mappedRom) ? "medium" : "low");
        }

        return TableContextSnapshot.Global();
    }

    private string? InferTableNameFromRunningVpx()
    {
        try
        {
            var commandLines = _vpxCommandLineProvider();
            foreach (var commandLine in commandLines)
            {
                if (string.IsNullOrWhiteSpace(commandLine))
                {
                    continue;
                }

                var match = VpxPathRegex.Match(commandLine);
                if (!match.Success)
                {
                    continue;
                }

                var rawPath = match.Groups["quoted"].Success ? match.Groups["quoted"].Value : match.Groups["plain"].Value;
                var fileName = Path.GetFileNameWithoutExtension(rawPath);
                var normalized = NormalizeTableName(fileName);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    return normalized;
                }
            }
        }
        catch
        {
            // Best-effort inference should never crash the app; unknown context is a valid fallback.
        }

        return null;
    }

    private string? MapRomFromTable(string? tableName, string? dofConfigFolderPath)
    {
        if (string.IsNullOrWhiteSpace(tableName))
        {
            return null;
        }

        EnsureMappingCache(dofConfigFolderPath);
        var normalized = NormalizeTableName(tableName);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        return _tableToRom.TryGetValue(normalized, out var rom) ? rom : null;
    }

    private void EnsureMappingCache(string? dofConfigFolderPath)
    {
        var mappingPath = ResolveTableMappingPath(dofConfigFolderPath);
        if (string.IsNullOrWhiteSpace(mappingPath) || !File.Exists(mappingPath))
        {
            _tableToRom = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _mappingCachePath = string.Empty;
            _mappingCacheWriteUtc = DateTime.MinValue;
            return;
        }

        var writeTime = File.GetLastWriteTimeUtc(mappingPath);
        if (mappingPath.Equals(_mappingCachePath, StringComparison.OrdinalIgnoreCase) && writeTime == _mappingCacheWriteUtc)
        {
            return;
        }

        var document = XDocument.Load(mappingPath);
        var mappings = document
            .Descendants()
            .Where(x => x.Name.LocalName == "Mapping")
            .Select(x => new
            {
                TableName = NormalizeTableName(x.Elements().FirstOrDefault(e => e.Name.LocalName == "TableName")?.Value),
                RomName = NormalizeRomName(x.Elements().FirstOrDefault(e => e.Name.LocalName == "RomName")?.Value),
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.TableName) && !string.IsNullOrWhiteSpace(x.RomName))
            .ToDictionary(x => x.TableName!, x => x.RomName!, StringComparer.OrdinalIgnoreCase);

        _tableToRom = mappings;
        _mappingCachePath = mappingPath;
        _mappingCacheWriteUtc = writeTime;
    }

    private static string? ResolveTableMappingPath(string? dofConfigFolderPath)
    {
        if (!string.IsNullOrWhiteSpace(dofConfigFolderPath))
        {
            var preferred = Path.Combine(dofConfigFolderPath, "tablemappings.xml");
            if (File.Exists(preferred))
            {
                return preferred;
            }
        }

        var localFallback = Path.Combine(AppContext.BaseDirectory, "DOF", "Config", "tablemappings.xml");
        if (File.Exists(localFallback))
        {
            return localFallback;
        }

        return null;
    }

    internal static (string? TableName, string? RomName) ParseExplicitArgs(IEnumerable<string> args)
    {
        var tableName = TryGetNamedArg(args, "--table")
            ?? TryGetNamedArg(args, "--table-name")
            ?? TryGetNamedArg(args, "table");
        var romName = TryGetNamedArg(args, "--rom")
            ?? TryGetNamedArg(args, "--rom-name")
            ?? TryGetNamedArg(args, "rom");

        return (NormalizeTableName(tableName), NormalizeRomName(romName));
    }

    private static string? TryGetNamedArg(IEnumerable<string> args, string expectedKey)
    {
        var values = args as string[] ?? args.ToArray();
        for (var i = 0; i < values.Length; i++)
        {
            var current = values[i];
            if (string.IsNullOrWhiteSpace(current))
            {
                continue;
            }

            if (current.Equals(expectedKey, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < values.Length && !string.IsNullOrWhiteSpace(values[i + 1]))
                {
                    return values[i + 1];
                }

                continue;
            }

            var delimiterIndex = current.IndexOf('=');
            if (delimiterIndex <= 0)
            {
                continue;
            }

            var key = current[..delimiterIndex];
            if (!key.Equals(expectedKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = current[(delimiterIndex + 1)..];
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static string? NormalizeTableName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = Path.GetFileNameWithoutExtension(value.Trim());
        normalized = normalized.Replace('_', ' ');
        normalized = Regex.Replace(normalized, "\\s+", " ").Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string? NormalizeRomName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    private static IReadOnlyList<string> ReadVpxCommandLines()
    {
        var lines = new List<string>();

        try
        {
            // Conversational note: WMI is the most reliable way to read another process command line on Windows.
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Name, CommandLine FROM Win32_Process WHERE Name LIKE 'VPinball%.exe'");
            foreach (var result in searcher.Get())
            {
                var commandLine = result["CommandLine"]?.ToString();
                if (!string.IsNullOrWhiteSpace(commandLine))
                {
                    lines.Add(commandLine);
                }
            }
        }
        catch
        {
            // Ignore; fallback remains "unknown".
        }

        return lines;
    }
}

public sealed record TableContextSnapshot(
    string? TableName,
    string? RomName,
    string Source,
    string Confidence)
{
    public static TableContextSnapshot Global() => new(null, null, "unknown", "low");

    public bool HasActiveTable => !string.IsNullOrWhiteSpace(TableName) || !string.IsNullOrWhiteSpace(RomName);

    public string BuildScopeLabel()
    {
        if (!HasActiveTable)
        {
            return "Global";
        }

        var tableOrRom = !string.IsNullOrWhiteSpace(TableName) ? TableName : RomName;
        return $"Table/ROM: {tableOrRom}";
    }

    public string BuildProvenanceLabel() => $"{Source}, {Confidence}";
}
