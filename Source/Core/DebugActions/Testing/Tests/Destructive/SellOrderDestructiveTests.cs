namespace FactionColonies.SupplyChain;

/* DESTRUCTIVE: exercises the settlement-context SellOrder overload, which reads the live
   SC_SellRateMultiplier stat off a real settlement (the pure SellOrderTests only cover the
   no-context overload). Uses throwaway fixture stockpiles; the only live read is the faction
   stat aggregation. May create a transient settlement; sell-rate settings are pinned positive and
   restored. Not otherwise reverted. */
public static class SellOrderDestructiveTests
{
    [EmpireDestructiveTest("SC.Destructive.SellOrder")]
    public static void Execute_WithSettlement_AppliesSellRateMultiplier()
    {
        FactionFC f = DestructiveTestUtil.RequireFaction();
        WorldSettlementFC s = SCDestructiveTestUtil.EnsureSCSettlement();
        ResourceTypeDef r = SCDestructiveTestUtil.RequireNonPoolResource();

        // Sell silver is drawn * silverPerResource * overflowPenaltyRate; pin both rates positive so
        // the multiplier is observable, restoring the player's values afterwards.
        SCTestHelper.SettingsSnapshot snapshot = SCTestHelper.SnapshotSettings();
        int origSilverPerResource = FCSettings.silverPerResource;
        try
        {
            if (SupplyChainSettings.overflowPenaltyRate <= 0f) SupplyChainSettings.overflowPenaltyRate = 0.5f;
            if (FCSettings.silverPerResource <= 0) FCSettings.silverPerResource = 1;

            // Two identical fixture stockpiles so both orders draw exactly the same amount.
            DictionaryStockpile spNoCtx = SCTestHelper.MakeStockpile(r, 100.0, 1000.0);
            DictionaryStockpile spCtx = SCTestHelper.MakeStockpile(r, 100.0, 1000.0);

            float baseSilver = new SellOrder(r, 40.0).Execute(spNoCtx);   // no context: penalty rate only
            float ctxSilver = new SellOrder(r, 40.0).Execute(spCtx, s);   // context: * SC_SellRateMultiplier

            TestAssert.GreaterThan(baseSilver, 0f, "A sell order with positive rates must produce silver");

            // The settlement overload multiplies the base silver by the settlement's sell-rate stat;
            // values <= 0 are ignored, leaving the base unchanged.
            FCStatDef sellStat = SCStatDefOf.SC_SellRateMultiplier;
            double mult = sellStat is not null ? FindFC.FactionComp.GetStatValue(sellStat, s) : 1.0;
            if (mult <= 0) mult = 1.0;

            TestAssert.AreEqual(baseSilver * mult, ctxSilver, baseSilver * 0.01 + 0.01,
                "The settlement overload must scale sell silver by SC_SellRateMultiplier");
        }
        finally
        {
            SCTestHelper.RestoreSettings(snapshot);
            FCSettings.silverPerResource = origSilverPerResource;
        }

        DestructiveTestUtil.AssertEmpireInvariants(f, "SellOrder_WithSettlement");
    }
}
