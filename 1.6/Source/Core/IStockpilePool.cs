namespace FactionColonies.SupplyChain
{
    /// <summary>
    /// Mode-agnostic abstraction for drawing/crediting resources from a stockpile pool.
    /// Simple mode uses FactionStockpilePool (one shared pool).
    /// Complex mode will use LocalStockpilePool (per-settlement pools).
    /// </summary>
    public interface IStockpilePool
    {
        /// <summary>Current amount of this resource in the pool.</summary>
        double GetAmount(ResourceTypeDef resource);

        /// <summary>Maximum capacity for this resource.</summary>
        double GetCap(ResourceTypeDef resource);

        /// <summary>
        /// Draw up to <paramref name="amount"/> units from the pool.
        /// Returns true if any amount was drawn. Actual amount drawn is returned via out param.
        /// </summary>
        bool TryDraw(ResourceTypeDef resource, double amount, out double drawn);

        /// <summary>
        /// Credit units to the pool, clamped to cap.
        /// Returns the excess that did not fit (0 if everything fit).
        /// </summary>
        double Credit(ResourceTypeDef resource, double amount);
    }
}
