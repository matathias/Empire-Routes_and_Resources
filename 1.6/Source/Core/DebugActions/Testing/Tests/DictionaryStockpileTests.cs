namespace FactionColonies.SupplyChain
{
    /// <summary>
    /// Pure coverage for <see cref="DictionaryStockpile"/> draw/credit semantics and boundary
    /// conditions. No game state required — uses throwaway <see cref="ResourceTypeDef"/> keys.
    /// </summary>
    public static class DictionaryStockpileTests
    {
        private static ResourceTypeDef Res() => SCTestHelper.MakeResourceType("SCTest_Stock");

        /*-*-*- TryDraw -*-*-*/

        [EmpireTest("SC.Stockpile")]
        public static void TryDraw_PartialAmount_DrawsRequestedAndReduces()
        {
            ResourceTypeDef r = Res();
            DictionaryStockpile sp = SCTestHelper.MakeStockpile(r, 10.0, 100.0);

            bool ok = sp.TryDraw(r, 4.0, out double drawn);

            TestAssert.IsTrue(ok, "TryDraw within available should return true");
            TestAssert.AreEqual(4.0, drawn);
            TestAssert.AreEqual(6.0, sp.GetAmount(r));
        }

        [EmpireTest("SC.Stockpile")]
        public static void TryDraw_MoreThanAvailable_DrawsAllAndEmpties()
        {
            ResourceTypeDef r = Res();
            DictionaryStockpile sp = SCTestHelper.MakeStockpile(r, 10.0, 100.0);

            bool ok = sp.TryDraw(r, 20.0, out double drawn);

            TestAssert.IsTrue(ok, "TryDraw should succeed and clamp to what is available");
            TestAssert.AreEqual(10.0, drawn);
            TestAssert.AreEqual(0.0, sp.GetAmount(r));
        }

        [EmpireTest("SC.Stockpile")]
        public static void TryDraw_EmptyStockpile_ReturnsFalse()
        {
            ResourceTypeDef r = Res();
            DictionaryStockpile sp = SCTestHelper.MakeEmptyStockpile();

            bool ok = sp.TryDraw(r, 5.0, out double drawn);

            TestAssert.IsFalse(ok, "TryDraw on missing/empty resource should return false");
            TestAssert.AreEqual(0.0, drawn);
        }

        [EmpireTest("SC.Stockpile")]
        public static void TryDraw_ZeroAmount_ReturnsFalse()
        {
            ResourceTypeDef r = Res();
            DictionaryStockpile sp = SCTestHelper.MakeStockpile(r, 10.0, 100.0);

            bool ok = sp.TryDraw(r, 0.0, out double drawn);

            TestAssert.IsFalse(ok, "TryDraw of zero should be a no-op false");
            TestAssert.AreEqual(0.0, drawn);
            TestAssert.AreEqual(10.0, sp.GetAmount(r), 0.001, "Amount must be unchanged");
        }

        [EmpireTest("SC.Stockpile")]
        public static void TryDraw_NegativeAmount_ReturnsFalse()
        {
            ResourceTypeDef r = Res();
            DictionaryStockpile sp = SCTestHelper.MakeStockpile(r, 10.0, 100.0);

            bool ok = sp.TryDraw(r, -5.0, out double drawn);

            TestAssert.IsFalse(ok, "TryDraw of a negative amount should return false");
            TestAssert.AreEqual(0.0, drawn);
            TestAssert.AreEqual(10.0, sp.GetAmount(r));
        }

        /*-*-*- Credit -*-*-*/

        [EmpireTest("SC.Stockpile")]
        public static void Credit_WithinCap_NoExcess()
        {
            ResourceTypeDef r = Res();
            DictionaryStockpile sp = SCTestHelper.MakeStockpile(r, 0.0, 100.0);

            double excess = sp.Credit(r, 30.0);

            TestAssert.AreEqual(0.0, excess, 0.001, "Credit within cap returns no excess");
            TestAssert.AreEqual(30.0, sp.GetAmount(r));
        }

        [EmpireTest("SC.Stockpile")]
        public static void Credit_OverCap_ReturnsExcessAndClamps()
        {
            ResourceTypeDef r = Res();
            DictionaryStockpile sp = SCTestHelper.MakeStockpile(r, 90.0, 100.0);

            double excess = sp.Credit(r, 30.0);

            TestAssert.AreEqual(20.0, excess, 0.001, "Only 10 fits; 20 should be returned as excess");
            TestAssert.AreEqual(100.0, sp.GetAmount(r), 0.001, "Amount is clamped to cap");
        }

        [EmpireTest("SC.Stockpile")]
        public static void Credit_ZeroCap_ReturnsAllAsExcess()
        {
            ResourceTypeDef r = Res();
            DictionaryStockpile sp = SCTestHelper.MakeStockpile(r, 0.0, 0.0);

            double excess = sp.Credit(r, 10.0);

            TestAssert.AreEqual(10.0, excess, 0.001, "Nothing fits under a zero cap");
            TestAssert.AreEqual(0.0, sp.GetAmount(r));
        }

        [EmpireTest("SC.Stockpile")]
        public static void CreditOverCap_ThenDrawAll_RoundTrips()
        {
            // The overflow contract end-to-end: crediting past the cap clamps and reports the excess,
            // and a subsequent over-draw takes back exactly the capped amount, leaving nothing behind.
            ResourceTypeDef r = Res();
            DictionaryStockpile sp = SCTestHelper.MakeStockpile(r, 0.0, 100.0);

            double excess = sp.Credit(r, 150.0);
            TestAssert.AreEqual(50.0, excess, 0.001, "50 over the 100 cap is returned as excess");
            TestAssert.AreEqual(100.0, sp.GetAmount(r), 0.001, "Stockpile fills to exactly the cap");

            bool ok = sp.TryDraw(r, 1000.0, out double drawn);
            TestAssert.IsTrue(ok, "Draw from a full stockpile succeeds");
            TestAssert.AreEqual(100.0, drawn, 0.001, "Draw clamps to the capped amount");
            TestAssert.AreEqual(0.0, sp.GetAmount(r), 0.001, "Stockpile empties after drawing everything");
        }

        [EmpireTest("SC.Stockpile")]
        public static void Credit_NegativeAmount_NoOp()
        {
            ResourceTypeDef r = Res();
            DictionaryStockpile sp = SCTestHelper.MakeStockpile(r, 5.0, 100.0);

            double excess = sp.Credit(r, -5.0);

            TestAssert.AreEqual(0.0, excess);
            TestAssert.AreEqual(5.0, sp.GetAmount(r), 0.001, "Negative credit must not change the amount");
        }

        /*-*-*- GetAmount / GetCap defaults -*-*-*/

        [EmpireTest("SC.Stockpile")]
        public static void GetAmount_MissingKey_IsZero()
        {
            DictionaryStockpile sp = SCTestHelper.MakeEmptyStockpile();
            TestAssert.AreEqual(0.0, sp.GetAmount(Res()));
        }

        [EmpireTest("SC.Stockpile")]
        public static void GetCap_MissingKey_IsZero()
        {
            DictionaryStockpile sp = SCTestHelper.MakeEmptyStockpile();
            TestAssert.AreEqual(0.0, sp.GetCap(Res()));
        }
    }
}
