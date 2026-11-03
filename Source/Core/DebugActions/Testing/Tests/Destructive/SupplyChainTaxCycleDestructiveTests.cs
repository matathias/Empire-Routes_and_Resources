namespace FactionColonies.SupplyChain
{
    /* DESTRUCTIVE: runs the real SupplyChain tax-resolution pass against live settlements. It moves
       resources between stockpiles, resolves needs, and overwrites need-states. Not reverted. */
    public static class SupplyChainTaxCycleDestructiveTests
    {
        [EmpireDestructiveTest("SC.Destructive.Tax")]
        public static void FullTaxCycle_DoesNotThrow_AndStockpilesNonNegative()
        {
            FactionFC f = DestructiveTestUtil.RequireFaction();
            WorldComponent_SupplyChain comp = SupplyChainCache.Comp;
            if (comp is null) TestAssert.Skip("No SupplyChain world component");

            if (f.settlements.Count == 0)
            {
                WorldSettlementFC created = DestructiveTestUtil.CreateTransientSettlement();
                if (created is null) TestAssert.Skip("No settlements and no valid tile to create one");
            }

            TestAssert.DoesNotThrow(() => comp.PreTaxResolution(f), "PreTaxResolution threw");
            TestAssert.DoesNotThrow(() => comp.PostTaxResolution(f), "PostTaxResolution threw");

            SCDestructiveTestUtil.AssertStockpilesNonNegative(f, comp, "FullTaxCycle");
            DestructiveTestUtil.AssertEmpireInvariants(f, "FullTaxCycle");
        }
    }
}
