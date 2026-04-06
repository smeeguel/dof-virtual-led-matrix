namespace VirtualDofMatrix.Core.Toys;

/// <summary>
/// Represents one normalized frame for a specific toy in canonical pixel order.
/// </summary>
/// <remarks>
/// The payload can be carried as packed RGB bytes or as <see cref="Rgb24"/> values depending on the
/// consumer's needs, but both forms are expected to describe the same canonical ordering for version
/// stability. A producer may choose either representation as long as the frame dimensions remain explicit.
/// </remarks>
public sealed record ToyFrame(
    string ToyId,
    ulong FrameId,
    DateTimeOffset TimestampUtc,
    int Width,
    int Height,
    byte[]? PayloadBytes = null,
    Rgb24[]? Pixels = null);
