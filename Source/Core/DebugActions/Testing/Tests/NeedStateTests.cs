namespace FactionColonies.SupplyChain
{
    /// <summary>
    /// Pure coverage for the <see cref="NeedState.Satisfaction"/> ratio property.
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
    }
}
