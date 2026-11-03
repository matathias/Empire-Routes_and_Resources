using System.Collections.Generic;
using System.Linq;

namespace FactionColonies.SupplyChain
{
    /* DESTRUCTIVE: links/unlinks real routes on live settlement comps to verify trade-network partner
       tracking. The two throwaway routes created here are removed again in a finally block, restoring
       the partner sets. Not otherwise reverted. */
    public static class NetworkDestructiveTests
    {
        [EmpireDestructiveTest("SC.Destructive.Network")]
        public static void PartnerLink_DroppedOnlyWhenLastRouteRemoved()
        {
            FactionFC f = DestructiveTestUtil.RequireFaction();
            WorldComponent_SupplyChain wc = SupplyChainCache.Comp;
            if (wc is null) TestAssert.Skip("No SupplyChain world component");
            ResourceTypeDef r = SupplyChainCache.AllResourceTypeDefs.FirstOrDefault();
            if (r is null) TestAssert.Skip("No resource types defined");

            WorldSettlementFC src = SCDestructiveTestUtil.SettlementAt(f, 0);
            WorldSettlementFC dst = SCDestructiveTestUtil.SettlementAt(f, 1);
            if (src is null || dst is null || src == dst) TestAssert.Skip("Need two distinct settlements");

            WorldObjectComp_SupplyChain srcComp = SupplyChainCache.GetSettlementComp(src);
            WorldObjectComp_SupplyChain dstComp = SupplyChainCache.GetSettlementComp(dst);
            if (srcComp is null || dstComp is null) TestAssert.Skip("Missing settlement comps");

            // The pair must not already be partnered, or we can't attribute the membership change.
            if (Contains(srcComp.OutPartners, dst)) TestAssert.Skip("src->dst are already trade partners in this save");

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

        private static bool Contains(IEnumerable<WorldSettlementFC> set, WorldSettlementFC s)
        {
            foreach (WorldSettlementFC x in set)
                if (x == s) return true;
            return false;
        }
    }
}
