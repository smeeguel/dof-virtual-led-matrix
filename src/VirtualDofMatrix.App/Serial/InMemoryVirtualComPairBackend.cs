using System.Collections.Concurrent;
using System.IO.Ports;

namespace VirtualDofMatrix.App.Serial;

/// <summary>
/// Process-local virtual COM pair backend used as a swappable default.
/// It models allocation state and refuses activation for ports that are already active on the host.
/// </summary>
public sealed class InMemoryVirtualComPairBackend : IVirtualComPairBackend
{
    private readonly ConcurrentDictionary<string, byte> _activatedPorts = new(StringComparer.OrdinalIgnoreCase);

    public Task<bool> TryActivatePairAsync(
        string transmitterPortName,
        string listenerPortName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (IsSystemPortActive(transmitterPortName) || IsSystemPortActive(listenerPortName))
        {
            return Task.FromResult(false);
        }

        var txAdded = _activatedPorts.TryAdd(transmitterPortName, 0);
        var rxAdded = _activatedPorts.TryAdd(listenerPortName, 0);

        if (!txAdded || !rxAdded)
        {
            if (txAdded)
            {
                _activatedPorts.TryRemove(transmitterPortName, out _);
            }

            if (rxAdded)
            {
                _activatedPorts.TryRemove(listenerPortName, out _);
            }

            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }

    public Task DeactivatePairAsync(
        string transmitterPortName,
        string listenerPortName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _activatedPorts.TryRemove(transmitterPortName, out _);
        _activatedPorts.TryRemove(listenerPortName, out _);
        return Task.CompletedTask;
    }

    private static bool IsSystemPortActive(string portName)
    {
        var activePorts = SerialPort.GetPortNames();
        return activePorts.Any(p => string.Equals(p, portName, StringComparison.OrdinalIgnoreCase));
    }
}
