using System.IO.Ports;
using System.Text.RegularExpressions;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Serial;

public sealed class VirtualComPairManager : IVirtualComPairManager
{
    private static readonly Regex ComPortRegex = new("^COM(?<number>\\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly AppConfig _config;
    private readonly IVirtualComPairBackend _backend;
    private readonly Action<string> _log;

    public VirtualComPairManager(
        AppConfig config,
        IVirtualComPairBackend? backend = null,
        Action<string>? log = null)
    {
        _config = config;
        _backend = backend ?? new InMemoryVirtualComPairBackend();
        _log = log ?? Console.WriteLine;
    }

    public async Task<ComPairReservation> EnsureActivePairAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var persistedTx = _config.Serial.VirtualTransmitterPortName;
        var persistedRx = _config.Serial.VirtualListenerPortName;

        if (IsValidComPort(persistedTx) && IsValidComPort(persistedRx))
        {
            _log($"[VirtualCom] Requested persisted pair {persistedTx}/{persistedRx}.");

            if (await _backend.TryActivatePairAsync(persistedTx, persistedRx, cancellationToken))
            {
                _log($"[VirtualCom] Activation success for persisted pair {persistedTx}/{persistedRx}.");
                _config.Serial.PortName = persistedRx;
                return new ComPairReservation(persistedTx, persistedRx, DateTimeOffset.UtcNow, ReusedPersistedPair: true, FallbackPairAllocated: false);
            }

            _log($"[VirtualCom] Persisted pair conflict for {persistedTx}/{persistedRx}. Falling back to next available pair.");
        }

        var (transmitterPort, listenerPort) = await ReserveFallbackPairAsync(cancellationToken);

        _config.Serial.VirtualTransmitterPortName = transmitterPort;
        _config.Serial.VirtualListenerPortName = listenerPort;
        _config.Serial.PortName = listenerPort;

        _log($"[VirtualCom] Persisted pair updated to {transmitterPort}/{listenerPort}.");

        return new ComPairReservation(transmitterPort, listenerPort, DateTimeOffset.UtcNow, ReusedPersistedPair: false, FallbackPairAllocated: true);
    }

    public async Task DeactivateAsync(ComPairReservation reservation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _log($"[VirtualCom] Deactivating pair {reservation.TransmitterPortName}/{reservation.ListenerPortName}.");
        await _backend.DeactivatePairAsync(reservation.TransmitterPortName, reservation.ListenerPortName, cancellationToken);
        _log($"[VirtualCom] Deactivation complete for {reservation.TransmitterPortName}/{reservation.ListenerPortName}.");
    }

    private async Task<(string transmitterPort, string listenerPort)> ReserveFallbackPairAsync(CancellationToken cancellationToken)
    {
        var highestActivePort = GetHighestActiveComPortNumber();
        var candidateTx = highestActivePort + 1;
        var candidateRx = highestActivePort + 2;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var transmitterPort = FormatComPort(candidateTx);
            var listenerPort = FormatComPort(candidateRx);

            _log($"[VirtualCom] Trying fallback pair {transmitterPort}/{listenerPort}.");

            var activated = await _backend.TryActivatePairAsync(transmitterPort, listenerPort, cancellationToken);
            if (activated)
            {
                _log($"[VirtualCom] Activation success for fallback pair {transmitterPort}/{listenerPort}.");
                return (transmitterPort, listenerPort);
            }

            _log($"[VirtualCom] Pair conflict for {transmitterPort}/{listenerPort}. Advancing to next pair.");
            candidateTx += 2;
            candidateRx += 2;
        }
    }

    private static int GetHighestActiveComPortNumber()
    {
        var activePorts = SerialPort.GetPortNames();
        var highest = 0;

        foreach (var port in activePorts)
        {
            if (!TryParseComPortNumber(port, out var number))
            {
                continue;
            }

            if (number > highest)
            {
                highest = number;
            }
        }

        return highest;
    }

    private static string FormatComPort(int portNumber) => $"COM{portNumber}";

    private static bool IsValidComPort(string? portName) =>
        !string.IsNullOrWhiteSpace(portName) && TryParseComPortNumber(portName, out _);

    private static bool TryParseComPortNumber(string? portName, out int portNumber)
    {
        portNumber = 0;

        if (string.IsNullOrWhiteSpace(portName))
        {
            return false;
        }

        var match = ComPortRegex.Match(portName);
        if (!match.Success)
        {
            return false;
        }

        return int.TryParse(match.Groups["number"].Value, out portNumber) && portNumber > 0;
    }
}
