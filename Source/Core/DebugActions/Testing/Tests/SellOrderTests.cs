namespace FactionColonies.SupplyChain
{
    /// <summary>
    /// Pure coverage for the no-settlement-context overload <see cref="SellOrder.Execute(IStockpile)"/>.
    /// The settlement-context overload reads <c>FindFC.FactionComp</c> and a DefDatabase stat, so it is
    /// exercised in the destructive tier instead.
    /// </summary>
    public static class SellOrderTests
    {
        private static ResourceTypeDef Res() => SCTestHelper.MakeResourceType("SCTest_Sell");

        [EmpireTest("SC.SellOrder")]
        public static void Execute_NullResource_ReturnsZero()
        {
            DictionaryStockpile sp = SCTestHelper.MakeStockpile(Res(), 100.0, 100.0);
            var order = new SellOrder(null, 10.0);
            TestAssert.AreEqual(0.0, order.Execute(sp));
        }

        [EmpireTest("SC.SellOrder")]
        public static void Execute_ZeroAmount_ReturnsZero()
        {
            ResourceTypeDef r = Res();
            DictionaryStockpile sp = SCTestHelper.MakeStockpile(r, 100.0, 100.0);
            var order = new SellOrder(r, 0.0);
            TestAssert.AreEqual(0.0, order.Execute(sp));
        }

        [EmpireTest("SC.SellOrder")]
        public static void Execute_DrawsAmountAndConvertsAtPenaltyRate()
        {
            var snap = SCTestHelper.SnapshotSettings();
            try
            {
                SupplyChainSettings.overflowPenaltyRate = 0.5f;
                ResourceTypeDef r = Res();
                DictionaryStockpile sp = SCTestHelper.MakeStockpile(r, 100.0, 100.0);
                var order = new SellOrder(r, 40.0);

                double silver = order.Execute(sp);

                double expected = 40.0 * FCSettings.silverPerResource * 0.5;
                TestAssert.AreEqual(expected, silver);
                TestAssert.AreEqual(60.0, sp.GetAmount(r), 0.001, "Sold amount should be drawn from the stockpile");
            }
            finally
            {
                SCTestHelper.RestoreSettings(snap);
            }
        }

        [EmpireTest("SC.SellOrder")]
        public static void Execute_ClampsToAvailable()
        {
            var snap = SCTestHelper.SnapshotSettings();
            try
            {
                SupplyChainSettings.overflowPenaltyRate = 0.5f;
                ResourceTypeDef r = Res();
                DictionaryStockpile sp = SCTestHelper.MakeStockpile(r, 10.0, 100.0);
                var order = new SellOrder(r, 40.0); // only 10 available

                double silver = order.Execute(sp);

                double expected = 10.0 * FCSettings.silverPerResource * 0.5;
                TestAssert.AreEqual(expected, silver);
                TestAssert.AreEqual(0.0, sp.GetAmount(r));
            }
            finally
            {
                SCTestHelper.RestoreSettings(snap);
            }
        }
    }
}
