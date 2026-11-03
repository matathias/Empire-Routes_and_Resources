using System;

namespace FactionColonies.SupplyChain
{
    /// <summary>
    /// Computes projected need fill rates from cached FlowBreakdown data.
    /// Used by the UI to show projected satisfaction between tax cycles.
    /// </summary>
    internal static class NeedProjection
    {
        /// <summary>
        /// Returns the projected fill rate for needs consuming the given resource
        /// at the given settlement (Complex mode).
        /// Uses the cached FlowBreakdown and current local stockpile amount.
        /// </summary>
        internal static float ProjectedFillRate(
            WorldComponent_SupplyChain wc,
            WorldSettlementFC settlement,
            WorldObjectComp_SupplyChain comp,
            ResourceTypeDef resource)
        {
            if (wc == null || settlement == null || comp == null || resource == null)
                return 1f;

            WorldComponent_SupplyChain.FlowBreakdown flow = wc.GetCachedFlow(settlement, comp, resource);
            if (flow.needs <= 0)
                return 1f;

            IStockpile stockpile = comp.GetStockpile();
            double currentAmount = stockpile != null ? stockpile.GetAmount(resource) : 0;

            // Daily-net basis: availableForNeeds = currentStockpile + the daily inflow available
            // to needs (production diverted in - tithe drawn). Algebraically that is
            // currentStockpile + flow.DailyNet + flow.needs. Routes, sell orders, and buy orders are
            // per-tax-cycle stockpile changes already reflected in currentAmount when they fire, so
            // they are deliberately NOT projected as a continuous daily flow.
            double available = currentAmount + flow.DailyNet + flow.needs;
            double rate = available / flow.needs;

            return (float)Math.Max(0.0, Math.Min(1.0, rate));
        }

        /// <summary>
        /// Returns the projected fill rate for needs consuming the given resource
        /// in Simple mode (shared faction stockpile, fair distribution).
        /// </summary>
        internal static float ProjectedFillRateSimple(
            WorldComponent_SupplyChain wc,
            FactionFC faction,
            ResourceTypeDef resource)
        {
            if (wc == null || faction == null || resource == null)
                return 1f;

            WorldComponent_SupplyChain.FlowBreakdown flow = wc.GetCachedSimpleFlow(faction, resource);
            if (flow.needs <= 0)
                return 1f;

            IStockpile stockpile = wc.Stockpile;
            double currentAmount = stockpile != null ? stockpile.GetAmount(resource) : 0;

            double available = currentAmount + flow.DailyNet + flow.needs;
            double rate = available / flow.needs;

            return (float)Math.Max(0.0, Math.Min(1.0, rate));
        }
    }
}
