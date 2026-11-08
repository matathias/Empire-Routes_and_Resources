namespace FactionColonies.SupplyChain;

/* DESTRUCTIVE: instantly constructs a storage building on a live settlement (into an empty slot, or
   over slot 0) so the cap-bonus summation has something to add. May create a transient settlement.
   Not reverted. */
public static class BuildingCapBonusDestructiveTests
{
    private static bool HasCapBonus(BuildingNeedExtension ext) =>
        ext?.capBonuses is not null && ext.capBonuses.Any(b => b.resource is not null && b.amount > 0);

    /// <summary>
    /// A settlement holding at least one building with a cap bonus, and the first resource such a
    /// building raises. Constructs this mod's first cap-bonus building when no settlement has one.
    /// </summary>
    private static WorldSettlementFC EnsureCapBuilding(out ResourceTypeDef bonusResource)
    {
        FactionFC f = DestructiveTestUtil.RequireFaction();
        foreach (WorldSettlementFC s in f.settlements)
        {
            BuildingFC existing = s?.BuildingsComp?.Buildings?.FirstOrDefault(b =>
                b?.def is not null && b.def != BuildingFCDefOf.Empty && HasCapBonus(SupplyChainCache.GetBuildingNeedExt(b.def)));
            if (existing is null) continue;
            bonusResource = SupplyChainCache.GetBuildingNeedExt(existing.def).capBonuses.First(b => b.resource is not null && b.amount > 0).resource;
            return s;
        }

        BuildingFCDef capDef = TestAssert.NotNull(
            DefDatabase<BuildingFCDef>.AllDefsListForReading.FirstOrDefault(d =>
                d != BuildingFCDefOf.Empty && d != BuildingFCDefOf.Construction
                && HasCapBonus(SupplyChainCache.GetBuildingNeedExt(d))),
            "No BuildingFCDef carries a cap bonus (this mod ships warehouses)");

        WorldSettlementFC target = SCDestructiveTestUtil.EnsureSCSettlement(x => x.BuildingsComp?.Buildings?.Count > 0);
        WorldObjectComp_SettlementBuildings bComp = target.BuildingsComp;
        int usable = Math.Min(bComp.Buildings.Count, Math.Max(1, bComp.NumBuildingSlots));
        int slot = 0;
        for (int i = 0; i < usable; i++)
        {
            if (bComp.BuildingSlotIsEmpty(i)) { slot = i; break; }
        }
        bComp.ConstructBuilding(capDef, slot);
        TestAssert.IsTrue(bComp.Buildings[slot].def == capDef,
            $"Constructing {capDef.defName} in slot {slot} of '{target.Name}' did not take");

        bonusResource = SupplyChainCache.GetBuildingNeedExt(capDef).capBonuses.First(b => b.resource is not null && b.amount > 0).resource;
        return target;
    }

    /// <summary>The cap bonus each resource gets from the settlement's buildings, summed directly from the defs.</summary>
    private static Dictionary<ResourceTypeDef, double> ExpectedBonuses(WorldSettlementFC s)
    {
        Dictionary<ResourceTypeDef, double> expected = [];
        foreach (BuildingFC b in s.BuildingsComp.Buildings)
        {
            if (b?.def is null || b.def == BuildingFCDefOf.Empty) continue;
            List<BuildingCapBonus> bonuses = SupplyChainCache.GetBuildingNeedExt(b.def)?.capBonuses;
            if (bonuses is null) continue;
            foreach (BuildingCapBonus bonus in bonuses)
            {
                if (bonus.resource is null) continue;
                expected.TryGetValue(bonus.resource, out double cur);
                expected[bonus.resource] = cur + bonus.amount;
            }
        }
        return expected;
    }

    [EmpireDestructiveTest("SC.Destructive.Caps")]
    public static void AddBuildingCapBonuses_AddsEveryBuildingsBonusOntoExistingCaps()
    {
        FactionFC f = DestructiveTestUtil.RequireFaction();
        WorldSettlementFC s = EnsureCapBuilding(out ResourceTypeDef bonusResource);
        Dictionary<ResourceTypeDef, double> expected = ExpectedBonuses(s);

        Dictionary<ResourceTypeDef, double> caps = [];
        foreach (ResourceTypeDef r in FactionCache.AllResourceTypeDefs)
            caps[r] = 100.0;

        BuildingNeedExtension.AddBuildingCapBonuses(s, caps);

        TestAssert.GreaterThan(caps[bonusResource], 100.0, $"The storage building must raise the {bonusResource.defName} cap");
        foreach (ResourceTypeDef r in FactionCache.AllResourceTypeDefs)
        {
            expected.TryGetValue(r, out double bonus);
            TestAssert.AreEqual(100.0 + bonus, caps[r], 0.001, $"Cap bonus for {r.defName} on '{s.Name}'");
        }

        DestructiveTestUtil.AssertEmpireInvariants(f, "AddBuildingCapBonuses_Sum");
    }

    [EmpireDestructiveTest("SC.Destructive.Caps")]
    public static void AddBuildingCapBonuses_OnlyRaisesResourcesAlreadyKeyed()
    {
        FactionFC f = DestructiveTestUtil.RequireFaction();
        WorldSettlementFC s = EnsureCapBuilding(out ResourceTypeDef bonusResource);
        Dictionary<ResourceTypeDef, double> expected = ExpectedBonuses(s);

        Dictionary<ResourceTypeDef, double> caps = new() { [bonusResource] = 0.0 };
        BuildingNeedExtension.AddBuildingCapBonuses(s, caps);

        TestAssert.AreEqual(1, caps.Count, "Resources missing from the cap table must not be added to it");
        TestAssert.AreEqual(expected[bonusResource], caps[bonusResource], 0.001);

        DestructiveTestUtil.AssertEmpireInvariants(f, "AddBuildingCapBonuses_KeyedOnly");
    }
}
