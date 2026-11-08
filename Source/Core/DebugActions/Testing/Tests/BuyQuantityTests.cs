namespace FactionColonies.SupplyChain;

/// <summary>
/// Pure coverage for <see cref="UIUtilSC.QuantityThatFits"/>, which decides how much of a buy-menu
/// purchase is delivered and charged when the pile has filled since the menu opened.
/// </summary>
public static class BuyQuantityTests
{
    private static ResourceTypeDef Res() => SCTestHelper.MakeResourceType("SCTest_Buy");

    [EmpireTest("SC.Stockpile")]
    public static void QuantityThatFits_PlentyOfRoom_IsRequested()
    {
        ResourceTypeDef r = Res();
        TestAssert.AreEqual(10, UIUtilSC.QuantityThatFits(10, SCTestHelper.MakeStockpile(r, 20.0, 100.0), r));
    }

    [EmpireTest("SC.Stockpile")]
    public static void QuantityThatFits_ExactRoom_IsRequested()
    {
        ResourceTypeDef r = Res();
        TestAssert.AreEqual(10, UIUtilSC.QuantityThatFits(10, SCTestHelper.MakeStockpile(r, 90.0, 100.0), r));
    }

    [EmpireTest("SC.Stockpile")]
    public static void QuantityThatFits_PartialRoom_IsWholeUnitsThatFit()
    {
        ResourceTypeDef r = Res();
        TestAssert.AreEqual(3, UIUtilSC.QuantityThatFits(10, SCTestHelper.MakeStockpile(r, 96.5, 100.0), r),
            "3.5 units of room fit 3 whole units");
    }

    [EmpireTest("SC.Stockpile")]
    public static void QuantityThatFits_LessThanOneUnitOfRoom_IsZero()
    {
        ResourceTypeDef r = Res();
        TestAssert.AreEqual(0, UIUtilSC.QuantityThatFits(5, SCTestHelper.MakeStockpile(r, 99.4, 100.0), r));
    }

    [EmpireTest("SC.Stockpile")]
    public static void QuantityThatFits_FullPile_IsZero()
    {
        ResourceTypeDef r = Res();
        TestAssert.AreEqual(0, UIUtilSC.QuantityThatFits(5, SCTestHelper.MakeStockpile(r, 100.0, 100.0), r));
    }

    [EmpireTest("SC.Stockpile")]
    public static void QuantityThatFits_OverCapPile_IsZeroNotNegative()
    {
        // Deposits land uncapped, so a pile can sit above its cap until the daily sweep.
        ResourceTypeDef r = Res();
        TestAssert.AreEqual(0, UIUtilSC.QuantityThatFits(5, SCTestHelper.MakeStockpile(r, 140.0, 100.0), r));
    }

    [EmpireTest("SC.Stockpile")]
    public static void QuantityThatFits_NoCap_IsZero()
    {
        ResourceTypeDef r = Res();
        TestAssert.AreEqual(0, UIUtilSC.QuantityThatFits(5, SCTestHelper.MakeEmptyStockpile(), r),
            "A resource the pile has no cap for cannot be bought into it");
    }
}
