namespace FactionColonies.SupplyChain;

/* DESTRUCTIVE: resolves needs against a real settlement, overwriting its live need-states and
   invalidating its stat cache. Stockpiles passed in are throwaway fixtures. May create a transient
   settlement. Not reverted. */
public static class NeedResolverDestructiveTests
{
    [EmpireDestructiveTest("SC.Destructive.Needs")]
    public static void ResolveSettlementNeeds_AbundantStockpile_AllNeedsMet()
    {
        FactionFC f = DestructiveTestUtil.RequireFaction();
        WorldSettlementFC s = SCDestructiveTestUtil.EnsureSCSettlement();
        WorldObjectComp_SettlementNeeds needsComp = SCDestructiveTestUtil.RequireNeedsComp(s);

        DictionaryStockpile sp = SCDestructiveTestUtil.AbundantStockpile();
        TestAssert.DoesNotThrow(() => NeedResolver.ResolveSettlementNeeds(s, sp, needsComp),
            "ResolveSettlementNeeds threw");

        // This mod ships ungated base needs (food, building materials), so every settlement has some.
        List<NeedState> states = needsComp.NeedStates;
        TestAssert.IsTrue(states is not null && states.Count > 0, "Settlement should have active needs to satisfy");

        foreach (NeedState ns in states)
            TestAssert.GreaterThan(ns.Satisfaction, 0.999,
                "Need '" + ns.label + "' should be fully met from an abundant stockpile");

        DestructiveTestUtil.AssertEmpireInvariants(f, "ResolveSettlementNeeds_Abundant");
    }

    [EmpireDestructiveTest("SC.Destructive.Needs")]
    public static void ResolveSettlementNeeds_EmptyStockpile_NothingFulfilled()
    {
        FactionFC f = DestructiveTestUtil.RequireFaction();
        WorldSettlementFC s = SCDestructiveTestUtil.EnsureSCSettlement();
        WorldObjectComp_SettlementNeeds needsComp = SCDestructiveTestUtil.RequireNeedsComp(s);

        DictionaryStockpile sp = SCTestHelper.MakeEmptyStockpile();
        TestAssert.DoesNotThrow(() => NeedResolver.ResolveSettlementNeeds(s, sp, needsComp),
            "ResolveSettlementNeeds threw");

        List<NeedState> states = needsComp.NeedStates;
        bool anyPositiveDemand = false;
        if (states is not null)
        {
            foreach (NeedState ns in states)
            {
                if (ns.demanded <= 0) continue;
                anyPositiveDemand = true;
                TestAssert.AreEqual(0.0, ns.fulfilled, 0.001,
                    "Empty stockpile should fulfill nothing for '" + ns.label + "'");
            }
        }
        // Building materials scale per level and every settlement is at least level 1.
        TestAssert.IsTrue(anyPositiveDemand, "Settlement should have at least one positive-demand need");

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
        WorldSettlementFC s = SCDestructiveTestUtil.EnsureSCSettlement();
        WorldObjectComp_SettlementNeeds needsComp = SCDestructiveTestUtil.RequireNeedsComp(s);
        WorldObjectComp_SupplyChain dataComp = SCDestructiveTestUtil.RequireSettlementComp(s);
        WorldComponent_SupplyChain worldComp = SCDestructiveTestUtil.RequireWorldComp();

        // Resolve fresh base need-states for this settlement.
        DictionaryStockpile sp = SCDestructiveTestUtil.AbundantStockpile();
        NeedResolver.ResolveSettlementNeeds(s, sp, needsComp);

        List<NeedState> states = needsComp.NeedStates;
        TestAssert.IsTrue(states is not null && states.Count > 0, "Settlement should have active needs to compare");

        foreach (ResourceTypeDef def in FactionCache.AllResourceTypeDefs)
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
        SCDestructiveTestUtil.EnsureSCSettlement();

        // 1. Establish per-resource total demand: demand is computed independently of supply, so an
        //    abundant run leaves each need fully fulfilled (fulfilled == demanded) to read back.
        NeedResolver.ResolveSettlementNeedsFair(f, SCDestructiveTestUtil.AbundantStockpile());

        ResourceTypeDef target = null;
        double totalDemand = 0.0;
        foreach (ResourceTypeDef def in FactionCache.AllResourceTypeDefs)
        {
            double d = SumNeedDemand(f, def);
            if (d > 0.0) { target = def; totalDemand = d; break; }
        }
        TestAssert.NotNull(target, "Some settlement need should have positive demand (building materials scale per level)");

        // 2. Supply exactly half the total demand of the target resource; everything else empty.
        Dictionary<ResourceTypeDef, double> amounts = [];
        Dictionary<ResourceTypeDef, double> caps = [];
        amounts[target] = totalDemand * 0.5;
        caps[target] = 1e9;
        DictionaryStockpile scarce = new(amounts, caps);

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
        WorldSettlementFC s = SCDestructiveTestUtil.EnsureSCSettlement(x => x.settlementDef.maxSettlementLevel >= 2);
        WorldObjectComp_SettlementNeeds needsComp = SCDestructiveTestUtil.RequireNeedsComp(s);

        int originalMaxLevel = FCSettings.settlementMaxLevel;
        int originalLevel = s.settlementLevel;
        try
        {
            // Need a level cap of at least 2 so the settlement can sit at level >= 1 and still go up one.
            if (FCSettings.settlementMaxLevel < 2) FCSettings.settlementMaxLevel = 2;
            int cap = System.Math.Min(FCSettings.settlementMaxLevel, s.settlementDef.maxSettlementLevel);
            TestAssert.IsTrue(cap >= 2, $"Settlement type {s.settlementDef.defName} must allow at least level 2");

            // Ensure there is headroom to upgrade by one level.
            if (s.settlementLevel >= cap)
                s.UpgradeSettlement(cap - 1 - s.settlementLevel);

            // Fully satisfy needs at the starting level so each need's satisfaction ratio is 1.0.
            NeedResolver.ResolveSettlementNeeds(s, SCDestructiveTestUtil.AbundantStockpile(), needsComp);

            Dictionary<string, double> demandBefore = [];
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

            // Building materials scale per level and are not tech-gated, so an upgrade always raises
            // at least one need's demand.
            TestAssert.GreaterThan(exercised, 0, "An upgrade should raise at least one per-level-scaled need");
        }
        finally
        {
            // Restore the settlement's original level (and the level cap) regardless of assertion outcome.
            int delta = originalLevel - s.settlementLevel;
            if (delta != 0) s.UpgradeSettlement(delta);
            FCSettings.settlementMaxLevel = originalMaxLevel;
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
