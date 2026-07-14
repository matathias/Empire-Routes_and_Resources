using System.Linq;
using Verse;

namespace FactionColonies.SupplyChain
{
    /* DESTRUCTIVE: needs two live settlements (may create transient ones) to compute a real route
       efficiency from travel distance. The stockpiles are throwaway fixtures, but settlement
       creation mutates the world. Not reverted. */
    public static class SupplyRouteDestructiveTests
    {
        [EmpireDestructiveTest("SC.Destructive.Routes")]
        public static void Dispatch_DrawsFromSource_ArrivesWithEfficiencyLoss()
        {
            FactionFC f = DestructiveTestUtil.RequireFaction();
            ResourceTypeDef r = SupplyChainCache.AllResourceTypeDefs.FirstOrDefault();
            if (r is null) TestAssert.Skip("No resource types defined");

            WorldSettlementFC src = SCDestructiveTestUtil.SettlementAt(f, 0);
            WorldSettlementFC dst = SCDestructiveTestUtil.SettlementAt(f, 1);
            if (src is null || dst is null) TestAssert.Skip("Could not obtain two settlements");
            if (src == dst) TestAssert.Skip("Only one settlement available; need a distinct source and destination");

            var route = new SupplyRoute(src, dst, r, 50.0);
            TestAssert.DoesNotThrow(() => route.RecacheIfDirty(), "RecacheIfDirty threw");
            double eff = route.CachedEfficiency;
            TestAssert.GreaterThan(eff, 0.0, "Route efficiency should be positive between valid settlements");
            TestAssert.LessThanOrEqual(eff, 1.0, "Route efficiency must not exceed 1.0");

            DictionaryStockpile sourceSp = SCTestHelper.MakeStockpile(r, 100.0, 100.0);
            DictionaryStockpile destSp = SCTestHelper.MakeStockpile(r, 0.0, 1000.0);

            // Dispatch draws the full amount from the source immediately (in-transit).
            PendingDelivery d = route.TryDispatch(sourceSp);
            TestAssert.IsNotNull(d, "TryDispatch should produce a delivery when the source has stock");
            TestAssert.AreEqual(50.0, d.amount, 0.01, "Delivery carries the drawn amount");
            TestAssert.AreEqual(eff, d.efficiency, 0.01, "Delivery snapshots the route efficiency");
            TestAssert.AreEqual(50.0, sourceSp.GetAmount(r), 0.01, "Source should have 100 - 50 = 50 left at dispatch");
            TestAssert.LessThan((double)Find.TickManager.TicksGame, d.arrivalTick + 1.0, "Arrival must be in the future");

            // Simulate arrival (mirrors WorldComponent_SupplyChain.ProcessArrivals): efficiency applied here.
            double credited = d.amount * d.efficiency;
            double excess = destSp.Credit(r, credited);
            double received = credited - excess;

            TestAssert.AreEqual(50.0 * eff, received, 0.01,
                "Received should equal drawn * efficiency when the destination has room");
            TestAssert.AreEqual(received, destSp.GetAmount(r), 0.01,
                "Destination should hold exactly the received amount");

            DestructiveTestUtil.AssertEmpireInvariants(f, "SupplyRoute_Dispatch");
        }

        [EmpireDestructiveTest("SC.Destructive.Routes")]
        public static void Dispatch_DestinationFullOnArrival_ExcessLost()
        {
            FactionFC f = DestructiveTestUtil.RequireFaction();
            ResourceTypeDef r = SupplyChainCache.AllResourceTypeDefs.FirstOrDefault();
            if (r is null) TestAssert.Skip("No resource types defined");

            WorldSettlementFC src = SCDestructiveTestUtil.SettlementAt(f, 0);
            WorldSettlementFC dst = SCDestructiveTestUtil.SettlementAt(f, 1);
            if (src is null || dst is null) TestAssert.Skip("Could not obtain two settlements");
            if (src == dst) TestAssert.Skip("Only one settlement available; need a distinct source and destination");

            var route = new SupplyRoute(src, dst, r, 50.0);
            route.RecacheIfDirty();
            double eff = route.CachedEfficiency;
            if (eff <= 0.0) TestAssert.Skip("Route resolved to zero efficiency");

            DictionaryStockpile sourceSp = SCTestHelper.MakeStockpile(r, 100.0, 100.0);
            DictionaryStockpile destSp = SCTestHelper.MakeStockpile(r, 0.0, 1.0); // tiny cap forces overflow on arrival

            PendingDelivery d = route.TryDispatch(sourceSp);
            TestAssert.IsNotNull(d, "TryDispatch should produce a delivery when the source has stock");

            // Simulate arrival into a nearly-full destination.
            double credited = d.amount * d.efficiency;
            double excess = destSp.Credit(r, credited);
            double received = credited - excess;

            TestAssert.LessThanOrEqual(received, 1.0, "Received cannot exceed the destination cap");
            TestAssert.AreEqual(received, destSp.GetAmount(r), 0.01,
                "Destination should hold exactly what fit; the rest is lost");

            DestructiveTestUtil.AssertEmpireInvariants(f, "SupplyRoute_Overflow");
        }

        [EmpireDestructiveTest("SC.Destructive.Routes")]
        public static void SetFrequencyDays_OutOfRange_ClampsToBounds()
        {
            // A10: dispatch frequency is clamped to [minRouteFrequencyDays, maxRouteFrequencyDays].
            ResourceTypeDef r = SupplyChainCache.AllResourceTypeDefs.FirstOrDefault();
            if (r is null) TestAssert.Skip("No resource types defined");
            FactionFC f = DestructiveTestUtil.RequireFaction();
            WorldSettlementFC src = SCDestructiveTestUtil.SettlementAt(f, 0);
            WorldSettlementFC dst = SCDestructiveTestUtil.SettlementAt(f, 1);
            if (src is null || dst is null || src == dst) TestAssert.Skip("Need two distinct settlements");

            var route = new SupplyRoute(src, dst, r, 10.0);
            route.SetFrequencyDays(-999);
            TestAssert.AreEqual(SupplyChainSettings.minRouteFrequencyDays, route.frequencyDays,
                "Frequency below the minimum must clamp to minRouteFrequencyDays");
            route.SetFrequencyDays(int.MaxValue);
            TestAssert.AreEqual(SupplyChainSettings.maxRouteFrequencyDays, route.frequencyDays,
                "Frequency above the maximum must clamp to maxRouteFrequencyDays");
        }

        [EmpireDestructiveTest("SC.Destructive.Routes")]
        public static void Dispatch_ConstrainedSource_EarlierRouteWinsFirst()
        {
            // A10: dispatch order is list order — when the shared source can't feed both routes, the
            // earlier route draws its full amount and the later one only gets the remainder.
            ResourceTypeDef r = SupplyChainCache.AllResourceTypeDefs.FirstOrDefault();
            if (r is null) TestAssert.Skip("No resource types defined");
            FactionFC f = DestructiveTestUtil.RequireFaction();
            WorldSettlementFC src = SCDestructiveTestUtil.SettlementAt(f, 0);
            WorldSettlementFC dst = SCDestructiveTestUtil.SettlementAt(f, 1);
            if (src is null || dst is null || src == dst) TestAssert.Skip("Need two distinct settlements");

            var first = new SupplyRoute(src, dst, r, 50.0);
            var second = new SupplyRoute(src, dst, r, 50.0);
            first.RecacheIfDirty();
            second.RecacheIfDirty();
            if (first.CachedEfficiency <= 0 || second.CachedEfficiency <= 0)
                TestAssert.Skip("Route efficiency resolved to zero");

            // Shared source holds 60 — enough for the first route's full 50, leaving 10 for the second.
            DictionaryStockpile sourceSp = SCTestHelper.MakeStockpile(r, 60.0, 1000.0);

            PendingDelivery d1 = first.TryDispatch(sourceSp);
            PendingDelivery d2 = second.TryDispatch(sourceSp);

            TestAssert.IsNotNull(d1, "The earlier route should dispatch from a stocked source");
            TestAssert.AreEqual(50.0, d1.amount, 0.01, "The earlier route draws its full amount first");
            if (d2 != null)
                TestAssert.AreEqual(10.0, d2.amount, 0.01, "The later route only gets the remaining 10");
            TestAssert.AreEqual(0.0, sourceSp.GetAmount(r), 0.01, "The constrained source is drained in dispatch order");

            DestructiveTestUtil.AssertEmpireInvariants(f, "Dispatch_ConstrainedSource");
        }

        [EmpireDestructiveTest("SC.Destructive.Routes")]
        public static void RouteModifierHook_IsApplied_AndEfficiencyClampedToOne()
        {
            // A10/A13: a registered ISupplyRouteModifier is invoked during the efficiency recompute, and
            // its (deliberately out-of-range) return is clamped back into [0,1].
            ResourceTypeDef r = SupplyChainCache.AllResourceTypeDefs.FirstOrDefault();
            if (r is null) TestAssert.Skip("No resource types defined");
            FactionFC f = DestructiveTestUtil.RequireFaction();
            WorldSettlementFC src = SCDestructiveTestUtil.SettlementAt(f, 0);
            WorldSettlementFC dst = SCDestructiveTestUtil.SettlementAt(f, 1);
            if (src is null || dst is null || src == dst) TestAssert.Skip("Need two distinct settlements");

            var route = new SupplyRoute(src, dst, r, 10.0);
            var mod = new FixedEfficiencyModifier(5.0); // out of range on purpose to exercise the clamp
            SupplyRouteModifierRegistry.Register(mod);
            try
            {
                route.RecacheIfDirty();
                TestAssert.AreEqual(1.0, route.CachedEfficiency, 0.001,
                    "A registered modifier is applied, and the result is clamped to at most 1.0");
            }
            finally
            {
                SupplyRouteModifierRegistry.Unregister(mod);
            }

            // With the modifier gone, efficiency recomputes back into the normal (0,1] range.
            route.MarkEfficiencyDirty();
            route.RecacheIfDirty();
            TestAssert.LessThanOrEqual(route.CachedEfficiency, 1.0, "Efficiency without the modifier stays within 1.0");

            DestructiveTestUtil.AssertEmpireInvariants(f, "RouteModifierHook");
        }

        /// <summary>Test ISupplyRouteModifier that forces a fixed efficiency, to prove the hook fires
        /// and its output is clamped.</summary>
        private class FixedEfficiencyModifier : ISupplyRouteModifier
        {
            private readonly double value;
            public FixedEfficiencyModifier(double value) { this.value = value; }
            public double ModifyRouteEfficiency(SupplyRoute route, double baseEfficiency) => value;
        }
    }
}
