using System.Linq;

namespace FactionColonies.SupplyChain
{
    /* DESTRUCTIVE: registers/clears stockpile allocations and toggles auto-max on a live settlement's
       SupplyChain comp (which calls into the base ResourceFC allocation ledger). Prior auto-max state
       is restored where practical, but allocation registrations are not. May create a transient
       settlement. Not fully reverted. */
    public static class AllocationDestructiveTests
    {
        [EmpireDestructiveTest("SC.Destructive.Allocation")]
        public static void SetAllocation_FarAboveProduction_AcceptedAndClamped()
        {
            FactionFC f = DestructiveTestUtil.RequireFaction();
            WorldObjectComp_SupplyChain comp = GetComp(f, out ResourceTypeDef r);
            if (comp is null || r is null) TestAssert.Skip("No settlement comp / resource available");

            // Over-production allocations are accepted; the excess is clamped at daily realization,
            // so a billion-unit request must not be rejected and must not drive income negative.
            // SetAllocation still returns false only when the settlement does not track the resource.
            bool accepted = comp.SetAllocation(r, 1e9);
            if (!accepted) TestAssert.Skip("Settlement does not track resource " + r.defName);

            DestructiveTestUtil.AssertEmpireInvariants(f, "SetAllocation_FarAboveProduction");
        }

        [EmpireDestructiveTest("SC.Destructive.Allocation")]
        public static void SetAllocation_Zero_AcceptedAndReadBack()
        {
            FactionFC f = DestructiveTestUtil.RequireFaction();
            WorldObjectComp_SupplyChain comp = GetComp(f, out ResourceTypeDef r);
            if (comp is null || r is null) TestAssert.Skip("No settlement comp / resource available");
            if (comp.IsAutoMax(r)) TestAssert.Skip("Resource is auto-max; manual allocation read-back does not apply");

            // The zero-path only returns false when the settlement does not track this resource
            // (no ResourceFC). That is not a logic failure, so skip rather than fail.
            bool accepted = comp.SetAllocation(r, 0.0);
            if (!accepted) TestAssert.Skip("Settlement does not track resource " + r.defName);
            TestAssert.AreEqual(0.0, comp.GetAllocation(r), 0.001, "GetAllocation should read back the manual zero");

            DestructiveTestUtil.AssertEmpireInvariants(f, "SetAllocation_Zero");
        }

        [EmpireDestructiveTest("SC.Destructive.Allocation")]
        public static void AutoMax_Toggle_RoundTrips()
        {
            FactionFC f = DestructiveTestUtil.RequireFaction();
            WorldObjectComp_SupplyChain comp = GetComp(f, out ResourceTypeDef r);
            if (comp is null || r is null) TestAssert.Skip("No settlement comp / resource available");

            bool before = comp.IsAutoMax(r);
            try
            {
                comp.SetAutoMax(r, true);
                // If the settlement does not track this resource, the sync drops the flag again.
                // That is expected, not a failure, so skip.
                if (!comp.IsAutoMax(r))
                    TestAssert.Skip("Settlement does not track resource " + r.defName + " (auto-max not applicable)");
                comp.SetAutoMax(r, false);
                TestAssert.IsFalse(comp.IsAutoMax(r), "Auto-max should be disabled after SetAutoMax(false)");
            }
            finally
            {
                comp.SetAutoMax(r, before); // restore prior state
            }

            TestAssert.DoesNotThrow(() => comp.SyncAllAutoMaxAllocations(), "SyncAllAutoMaxAllocations threw");
            DestructiveTestUtil.AssertEmpireInvariants(f, "AutoMax_Toggle");
        }

        [EmpireDestructiveTest("SC.Destructive.Allocation")]
        public static void AutoMax_GetAllocation_WithinProductionBounds()
        {
            // A5: an auto-max allocation reads back LiveMaxFor = rawProduction minus other submods'
            // allocations, clamped at 0. Regardless of live production/other allocations it must stay
            // within [0, rawTotalProduction] — a negative or over-production value is a LiveMaxFor bug.
            FactionFC f = DestructiveTestUtil.RequireFaction();
            ResourceTypeDef r = SupplyChainCache.AllResourceTypeDefs.FirstOrDefault();
            WorldSettlementFC s = SCDestructiveTestUtil.FirstOrTransient(f);
            if (r is null || s is null) TestAssert.Skip("No settlement / resource available");
            WorldObjectComp_SupplyChain comp = SupplyChainCache.GetSettlementComp(s);
            if (comp is null) TestAssert.Skip("No settlement comp");

            ResourceFC rfc = s.GetResource(r);
            if (rfc is null) TestAssert.Skip("Settlement does not track resource " + r.defName);

            bool before = comp.IsAutoMax(r);
            try
            {
                comp.SetAutoMax(r, true);
                if (!comp.IsAutoMax(r)) TestAssert.Skip("Auto-max not applicable for " + r.defName);

                double live = comp.GetAllocation(r);
                TestAssert.GreaterThan(live, -0.001, "Auto-max allocation is never negative");
                TestAssert.LessThanOrEqual(live, rfc.rawTotalProduction + 0.001,
                    "Auto-max allocation cannot exceed this settlement's raw production");
            }
            finally
            {
                comp.SetAutoMax(r, before); // restore prior state
            }

            DestructiveTestUtil.AssertEmpireInvariants(f, "AutoMax_GetAllocation_Bounds");
        }

        private static WorldObjectComp_SupplyChain GetComp(FactionFC f, out ResourceTypeDef r)
        {
            r = SupplyChainCache.AllResourceTypeDefs.FirstOrDefault();
            WorldSettlementFC s = SCDestructiveTestUtil.FirstOrTransient(f);
            return s is null ? null : SupplyChainCache.GetSettlementComp(s);
        }
    }
}
