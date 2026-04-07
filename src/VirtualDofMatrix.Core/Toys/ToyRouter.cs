using System.Collections.Concurrent;
using System.Text;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.Core.Toys;

/// <summary>
/// Default v1 router that treats each incoming payload as the authoritative full frame.
/// </summary>
/// <remarks>
/// Conversational note: this engine intentionally does no incremental merge/replay in v1.
/// Every route call starts from a clean toy buffer and maps only what exists in the current payload.
/// </remarks>
public sealed class ToyRouter : IToyRouter
{
    private readonly RoutingPolicyConfig _policy;
    private readonly ConcurrentDictionary<string, ToyFrame> _lastFrameByToyId = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ulong> _lastSequenceByToyId = new(StringComparer.OrdinalIgnoreCase);

    public ToyRouter(RoutingPolicyConfig? policy = null)
    {
        _policy = policy ?? new RoutingPolicyConfig();
    }

    public ToyRoutingResult Route(
        ReadOnlySpan<byte> payload,
        RoutingFrameContext context,
        IReadOnlyList<ToyDefinition> toyDefinitions)
    {
        var frames = new List<ToyFrame>(toyDefinitions.Count);
        var diagnostics = new List<ToyRoutingDiagnostic>(toyDefinitions.Count);
        var globalFrameId = context.InputSequence;

        foreach (var toy in toyDefinitions)
        {
            try
            {
                RouteSingleToy(payload, context, globalFrameId, toy, frames, diagnostics);
            }
            catch (Exception ex)
            {
                // Conversational note: per-toy failure isolation is intentional so one bad toy never freezes the others.
                diagnostics.Add(new ToyRoutingDiagnostic(
                    toy.Id,
                    MappedLedCount: 0,
                    MissingBytes: 0,
                    PolicyAction: ToyRoutingPolicyAction.PerToyFailureIsolated,
                    Message: $"Toy routing failed in isolation: {ex.Message}"));
            }
        }

        return new ToyRoutingResult(globalFrameId, frames, diagnostics);
    }

    private void RouteSingleToy(
        ReadOnlySpan<byte> payload,
        RoutingFrameContext context,
        ulong globalFrameId,
        ToyDefinition toy,
        ICollection<ToyFrame> frames,
        ICollection<ToyRoutingDiagnostic> diagnostics)
    {
        if (!toy.Enabled)
        {
            diagnostics.Add(new ToyRoutingDiagnostic(
                toy.Id,
                MappedLedCount: 0,
                MissingBytes: 0,
                PolicyAction: ToyRoutingPolicyAction.SkippedDisabled,
                Message: "Toy is disabled in the routing plan."));
            return;
        }

        if (ShouldDropForFrameRatePolicy(context.InputSequence, toy.Id))
        {
            diagnostics.Add(new ToyRoutingDiagnostic(
                toy.Id,
                MappedLedCount: 0,
                MissingBytes: 0,
                PolicyAction: ToyRoutingPolicyAction.FrameDroppedLatestWins,
                Message: "Frame dropped because onFrameRateSpike=latest-wins and a newer/equal sequence already exists for this toy."));
            return;
        }

        var frame = BuildToyFrame(payload, context, toy, globalFrameId, out var diagnostic);
        diagnostics.Add(diagnostic);

        if (frame is not null)
        {
            frames.Add(frame);
            _lastFrameByToyId[toy.Id] = frame;
            _lastSequenceByToyId[toy.Id] = context.InputSequence;
        }
    }

    private bool ShouldDropForFrameRatePolicy(ulong inputSequence, string toyId)
    {
        if (!PolicyEquals(_policy.OnFrameRateSpike, "latest-wins"))
        {
            return false;
        }

        if (!_lastSequenceByToyId.TryGetValue(toyId, out var lastSequence))
        {
            return false;
        }

        return inputSequence <= lastSequence;
    }

    private ToyFrame? BuildToyFrame(
        ReadOnlySpan<byte> payload,
        RoutingFrameContext context,
        ToyDefinition toy,
        ulong globalFrameId,
        out ToyRoutingDiagnostic diagnostic)
    {
        var width = Math.Max(1, toy.Width);
        var height = Math.Max(1, toy.Height);
        var matrixLedCapacity = width * height;

        var canonicalStart = Math.Max(0, toy.SourceDescriptor.CanonicalStart);
        var requestedLedCount = Math.Max(0, toy.SourceDescriptor.Length);
        var mappedLedCount = requestedLedCount;

        if (requestedLedCount > matrixLedCapacity)
        {
            if (PolicyEquals(_policy.OnOversizeRange, "reject-config"))
            {
                diagnostic = new ToyRoutingDiagnostic(
                    toy.Id,
                    MappedLedCount: 0,
                    MissingBytes: 0,
                    PolicyAction: ToyRoutingPolicyAction.RejectedOversizeRange,
                    Message: $"Toy source length {requestedLedCount} exceeds matrix capacity {matrixLedCapacity}; rejected by onOversizeRange=reject-config.");
                return null;
            }

            mappedLedCount = matrixLedCapacity;
        }

        // Conversational note: v1 starts each output as black; policy decides whether missing bytes stay black, drop, or hold-last.
        var pixels = new Rgb24[matrixLedCapacity];
        var payloadStartByte = canonicalStart * 3;

        if (payloadStartByte >= payload.Length || mappedLedCount == 0)
        {
            return ApplyMissingDataPolicy(
                toy,
                context,
                globalFrameId,
                width,
                height,
                pixels,
                mappedLedCount,
                ToyRoutingPolicyAction.SourceOutOfRange,
                "Canonical source range starts outside this payload frame.",
                out diagnostic);
        }

        var availableBytes = payload.Length - payloadStartByte;
        var availableLeds = Math.Min(mappedLedCount, availableBytes / 3);

        var policyAction = requestedLedCount > matrixLedCapacity
            ? ToyRoutingPolicyAction.ClampedOversizeRange
            : ToyRoutingPolicyAction.None;
        var policyMessage = requestedLedCount > matrixLedCapacity
            ? $"Requested {requestedLedCount} LEDs but clamped to matrix capacity {matrixLedCapacity}."
            : "Mapped canonical range successfully.";

        for (var ledIndex = 0; ledIndex < availableLeds; ledIndex++)
        {
            var sourceByteOffset = payloadStartByte + (ledIndex * 3);
            var rgb = new Rgb24(
                payload[sourceByteOffset],
                payload[sourceByteOffset + 1],
                payload[sourceByteOffset + 2]);

            // Conversational note: ToyRouter keeps output in canonical linear order so downstream renderers
            // can apply exactly one mapping pass (avoids accidental double-rotation/double-serpentine).
            pixels[ledIndex] = rgb;
        }

        var missingBytes = (mappedLedCount - availableLeds) * 3;
        if (missingBytes > 0)
        {
            return ApplyMissingDataPolicy(
                toy,
                context,
                globalFrameId,
                width,
                height,
                pixels,
                mappedLedCount,
                policyAction,
                $"Mapped {availableLeds}/{mappedLedCount} LEDs; {missingBytes} bytes missing.",
                out diagnostic,
                availableLeds,
                missingBytes);
        }

        diagnostic = new ToyRoutingDiagnostic(
            toy.Id,
            MappedLedCount: availableLeds,
            MissingBytes: 0,
            PolicyAction: policyAction,
            Message: policyMessage);

        return new ToyFrame(
            toy.Id,
            DeriveToyFrameId(globalFrameId, toy.Id),
            context.ReceivedAtUtc,
            width,
            height,
            PixelsToBytes(pixels),
            pixels);
    }

    private ToyFrame? ApplyMissingDataPolicy(
        ToyDefinition toy,
        RoutingFrameContext context,
        ulong globalFrameId,
        int width,
        int height,
        Rgb24[] pixels,
        int mappedLedCount,
        ToyRoutingPolicyAction existingPolicyAction,
        string baseMessage,
        out ToyRoutingDiagnostic diagnostic,
        int availableLeds = 0,
        int? explicitMissingBytes = null)
    {
        var missingBytes = explicitMissingBytes ?? Math.Max(0, mappedLedCount * 3);

        if (PolicyEquals(_policy.OnMissingData, "drop"))
        {
            diagnostic = new ToyRoutingDiagnostic(
                toy.Id,
                MappedLedCount: availableLeds,
                MissingBytes: missingBytes,
                PolicyAction: ToyRoutingPolicyAction.DroppedMissingData,
                Message: $"{baseMessage} Dropped by onMissingData=drop.");
            return null;
        }

        if (PolicyEquals(_policy.OnMissingData, "hold-last") && _lastFrameByToyId.TryGetValue(toy.Id, out var lastFrame))
        {
            diagnostic = new ToyRoutingDiagnostic(
                toy.Id,
                MappedLedCount: availableLeds,
                MissingBytes: missingBytes,
                PolicyAction: ToyRoutingPolicyAction.HeldLastFrame,
                Message: $"{baseMessage} Holding last frame due to onMissingData=hold-last.");
            return lastFrame with { FrameId = DeriveToyFrameId(globalFrameId, toy.Id), TimestampUtc = context.ReceivedAtUtc };
        }

        var policyAction = existingPolicyAction == ToyRoutingPolicyAction.None
            ? ToyRoutingPolicyAction.PaddedMissingBytesWithBlack
            : existingPolicyAction;

        diagnostic = new ToyRoutingDiagnostic(
            toy.Id,
            MappedLedCount: availableLeds,
            MissingBytes: missingBytes,
            PolicyAction: policyAction,
            Message: $"{baseMessage} Filled missing bytes with black due to onMissingData=partial-black-fill.");

        return new ToyFrame(
            toy.Id,
            DeriveToyFrameId(globalFrameId, toy.Id),
            context.ReceivedAtUtc,
            width,
            height,
            PixelsToBytes(pixels),
            pixels);
    }

    private static bool PolicyEquals(string? value, string expected)
        => string.Equals(value?.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    private static ulong DeriveToyFrameId(ulong globalFrameId, string toyId)
    {
        // Conversational note: combine global input sequence with a stable toy hash so IDs stay deterministic.
        var toyHash = ComputeFnv1a32(toyId);
        return (globalFrameId << 32) | toyHash;
    }

    private static uint ComputeFnv1a32(string text)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;

        var hash = offsetBasis;
        var bytes = Encoding.UTF8.GetBytes(text ?? string.Empty);
        foreach (var value in bytes)
        {
            hash ^= value;
            hash *= prime;
        }

        return hash;
    }

    private static byte[] PixelsToBytes(IReadOnlyList<Rgb24> pixels)
    {
        var bytes = new byte[pixels.Count * 3];

        for (var i = 0; i < pixels.Count; i++)
        {
            var offset = i * 3;
            bytes[offset] = pixels[i].R;
            bytes[offset + 1] = pixels[i].G;
            bytes[offset + 2] = pixels[i].B;
        }

        return bytes;
    }
}

/// <summary>
/// Aggregate output from one router pass over a full incoming payload frame.
/// </summary>
public sealed record ToyRoutingResult(
    ulong GlobalFrameId,
    IReadOnlyList<ToyFrame> Frames,
    IReadOnlyList<ToyRoutingDiagnostic> Diagnostics);

/// <summary>
/// Per-toy routing diagnostics describing how v1 routing policies were applied.
/// </summary>
public sealed record ToyRoutingDiagnostic(
    string ToyId,
    int MappedLedCount,
    int MissingBytes,
    ToyRoutingPolicyAction PolicyAction,
    string Message);

/// <summary>
/// Policy action tags surfaced by routing diagnostics for observability.
/// </summary>
public enum ToyRoutingPolicyAction
{
    None,
    SkippedDisabled,
    SourceOutOfRange,
    PaddedMissingBytesWithBlack,
    DroppedMissingData,
    HeldLastFrame,
    RejectedOversizeRange,
    ClampedOversizeRange,
    FrameDroppedLatestWins,
    PerToyFailureIsolated,
}
