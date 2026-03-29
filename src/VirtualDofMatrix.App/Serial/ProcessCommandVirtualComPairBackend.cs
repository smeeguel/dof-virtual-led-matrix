using System.Diagnostics;
using System.IO.Ports;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Serial;

/// <summary>
/// Executes user-configured shell commands to activate/deactivate a virtual COM pair.
/// Use placeholders {tx} and {rx} inside command templates.
/// Example activation command:
///   powershell -ExecutionPolicy Bypass -File "C:\\Tools\\vspe-create.ps1" -Tx {tx} -Rx {rx}
/// </summary>
public sealed class ProcessCommandVirtualComPairBackend : IVirtualComPairBackend
{
    private readonly SerialConfig _config;
    private readonly Action<string> _log;

    public ProcessCommandVirtualComPairBackend(SerialConfig config, Action<string>? log = null)
    {
        _config = config;
        _log = log ?? Console.WriteLine;
    }

    public async Task<bool> TryActivatePairAsync(string transmitterPortName, string listenerPortName, CancellationToken cancellationToken)
    {
        var commandTemplate = _config.VirtualPairActivateCommand;
        if (string.IsNullOrWhiteSpace(commandTemplate))
        {
            _log("[VirtualCom] No activation command configured; cannot create virtual COM pair.");
            return false;
        }

        var command = ExpandTemplate(commandTemplate, transmitterPortName, listenerPortName);
        _log($"[VirtualCom] Executing activation command for {transmitterPortName}/{listenerPortName}.");

        var exitCode = await ExecuteShellCommandAsync(command, _config.VirtualPairCommandTimeoutMs, cancellationToken);
        if (exitCode != 0)
        {
            _log($"[VirtualCom] Activation command failed with exit code {exitCode}.");
            return false;
        }

        var activePorts = SerialPort.GetPortNames();
        var txExists = activePorts.Any(p => string.Equals(p, transmitterPortName, StringComparison.OrdinalIgnoreCase));
        var rxExists = activePorts.Any(p => string.Equals(p, listenerPortName, StringComparison.OrdinalIgnoreCase));

        if (!txExists || !rxExists)
        {
            _log($"[VirtualCom] Activation command completed, but ports were not detected ({transmitterPortName}/{listenerPortName}).");
            return false;
        }

        return true;
    }

    public async Task DeactivatePairAsync(string transmitterPortName, string listenerPortName, CancellationToken cancellationToken)
    {
        var commandTemplate = _config.VirtualPairDeactivateCommand;
        if (string.IsNullOrWhiteSpace(commandTemplate))
        {
            _log("[VirtualCom] No deactivation command configured; skipping virtual pair deactivation.");
            return;
        }

        var command = ExpandTemplate(commandTemplate, transmitterPortName, listenerPortName);
        _log($"[VirtualCom] Executing deactivation command for {transmitterPortName}/{listenerPortName}.");

        var exitCode = await ExecuteShellCommandAsync(command, _config.VirtualPairCommandTimeoutMs, cancellationToken);
        if (exitCode != 0)
        {
            _log($"[VirtualCom] Deactivation command failed with exit code {exitCode}.");
        }
    }

    private static string ExpandTemplate(string template, string tx, string rx)
    {
        return template
            .Replace("{tx}", tx, StringComparison.OrdinalIgnoreCase)
            .Replace("{rx}", rx, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<int> ExecuteShellCommandAsync(string command, int timeoutMs, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/C {command}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        var waitForExitTask = process.WaitForExitAsync(cancellationToken);
        var completed = await Task.WhenAny(waitForExitTask, Task.Delay(timeoutMs, cancellationToken));
        if (completed != waitForExitTask)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort cleanup.
            }

            _log($"[VirtualCom] Command timed out after {timeoutMs}ms: {command}");
            return -1;
        }

        var output = await outputTask;
        var error = await errorTask;

        if (!string.IsNullOrWhiteSpace(output))
        {
            _log($"[VirtualCom] stdout: {output.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            _log($"[VirtualCom] stderr: {error.Trim()}");
        }

        return process.ExitCode;
    }
}
