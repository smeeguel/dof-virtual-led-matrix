using Microsoft.Win32;

namespace VirtualDofMatrix.App.Configuration;

// Note: installer stores first-run DOF bootstrap values in HKLM so the app can
// honor installer-detected paths without leaving payload files in Program Files.
public sealed class InstallerSelectionRegistryHintService
{
    internal const string RegistryPath = @"Software\VirtualDofMatrix";
    internal const string DofConfigPathValueName = "DofConfigPath";

    private readonly Func<string?> _registryValueReader;

    public InstallerSelectionRegistryHintService()
        : this(ReadDofConfigPathFromRegistry)
    {
    }

    internal InstallerSelectionRegistryHintService(Func<string?> registryValueReader)
    {
        _registryValueReader = registryValueReader ?? throw new ArgumentNullException(nameof(registryValueReader));
    }

    public string? TryResolveDofConfigFolder()
    {
        try
        {
            var candidate = _registryValueReader.Invoke();
            return string.IsNullOrWhiteSpace(candidate) ? null : candidate.Trim();
        }
        catch
        {
            // Keep startup resilient if registry access fails for any reason.
            return null;
        }
    }

    private static string? ReadDofConfigPathFromRegistry()
    {
        // Probe both registry views so x64 installer writes are visible to AnyCPU/x86 runtime permutations.
        var views = new[] { RegistryView.Registry64, RegistryView.Registry32 };
        for (var i = 0; i < views.Length; i++)
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, views[i]);
            using var productKey = baseKey.OpenSubKey(RegistryPath, writable: false);
            var value = productKey?.GetValue(DofConfigPathValueName) as string;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
