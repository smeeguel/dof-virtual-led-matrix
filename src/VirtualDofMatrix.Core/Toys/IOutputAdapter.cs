namespace VirtualDofMatrix.Core.Toys;

/// <summary>
/// Consumes canonical toy frames and emits them to a destination (viewer, file, pipe, etc.).
/// </summary>
public interface IOutputAdapter
{
    /// <summary>
    /// Gets a stable adapter name for diagnostics and versioned routing configuration.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Writes a routed frame to the adapter's destination.
    /// </summary>
    /// <param name="frame">Canonical toy frame to output.</param>
    /// <param name="context">Original routing context used to produce the frame.</param>
    void Write(ToyFrame frame, RoutingFrameContext context);
}
