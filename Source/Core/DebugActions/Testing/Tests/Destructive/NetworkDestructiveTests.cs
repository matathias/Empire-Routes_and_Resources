namespace FactionColonies.SupplyChain;

/* DESTRUCTIVE: links/unlinks real routes on live settlement comps to verify trade-network partner
   tracking. May create a transient settlement to get an unpartnered pair. The two throwaway routes
   created here are removed again in a finally block, restoring the partner sets. Not otherwise
   reverted. */
public static class NetworkDestructiveTests
{
    [EmpireDestructiveTest("SC.Destructive.Network")]
    public static void PartnerLink_DroppedOnlyWhenLastRouteRemoved()
    {
        FactionFC f = DestructiveTestUtil.RequireFaction();
        WorldComponent_SupplyChain wc = SCDestructiveTestUtil.RequireWorldComp();
        ResourceTypeDef r = SCDestructiveTestUtil.RequireAnyResource();

        // The pair must not already be partnered, or we can't attribute the membership change.
        (WorldSettlementFC src, WorldSettlementFC dst) = SCDestructiveTestUtil.EnsureUnpartneredPair();
        WorldObjectComp_SupplyChain srcComp = SCDestructiveTestUtil.RequireSettlementComp(src);
        WorldObjectComp_SupplyChain dstComp = SCDestructiveTestUtil.RequireSettlementComp(dst);
        TestAssert.IsFalse(Contains(srcComp.OutPartners, dst), "precondition: src->dst are not yet trade partners");

        var route1 = new SupplyRoute(src, dst, r, 10.0);
        var route2 = new SupplyRoute(src, dst, r, 10.0);
        try
        {
            wc.LinkRoute(route1);
            TestAssert.IsTrue(Contains(srcComp.OutPartners, dst), "Linking a route registers dst as an out-partner of src");
            TestAssert.IsTrue(Contains(dstComp.InPartners, src), "...and src as an in-partner of dst");

            wc.LinkRoute(route2);   // second route on the same pair — still one partner link
            wc.UnlinkRoute(route1);
            TestAssert.IsTrue(Contains(srcComp.OutPartners, dst),
                "The partner link must survive while another route still connects the pair");

            wc.UnlinkRoute(route2); // last route removed — the link drops now
            TestAssert.IsFalse(Contains(srcComp.OutPartners, dst),
                "Removing the last route between the pair drops the out-partner link");
            TestAssert.IsFalse(Contains(dstComp.InPartners, src),
                "...and the in-partner link");
        }
        finally
        {
            // Idempotent cleanup — UnlinkRoute is a no-op if the route is already gone.
            wc.UnlinkRoute(route1);
            wc.UnlinkRoute(route2);
        }

        DestructiveTestUtil.AssertEmpireInvariants(f, "PartnerLink_LastRouteDrop");
    }

    [EmpireDestructiveTest("SC.Destructive.Network")]
    public static void ClearPartners_EmptySets_StillResetsStaleCounters()
    {
        // After a load the partner sets start empty while the saved partner/hub counters keep their
        // old values. Clearing the (already empty) sets must still zero the counters and their bonuses.
        FactionFC f = DestructiveTestUtil.RequireFaction();
        WorldSettlementFC s = SCDestructiveTestUtil.EnsureSCSettlement(x =>
        {
            WorldObjectComp_SupplyChain c = SupplyChainCache.GetSettlementComp(x);
            return c is not null && !c.OutPartners.Any() && !c.InPartners.Any();
        });
        WorldObjectComp_SupplyChain sc = SCDestructiveTestUtil.RequireSettlementComp(s);
        FCStatDef happiness = FCStatDefOf.happinessGainedBase;
        FCStatDef sellRate = SCStatDefOf.SC_SellRateMultiplier;

        sc.SetNetworkInfo(3, 2);
        TestAssert.AreEqual(happiness.IdentityValue + FormulaUtil.HappinessNetworkBonus(3), sc.GetStatModifier(happiness), 0.0001,
            "precondition: the stale counters grant a happiness bonus");
        TestAssert.GreaterThan(sc.GetStatModifier(sellRate), sellRate.IdentityValue,
            "precondition: the stale counters grant a sell-rate bonus");

        sc.ClearPartners();

        TestAssert.AreEqual(happiness.IdentityValue, sc.GetStatModifier(happiness), 0.0001,
            "ClearPartners on empty sets must reset the stale happiness bonus");
        TestAssert.AreEqual(sellRate.IdentityValue, sc.GetStatModifier(sellRate), 0.0001,
            "ClearPartners on empty sets must reset the stale sell-rate bonus");

        DestructiveTestUtil.AssertEmpireInvariants(f, "ClearPartners_StaleCounters");
    }

    private static bool Contains(IEnumerable<WorldSettlementFC> set, WorldSettlementFC s)
    {
        foreach (WorldSettlementFC x in set)
            if (x == s) return true;
        return false;
    }
}
