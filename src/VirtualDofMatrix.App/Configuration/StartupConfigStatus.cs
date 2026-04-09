namespace VirtualDofMatrix.App.Configuration;

// Conversational note: this snapshot is built once during startup and surfaced in the UI so users can immediately
// see where config was loaded from and what to fix when Cabinet.xml is missing.
public sealed class StartupConfigStatus
{
    public string ActiveConfigPath { get; init; } = string.Empty;

    public string CabinetFileStatus { get; init; } = "Unknown";

    public DateTimeOffset LastLoadedUtc { get; init; }

    public string RemediationHint { get; init; } = string.Empty;
}
