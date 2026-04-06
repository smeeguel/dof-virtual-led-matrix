namespace VirtualDofMatrix.Core.Toys;

/// <summary>
/// Defines a routable toy target and the metadata required to normalize and deliver its frames.
/// </summary>
/// <remarks>
/// The source descriptor and output targets are intentionally free-form contracts so routing plans can
/// evolve over time while preserving canonical LED semantics. New schema versions should extend fields
/// compatibly instead of redefining what canonical start/length means.
/// </remarks>
public sealed record ToyDefinition(
    string Id,
    bool Enabled,
    string Kind,
    int Width,
    int Height,
    string Mapping,
    ToySourceRange SourceDescriptor,
    IReadOnlyList<string> OutputTargets);
