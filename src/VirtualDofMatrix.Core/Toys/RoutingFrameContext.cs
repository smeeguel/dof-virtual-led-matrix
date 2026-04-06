namespace VirtualDofMatrix.Core.Toys;

/// <summary>
/// Carries raw input metadata used while turning transport payloads into canonical toy frames.
/// </summary>
/// <remarks>
/// Keep this context transport-aware and frame-aware, but avoid baking renderer assumptions into it.
/// Version fields should be treated as protocol metadata so routers can branch behavior safely when
/// payload shape or source semantics change over time.
/// </remarks>
public sealed record RoutingFrameContext(
    ulong InputSequence,
    int PayloadLength,
    string PayloadKind,
    DateTimeOffset ReceivedAtUtc,
    DateTimeOffset? PresentedAtUtc = null,
    int? SourceStripIndex = null,
    int? SourceOffset = null,
    string SchemaVersion = "1.0");
