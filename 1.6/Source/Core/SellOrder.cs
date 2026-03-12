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
            if (resource == null || amountPerPeriod <= 0)
                return 0f;

            double drawn;
            if (!pool.TryDraw(resource, amountPerPeriod, out drawn) || drawn <= 0)
                return 0f;

            float silver = (float)(drawn * FCSettings.silverPerResource * SupplyChainSettings.overflowPenaltyRate);
            return silver;
        }

        public void ExposeData()
        {
            Scribe_Defs.Look(ref resource, "resource");
            Scribe_Values.Look(ref amountPerPeriod, "amountPerPeriod", 0.0);
        }
    }
}
