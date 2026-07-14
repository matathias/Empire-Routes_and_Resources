namespace FactionColonies.SupplyChain
{
    /* DESTRUCTIVE + INVASIVE: toggles the faction-wide supply-chain mode, which redistributes every
       settlement's resources and re-staggers routes. The original mode is restored in a finally block
       (itself a second switch). Run only when a live mode round-trip on the current save is acceptable.
       Not otherwise reverted. */
    public static class ModeSwitchDestructiveTests
    {
        [EmpireDestructiveTest("SC.Destructive.Mode")]
        public static void SwitchSimpleToComplex_DistributesAndClearsFactionStockpile()
        {
            FactionFC f = DestructiveTestUtil.RequireFaction();
            WorldComponent_SupplyChain wc = SupplyChainCache.Comp;
            if (wc is null) TestAssert.Skip("No SupplyChain world component");
            if (f.settlements.Count == 0) TestAssert.Skip("Mode switch needs at least one settlement");

            SupplyChainMode original = wc.Mode;
            try
            {
                // Start from Simple so we can seed the shared faction stockpile.
                if (wc.Mode != SupplyChainMode.Simple) wc.SwitchMode(SupplyChainMode.Simple);

                // Over-cap pool resources are capped silently (not distributed), so pick a normal one.
                ResourceTypeDef r = null;
                foreach (ResourceTypeDef def in SupplyChainCache.AllResourceTypeDefs)
                {
                    if (def.isPoolResource) continue;
                    r = def; break;
                }
                if (r is null) TestAssert.Skip("No non-pool resource to seed");

                // Seed a small amount into the faction stockpile (small enough to sit under local caps,
                // so the switch distributes it rather than auto-selling the over-cap remainder).
                double seeded = wc.Stockpile.GetAmount(r);
                double refused = wc.Stockpile.Credit(r, 10.0); // clamps to cap; returns any excess
                double factionTotalBefore = seeded + (10.0 - refused);

                wc.SwitchMode(SupplyChainMode.Complex);

                TestAssert.AreEqual(SupplyChainMode.Complex, wc.Mode, "Mode should be Complex after the switch");
                TestAssert.AreEqual(0.0, wc.Stockpile.GetAmount(r), 0.01,
                    "The faction stockpile is cleared when switching to Complex");

                // The resource now lives in the per-settlement locals; distribution never mints value,
                // so the local sum cannot exceed the faction total that preceded the switch.
                double localSum = 0.0;
                foreach (WorldSettlementFC s in f.settlements)
                {
                    IStockpile sp = SupplyChainCache.GetSettlementComp(s)?.GetStockpile();
                    if (sp != null) localSum += sp.GetAmount(r);
                }
                TestAssert.LessThanOrEqual(localSum, factionTotalBefore + 0.01,
                    "Distributed local total cannot exceed the faction total before the switch");
                TestAssert.GreaterThan(localSum, -0.001, "Distributed local total is non-negative");
            }
            finally
            {
                if (wc.Mode != original) wc.SwitchMode(original); // restore the player's mode
            }

            DestructiveTestUtil.AssertEmpireInvariants(f, "SwitchSimpleToComplex");
        }
    }
}
