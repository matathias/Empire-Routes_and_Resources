using System.Collections.Generic;

namespace FactionColonies.SupplyChain
{
    /* DESTRUCTIVE: fires SCEventHandler_Stockpile against a live settlement's real stockpile (the
       faction pile in Simple mode, the local pile in Complex). It credits/draws resources and, in the
       convert-to-silver case, pays out one-time silver. Not reverted. */
    public static class EventHandlerDestructiveTests
    {
        [EmpireDestructiveTest("SC.Destructive.Event")]
        public static void FlatCredit_IncreasesStockpile()
        {
            FactionFC f = DestructiveTestUtil.RequireFaction();
            WorldComponent_SupplyChain wc = SupplyChainCache.Comp;
            if (wc is null) TestAssert.Skip("No SupplyChain world component");
            WorldSettlementFC s = SCDestructiveTestUtil.FirstOrTransient(f);
            if (s is null) TestAssert.Skip("No settlement available");

            ResourceTypeDef r = FirstNonPoolResource();
            if (r is null) TestAssert.Skip("No non-pool resource available");

            IStockpile sp = StockpileFor(wc, s);
            if (sp is null) TestAssert.Skip("No stockpile available for the settlement");
            if (sp.GetAmount(r) >= sp.GetCap(r) - 0.01) TestAssert.Skip("Target stockpile is already at cap; a credit is unobservable");

            double before = sp.GetAmount(r);

            // A plain flat +baseAmount event stores the resource.
            SCEventHandler_Stockpile handler = new SCEventHandler_Stockpile
            {
                resource = r, mult = 1f, baseAmount = 5f, perWorkerAmount = 0f, convertToSilver = false
            };
            handler.OnEventTriggered(MakeEvent(s));

            TestAssert.GreaterThan(sp.GetAmount(r), before, "A flat +baseAmount credit event must raise the stockpile");

            DestructiveTestUtil.AssertEmpireInvariants(f, "EventHandler_FlatCredit");
        }

        [EmpireDestructiveTest("SC.Destructive.Event")]
        public static void FlatGain_ConvertToSilver_DoesNotChangeStockpile()
        {
            FactionFC f = DestructiveTestUtil.RequireFaction();
            WorldComponent_SupplyChain wc = SupplyChainCache.Comp;
            if (wc is null) TestAssert.Skip("No SupplyChain world component");
            WorldSettlementFC s = SCDestructiveTestUtil.FirstOrTransient(f);
            if (s is null) TestAssert.Skip("No settlement available");

            ResourceTypeDef r = FirstNonPoolResource();
            if (r is null) TestAssert.Skip("No non-pool resource available");

            IStockpile sp = StockpileFor(wc, s);
            if (sp is null) TestAssert.Skip("No stockpile available for the settlement");

            double before = sp.GetAmount(r);

            // convertToSilver: a flat gain is paid out as silver instead of stored — the pile is untouched.
            SCEventHandler_Stockpile handler = new SCEventHandler_Stockpile
            {
                resource = r, mult = 1f, baseAmount = 5f, perWorkerAmount = 0f, convertToSilver = true
            };
            handler.OnEventTriggered(MakeEvent(s));

            TestAssert.AreEqual(before, sp.GetAmount(r), 0.001,
                "A convertToSilver flat gain is paid as silver and must not change the stockpile");

            DestructiveTestUtil.AssertEmpireInvariants(f, "EventHandler_FlatGainSilver");
        }

        private static ResourceTypeDef FirstNonPoolResource()
        {
            foreach (ResourceTypeDef def in SupplyChainCache.AllResourceTypeDefs)
                if (!def.isPoolResource) return def;
            return null;
        }

        // The handler resolves the same stockpile object: EnsureLocalStockpile initializes and returns
        // the persistent local wrapper, which the handler's own GetStockpile then reads/mutates.
        private static IStockpile StockpileFor(WorldComponent_SupplyChain wc, WorldSettlementFC s)
        {
            if (wc.Mode == SupplyChainMode.Simple) return wc.Stockpile;
            return SupplyChainCache.GetSettlementComp(s)?.EnsureLocalStockpile();
        }

        private static FCEvent MakeEvent(WorldSettlementFC s)
        {
            FCEvent evt = new FCEvent();
            evt.settlementTraitLocations = new List<WorldSettlementFC> { s };
            return evt;
        }
    }
}
