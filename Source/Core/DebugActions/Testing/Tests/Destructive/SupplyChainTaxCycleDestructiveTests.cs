namespace FactionColonies.SupplyChain;

/* DESTRUCTIVE: runs the real SupplyChain tax-resolution pass against live settlements. It moves
   resources between stockpiles, resolves needs, and overwrites need-states. Not reverted. */
public static class SupplyChainTaxCycleDestructiveTests
{
    [EmpireDestructiveTest("SC.Destructive.Tax")]
    public static void FullTaxCycle_DoesNotThrow_AndStockpilesNonNegative()
    {
        FactionFC f = DestructiveTestUtil.RequireFaction();
        WorldComponent_SupplyChain comp = SCDestructiveTestUtil.RequireWorldComp();
        SCDestructiveTestUtil.EnsureSCSettlement();

        TestAssert.DoesNotThrow(() => comp.PreTaxResolution(f), "PreTaxResolution threw");
        TestAssert.DoesNotThrow(() => ((ITaxTickParticipant)comp).PostTaxResolution(f), "PostTaxResolution threw");

        SCDestructiveTestUtil.AssertStockpilesNonNegative(f, comp, "FullTaxCycle");
        DestructiveTestUtil.AssertEmpireInvariants(f, "FullTaxCycle");
    }
}
