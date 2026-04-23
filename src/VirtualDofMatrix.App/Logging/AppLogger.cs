using System.IO;
using System.Text;

namespace VirtualDofMatrix.App.Logging;

// Overview: AppLogger is intentionally tiny and file-based so protocol debugging works even on end-user cabinets.
internal static class AppLogger
{
    private static readonly object Gate = new();
    private static bool _enabled;
    private static string _logFilePath = BuildLogFilePath();

    // Note: logs now live under a per-user writable profile location to avoid write failures under Program Files.
    // Legacy debug.log files beside the executable are intentionally ignored from this point forward.
    private static string GetWritableLogRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            return Path.Combine(localAppData, "VirtualDofMatrix", "Logs");
        }

        // Safety fallback for unusual environments where LocalApplicationData is unavailable.
        return Path.Combine(Path.GetTempPath(), "VirtualDofMatrix", "Logs");
    }

    private static string BuildLogFilePath()
    {
        return Path.Combine(GetWritableLogRoot(), "debug.log");
    }

    public static void ClearForNewLaunch()
    {
        lock (Gate)
        {
            _logFilePath = BuildLogFilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(_logFilePath) ?? GetWritableLogRoot());

            // Note: we wipe the prior session so each app launch starts with a clean debug.log.
            File.WriteAllText(_logFilePath, string.Empty, Encoding.UTF8);
        }
    }

    public static void Configure(bool enabled)
    {
        lock (Gate)
        {
            _enabled = enabled;
            _logFilePath = BuildLogFilePath();
            if (!_enabled)
            {
                return;
            }

            // Note: we stamp a session marker so people can quickly tell when a new app run started.
            AppendLine($"--- logging enabled (pid={Environment.ProcessId}) ---");
        }
    }

    public static void SetEnabled(bool enabled)
    {
        Configure(enabled);
    }

    public static string GetLogFilePath()
    {
        lock (Gate)
        {
            // Note: expose the active runtime path so UI commands and diagnostics stay tied to one source of truth.
            return _logFilePath;
        }
    }

    public static void Info(string message)
    {
        WriteLine("INFO", message);
    }

    public static void Warn(string message)
    {
        WriteLine("WARN", message);
    }

    public static void Error(string message)
    {
        WriteLine("ERROR", message);
    }

    private static void WriteLine(string level, string message)
    {
        lock (Gate)
        {
            if (!_enabled)
            {
                return;
            }

            AppendLine($"[{DateTimeOffset.UtcNow:O}] [{level}] {message}");
        }
    }

    private static void AppendLine(string line)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_logFilePath) ?? GetWritableLogRoot());
        File.AppendAllText(_logFilePath, line + Environment.NewLine, Encoding.UTF8);
    }
}
