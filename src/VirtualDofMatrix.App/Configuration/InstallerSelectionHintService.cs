using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VirtualDofMatrix.App.Configuration;

// Note: installer writes this lightweight hint so first app launch can honor
// detected DOF paths without forcing a manual folder picker.
public sealed class InstallerSelectionHintService
{
    public const string HintFileName = "installer-selections.json";

    public string? TryResolveDofConfigFolder(string installBaseDirectory)
    {
        if (string.IsNullOrWhiteSpace(installBaseDirectory))
        {
            return null;
        }

        var hintPath = Path.Combine(installBaseDirectory, HintFileName);
        if (!File.Exists(hintPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(hintPath);
            var hint = JsonSerializer.Deserialize<InstallerSelectionHint>(json);
            var candidate = hint?.DofConfigPath?.Trim();
            return string.IsNullOrWhiteSpace(candidate) ? null : candidate;
        }
        catch
        {
            // Keep startup resilient if hint file is missing/corrupt.
            return null;
        }
    }

    private sealed class InstallerSelectionHint
    {
        [JsonPropertyName("dofConfigPath")]
        public string? DofConfigPath { get; set; }
    }
}
