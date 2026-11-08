namespace FactionColonies.SupplyChain;

/* DESTRUCTIVE: switches the faction-wide supply-chain mode (which redistributes every settlement's
   stockpile) and switches it back in a finally block, and empties one settlement's local pile. May
   create transient settlements. Stockpile contents are not restored: a round trip through the other
   mode re-splits them. */
public static class StockpileForDestructiveTests
{
    [EmpireDestructiveTest("SC.Destructive.Stockpile")]
    public static void StockpileFor_NullSettlement_IsNull()
    {
        SCDestructiveTestUtil.RequireWorldComp();
        TestAssert.IsNull(WorldComponent_SupplyChain.StockpileFor(null));
    }

    [EmpireDestructiveTest("SC.Destructive.Stockpile")]
    public static void StockpileFor_SimpleMode_IsTheSharedFactionPile()
    {
        FactionFC f = DestructiveTestUtil.RequireFaction();
        WorldComponent_SupplyChain wc = SCDestructiveTestUtil.RequireWorldComp();
        (WorldSettlementFC a, WorldSettlementFC b) = SCDestructiveTestUtil.EnsureTwoSettlements();
        SupplyChainMode original = SCDestructiveTestUtil.ForceMode(wc, SupplyChainMode.Simple);
        try
        {
            IStockpile pa = WorldComponent_SupplyChain.StockpileFor(a);
            IStockpile pb = WorldComponent_SupplyChain.StockpileFor(b);
            TestAssert.NotNull(pa, "Simple mode must give a stockpile");
            TestAssert.IsTrue(ReferenceEquals(pa, wc.Stockpile), "Simple mode uses the faction-wide pile");
            TestAssert.IsTrue(ReferenceEquals(pa, pb), "Every settlement shares the one pile in Simple mode");
        }
        finally
        {
            SCDestructiveTestUtil.RestoreMode(wc, original);
        }

        DestructiveTestUtil.AssertEmpireInvariants(f, "StockpileFor_Simple");
    }

    [EmpireDestructiveTest("SC.Destructive.Stockpile")]
    public static void StockpileFor_ComplexMode_IsEachSettlementsOwnPile()
    {
        FactionFC f = DestructiveTestUtil.RequireFaction();
        WorldComponent_SupplyChain wc = SCDestructiveTestUtil.RequireWorldComp();
        (WorldSettlementFC a, WorldSettlementFC b) = SCDestructiveTestUtil.EnsureTwoSettlements();
        SupplyChainMode original = SCDestructiveTestUtil.ForceMode(wc, SupplyChainMode.Complex);
        try
        {
            IStockpile pa = TestAssert.NotNull(WorldComponent_SupplyChain.StockpileFor(a), "Complex mode must give a local pile");
            IStockpile pb = TestAssert.NotNull(WorldComponent_SupplyChain.StockpileFor(b), "Complex mode must give a local pile");
            TestAssert.IsTrue(ReferenceEquals(pa, SCDestructiveTestUtil.RequireSettlementComp(a).GetStockpile()),
                "Complex mode uses the settlement's own pile");
            TestAssert.IsFalse(ReferenceEquals(pa, pb), "Two settlements have separate piles in Complex mode");
            TestAssert.IsFalse(ReferenceEquals(pa, wc.Stockpile), "Complex mode never hands out the faction pile");
        }
        finally
        {
            SCDestructiveTestUtil.RestoreMode(wc, original);
        }

        DestructiveTestUtil.AssertEmpireInvariants(f, "StockpileFor_Complex");
    }

    [EmpireDestructiveTest("SC.Destructive.Stockpile")]
    public static void StockpileFor_ComplexMode_CreatesAMissingLocalPile()
    {
        // A settlement whose local pile wrapper does not exist yet still gets one, rather than null.
        // Dropping the wrapper here empties that settlement's local pile.
        FactionFC f = DestructiveTestUtil.RequireFaction();
        WorldComponent_SupplyChain wc = SCDestructiveTestUtil.RequireWorldComp();
        WorldSettlementFC s = SCDestructiveTestUtil.EnsureSCSettlement();
        WorldObjectComp_SupplyChain sc = SCDestructiveTestUtil.RequireSettlementComp(s);
        SupplyChainMode original = SCDestructiveTestUtil.ForceMode(wc, SupplyChainMode.Complex);
        try
        {
            sc.ClearLocalData();
            TestAssert.IsNull(sc.GetStockpile(), "precondition: the local pile wrapper is gone");

            IStockpile sp = WorldComponent_SupplyChain.StockpileFor(s);
            TestAssert.NotNull(sp, "StockpileFor must build the local pile on demand");
            TestAssert.IsTrue(ReferenceEquals(sp, sc.GetStockpile()), "...and keep it on the comp");
        }
        finally
        {
            sc.DirtyLocalCaps();
            SCDestructiveTestUtil.RestoreMode(wc, original);
        }

        DestructiveTestUtil.AssertEmpireInvariants(f, "StockpileFor_ComplexOnDemand");
    }
}
