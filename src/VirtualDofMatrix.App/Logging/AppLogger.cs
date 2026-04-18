using System.IO;
using System.Text;

namespace VirtualDofMatrix.App.Logging;

// Overview: AppLogger is intentionally tiny and file-based so protocol debugging works even on end-user cabinets.
internal static class AppLogger
{
    private static readonly object Gate = new();
    private static bool _enabled;
    private static string _logFilePath = Path.Combine(AppContext.BaseDirectory, "debug.log");

    public static void ClearForNewLaunch()
    {
        lock (Gate)
        {
            _logFilePath = Path.Combine(AppContext.BaseDirectory, "debug.log");
            Directory.CreateDirectory(Path.GetDirectoryName(_logFilePath) ?? AppContext.BaseDirectory);

            // Note: we wipe the prior session so each app launch starts with a clean debug.log.
            File.WriteAllText(_logFilePath, string.Empty, Encoding.UTF8);
        }
    }

    public static void Configure(bool enabled)
    {
        lock (Gate)
        {
            _enabled = enabled;
            _logFilePath = Path.Combine(AppContext.BaseDirectory, "debug.log");
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
        Directory.CreateDirectory(Path.GetDirectoryName(_logFilePath) ?? AppContext.BaseDirectory);
        File.AppendAllText(_logFilePath, line + Environment.NewLine, Encoding.UTF8);
    }
}
