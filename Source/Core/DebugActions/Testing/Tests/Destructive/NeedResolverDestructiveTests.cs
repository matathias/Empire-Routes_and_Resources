using System.Collections.Generic;

namespace FactionColonies.SupplyChain
{
    /* DESTRUCTIVE: resolves needs against a real settlement, overwriting its live need-states and
       invalidating its stat cache. Stockpiles passed in are throwaway fixtures. Not reverted. */
    public static class NeedResolverDestructiveTests
    {
        [EmpireDestructiveTest("SC.Destructive.Needs")]
        public static void ResolveSettlementNeeds_AbundantStockpile_AllNeedsMet()
        {
            FactionFC f = DestructiveTestUtil.RequireFaction();
            WorldSettlementFC s = SCDestructiveTestUtil.FirstOrTransient(f);
            if (s is null) TestAssert.Skip("No settlement available");
            WorldObjectComp_SettlementNeeds needsComp = SupplyChainCache.GetNeedsComp(s);
            if (needsComp is null) TestAssert.Skip("Settlement has no needs comp");

            DictionaryStockpile sp = SCDestructiveTestUtil.AbundantStockpile();
            TestAssert.DoesNotThrow(() => NeedResolver.ResolveSettlementNeeds(s, sp, needsComp),
                "ResolveSettlementNeeds threw");

            List<NeedState> states = needsComp.NeedStates;
            if (states is null || states.Count == 0)
                TestAssert.Skip("Settlement has no active needs to satisfy");

            foreach (NeedState ns in states)
                TestAssert.GreaterThan(ns.Satisfaction, 0.999,
                    "Need '" + ns.label + "' should be fully met from an abundant stockpile");

            DestructiveTestUtil.AssertEmpireInvariants(f, "ResolveSettlementNeeds_Abundant");
        }

        [EmpireDestructiveTest("SC.Destructive.Needs")]
        public static void ResolveSettlementNeeds_EmptyStockpile_NothingFulfilled()
        {
            FactionFC f = DestructiveTestUtil.RequireFaction();
            WorldSettlementFC s = SCDestructiveTestUtil.FirstOrTransient(f);
            if (s is null) TestAssert.Skip("No settlement available");
            WorldObjectComp_SettlementNeeds needsComp = SupplyChainCache.GetNeedsComp(s);
            if (needsComp is null) TestAssert.Skip("Settlement has no needs comp");

            DictionaryStockpile sp = SCTestHelper.MakeEmptyStockpile();
            TestAssert.DoesNotThrow(() => NeedResolver.ResolveSettlementNeeds(s, sp, needsComp),
                "ResolveSettlementNeeds threw");

            List<NeedState> states = needsComp.NeedStates;
            bool anyPositiveDemand = false;
            if (states != null)
            {
                foreach (NeedState ns in states)
                {
                    if (ns.demanded <= 0) continue;
                    anyPositiveDemand = true;
                    TestAssert.AreEqual(0.0, ns.fulfilled, 0.001,
                        "Empty stockpile should fulfill nothing for '" + ns.label + "'");
                }
            }
            if (!anyPositiveDemand)
                TestAssert.Skip("Settlement has no positive-demand needs to test");

            DestructiveTestUtil.AssertEmpireInvariants(f, "ResolveSettlementNeeds_Empty");
        }

        [EmpireDestructiveTest("SC.Destructive.Needs")]
        public static void StatusBarFlow_BaseNeeds_MatchesResolvedBaseNeeds()
        {
            // Regression: the status-bar flow accumulator and the needs resolver must apply the
            // same activation filter. A settlement-type-restricted need (e.g. UrbanRural's city-only
            // food) that the resolver excludes must not leak into flow.baseNeeds, or the bottom bar
            // reports more demand than the settlement actually has.
            FactionFC f = DestructiveTestUtil.RequireFaction();
            WorldSettlementFC s = SCDestructiveTestUtil.FirstOrTransient(f);
            if (s is null) TestAssert.Skip("No settlement available");
            WorldObjectComp_SettlementNeeds needsComp = SupplyChainCache.GetNeedsComp(s);
            if (needsComp is null) TestAssert.Skip("Settlement has no needs comp");
            WorldObjectComp_SupplyChain dataComp = WorldComponent_SupplyChain.GetComp(s);
            if (dataComp is null) TestAssert.Skip("Settlement has no supply-chain comp");
            WorldComponent_SupplyChain worldComp = SupplyChainCache.Comp;
            if (worldComp is null) TestAssert.Skip("No supply-chain world component");

            // Resolve fresh base need-states for this settlement.
            DictionaryStockpile sp = SCDestructiveTestUtil.AbundantStockpile();
            NeedResolver.ResolveSettlementNeeds(s, sp, needsComp);

            List<NeedState> states = needsComp.NeedStates;
            if (states is null || states.Count == 0)
                TestAssert.Skip("Settlement has no active needs to compare");

            foreach (ResourceTypeDef def in SupplyChainCache.AllResourceTypeDefs)
            {
                double resolvedBase = 0.0;
                foreach (NeedState ns in states)
                {
                    if (ns.category == NeedCategory.Base && ns.resource == def)
                        resolvedBase += ns.demanded;
                }

                WorldComponent_SupplyChain.FlowBreakdown flow = worldComp.CalculateFlow(s, dataComp, def);
                TestAssert.AreEqual(resolvedBase, flow.baseNeeds, 0.001,
                    "flow.baseNeeds for '" + def.defName + "' must match the resolved base needs "
                    + "(restricted needs must be filtered identically in both paths)");
            }

            DestructiveTestUtil.AssertEmpireInvariants(f, "StatusBarFlow_BaseNeeds");
        }

        [EmpireDestructiveTest("SC.Destructive.Needs")]
        public static void ResolveSettlementNeedsFair_ScarceSupply_SplitsProportionally()
        {
            // A1: the Simple-mode fair split is the most bug-prone routine — when the shared faction
            // stockpile can't cover total demand, every need must be filled to the SAME fraction
            // (available / totalDemand), not first-come-first-served. Supplying exactly half the total
            // demand of a resource must fill each of its needs to ~50%.
            FactionFC f = DestructiveTestUtil.RequireFaction();
            if (f.settlements.Count == 0)
            {
                WorldSettlementFC created = DestructiveTestUtil.CreateTransientSettlement();
                if (created is null) TestAssert.Skip("No settlements and no valid tile to create one");
            }

            // 1. Establish per-resource total demand: demand is computed independently of supply, so an
            //    abundant run leaves each need fully fulfilled (fulfilled == demanded) to read back.
            NeedResolver.ResolveSettlementNeedsFair(f, SCDestructiveTestUtil.AbundantStockpile());

            ResourceTypeDef target = null;
            double totalDemand = 0.0;
            foreach (ResourceTypeDef def in SupplyChainCache.AllResourceTypeDefs)
            {
                double d = SumNeedDemand(f, def);
                if (d > 0.0) { target = def; totalDemand = d; break; }
            }
            if (target is null) TestAssert.Skip("No settlement need has positive demand for any resource");

            // 2. Supply exactly half the total demand of the target resource; everything else empty.
            Dictionary<ResourceTypeDef, double> amounts = new Dictionary<ResourceTypeDef, double>();
            Dictionary<ResourceTypeDef, double> caps = new Dictionary<ResourceTypeDef, double>();
            amounts[target] = totalDemand * 0.5;
            caps[target] = 1e9;
            DictionaryStockpile scarce = new DictionaryStockpile(amounts, caps);

            NeedResolver.ResolveSettlementNeedsFair(f, scarce);

            // 3. Each need for the target must be filled to ~50% of its own demand; the total drawn must
            //    equal the available half of total demand.
            double totalFulfilled = 0.0;
            foreach (WorldSettlementFC s in f.settlements)
            {
                WorldObjectComp_SettlementNeeds comp = SupplyChainCache.GetNeedsComp(s);
                if (comp?.NeedStates is null) continue;
                foreach (NeedState ns in comp.NeedStates)
                {
                    if (ns.resource != target || ns.demanded <= 0) continue;
                    TestAssert.AreEqual(ns.demanded * 0.5, ns.fulfilled, ns.demanded * 0.01 + 0.001,
                        "Scarce supply must fill each need proportionally (fillRate 0.5) for " + target.defName);
                    totalFulfilled += ns.fulfilled;
                }
            }
            TestAssert.AreEqual(totalDemand * 0.5, totalFulfilled, totalDemand * 0.01 + 0.01,
                "Total drawn must equal the available half of total demand");

            DestructiveTestUtil.AssertEmpireInvariants(f, "ResolveSettlementNeedsFair_Scarce");
        }

        [EmpireDestructiveTest("SC.Destructive.Needs")]
        public static void RebuildNeedStates_AfterUpgrade_MetNeedStaysMet()
        {
            // Regression: a settlement upgrade raises per-level need demand and rebuilds need-states.
            // RebuildNeedStates must rescale the preserved satisfaction to the new demand, so a need
            // that was fully met stays met — not flash a false shortfall (and its stat penalty) until
            // the next daily resolution redraws. Reproduces the reported "needs unmet right after
            // upgrade, fixed by invoking daily accrual" bug.
            FactionFC f = DestructiveTestUtil.RequireFaction();
            WorldSettlementFC s = SCDestructiveTestUtil.FirstOrTransient(f);
            if (s is null) TestAssert.Skip("No settlement available");
            WorldObjectComp_SettlementNeeds needsComp = SupplyChainCache.GetNeedsComp(s);
            if (needsComp is null) TestAssert.Skip("Settlement has no needs comp");

            int cap = System.Math.Min(FCSettings.settlementMaxLevel, s.settlementDef.maxSettlementLevel);
            if (cap < 1) TestAssert.Skip("Settlement type cannot be leveled");

            int originalLevel = s.settlementLevel;
            try
            {
                // Ensure there is headroom to upgrade by one level.
                if (s.settlementLevel >= cap)
                    s.UpgradeSettlement(cap - 1 - s.settlementLevel);

                // Fully satisfy needs at the starting level so each need's satisfaction ratio is 1.0.
                NeedResolver.ResolveSettlementNeeds(s, SCDestructiveTestUtil.AbundantStockpile(), needsComp);

                Dictionary<string, double> demandBefore = new Dictionary<string, double>();
                foreach (NeedState ns in needsComp.NeedStates)
                    demandBefore[ns.needId] = ns.demanded;

                // The reported action: upgrade one level. Fires OnSettlementUpgraded -> RebuildNeedStates.
                s.UpgradeSettlement(1);

                // Needs whose demand actually rose (per-level scaling) exercise the rescale path.
                int exercised = 0;
                foreach (NeedState ns in needsComp.NeedStates)
                {
                    if (ns.demanded <= 0) continue;
                    if (!demandBefore.TryGetValue(ns.needId, out double prev) || ns.demanded <= prev + 0.001)
                        continue;
                    exercised++;
                    TestAssert.GreaterThan(ns.Satisfaction, 0.999,
                        "Need '" + ns.label + "' was fully met before upgrade; after the level-up rebuild "
                        + "it must stay met (fulfilled rescaled to the higher demand), not show a false shortfall");
                }

                if (exercised == 0)
                    TestAssert.Skip("Settlement has no per-level-scaled needs to exercise the rebuild");
            }
            finally
            {
                // Restore the settlement's original level regardless of assertion outcome.
                int delta = originalLevel - s.settlementLevel;
                if (delta != 0) s.UpgradeSettlement(delta);
            }

            DestructiveTestUtil.AssertEmpireInvariants(f, "RebuildNeedStates_AfterUpgrade");
        }

        /// <summary>Sum of demanded amounts across every settlement's need-states for one resource
        /// (all categories — matches the resolver's per-resource fill-rate denominator).</summary>
        private static double SumNeedDemand(FactionFC f, ResourceTypeDef def)
        {
            double total = 0.0;
            foreach (WorldSettlementFC s in f.settlements)
            {
                WorldObjectComp_SettlementNeeds comp = SupplyChainCache.GetNeedsComp(s);
                if (comp?.NeedStates is null) continue;
                foreach (NeedState ns in comp.NeedStates)
                    if (ns.resource == def && ns.demanded > 0)
                        total += ns.demanded;
            }
            return total;
        }
    }
}
