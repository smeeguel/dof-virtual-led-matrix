using System.Diagnostics;

namespace VirtualDofMatrix.App.Serial;

public sealed class LegacyProcessVirtualComPairBackend : IVirtualComPairBackend
{
    private readonly VirtualComProvisioningConfig _config;

    public LegacyProcessVirtualComPairBackend(VirtualComProvisioningConfig config)
    {
        _config = config;
    }

    public Task CreatePairAsync(string txPort, string rxPort, CancellationToken cancellationToken = default)
        => RunCommandAsync(_config.LegacyCommands.CreatePairCommand, txPort, rxPort, cancellationToken);

    public Task DeletePairAsync(string txPort, string rxPort, CancellationToken cancellationToken = default)
        => RunCommandAsync(_config.LegacyCommands.DeletePairCommand, txPort, rxPort, cancellationToken);

    public async Task<IReadOnlyList<VirtualComPairInfo>> ListPairsAsync(CancellationToken cancellationToken = default)
    {
        var output = await RunCommandAsync(_config.LegacyCommands.ListPairsCommand, cancellationToken: cancellationToken);
        var results = new List<VirtualComPairInfo>();

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length >= 2)
            {
                results.Add(new VirtualComPairInfo(parts[0], parts[1], Driver: "legacy-process", Status: parts.Length >= 3 ? parts[2] : null));
            }
        }

        return results;
    }

    public async Task<VirtualComHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        var output = await RunCommandAsync(_config.LegacyCommands.HealthCommand, cancellationToken: cancellationToken);
        var message = output.Trim();
        var healthy = !string.IsNullOrWhiteSpace(message) &&
                      (message.Contains("ok", StringComparison.OrdinalIgnoreCase) ||
                       message.Contains("healthy", StringComparison.OrdinalIgnoreCase));

        return new VirtualComHealth(healthy, message);
    }

    private async Task<string> RunCommandAsync(string template, string? txPort = null, string? rxPort = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            throw new InvalidOperationException("Legacy virtual COM command template is not configured.");
        }

        var command = template
            .Replace("{txPort}", txPort ?? string.Empty, StringComparison.Ordinal)
            .Replace("{rxPort}", rxPort ?? string.Empty, StringComparison.Ordinal);

        var startInfo = new ProcessStartInfo
        {
            FileName = _config.LegacyCommands.ShellExecutable,
            Arguments = string.Format(_config.LegacyCommands.ShellArgumentFormat, command),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start legacy process backend command.");

        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Legacy virtual COM command failed with exit code {process.ExitCode}: {stdErr}");
        }

        return stdOut;
    }
}
