using Verse;

namespace FactionColonies.SupplyChain
{
    public class SellOrder : IExposable
    {
        public ResourceTypeDef resource;
        public double amountPerPeriod;

        public SellOrder()
        {
        }

        public SellOrder(ResourceTypeDef resource, double amountPerPeriod)
        {
            this.resource = resource;
            this.amountPerPeriod = amountPerPeriod;
        }

        /// <summary>
        /// Execute this sell order against the given pool.
        /// Returns the silver value generated.
        /// </summary>
        public float Execute(IStockpilePool pool)
        {
            return Execute(pool, null);
        }

        /// <summary>
        /// Execute this sell order against the given pool, optionally applying the sell rate
        /// multiplier stat from the settlement context.
        /// </summary>
        public float Execute(IStockpilePool pool, WorldSettlementFC settlement)
        {
            if (resource == null || amountPerPeriod <= 0)
                return 0f;

            double drawn;
            if (!pool.TryDraw(resource, amountPerPeriod, out drawn) || drawn <= 0)
                return 0f;

            double sellRate = SupplyChainSettings.overflowPenaltyRate;

            // Apply sell rate multiplier stat if a settlement context is available
            if (settlement != null && FactionCache.FactionComp != null)
            {
                FCStatDef sellRateStat = DefDatabase<FCStatDef>.GetNamedSilentFail("SC_SellRateMultiplier");
                if (sellRateStat != null)
                {
                    double mult = FactionCache.FactionComp.GetStatValue(sellRateStat, settlement);
                    if (mult > 0)
                        sellRate *= mult;
                }
            }

            float silver = (float)(drawn * FCSettings.silverPerResource * sellRate);
            return silver;
        }

        public void ExposeData()
        {
            Scribe_Defs.Look(ref resource, "resource");
            Scribe_Values.Look(ref amountPerPeriod, "amountPerPeriod", 0.0);
        }
    }
}
