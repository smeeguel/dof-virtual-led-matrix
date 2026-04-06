namespace VirtualDofMatrix.Core.Toys;

/// <summary>
/// Supplies the active routing plan that maps canonical input ranges to toy definitions.
/// </summary>
public interface IRoutingPlanProvider
{
    /// <summary>
    /// Gets the currently active toy definitions in their schema-compatible canonical form.
    /// </summary>
    IReadOnlyList<ToyDefinition> GetActiveToyDefinitions();
}
