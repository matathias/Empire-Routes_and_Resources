using System.Collections.Generic;
using RimWorld;

namespace FactionColonies.SupplyChain
{
    /// <summary>
    /// Pure coverage for the tech-weighted demand-split logic on <see cref="SettlementNeedDef"/> and
    /// <see cref="SettlementNeedResourceInput"/>. Everything here runs on hand-built defs with
    /// throwaway resources — no live settlement is touched. (Worker/level-scaled
    /// <see cref="SettlementNeedDef.CalculateDemand"/> needs a real settlement and lives in the
    /// destructive tier; only the settlement-free Flat branch is asserted here.)
    /// </summary>
    public static class SettlementNeedDefTests
    {
        private static SettlementNeedResourceInput Input(ResourceTypeDef r, float weight)
        {
            return new SettlementNeedResourceInput { resource = r, weight = weight };
        }

        private static SettlementNeedDef NeedWith(params SettlementNeedResourceInput[] inputs)
        {
            return new SettlementNeedDef { resources = new List<SettlementNeedResourceInput>(inputs) };
        }

        /*-*-*- SettlementNeedResourceInput.GetWeightAt -*-*-*/

        [EmpireTest("SC.Needs")]
        public static void GetWeightAt_NoTechTable_ReturnsFlatWeight()
        {
            var input = Input(SCTestHelper.MakeResourceType("SCTest_Flat"), 2f);
            TestAssert.AreEqual(2.0, input.GetWeightAt(TechLevel.Industrial));
        }

        [EmpireTest("SC.Needs")]
        public static void GetWeightAt_ExactTechMatch_ReturnsThatWeight()
        {
            var input = Input(SCTestHelper.MakeResourceType("SCTest_Exact"), 1f);
            input.weightsByTech = new List<TechLevelWeight>
            {
                new TechLevelWeight { level = TechLevel.Medieval, weight = 1f },
                new TechLevelWeight { level = TechLevel.Industrial, weight = 3f },
            };
            TestAssert.AreEqual(3.0, input.GetWeightAt(TechLevel.Industrial));
        }

        [EmpireTest("SC.Needs")]
        public static void GetWeightAt_NoExactMatch_UsesNearestLowerKey()
        {
            var input = Input(SCTestHelper.MakeResourceType("SCTest_Lower"), 1f);
            input.weightsByTech = new List<TechLevelWeight>
            {
                new TechLevelWeight { level = TechLevel.Medieval, weight = 1f },
                new TechLevelWeight { level = TechLevel.Industrial, weight = 3f },
            };
            // Spacer has no exact key; nearest lower defined key is Industrial -> 3.
            TestAssert.AreEqual(3.0, input.GetWeightAt(TechLevel.Spacer));
        }

        [EmpireTest("SC.Needs")]
        public static void GetWeightAt_BelowAllKeys_UsesSmallestKey()
        {
            var input = Input(SCTestHelper.MakeResourceType("SCTest_Floor"), 1f);
            input.weightsByTech = new List<TechLevelWeight>
            {
                new TechLevelWeight { level = TechLevel.Industrial, weight = 3f },
                new TechLevelWeight { level = TechLevel.Spacer, weight = 5f },
            };
            // Neolithic is below every key -> floor to the smallest defined key (Industrial) -> 3.
            TestAssert.AreEqual(3.0, input.GetWeightAt(TechLevel.Neolithic));
        }

        /*-*-*- GetResourceSplit -*-*-*/

        [EmpireTest("SC.Needs")]
        public static void GetResourceSplit_SingleResource_FractionIsOne()
        {
            ResourceTypeDef r = SCTestHelper.MakeResourceType("SCTest_Single");
            var split = NeedWith(Input(r, 1f)).GetResourceSplit(TechLevel.Undefined);
            TestAssert.AreEqual(1, split.Count);
            TestAssert.AreEqual(1.0, split[0].Value);
        }

        [EmpireTest("SC.Needs")]
        public static void GetResourceSplit_EqualWeights_SplitEvenly()
        {
            ResourceTypeDef r1 = SCTestHelper.MakeResourceType("SCTest_Eq1");
            ResourceTypeDef r2 = SCTestHelper.MakeResourceType("SCTest_Eq2");
            SettlementNeedDef need = NeedWith(Input(r1, 1f), Input(r2, 1f));
            TestAssert.AreEqual(0.5, need.GetResourceFraction(TechLevel.Undefined, r1));
            TestAssert.AreEqual(0.5, need.GetResourceFraction(TechLevel.Undefined, r2));
        }

        [EmpireTest("SC.Needs")]
        public static void GetResourceSplit_WeightedNormalizesToOne()
        {
            ResourceTypeDef r1 = SCTestHelper.MakeResourceType("SCTest_W1");
            ResourceTypeDef r2 = SCTestHelper.MakeResourceType("SCTest_W2");
            SettlementNeedDef need = NeedWith(Input(r1, 3f), Input(r2, 1f));
            TestAssert.AreEqual(0.75, need.GetResourceFraction(TechLevel.Undefined, r1));
            TestAssert.AreEqual(0.25, need.GetResourceFraction(TechLevel.Undefined, r2));
        }

        [EmpireTest("SC.Needs")]
        public static void GetResourceSplit_ZeroWeightEntry_Dropped()
        {
            ResourceTypeDef r1 = SCTestHelper.MakeResourceType("SCTest_Z1");
            ResourceTypeDef r2 = SCTestHelper.MakeResourceType("SCTest_Z2");
            SettlementNeedDef need = NeedWith(Input(r1, 1f), Input(r2, 0f));
            var split = need.GetResourceSplit(TechLevel.Undefined);
            TestAssert.AreEqual(1, split.Count, "Zero-weight entry should be dropped");
            TestAssert.AreEqual(1.0, need.GetResourceFraction(TechLevel.Undefined, r1));
            TestAssert.AreEqual(0.0, need.GetResourceFraction(TechLevel.Undefined, r2));
        }

        [EmpireTest("SC.Needs")]
        public static void GetResourceSplit_AllZeroWeights_FallsBackToFirstResource()
        {
            ResourceTypeDef r1 = SCTestHelper.MakeResourceType("SCTest_AZ1");
            ResourceTypeDef r2 = SCTestHelper.MakeResourceType("SCTest_AZ2");
            SettlementNeedDef need = NeedWith(Input(r1, 0f), Input(r2, 0f));
            var split = need.GetResourceSplit(TechLevel.Undefined);
            TestAssert.AreEqual(1, split.Count, "All-zero weights collapse to the first resource");
            TestAssert.AreEqual(1.0, need.GetResourceFraction(TechLevel.Undefined, r1));
        }

        /*-*-*- GetResourceFraction edge -*-*-*/

        [EmpireTest("SC.Needs")]
        public static void GetResourceFraction_UnknownResource_IsZero()
        {
            ResourceTypeDef r1 = SCTestHelper.MakeResourceType("SCTest_K1");
            ResourceTypeDef other = SCTestHelper.MakeResourceType("SCTest_KOther");
            SettlementNeedDef need = NeedWith(Input(r1, 1f));
            TestAssert.AreEqual(0.0, need.GetResourceFraction(TechLevel.Undefined, other));
        }

        /*-*-*- CalculateDemand (Flat branch only — no settlement deref) -*-*-*/

        [EmpireTest("SC.Needs")]
        public static void CalculateDemand_FlatScaling_ReturnsBaseAmount()
        {
            // Flat is the default scaling; it returns baseAmount without dereferencing the settlement.
            var need = new SettlementNeedDef { baseAmount = 12.0 };
            TestAssert.AreEqual(12.0, need.CalculateDemand(null));
        }
    }
}
