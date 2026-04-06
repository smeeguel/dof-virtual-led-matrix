using System.Text;

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
            if (!toy.Enabled)
            {
                diagnostics.Add(new ToyRoutingDiagnostic(
                    toy.Id,
                    MappedLedCount: 0,
                    MissingBytes: 0,
                    PolicyAction: ToyRoutingPolicyAction.SkippedDisabled,
                    Message: "Toy is disabled in the routing plan."));
                continue;
            }

            var frame = BuildToyFrame(payload, context, toy, globalFrameId, out var diagnostic);
            diagnostics.Add(diagnostic);

            if (frame is not null)
            {
                frames.Add(frame);
            }
        }

        return new ToyRoutingResult(globalFrameId, frames, diagnostics);
    }

    private static ToyFrame? BuildToyFrame(
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
        var mappedLedCount = Math.Min(matrixLedCapacity, requestedLedCount);

        // Conversational note: v1 policy chooses a conservative "paint black for missing data" behavior.
        var pixels = new Rgb24[matrixLedCapacity];
        var payloadStartByte = canonicalStart * 3;

        if (payloadStartByte >= payload.Length || mappedLedCount == 0)
        {
            diagnostic = new ToyRoutingDiagnostic(
                toy.Id,
                MappedLedCount: 0,
                MissingBytes: mappedLedCount * 3,
                PolicyAction: ToyRoutingPolicyAction.SourceOutOfRange,
                Message: "Canonical source range starts outside this payload frame; toy output filled with black.");

            return new ToyFrame(
                toy.Id,
                DeriveToyFrameId(globalFrameId, toy.Id),
                context.ReceivedAtUtc,
                width,
                height,
                PixelsToBytes(pixels),
                pixels);
        }

        var availableBytes = payload.Length - payloadStartByte;
        var availableLeds = Math.Min(mappedLedCount, availableBytes / 3);

        var mapping = toy.Mapping;
        var policyAction = ToyRoutingPolicyAction.None;
        var policyMessage = "Mapped canonical range successfully.";

        for (var ledIndex = 0; ledIndex < availableLeds; ledIndex++)
        {
            var sourceByteOffset = payloadStartByte + (ledIndex * 3);
            var rgb = new Rgb24(
                payload[sourceByteOffset],
                payload[sourceByteOffset + 1],
                payload[sourceByteOffset + 2]);

            var (x, y, mappingAction) = MapWithFallback(ledIndex, width, height, mapping);
            if (mappingAction != ToyRoutingPolicyAction.None)
            {
                policyAction = mappingAction;
                policyMessage = $"Mapping '{mapping}' was unsupported; used RowMajor fallback.";
            }

            var targetIndex = (y * width) + x;
            pixels[targetIndex] = rgb;
        }

        var missingBytes = (mappedLedCount - availableLeds) * 3;
        if (missingBytes > 0)
        {
            policyAction = policyAction == ToyRoutingPolicyAction.None
                ? ToyRoutingPolicyAction.PaddedMissingBytesWithBlack
                : policyAction;
            policyMessage = $"Mapped {availableLeds}/{mappedLedCount} LEDs; padded missing bytes with black.";
        }

        diagnostic = new ToyRoutingDiagnostic(
            toy.Id,
            MappedLedCount: availableLeds,
            MissingBytes: Math.Max(0, missingBytes),
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

    private static (int X, int Y, ToyRoutingPolicyAction PolicyAction) MapWithFallback(int ledIndex, int width, int height, string mapping)
    {
        try
        {
            var (x, y) = MatrixMapper.MapLinearIndex(ledIndex, width, height, mapping);
            return (x, y, ToyRoutingPolicyAction.None);
        }
        catch (NotSupportedException)
        {
            var x = ledIndex % width;
            var y = ledIndex / width;
            return (x, y, ToyRoutingPolicyAction.MappingFallbackRowMajor);
        }
    }

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
    MappingFallbackRowMajor,
}
