using System.Text.Json;

namespace VirtualDofMatrix.Core.Diagnostics;

public static class StructuredLogWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "VirtualDofMatrix",
        "logs");

    private static readonly string LogFilePath = Path.Combine(LogDirectory, $"virtual-dof-matrix-{DateTime.UtcNow:yyyyMMdd}.log");

    public static async Task WriteAsync(string source, string eventName, object? data = null, CancellationToken cancellationToken = default)
    {
        var payload = new StructuredLogEntry(DateTimeOffset.UtcNow, source, eventName, data);
        var line = JsonSerializer.Serialize(payload, JsonOptions);

        Directory.CreateDirectory(LogDirectory);

        await Gate.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(LogFilePath, line + Environment.NewLine, cancellationToken);
        }
        finally
        {
            Gate.Release();
        }
    }

    public static string GetLogDirectory() => LogDirectory;

    private sealed record StructuredLogEntry(DateTimeOffset TimestampUtc, string Source, string EventName, object? Data);
}
