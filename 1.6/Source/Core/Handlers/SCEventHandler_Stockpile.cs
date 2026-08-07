using System;
using System.Collections.Generic;
using Verse;

namespace FactionColonies.SupplyChain
{
    /// <summary>
    /// Event handler that manipulates stockpiles when an event triggers.
    /// All fields are XML-configurable via DefModExtension on FCEventDef.
    /// <para><c>mult</c>: multiplier on current stock. &lt;1 = loss, &gt;1 = gain, 1 = no effect.</para>
    /// <para><c>baseAmount</c>/<c>perWorkerAmount</c>: flat credit/draw. Positive = gain, negative = loss.</para>
    /// <para><c>convertToSilver</c>: when true, gains (mult or flat) are paid out as silver at the
    /// overflow rate instead of stored, and mult-phase losses are salvaged to silver; the settlement
    /// receives the total as one-time silver income. Flat losses remain a plain draw.</para>
    /// <para>All three fields are applied independently and can be combined.</para>
    /// </summary>
    public class SCEventHandler_Stockpile : FCEventHandlerExtension
    {
        public ResourceTypeDef resource;
        public float mult = 1f;
        public bool convertToSilver = false;
        public float baseAmount = 0f;
        public float perWorkerAmount = 0f;

        public override void OnEventTriggered(FCEvent evt)
        {
            WorldComponent_SupplyChain wc = SupplyChainCache.Comp;
            if (wc == null) return;

            if (evt?.settlementTraitLocations == null) return;

            bool debug = SupplyChainSettings.PrintDebug;
            List<ResourceTypeDef> targets = GetTargetResources();

            // The mult phase scales a proportion of the current pile, so it must run once per
            // physical pile. In Simple mode every affected settlement shares wc.Stockpile — applying
            // the mult per settlement would compound it to mult^N. Track distinct piles and apply the
            // mult only the first time we see each. Flat gains/draws stay per settlement.
            HashSet<IStockpile> multAppliedPiles = new HashSet<IStockpile>();

            foreach (WorldSettlementFC settlement in evt.settlementTraitLocations)
            {
                if (settlement == null) continue;

                IStockpile stockpile = GetStockpile(wc, settlement);
                if (stockpile == null) continue;

                bool applyMult = multAppliedPiles.Add(stockpile);

                float silverAccum = 0f;

                foreach (ResourceTypeDef r in targets)
                {
                    // Mult phase
                    if (applyMult && Math.Abs(mult - 1f) > 0.001f)
                    {
                        double current = stockpile.GetAmount(r);
                        if (current > 0.01)
                        {
                            double target = current * mult;
                            if (target < current)
                            {
                                double loss = current - target;
                                double drawn;
                                stockpile.TryDraw(r, loss, out drawn);
                                if (convertToSilver && drawn > 0)
                                    silverAccum += FormulaUtil.OverflowSilver(drawn);
                                LogSC.Message("Stockpile event: "
                                    + settlement.Name + " " + r.label
                                    + " mult=" + mult + " drew " + drawn.ToString("F1"));
                            }
                            else if (target > current)
                            {
                                double gain = target - current;
                                if (convertToSilver)
                                {
                                    silverAccum += FormulaUtil.OverflowSilver(gain);
                                    LogSC.Message("Stockpile event: "
                                        + settlement.Name + " " + r.label
                                        + " mult=" + mult + " sold " + gain.ToString("F1") + " for silver");
                                }
                                else
                                {
                                    stockpile.Credit(r, gain);
                                    LogSC.Message("Stockpile event: "
                                        + settlement.Name + " " + r.label
                                        + " mult=" + mult + " credited " + gain.ToString("F1"));
                                }
                            }
                        }
                    }

                    // Flat phase
                    double delta = baseAmount + (perWorkerAmount * settlement.workers);
                    if (Math.Abs(delta) > 0.001)
                    {
                        if (delta > 0)
                        {
                            if (convertToSilver)
                            {
                                silverAccum += FormulaUtil.OverflowSilver(delta);
                                LogSC.Message("Stockpile event: "
                                    + settlement.Name + " " + r.label
                                    + " sold " + delta.ToString("F1") + " for silver (flat)");
                            }
                            else
                            {
                                stockpile.Credit(r, delta);
                                LogSC.Message("Stockpile event: "
                                    + settlement.Name + " " + r.label
                                    + " credited " + delta.ToString("F1") + " (flat)");
                            }
                        }
                        else
                        {
                            double drawn;
                            stockpile.TryDraw(r, -delta, out drawn);
                            if (debug)
                                LogSC.Message("Stockpile event: "
                                    + settlement.Name + " " + r.label
                                    + " drew " + drawn.ToString("F1") + " (flat)");
                        }
                    }
                }

                if (convertToSilver && silverAccum > 0.01f)
                {
                    settlement.AddOneTimeSilverIncome(silverAccum);
                    if (debug)
                        LogSC.Message("Stockpile event: "
                            + settlement.Name + " salvaged " + silverAccum.ToString("F0") + " silver");
                }
            }
        }

        private List<ResourceTypeDef> GetTargetResources()
        {
            if (resource != null)
                return new List<ResourceTypeDef> { resource };
            return new List<ResourceTypeDef>(SupplyChainCache.AllResourceTypeDefs);
        }

        private IStockpile GetStockpile(WorldComponent_SupplyChain wc, WorldSettlementFC settlement)
        {
            if (wc.Mode == SupplyChainMode.Simple)
                return wc.Stockpile;

            WorldObjectComp_SupplyChain comp = SupplyChainCache.GetSettlementComp(settlement);
            if (comp == null) return null;
            return comp.GetStockpile();
        }
    }
}
