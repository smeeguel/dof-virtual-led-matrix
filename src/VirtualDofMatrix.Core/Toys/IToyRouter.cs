namespace VirtualDofMatrix.Core.Toys;

/// <summary>
/// Converts transport-level payloads plus routing context into canonical <see cref="ToyFrame"/> instances.
/// </summary>
public interface IToyRouter
{
    /// <summary>
    /// Routes one incoming payload into zero or more toy frames.
    /// </summary>
    /// <param name="payload">Raw transport payload in source order.</param>
    /// <param name="context">Metadata describing sequence, source, and version expectations.</param>
    /// <param name="toyDefinitions">Enabled toy definitions that define canonical mapping and targets.</param>
    /// <returns>Canonical frames ready for output adapters.</returns>
    IReadOnlyList<ToyFrame> Route(
        ReadOnlySpan<byte> payload,
        RoutingFrameContext context,
        IReadOnlyList<ToyDefinition> toyDefinitions);
}
