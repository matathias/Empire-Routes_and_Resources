using System.Collections.Generic;

namespace FactionColonies.SupplyChain
{
    /* DESTRUCTIVE: runs the real daily-accrual consume pass (PostDailyAccrual) and the per-building
       dormancy driver against live settlements. Deposits via Realize, draws needs/inputs/tithe, and
       toggles BuildingFC.active. Not reverted. */
    public static class DailyAccrualDestructiveTests
    {
        [EmpireDestructiveTest("SC.Destructive.Daily")]
        public static void PostDailyAccrual_DoesNotThrow_AndStockpilesNonNegative()
        {
            FactionFC f = DestructiveTestUtil.RequireFaction();
            WorldComponent_SupplyChain comp = SupplyChainCache.Comp;
            if (comp is null) TestAssert.Skip("No SupplyChain world component");

            if (f.settlements.Count == 0)
            {
                WorldSettlementFC created = DestructiveTestUtil.CreateTransientSettlement();
                if (created is null) TestAssert.Skip("No settlements and no valid tile to create one");
            }

            TestAssert.DoesNotThrow(() => comp.PostDailyAccrual(f), "PostDailyAccrual threw");

            SCDestructiveTestUtil.AssertStockpilesNonNegative(f, comp, "PostDailyAccrual");
            DestructiveTestUtil.AssertEmpireInvariants(f, "PostDailyAccrual");
        }

        [EmpireDestructiveTest("SC.Destructive.Daily")]
        public static void Realize_Deposits_AndStaysNonNegative()
        {
            FactionFC f = DestructiveTestUtil.RequireFaction();
            WorldComponent_SupplyChain comp = SupplyChainCache.Comp;
            if (comp is null) TestAssert.Skip("No SupplyChain world component");

            WorldSettlementFC s = SCDestructiveTestUtil.FirstOrTransient(f);
            if (s is null) TestAssert.Skip("No settlement available");
            WorldObjectComp_SupplyChain sc = SupplyChainCache.GetSettlementComp(s);
            if (sc is null) TestAssert.Skip("No settlement comp");

            ResourceTypeDef r = null;
            foreach (ResourceTypeDef def in SupplyChainCache.AllResourceTypeDefs) { r = def; break; }
            if (r is null) TestAssert.Skip("No resource defs");

            // Deposit a modest per-day amount the way the base mod's realize callback would.
            TestAssert.DoesNotThrow(() => sc.Realize(r, 5.0, 5.0), "Realize threw");

            SCDestructiveTestUtil.AssertStockpilesNonNegative(f, comp, "Realize");
            DestructiveTestUtil.AssertEmpireInvariants(f, "Realize");
        }

        [EmpireDestructiveTest("SC.Destructive.Daily")]
        public static void Realize_OverCap_ClampsLocalStockpile()
        {
            // A7: the daily realize deposit must never push a stockpile past its cap — the over-cap
            // remainder is auto-sold to silver, and the stockpile lands exactly at the cap. Tested in
            // Complex mode where each settlement has a bounded local cap we can read back.
            FactionFC f = DestructiveTestUtil.RequireFaction();
            WorldComponent_SupplyChain comp = SupplyChainCache.Comp;
            if (comp is null) TestAssert.Skip("No SupplyChain world component");
            if (comp.Mode != SupplyChainMode.Complex)
                TestAssert.Skip("Over-cap clamp is only observable on a per-settlement local stockpile (Complex mode)");

            WorldSettlementFC s = SCDestructiveTestUtil.FirstOrTransient(f);
            if (s is null) TestAssert.Skip("No settlement available");
            WorldObjectComp_SupplyChain sc = SupplyChainCache.GetSettlementComp(s);
            if (sc is null) TestAssert.Skip("No settlement comp");

            ResourceTypeDef r = null;
            foreach (ResourceTypeDef def in SupplyChainCache.AllResourceTypeDefs) { r = def; break; }
            if (r is null) TestAssert.Skip("No resource defs");

            IStockpile sp = sc.EnsureLocalStockpile();
            double cap = sp.GetCap(r);
            if (cap <= 0) TestAssert.Skip("Resource " + r.defName + " has no local cap headroom to test");

            // Depositing far more than the cap must clamp to exactly the cap, never overflow it.
            sc.Realize(r, cap * 2.0, cap * 2.0);

            TestAssert.LessThanOrEqual(sp.GetAmount(r), cap + 0.001, "Realize must not push the stockpile past its cap");
            TestAssert.GreaterThan(sp.GetAmount(r), cap - 0.001, "An over-cap deposit should fill the stockpile to exactly the cap");

            SCDestructiveTestUtil.AssertStockpilesNonNegative(f, comp, "Realize_OverCap");
            DestructiveTestUtil.AssertEmpireInvariants(f, "Realize_OverCap");
        }

        [EmpireDestructiveTest("SC.Destructive.Daily")]
        public static void ResolveBuildingDormancy_DoesNotThrow()
        {
            FactionFC f = DestructiveTestUtil.RequireFaction();
            WorldComponent_SupplyChain comp = SupplyChainCache.Comp;
            if (comp is null) TestAssert.Skip("No SupplyChain world component");
            if (f.settlements.Count == 0) TestAssert.Skip("No settlements");

            foreach (WorldSettlementFC s in f.settlements)
            {
                WorldObjectComp_SupplyChain sc = SupplyChainCache.GetSettlementComp(s);
                IStockpile sp = comp.Mode == SupplyChainMode.Simple ? comp.Stockpile : sc?.GetStockpile();
                if (sp is null) continue;
                WorldSettlementFC captured = s;
                TestAssert.DoesNotThrow(
                    () => NeedResolver.ResolveBuildingDormancy(captured, sp),
                    "ResolveBuildingDormancy threw for " + s.Name);
            }
            DestructiveTestUtil.AssertEmpireInvariants(f, "ResolveBuildingDormancy");
        }

        [EmpireDestructiveTest("SC.Destructive.Daily")]
        public static void BuildingDormancy_StarvedInputBuilding_GoesDormant()
        {
            FactionFC f = DestructiveTestUtil.RequireFaction();
            WorldComponent_SupplyChain comp = SupplyChainCache.Comp;
            if (comp is null) TestAssert.Skip("No SupplyChain world component");

            // Find a settlement with an input-requiring building.
            foreach (WorldSettlementFC s in f.settlements)
            {
                if (s.BuildingsComp is null) continue;

                List<BuildingFC> buildings = s.BuildingsComp.Buildings;
                for (int slot = 0; slot < buildings.Count; slot++)
                {
                    BuildingFC b = buildings[slot];
                    if (b.def is null || b.def == BuildingFCDefOf.Empty) continue;
                    BuildingNeedExtension ext = SupplyChainCache.GetBuildingNeedExt(b.def);
                    if (ext?.inputs is null || ext.inputs.Count == 0) continue;

                    WorldObjectComp_SupplyChain sc = SupplyChainCache.GetSettlementComp(s);
                    IStockpile sp = comp.Mode == SupplyChainMode.Simple ? comp.Stockpile : sc?.GetStockpile();
                    if (sp is null) continue;

                    // Drain this building's inputs from the stockpile so it cannot be afforded.
                    foreach (BuildingResourceInput input in ext.inputs)
                    {
                        if (input.resource is null) continue;
                        sp.TryDraw(input.resource, sp.GetAmount(input.resource), out _);
                    }

                    NeedResolver.ResolveBuildingDormancy(s, sp);
                    TestAssert.IsFalse(buildings[slot].active,
                        "A starved input building (" + b.def.defName + ") should be dormant");
                    DestructiveTestUtil.AssertEmpireInvariants(f, "BuildingDormancy_Starved");
                    return;
                }
            }
            TestAssert.Skip("No input-requiring building available to starve");
        }

        [EmpireDestructiveTest("SC.Destructive.Daily")]
        public static void BuildingDormancy_FullySupplied_StaysActiveAndDrawsInputs()
        {
            // A4 (positive case): when every input is present in full, the building stays active AND its
            // inputs are drawn down by exactly their per-day amount (against a throwaway fixture stockpile).
            FactionFC f = DestructiveTestUtil.RequireFaction();
            if (SupplyChainCache.Comp is null) TestAssert.Skip("No SupplyChain world component");

            foreach (WorldSettlementFC s in f.settlements)
            {
                if (s.BuildingsComp is null) continue;
                List<BuildingFC> buildings = s.BuildingsComp.Buildings;
                for (int slot = 0; slot < buildings.Count; slot++)
                {
                    BuildingFC b = buildings[slot];
                    if (b.def is null || b.def == BuildingFCDefOf.Empty) continue;
                    BuildingNeedExtension ext = SupplyChainCache.GetBuildingNeedExt(b.def);
                    if (ext?.inputs is null || ext.inputs.Count == 0) continue;

                    // Seed every input at twice its requirement in a throwaway stockpile.
                    DictionaryStockpile sp = BuildInputStockpile(ext, 2.0);

                    NeedResolver.ResolveBuildingDormancy(s, sp);

                    TestAssert.IsTrue(buildings[slot].active,
                        "A fully-supplied building (" + b.def.defName + ") should stay active");
                    foreach (BuildingResourceInput input in ext.inputs)
                    {
                        if (input.resource is null || input.amount <= 0) continue;
                        // Seeded 2x, one day's input drawn -> exactly amount remains.
                        TestAssert.AreEqual(input.amount, sp.GetAmount(input.resource), 0.001,
                            "An active building must draw exactly one period of " + input.resource.defName);
                    }
                    DestructiveTestUtil.AssertEmpireInvariants(f, "BuildingDormancy_FullySupplied");
                    return;
                }
            }
            TestAssert.Skip("No input-requiring building available to supply");
        }

        [EmpireDestructiveTest("SC.Destructive.Daily")]
        public static void BuildingDormancy_PartialInputs_DormantAndDrawsNothing()
        {
            // A4 (all-or-nothing): if ANY required input is short, the building goes dormant and draws
            // NOTHING — the inputs it can afford stay in the pile for other buildings.
            FactionFC f = DestructiveTestUtil.RequireFaction();
            if (SupplyChainCache.Comp is null) TestAssert.Skip("No SupplyChain world component");

            foreach (WorldSettlementFC s in f.settlements)
            {
                if (s.BuildingsComp is null) continue;
                List<BuildingFC> buildings = s.BuildingsComp.Buildings;
                for (int slot = 0; slot < buildings.Count; slot++)
                {
                    BuildingFC b = buildings[slot];
                    if (b.def is null || b.def == BuildingFCDefOf.Empty) continue;
                    BuildingNeedExtension ext = SupplyChainCache.GetBuildingNeedExt(b.def);
                    if (ext?.inputs is null || ext.inputs.Count == 0) continue;

                    // Find the first real input to short; skip buildings whose inputs are all null/zero.
                    BuildingResourceInput shorted = null;
                    foreach (BuildingResourceInput input in ext.inputs)
                    {
                        if (input.resource is null || input.amount <= 0) continue;
                        shorted = input;
                        break;
                    }
                    if (shorted is null) continue;

                    // Seed every input abundantly EXCEPT the shorted one, which gets half its requirement.
                    Dictionary<ResourceTypeDef, double> amounts = new Dictionary<ResourceTypeDef, double>();
                    Dictionary<ResourceTypeDef, double> caps = new Dictionary<ResourceTypeDef, double>();
                    foreach (BuildingResourceInput input in ext.inputs)
                    {
                        if (input.resource is null) continue;
                        amounts[input.resource] = input == shorted ? shorted.amount * 0.5 : input.amount * 2.0;
                        caps[input.resource] = 1e9;
                    }
                    DictionaryStockpile sp = new DictionaryStockpile(amounts, caps);

                    // Snapshot the affordable inputs so we can prove none were drawn.
                    Dictionary<ResourceTypeDef, double> before = new Dictionary<ResourceTypeDef, double>();
                    foreach (BuildingResourceInput input in ext.inputs)
                        if (input.resource != null)
                            before[input.resource] = sp.GetAmount(input.resource);

                    NeedResolver.ResolveBuildingDormancy(s, sp);

                    TestAssert.IsFalse(buildings[slot].active,
                        "A partially-supplied building (" + b.def.defName + ") should be dormant");
                    foreach (KeyValuePair<ResourceTypeDef, double> kv in before)
                        TestAssert.AreEqual(kv.Value, sp.GetAmount(kv.Key), 0.001,
                            "A dormant building must draw NOTHING — " + kv.Key.defName + " should be untouched");

                    DestructiveTestUtil.AssertEmpireInvariants(f, "BuildingDormancy_Partial");
                    return;
                }
            }
            TestAssert.Skip("No input-requiring building available to short");
        }

        /// <summary>Throwaway stockpile seeded with each of a building's inputs at (amount * factor).</summary>
        private static DictionaryStockpile BuildInputStockpile(BuildingNeedExtension ext, double factor)
        {
            Dictionary<ResourceTypeDef, double> amounts = new Dictionary<ResourceTypeDef, double>();
            Dictionary<ResourceTypeDef, double> caps = new Dictionary<ResourceTypeDef, double>();
            foreach (BuildingResourceInput input in ext.inputs)
            {
                if (input.resource is null) continue;
                amounts[input.resource] = input.amount * factor;
                caps[input.resource] = 1e9;
            }
            return new DictionaryStockpile(amounts, caps);
        }
    }
}
