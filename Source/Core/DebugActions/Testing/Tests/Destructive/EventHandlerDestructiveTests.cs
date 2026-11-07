namespace FactionColonies.SupplyChain;

/* DESTRUCTIVE: fires SCEventHandler_Stockpile against a live settlement's real stockpile (the
   faction pile in Simple mode, the local pile in Complex). It credits/draws resources and, in the
   convert-to-silver case, pays out one-time silver. May create a transient settlement. Not reverted. */
public static class EventHandlerDestructiveTests
{
    [EmpireDestructiveTest("SC.Destructive.Event")]
    public static void FlatCredit_IncreasesStockpile()
    {
        FactionFC f = DestructiveTestUtil.RequireFaction();
        WorldComponent_SupplyChain wc = SCDestructiveTestUtil.RequireWorldComp();
        WorldSettlementFC s = SCDestructiveTestUtil.EnsureSCSettlement();
        ResourceTypeDef r = SCDestructiveTestUtil.RequireNonPoolResource();

        // A credit into a full pile is unobservable, so make room first.
        IStockpile sp = SCDestructiveTestUtil.LiveStockpileFor(wc, s);
        SCDestructiveTestUtil.EnsureHeadroom(sp, r);

        double before = sp.GetAmount(r);

        // A plain flat +baseAmount event stores the resource.
        SCEventHandler_Stockpile handler = new()
        {
            resource = r, mult = 1f, baseAmount = 5f, perWorkerAmount = 0f, convertToSilver = false
        };
        handler.OnEventTriggered(MakeEvent(s));

        TestAssert.GreaterThan(sp.GetAmount(r), before, "A flat +baseAmount credit event must raise the stockpile");

        DestructiveTestUtil.AssertEmpireInvariants(f, "EventHandler_FlatCredit");
    }

    [EmpireDestructiveTest("SC.Destructive.Event")]
    public static void FlatGain_ConvertToSilver_DoesNotChangeStockpile()
    {
        FactionFC f = DestructiveTestUtil.RequireFaction();
        WorldComponent_SupplyChain wc = SCDestructiveTestUtil.RequireWorldComp();
        WorldSettlementFC s = SCDestructiveTestUtil.EnsureSCSettlement();
        ResourceTypeDef r = SCDestructiveTestUtil.RequireNonPoolResource();

        IStockpile sp = SCDestructiveTestUtil.LiveStockpileFor(wc, s);

        double before = sp.GetAmount(r);

        // convertToSilver: a flat gain is paid out as silver instead of stored — the pile is untouched.
        SCEventHandler_Stockpile handler = new()
        {
            resource = r, mult = 1f, baseAmount = 5f, perWorkerAmount = 0f, convertToSilver = true
        };
        handler.OnEventTriggered(MakeEvent(s));

        TestAssert.AreEqual(before, sp.GetAmount(r), 0.001,
            "A convertToSilver flat gain is paid as silver and must not change the stockpile");

        DestructiveTestUtil.AssertEmpireInvariants(f, "EventHandler_FlatGainSilver");
    }

    private static FCEvent MakeEvent(WorldSettlementFC s)
    {
        FCEvent evt = new();
        evt.settlementTraitLocations = [s];
        return evt;
    }
}
