namespace FactionColonies.SupplyChain
{
    /// <summary>
    /// Pure-math coverage for <see cref="FormulaUtil"/>. Settings-independent formulas use literal
    /// asserts (including clamp boundaries); formulas that read <see cref="SupplyChainSettings"/>
    /// pin a known value via <see cref="SCTestHelper.SnapshotSettings"/> and restore it in a finally
    /// block so the non-destructive contract holds.
    /// </summary>
    public static class FormulaUtilTests
    {
        /*-*-*- TaxEfficiency: 1 + 0.20 * averageSatisfaction (settings-independent) -*-*-*/

        [EmpireTest("SC.Formula")]
        public static void TaxEfficiency_ZeroSatisfaction_IsOne()
        {
            TestAssert.AreEqual(1.0, FormulaUtil.TaxEfficiency(0.0));
        }

        [EmpireTest("SC.Formula")]
        public static void TaxEfficiency_HalfSatisfaction_IsOnePointOne()
        {
            TestAssert.AreEqual(1.1, FormulaUtil.TaxEfficiency(0.5));
        }

        [EmpireTest("SC.Formula")]
        public static void TaxEfficiency_FullSatisfaction_IsOnePointTwo()
        {
            TestAssert.AreEqual(1.2, FormulaUtil.TaxEfficiency(1.0));
        }

        /*-*-*- SellRateMultiplier: 1 + 0.10*min(partners,5) + 0.10*min(hub,3) -*-*-*/

        [EmpireTest("SC.Formula")]
        public static void SellRateMultiplier_NoNetwork_IsOne()
        {
            TestAssert.AreEqual(1.0, FormulaUtil.SellRateMultiplier(0, 0));
        }

        [EmpireTest("SC.Formula")]
        public static void SellRateMultiplier_MidNetwork_AddsBoth()
        {
            // 1 + 0.10*3 + 0.10*2 = 1.5
            TestAssert.AreEqual(1.5, FormulaUtil.SellRateMultiplier(3, 2));
        }

        [EmpireTest("SC.Formula")]
        public static void SellRateMultiplier_ClampsPartnersAt5AndHubAt3()
        {
            // 1 + 0.10*5 + 0.10*3 = 1.8, regardless of how far over the caps we go
            TestAssert.AreEqual(1.8, FormulaUtil.SellRateMultiplier(50, 50));
            TestAssert.AreEqual(1.8, FormulaUtil.SellRateMultiplier(5, 3));
        }

        /*-*-*- HappinessNetworkBonus: 0.5 * min(partners,5) -*-*-*/

        [EmpireTest("SC.Formula")]
        public static void HappinessNetworkBonus_ScalesAndClamps()
        {
            TestAssert.AreEqual(0.0, FormulaUtil.HappinessNetworkBonus(0));
            TestAssert.AreEqual(1.5, FormulaUtil.HappinessNetworkBonus(3));
            TestAssert.AreEqual(2.5, FormulaUtil.HappinessNetworkBonus(50)); // clamp at 5
        }

        /*-*-*- ProsperityNetworkBonus: 1.0 * min(hub,3) -*-*-*/

        [EmpireTest("SC.Formula")]
        public static void ProsperityNetworkBonus_ScalesAndClamps()
        {
            TestAssert.AreEqual(0.0, FormulaUtil.ProsperityNetworkBonus(0));
            TestAssert.AreEqual(2.0, FormulaUtil.ProsperityNetworkBonus(2));
            TestAssert.AreEqual(3.0, FormulaUtil.ProsperityNetworkBonus(50)); // clamp at 3
        }

        /*-*-*- RouteEfficiency: 1 / (1 + travelDays * routeDecayPerDay) (settings-dependent) -*-*-*/

        [EmpireTest("SC.Formula")]
        public static void RouteEfficiency_HonorsDecayRate()
        {
            var snap = SCTestHelper.SnapshotSettings();
            try
            {
                SupplyChainSettings.routeDecayPerDay = 0.1f;
                TestAssert.AreEqual(1.0, FormulaUtil.RouteEfficiency(0.0));      // no travel -> perfect
                TestAssert.AreEqual(0.5, FormulaUtil.RouteEfficiency(10.0));     // 1/(1+1.0)
                TestAssert.AreEqual(1.0 / 1.5, FormulaUtil.RouteEfficiency(5.0)); // 1/(1+0.5)
            }
            finally
            {
                SCTestHelper.RestoreSettings(snap);
            }
        }

        [EmpireTest("SC.Formula")]
        public static void RouteEfficiency_DecreasesWithDistance()
        {
            var snap = SCTestHelper.SnapshotSettings();
            try
            {
                SupplyChainSettings.routeDecayPerDay = 0.1f;
                double near = FormulaUtil.RouteEfficiency(2.0);
                double far = FormulaUtil.RouteEfficiency(8.0);
                TestAssert.LessThan(far, near, "A longer route must have strictly lower efficiency");
                TestAssert.LessThanOrEqual(near, 1.0, "Efficiency never exceeds 1.0");
                TestAssert.GreaterThan(far, 0.0, "Efficiency stays positive for finite travel");
            }
            finally
            {
                SCTestHelper.RestoreSettings(snap);
            }
        }

        /*-*-*- OverflowSilver: amount * silverPerResource * overflowPenaltyRate -*-*-*/

        [EmpireTest("SC.Formula")]
        public static void OverflowSilver_HonorsPenaltyRate()
        {
            var snap = SCTestHelper.SnapshotSettings();
            try
            {
                SupplyChainSettings.overflowPenaltyRate = 0.5f;
                double expected = 100.0 * FCSettings.silverPerResource * 0.5;
                TestAssert.AreEqual(expected, FormulaUtil.OverflowSilver(100.0));
            }
            finally
            {
                SCTestHelper.RestoreSettings(snap);
            }
        }

        /*-*-*- ResourceCost: amount * mult * resourceCostMultiplier -*-*-*/

        [EmpireTest("SC.Formula")]
        public static void ResourceCost_HonorsMultiplier()
        {
            var snap = SCTestHelper.SnapshotSettings();
            try
            {
                SupplyChainSettings.resourceCostMultiplier = 2.0f;
                // 10 * 3 * 2 = 60
                TestAssert.AreEqual(60.0, FormulaUtil.ResourceCost(10.0, 3.0));
            }
            finally
            {
                SCTestHelper.RestoreSettings(snap);
            }
        }
    }
}
