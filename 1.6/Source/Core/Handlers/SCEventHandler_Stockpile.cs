using System;
using System.Collections.Generic;
using Verse;

namespace FactionColonies.SupplyChain
{
    /// <summary>
    /// Event handler that manipulates stockpile pools when an event triggers.
    /// All fields are XML-configurable via DefModExtension on FCEventDef.
    /// <para><c>mult</c>: multiplier on current stock. &lt;1 = loss, &gt;1 = gain, 1 = no effect.</para>
    /// <para><c>baseAmount</c>/<c>perWorkerAmount</c>: flat credit/draw. Positive = gain, negative = loss.</para>
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

            bool debug = SupplyChainSettings.PrintDebug;
            List<ResourceTypeDef> targets = GetTargetResources();

            foreach (WorldSettlementFC settlement in evt.settlementTraitLocations)
            {
                if (settlement == null) continue;

                IStockpilePool pool = GetPool(wc, settlement);
                if (pool == null) continue;

                float silverAccum = 0f;

                foreach (ResourceTypeDef r in targets)
                {
                    // Mult phase
                    if (Math.Abs(mult - 1f) > 0.001f)
                    {
                        double current = pool.GetAmount(r);
                        if (current > 0.01)
                        {
                            double target = current * mult;
                            if (target < current)
                            {
                                double loss = current - target;
                                double drawn;
                                pool.TryDraw(r, loss, out drawn);
                                if (convertToSilver && drawn > 0)
                                    silverAccum += (float)(drawn * FCSettings.silverPerResource
                                        * SupplyChainSettings.overflowPenaltyRate);
                                if (debug)
                                    LogUtil.Message("[Empire-SupplyChain] Stockpile event: "
                                        + settlement.Name + " " + r.label
                                        + " mult=" + mult + " drew " + drawn.ToString("F1"));
                            }
                            else if (target > current)
                            {
                                double gain = target - current;
                                pool.Credit(r, gain);
                                if (debug)
                                    LogUtil.Message("[Empire-SupplyChain] Stockpile event: "
                                        + settlement.Name + " " + r.label
                                        + " mult=" + mult + " credited " + gain.ToString("F1"));
                            }
                        }
                    }

                    // Flat phase
                    double delta = baseAmount + (perWorkerAmount * settlement.workers);
                    if (Math.Abs(delta) > 0.001)
                    {
                        if (delta > 0)
                        {
                            pool.Credit(r, delta);
                            if (debug)
                                LogUtil.Message("[Empire-SupplyChain] Stockpile event: "
                                    + settlement.Name + " " + r.label
                                    + " credited " + delta.ToString("F1") + " (flat)");
                        }
                        else
                        {
                            double drawn;
                            pool.TryDraw(r, -delta, out drawn);
                            if (debug)
                                LogUtil.Message("[Empire-SupplyChain] Stockpile event: "
                                    + settlement.Name + " " + r.label
                                    + " drew " + drawn.ToString("F1") + " (flat)");
                        }
                    }
                }

                if (convertToSilver && silverAccum > 0.01f)
                {
                    settlement.AddOneTimeSilverIncome(silverAccum);
                    if (debug)
                        LogUtil.Message("[Empire-SupplyChain] Stockpile event: "
                            + settlement.Name + " salvaged " + silverAccum.ToString("F0") + " silver");
                }
            }
        }

        private List<ResourceTypeDef> GetTargetResources()
        {
            if (resource != null)
                return new List<ResourceTypeDef> { resource };
            return new List<ResourceTypeDef>(DefDatabase<ResourceTypeDef>.AllDefsListForReading);
        }

        private IStockpilePool GetPool(WorldComponent_SupplyChain wc, WorldSettlementFC settlement)
        {
            if (wc.Mode == SupplyChainMode.Simple)
                return wc.Pool;

            WorldObjectComp_SupplyChain comp = SupplyChainCache.GetSettlementComp(settlement);
            if (comp == null) return null;
            return comp.GetPool();
        }
    }
}
