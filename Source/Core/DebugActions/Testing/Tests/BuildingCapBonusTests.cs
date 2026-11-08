namespace FactionColonies.SupplyChain;

/// <summary>
/// Pure coverage for the guard paths of <see cref="BuildingNeedExtension.AddBuildingCapBonuses"/>. The
/// summation over real buildings is covered in the destructive tier.
/// </summary>
public static class BuildingCapBonusTests
{
    [EmpireTest("SC.Caps")]
    public static void AddBuildingCapBonuses_NullSettlement_LeavesCapsUnchanged()
    {
        ResourceTypeDef r = SCTestHelper.MakeResourceType("SCTest_Cap");
        Dictionary<ResourceTypeDef, double> caps = new() { [r] = 50.0 };

        TestAssert.DoesNotThrow(() => BuildingNeedExtension.AddBuildingCapBonuses(null, caps));

        TestAssert.AreEqual(1, caps.Count);
        TestAssert.AreEqual(50.0, caps[r]);
    }

    [EmpireTest("SC.Caps")]
    public static void AddBuildingCapBonuses_SettlementWithoutBuildingsComp_LeavesCapsUnchanged()
    {
        ResourceTypeDef r = SCTestHelper.MakeResourceType("SCTest_Cap");
        Dictionary<ResourceTypeDef, double> caps = new() { [r] = 50.0 };

        TestAssert.DoesNotThrow(() => BuildingNeedExtension.AddBuildingCapBonuses(new WorldSettlementFC(), caps));

        TestAssert.AreEqual(1, caps.Count);
        TestAssert.AreEqual(50.0, caps[r]);
    }
}
