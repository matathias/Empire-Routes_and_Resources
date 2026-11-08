namespace FactionColonies.SupplyChain;

/// <summary>
/// Pure coverage for the <see cref="NeedState.Satisfaction"/> ratio and the <see cref="NeedState.IsShort"/>
/// shortfall test.
/// </summary>
public static class NeedStateTests
{
    private static NeedState Make(double demanded, double fulfilled)
    {
        var ns = new NeedState();
        ns.demanded = demanded;
        ns.fulfilled = fulfilled;
        return ns;
    }

    [EmpireTest("SC.Needs")]
    public static void Satisfaction_ZeroDemand_IsFullySatisfied()
    {
        // No demand means nothing to fail to meet -> treated as 1.0.
        TestAssert.AreEqual(1.0, Make(0.0, 0.0).Satisfaction);
    }

    [EmpireTest("SC.Needs")]
    public static void Satisfaction_PartiallyMet_IsRatio()
    {
        TestAssert.AreEqual(0.4, Make(100.0, 40.0).Satisfaction);
    }

    [EmpireTest("SC.Needs")]
    public static void Satisfaction_ExactlyMet_IsOne()
    {
        TestAssert.AreEqual(1.0, Make(50.0, 50.0).Satisfaction);
    }

    [EmpireTest("SC.Needs")]
    public static void Satisfaction_OverFulfilled_ExceedsOne()
    {
        TestAssert.AreEqual(1.5, Make(50.0, 75.0).Satisfaction);
    }

    /*-*-*- IsShort -*-*-*/

    [EmpireTest("SC.Needs")]
    public static void IsShort_ZeroDemand_IsNotShort()
    {
        TestAssert.IsFalse(Make(0.0, 0.0).IsShort, "A need with no demand cannot be short");
    }

    [EmpireTest("SC.Needs")]
    public static void IsShort_NegativeDemand_IsNotShort()
    {
        TestAssert.IsFalse(Make(-5.0, 0.0).IsShort, "A non-positive demand cannot be short");
    }

    [EmpireTest("SC.Needs")]
    public static void IsShort_ExactlyMet_IsNotShort()
    {
        TestAssert.IsFalse(Make(50.0, 50.0).IsShort);
    }

    [EmpireTest("SC.Needs")]
    public static void IsShort_OverFulfilled_IsNotShort()
    {
        TestAssert.IsFalse(Make(50.0, 75.0).IsShort);
    }

    [EmpireTest("SC.Needs")]
    public static void IsShort_FloatErrorBelowTolerance_IsNotShort()
    {
        // Sequential proportional draws can leave a met need a few ulps under its demand.
        double demand = 0.1 + 0.2;
        TestAssert.IsFalse(Make(demand, demand - 1e-12).IsShort,
            "A shortfall far below MetTolerance is float error, not a real shortfall");
        TestAssert.IsFalse(Make(1000.0, 1000.0 - NeedState.MetTolerance * 0.5).IsShort,
            "Half the tolerance short still counts as met");
    }

    [EmpireTest("SC.Needs")]
    public static void IsShort_ShortfallAboveTolerance_IsShort()
    {
        TestAssert.IsTrue(Make(50.0, 50.0 - NeedState.MetTolerance * 10).IsShort,
            "Ten times the tolerance short is a real shortfall");
        TestAssert.IsTrue(Make(50.0, 49.0).IsShort);
        TestAssert.IsTrue(Make(50.0, 0.0).IsShort);
    }

    [EmpireTest("SC.Needs")]
    public static void IsShort_TinyDemandNothingFulfilled_IsShortOnlyAboveTolerance()
    {
        // The tolerance is absolute, so a demand smaller than it never reads as short.
        TestAssert.IsFalse(Make(NeedState.MetTolerance * 0.5, 0.0).IsShort);
        TestAssert.IsTrue(Make(NeedState.MetTolerance * 2, 0.0).IsShort);
    }
}
