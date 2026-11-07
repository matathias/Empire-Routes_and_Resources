namespace FactionColonies.SupplyChain;

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
        WorldObjectComp_SupplyChain comp = GetComp(out ResourceTypeDef r);

        // Over-production allocations are accepted; the day's production deposits uncapped and any
        // true surplus is reconciled by the daily overflow sweep, so a billion-unit request must not
        // be rejected and must not drive income negative. SetAllocation returns false only when the
        // settlement does not track the resource, and GetComp picked one it tracks.
        bool accepted = comp.SetAllocation(r, 1e9);
        TestAssert.IsTrue(accepted, "SetAllocation must accept an over-production amount for tracked resource " + r.defName);

        DestructiveTestUtil.AssertEmpireInvariants(f, "SetAllocation_FarAboveProduction");
    }

    [EmpireDestructiveTest("SC.Destructive.Allocation")]
    public static void SetAllocation_Zero_AcceptedAndReadBack()
    {
        FactionFC f = DestructiveTestUtil.RequireFaction();
        WorldObjectComp_SupplyChain comp = GetComp(out ResourceTypeDef r);

        // An auto-max resource reads back its live max, not the manual value, so turn auto-max off
        // for the duration of the check.
        bool before = comp.IsAutoMax(r);
        try
        {
            comp.SetAutoMax(r, false);
            TestAssert.IsFalse(comp.IsAutoMax(r), "precondition: auto-max is off for " + r.defName);

            // The zero-path only returns false when the settlement does not track this resource.
            bool accepted = comp.SetAllocation(r, 0.0);
            TestAssert.IsTrue(accepted, "SetAllocation(0) must be accepted for tracked resource " + r.defName);
            TestAssert.AreEqual(0.0, comp.GetAllocation(r), 0.001, "GetAllocation should read back the manual zero");
        }
        finally
        {
            comp.SetAutoMax(r, before); // restore prior state
        }

        DestructiveTestUtil.AssertEmpireInvariants(f, "SetAllocation_Zero");
    }

    [EmpireDestructiveTest("SC.Destructive.Allocation")]
    public static void AutoMax_Toggle_RoundTrips()
    {
        FactionFC f = DestructiveTestUtil.RequireFaction();
        WorldObjectComp_SupplyChain comp = GetComp(out ResourceTypeDef r);

        bool before = comp.IsAutoMax(r);
        try
        {
            comp.SetAutoMax(r, true);
            // The sync only drops the flag again when the settlement does not track the resource.
            TestAssert.IsTrue(comp.IsAutoMax(r), "Auto-max should stick for tracked resource " + r.defName);
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
        WorldSettlementFC s = SCDestructiveTestUtil.EnsureSettlementTracking(def => !def.isPoolResource, out ResourceTypeDef r);
        WorldObjectComp_SupplyChain comp = SCDestructiveTestUtil.RequireSettlementComp(s);
        ResourceFC rfc = TestAssert.NotNull(s.GetResource(r), "Settlement does not track resource " + r.defName);

        bool before = comp.IsAutoMax(r);
        try
        {
            comp.SetAutoMax(r, true);
            TestAssert.IsTrue(comp.IsAutoMax(r), "Auto-max should stick for tracked resource " + r.defName);

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

    /// <summary>The SupplyChain comp of a settlement that tracks a non-pool resource, plus that resource.</summary>
    private static WorldObjectComp_SupplyChain GetComp(out ResourceTypeDef r)
    {
        WorldSettlementFC s = SCDestructiveTestUtil.EnsureSettlementTracking(def => !def.isPoolResource, out r);
        return SCDestructiveTestUtil.RequireSettlementComp(s);
    }
}
