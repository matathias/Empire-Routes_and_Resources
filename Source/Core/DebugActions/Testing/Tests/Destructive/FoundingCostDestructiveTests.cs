namespace FactionColonies.SupplyChain;

/* DESTRUCTIVE: queries founding-cost logic against live world/faction state. CanFoundSettlement
   is read-only, but obtaining a settlement may create a transient one, and a settings field is
   temporarily overridden (and restored). Not fully reverted. */
public static class FoundingCostDestructiveTests
{
    [EmpireDestructiveTest("SC.Destructive.Founding")]
    public static void ComputeDistanceMultiplier_AtExistingSettlement_IsAboutOne()
    {
        FactionFC f = DestructiveTestUtil.RequireFaction();
        WorldSettlementFC s = SCDestructiveTestUtil.EnsureSCSettlement();
        TestAssert.IsTrue(s.Tile.Valid, $"Settlement '{s.Name}' has an invalid tile");

        FoundingCostUtil.InvalidateCache();
        double mult = FoundingCostUtil.ComputeDistanceMultiplier(s.Tile);

        // The nearest settlement to a settlement's own tile is itself (travel ~0) -> multiplier ~1.0.
        TestAssert.GreaterThan(mult, 0.999, "Distance multiplier should be at least ~1.0");
        TestAssert.LessThan(mult, 1.05, "At an existing settlement tile travel is ~0, so multiplier ~1.0");

        DestructiveTestUtil.AssertEmpireInvariants(f, "ComputeDistanceMultiplier_AtSettlement");
    }

    [EmpireDestructiveTest("SC.Destructive.Founding")]
    public static void CanFoundSettlement_BelowFreeThreshold_AlwaysAllowed()
    {
        FactionFC f = DestructiveTestUtil.RequireFaction();
        WorldComponent_SupplyChain comp = SCDestructiveTestUtil.RequireWorldComp();
        WorldSettlementFC s = SCDestructiveTestUtil.EnsureSCSettlement();
        TestAssert.IsTrue(s.Tile.Valid, $"Settlement '{s.Name}' has an invalid tile");

        int origThreshold = SupplyChainSettings.freeSettlementThreshold;
        try
        {
            SupplyChainSettings.freeSettlementThreshold = 9999; // force the "below threshold" branch
            var validator = new FoundingCostValidator(comp);
            string reason;
            bool ok = validator.CanFoundSettlement(s.Tile, s.settlementDef, out reason, 1f);
            TestAssert.IsTrue(ok, "Founding below the free-settlement threshold should always be allowed");
        }
        finally
        {
            SupplyChainSettings.freeSettlementThreshold = origThreshold;
        }

        DestructiveTestUtil.AssertEmpireInvariants(f, "CanFoundSettlement_BelowThreshold");
    }

    [EmpireDestructiveTest("SC.Destructive.Founding")]
    public static void ComputeSilverSurcharge_AtExistingSettlement_IsZero()
    {
        // A9: the distance silver surcharge = baseSurcharge * (travelDays / normalizingDays). The
        // nearest settlement to a settlement's own tile is itself (travel ~0), so the surcharge
        // there must be ~0 and never negative.
        FactionFC f = DestructiveTestUtil.RequireFaction();
        WorldSettlementFC s = SCDestructiveTestUtil.EnsureSCSettlement();
        TestAssert.IsTrue(s.Tile.Valid, $"Settlement '{s.Name}' has an invalid tile");

        FoundingCostUtil.InvalidateCache();
        int surcharge = FoundingCostUtil.ComputeSilverSurcharge(s.Tile);

        TestAssert.GreaterThan(surcharge, -1.0, "Silver surcharge is never negative");
        TestAssert.LessThan(surcharge, 1.0, "At an existing settlement tile travel is ~0, so the surcharge is ~0");

        DestructiveTestUtil.AssertEmpireInvariants(f, "ComputeSilverSurcharge_AtSettlement");
    }

    [EmpireDestructiveTest("SC.Destructive.Founding")]
    public static void CanFoundSettlement_AboveThreshold_EvaluatesCostsWithoutThrowing()
    {
        // A9: above the free-settlement threshold the validator runs the resource-cost branch that
        // the below-threshold test never reaches. It must evaluate cleanly and, when it rejects,
        // supply a player-facing reason.
        FactionFC f = DestructiveTestUtil.RequireFaction();
        WorldComponent_SupplyChain comp = SCDestructiveTestUtil.RequireWorldComp();
        WorldSettlementFC s = SCDestructiveTestUtil.EnsureSCSettlement();
        TestAssert.IsTrue(s.Tile.Valid, $"Settlement '{s.Name}' has an invalid tile");

        int origThreshold = SupplyChainSettings.freeSettlementThreshold;
        try
        {
            SupplyChainSettings.freeSettlementThreshold = 0; // force the cost-evaluation branch
            var validator = new FoundingCostValidator(comp);
            bool ok = validator.CanFoundSettlement(s.Tile, s.settlementDef, out string reason, 1f);
            if (!ok) TestAssert.IsNotNull(reason, "A rejected founding must supply a reason string");
        }
        finally
        {
            SupplyChainSettings.freeSettlementThreshold = origThreshold;
        }

        DestructiveTestUtil.AssertEmpireInvariants(f, "CanFoundSettlement_AboveThreshold");
    }

    [EmpireDestructiveTest("SC.Destructive.Founding")]
    public static void FoundingCancelled_GivesBackWhatTheFoundingDrew()
    {
        // A founding that fails after the validators were notified (e.g. an outpost conversion
        // whose settlement creation throws) must get its resources back. Founding on an existing
        // settlement's tile makes that settlement the source in both modes.
        FactionFC f = DestructiveTestUtil.RequireFaction();
        WorldComponent_SupplyChain comp = SCDestructiveTestUtil.RequireWorldComp();
        WorldSettlementFC s = SCDestructiveTestUtil.EnsureSCSettlement();
        WorldSettlementDef type = DefDatabase<WorldSettlementDef>.AllDefsListForReading
            .FirstOrDefault(d => FoundingCostUtil.GetFoundingResourceCosts(d)?.Count > 0);
        TestAssert.IsNotNull(type, "No settlement type has founding resource costs");
        IStockpile stockpile = TestAssert.NotNull(WorldComponent_SupplyChain.StockpileFor(s), "The source settlement has a stockpile");

        List<ResourceTypeDef> resources = FoundingCostUtil.GetFoundingResourceCosts(type).Select(c => c.resource).Distinct().ToList();
        foreach (ResourceTypeDef r in resources) stockpile.Credit(r, 50);
        Dictionary<ResourceTypeDef, double> before = resources.ToDictionary(r => r, r => stockpile.GetAmount(r));

        int origThreshold = SupplyChainSettings.freeSettlementThreshold;
        try
        {
            SupplyChainSettings.freeSettlementThreshold = 0; // the founding draws resources
            FoundingCostValidator validator = new(comp);
            FoundingCostUtil.InvalidateCache();
            validator.OnSettlementFounded(s.Tile, type, 1f);
            TestAssert.IsTrue(resources.Any(r => stockpile.GetAmount(r) < before[r] - 0.001),
                "The founding should have drawn some of its resources");

            ((ISettlementFoundingValidator)validator).OnFoundingCancelled(s.Tile, type);
            foreach (ResourceTypeDef r in resources)
                TestAssert.AreEqual(before[r], stockpile.GetAmount(r), tolerance: 0.001,
                    message: $"{r.defName} should be back to its amount before the founding");
        }
        finally
        {
            SupplyChainSettings.freeSettlementThreshold = origThreshold;
        }

        DestructiveTestUtil.AssertEmpireInvariants(f, "FoundingCancelled_GivesBackWhatTheFoundingDrew");
    }
}
