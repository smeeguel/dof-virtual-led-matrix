namespace VirtualDofMatrix.Core.Toys;

/// <summary>
/// Describes how an incoming source payload maps into the canonical global LED space for a toy.
/// </summary>
/// <remarks>
/// Canonical coordinates are a versioned, transport-agnostic index space that starts at LED 0 and
/// is contiguous for the toy's full logical length. Optional source strip metadata exists so older
/// strip/offset feeds can be normalized into that canonical space without changing downstream routing.
/// </remarks>
public sealed record ToySourceRange(
    int CanonicalStart,
    int Length,
    int? SourceStripIndex = null,
    int? SourceOffset = null);
