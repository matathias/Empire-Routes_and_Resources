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
        public static void Realize_OverCap_DepositsUncapped()
        {
            // The daily realize deposit lands the FULL day's production even past the cap
            // (produce-then-consume): the over-cap surplus is reconciled later by the daily overflow
            // sweep, NOT clamped or sold at deposit time. This is what lets today's production cover
            // today's routes/needs instead of being liquidated before consumption runs. Tested in
            // Complex mode where the per-settlement local cap is observable.
            FactionFC f = DestructiveTestUtil.RequireFaction();
            WorldComponent_SupplyChain comp = SupplyChainCache.Comp;
            if (comp is null) TestAssert.Skip("No SupplyChain world component");
            if (comp.Mode != SupplyChainMode.Complex)
                TestAssert.Skip("Uncapped deposit is only observable on a per-settlement local stockpile (Complex mode)");

            WorldSettlementFC s = SCDestructiveTestUtil.FirstOrTransient(f);
            if (s is null) TestAssert.Skip("No settlement available");
            WorldObjectComp_SupplyChain sc = SupplyChainCache.GetSettlementComp(s);
            if (sc is null) TestAssert.Skip("No settlement comp");

            ResourceTypeDef r = null;
            foreach (ResourceTypeDef def in SupplyChainCache.AllResourceTypeDefs) { if (!def.isPoolResource) { r = def; break; } }
            if (r is null) TestAssert.Skip("No non-pool resource defs");

            IStockpile sp = sc.EnsureLocalStockpile();
            double cap = sp.GetCap(r);
            if (cap <= 0) TestAssert.Skip("Resource " + r.defName + " has no local cap headroom to test");

            double before = sp.GetAmount(r);

            // Depositing far more than the cap must land in full (uncapped), not clamp to the cap.
            sc.Realize(r, cap * 2.0, cap * 2.0);

            TestAssert.AreEqual(before + cap * 2.0, sp.GetAmount(r), 0.001,
                "Realize must deposit the full amount uncapped (surplus is swept after consumption, not clamped at deposit)");

            // Restore a <= cap state so we don't leave this settlement massively over-cap for later tests.
            comp.SweepOverflow(sp);

            SCDestructiveTestUtil.AssertStockpilesNonNegative(f, comp, "Realize_OverCap");
            DestructiveTestUtil.AssertEmpireInvariants(f, "Realize_OverCap");
        }

        [EmpireDestructiveTest("SC.Destructive.Daily")]
        public static void Realize_ThenConsume_ThenSweep_RetainsFullStockpile()
        {
            // Regression for the player report: a net-positive producer (produces more than it consumes
            // + routes out) must KEEP a full stockpile across a daily cycle and sell only the genuine
            // surplus — NOT sell all its production as overflow and then empty out. Exercises the real
            // ordering: deposit uncapped (Realize) -> consume (routes/needs) -> sweep true surplus.
            FactionFC f = DestructiveTestUtil.RequireFaction();
            WorldComponent_SupplyChain comp = SupplyChainCache.Comp;
            if (comp is null) TestAssert.Skip("No SupplyChain world component");
            if (comp.Mode != SupplyChainMode.Complex)
                TestAssert.Skip("Local per-settlement stockpile is only observable in Complex mode");

            WorldSettlementFC s = SCDestructiveTestUtil.FirstOrTransient(f);
            if (s is null) TestAssert.Skip("No settlement available");
            WorldObjectComp_SupplyChain sc = SupplyChainCache.GetSettlementComp(s);
            if (sc is null) TestAssert.Skip("No settlement comp");

            ResourceTypeDef r = null;
            foreach (ResourceTypeDef def in SupplyChainCache.AllResourceTypeDefs) { if (!def.isPoolResource) { r = def; break; } }
            if (r is null) TestAssert.Skip("No non-pool resource defs");

            IStockpile sp = sc.EnsureLocalStockpile();
            double cap = sp.GetCap(r);
            if (cap <= 0) TestAssert.Skip("Resource " + r.defName + " has no local cap headroom to test");

            // Precondition: stockpile starts full (mirrors "bought a full stockpile with silver").
            sp.TryDraw(r, sp.GetAmount(r), out _);
            sp.Add(r, cap);
            TestAssert.AreEqual(cap, sp.GetAmount(r), 0.001, "precondition: stockpile starts full");

            // Produce a large batch — auto-max deposits the full day's production, uncapped.
            double production = cap * 1.2;
            sc.Realize(r, production, production);
            TestAssert.GreaterThan(sp.GetAmount(r), cap,
                "deposit must land uncapped so consumption can draw from the day's production");

            // Consume less than we produced (routes out + needs), staying net-positive on the day.
            double consumed = production * 0.8;
            sp.TryDraw(r, consumed, out _);

            // Sell only what storage still cannot hold, after consumption.
            float silver = comp.SweepOverflow(sp);

            TestAssert.AreEqual(cap, sp.GetAmount(r), 0.5,
                "a net-positive settlement must END the day with a FULL stockpile, not empty");
            TestAssert.GreaterThan(silver, 0f, "only the genuine over-cap surplus is sold to silver");

            SCDestructiveTestUtil.AssertStockpilesNonNegative(f, comp, "Realize_ThenConsume_ThenSweep");
            DestructiveTestUtil.AssertEmpireInvariants(f, "Realize_ThenConsume_ThenSweep");
        }

        [EmpireDestructiveTest("SC.Destructive.Daily")]
        public static void DeliveryArrival_OverCap_DepositsUncapped()
        {
            // A route delivery lands via CompleteDelivery BEFORE needs draw in the daily consume pass, so —
            // like the day's production — it must deposit the FULL credited amount uncapped, not clamp to
            // cap and silently drop the remainder. Clamping at arrival was the cause of the player's
            // static-stockpile / under-delivery report. Complex mode only (routes/local caps).
            FactionFC f = DestructiveTestUtil.RequireFaction();
            WorldComponent_SupplyChain comp = SupplyChainCache.Comp;
            if (comp is null) TestAssert.Skip("No SupplyChain world component");
            if (comp.Mode != SupplyChainMode.Complex)
                TestAssert.Skip("Route arrivals are only observable on a per-settlement local stockpile (Complex mode)");

            WorldSettlementFC s = SCDestructiveTestUtil.FirstOrTransient(f);
            if (s is null) TestAssert.Skip("No settlement available");
            WorldObjectComp_SupplyChain sc = SupplyChainCache.GetSettlementComp(s);
            if (sc is null) TestAssert.Skip("No settlement comp");

            ResourceTypeDef r = null;
            foreach (ResourceTypeDef def in SupplyChainCache.AllResourceTypeDefs) { if (!def.isPoolResource) { r = def; break; } }
            if (r is null) TestAssert.Skip("No non-pool resource defs");

            IStockpile sp = sc.EnsureLocalStockpile();
            double cap = sp.GetCap(r);
            if (cap <= 0) TestAssert.Skip("Resource " + r.defName + " has no local cap headroom to test");

            // Start full so any arrival would overflow a capped credit.
            sp.TryDraw(r, sp.GetAmount(r), out _);
            sp.Add(r, cap);
            double before = sp.GetAmount(r);

            // Arrive a full cap's worth (efficiency 1.0 -> credited == cap) into the already-full stockpile.
            PendingDelivery d = new PendingDelivery
            {
                loadId = -1001, source = s, destination = s, resource = r, amount = cap, efficiency = 1.0
            };
            comp.PendingDeliveries.Add(d);
            comp.CompleteDelivery(d);

            TestAssert.AreEqual(before + cap, sp.GetAmount(r), 0.001,
                "Route arrival must deposit uncapped (surplus is swept after consumption, not clamped at arrival)");

            // Restore a <= cap state so we don't leave this settlement massively over-cap for later tests.
            comp.SweepOverflow(sp);

            SCDestructiveTestUtil.AssertStockpilesNonNegative(f, comp, "DeliveryArrival_OverCap");
            DestructiveTestUtil.AssertEmpireInvariants(f, "DeliveryArrival_OverCap");
        }

        [EmpireDestructiveTest("SC.Destructive.Daily")]
        public static void DeliveryArrival_ThenConsume_GrowsStockpile()
        {
            // Exact player report: a settlement below cap, fed by a route whose incoming exceeds its
            // consumption, must GROW day over day — not stay static. Old (capped-arrival) behavior clipped
            // the delivery to the cap before needs drew, netting back to the starting amount. With the
            // uncapped arrival + end-of-pass sweep, the stockpile grows by (incoming - consumption).
            FactionFC f = DestructiveTestUtil.RequireFaction();
            WorldComponent_SupplyChain comp = SupplyChainCache.Comp;
            if (comp is null) TestAssert.Skip("No SupplyChain world component");
            if (comp.Mode != SupplyChainMode.Complex)
                TestAssert.Skip("Local per-settlement stockpile is only observable in Complex mode");

            WorldSettlementFC s = SCDestructiveTestUtil.FirstOrTransient(f);
            if (s is null) TestAssert.Skip("No settlement available");
            WorldObjectComp_SupplyChain sc = SupplyChainCache.GetSettlementComp(s);
            if (sc is null) TestAssert.Skip("No settlement comp");

            ResourceTypeDef r = null;
            foreach (ResourceTypeDef def in SupplyChainCache.AllResourceTypeDefs) { if (!def.isPoolResource) { r = def; break; } }
            if (r is null) TestAssert.Skip("No non-pool resource defs");

            IStockpile sp = sc.EnsureLocalStockpile();
            double cap = sp.GetCap(r);
            if (cap <= 0) TestAssert.Skip("Resource " + r.defName + " has no local cap headroom to test");

            // Scaled to the reported numbers at cap 50: start 37.5, incoming 16, consume 12.5. The
            // arrival momentarily pushes over cap (37.5 + 16 = 53.5) — the fix is that consumption then
            // reclaims it (down to 41) instead of the delivery being clipped to cap and lost.
            double start = cap * 0.75;
            double incoming = cap * 0.32;
            double consume = cap * 0.25;

            sp.TryDraw(r, sp.GetAmount(r), out _);
            sp.Add(r, start);

            PendingDelivery d = new PendingDelivery
            {
                loadId = -1002, source = s, destination = s, resource = r, amount = incoming, efficiency = 1.0
            };
            comp.PendingDeliveries.Add(d);
            comp.CompleteDelivery(d);        // uncapped deposit (may exceed cap)

            sp.TryDraw(r, consume, out _);   // needs draw from the now-filled stockpile
            comp.SweepOverflow(sp);          // sell only the genuine over-cap surplus

            double expected = System.Math.Min(cap, start + incoming - consume);
            TestAssert.AreEqual(expected, sp.GetAmount(r), 0.5,
                "arrival must let the stockpile grow by (incoming - consumption), bounded by cap");
            TestAssert.GreaterThan(sp.GetAmount(r), start,
                "a net-positive settlement must GROW its stockpile, not stay static at the starting amount");

            SCDestructiveTestUtil.AssertStockpilesNonNegative(f, comp, "DeliveryArrival_ThenConsume");
            DestructiveTestUtil.AssertEmpireInvariants(f, "DeliveryArrival_ThenConsume");
        }

        [EmpireDestructiveTest("SC.Destructive.Daily")]
        public static void SweepOverflow_PoolResource_ClampedButNotSold()
        {
            // Pool resources (power/research) can be diverted into a stockpile and auto-maxed, so the
            // daily sweep MUST clamp them to cap like any other resource — otherwise, now that the deposit
            // is uncapped, an auto-maxed pool allocation would accumulate over cap forever. But pool
            // resources have no silver value, so their over-cap surplus is dropped, never auto-sold.
            WorldComponent_SupplyChain comp = SupplyChainCache.Comp;
            if (comp is null) TestAssert.Skip("No SupplyChain world component");

            ResourceTypeDef pool = null;
            foreach (ResourceTypeDef def in SupplyChainCache.AllResourceTypeDefs) { if (def.isPoolResource) { pool = def; break; } }
            if (pool is null) TestAssert.Skip("No pool resource types defined");

            // A throwaway stockpile holding ONLY the pool resource, seeded to twice its cap. Every other
            // resource reads amount/cap 0 and is skipped, so the returned silver isolates the pool.
            const double cap = 50.0;
            DictionaryStockpile sp = SCTestHelper.MakeStockpile(pool, cap * 2.0, cap);

            float silver = comp.SweepOverflow(sp);

            TestAssert.AreEqual(cap, sp.GetAmount(pool), 0.001, "the sweep must clamp a pool resource to its cap");
            TestAssert.AreEqual(0.0, silver, 0.001, "pool overflow has no silver value and must not be auto-sold");
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
