namespace FactionColonies.SupplyChain
{
    /// <summary>
    /// Mode-agnostic abstraction for drawing/crediting resources from a stockpile.
    /// Both Simple and Complex modes use DictionaryStockpile backed by different dictionaries.
    /// </summary>
    public interface IStockpile
    {
        /// <summary>Current amount of this resource in the stockpile.</summary>
        double GetAmount(ResourceTypeDef resource);

        /// <summary>Maximum capacity for this resource.</summary>
        double GetCap(ResourceTypeDef resource);

        /// <summary>
        /// Draw up to <paramref name="amount"/> units from the stockpile.
        /// Returns true if any amount was drawn. Actual amount drawn is returned via out param.
        /// </summary>
        bool TryDraw(ResourceTypeDef resource, double amount, out double drawn);

        /// <summary>
        /// Credit units to the stockpile, clamped to cap.
        /// Returns the excess that did not fit (0 if everything fit).
        /// </summary>
        double Credit(ResourceTypeDef resource, double amount);

        /// <summary>
        /// Add units to the stockpile WITHOUT enforcing the cap — the amount may temporarily exceed
        /// the cap. Callers are responsible for reconciling the over-cap surplus later (the daily
        /// consume pass draws needs/routes first, then sweeps whatever storage still cannot hold).
        /// Used only for the produce-then-consume deposit path; every other deposit uses <see cref="Credit"/>.
        /// </summary>
        void Add(ResourceTypeDef resource, double amount);
    }
}
