namespace VirtualDofMatrix.Core.Toys;

/// <summary>
/// Converts transport-level payloads plus routing context into canonical <see cref="ToyFrame"/> instances.
/// </summary>
public interface IToyRouter
{
    /// <summary>
    /// Routes one full incoming payload frame across enabled toys in the active routing plan.
    /// </summary>
    /// <param name="payload">Raw transport payload in source order.</param>
    /// <param name="context">Metadata describing sequence, source, and version expectations.</param>
    /// <param name="toyDefinitions">Enabled toy definitions that define canonical mapping and targets.</param>
    /// <returns>
    /// Aggregated routing output including per-toy frames and diagnostics. Version 1 is full-frame authoritative,
    /// so each call is treated as a complete source-of-truth snapshot with no incremental merge behavior.
    /// </returns>
    ToyRoutingResult Route(
        ReadOnlySpan<byte> payload,
        RoutingFrameContext context,
        IReadOnlyList<ToyDefinition> toyDefinitions);
}
