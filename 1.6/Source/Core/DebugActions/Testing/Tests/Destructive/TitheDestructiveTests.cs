namespace FactionColonies.SupplyChain
{
    /* DESTRUCTIVE: sets a tithe injection on a live settlement comp and resolves it against a throwaway
       stockpile, then reads back the external tithe budget. The injection config is restored in a
       finally block. Not otherwise reverted. */
    public static class TitheDestructiveTests
    {
        [EmpireDestructiveTest("SC.Destructive.Tithe")]
        public static void ResolveTitheInjection_ShortStockpile_DrawsPartialAndReportsSilver()
        {
            FactionFC f = DestructiveTestUtil.RequireFaction();
            WorldSettlementFC s = SCDestructiveTestUtil.FirstOrTransient(f);
            if (s is null) TestAssert.Skip("No settlement available");
            WorldObjectComp_SupplyChain comp = SupplyChainCache.GetSettlementComp(s);
            if (comp is null) TestAssert.Skip("No settlement comp");

            // Need a tithe-able resource the settlement actually tracks (GetResource -> ResourceFC).
            ResourceTypeDef target = null;
            foreach (ResourceTypeDef def in SupplyChainCache.AllResourceTypeDefs)
            {
                if (def.CanTithe && s.GetResource(def) != null) { target = def; break; }
            }
            if (target is null) TestAssert.Skip("No tithe-able resource tracked by the settlement");

            double origInjection = comp.GetTitheInjection(target);
            try
            {
                comp.SetTitheInjection(target, 10.0); // want 10/day

                // Stockpile is short: only 4 available. The daily draw must clamp to 4.
                DictionaryStockpile sp = SCTestHelper.MakeStockpile(target, 4.0, 1000.0);
                comp.ResolveTitheInjections(sp);

                TestAssert.AreEqual(0.0, sp.GetAmount(target), 0.001, "A short tithe draw empties what was available");

                // Budget is derived from the actual amount drawn = drawn * silverPerResource.
                ResourceFC rfc = s.GetResource(target);
                double budget = comp.GetDailyExternalTitheBudget(rfc);
                TestAssert.AreEqual(4.0 * FCSettings.silverPerResource, budget, 0.01,
                    "External tithe budget reflects the actual (short) amount drawn, not the configured amount");
            }
            finally
            {
                comp.SetTitheInjection(target, origInjection); // restore prior config (0 clears the injection)
            }

            DestructiveTestUtil.AssertEmpireInvariants(f, "TitheInjection_Short");
        }
    }
}
