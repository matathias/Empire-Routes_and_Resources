namespace FactionColonies.SupplyChain
{
    /// <summary>
    /// Extensibility interface for modifying supply route efficiency.
    /// Register implementations with SupplyRouteModifierRegistry.
    /// Example: UrbanRural submod could boost efficiency for linked settlements.
    /// </summary>
    public interface ISupplyRouteModifier
    {
        /// <summary>
        /// Modify the efficiency of a supply route. Called after base efficiency is calculated.
        /// Return the modified efficiency value (should stay in 0..1 range).
        /// </summary>
        double ModifyRouteEfficiency(SupplyRoute route, double baseEfficiency);
    }
}
