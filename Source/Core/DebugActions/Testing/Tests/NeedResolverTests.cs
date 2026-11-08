namespace FactionColonies.SupplyChain;

/// <summary>
/// Pure coverage for the need-resolver helpers: <see cref="NeedResolver.ForEachProviderNeeds"/>
/// (provider discovery, entry filtering, isolation of a throwing provider) and
/// <see cref="NeedResolver.ApplySurplusRatios"/> (own vs shared demand). The settlements here are
/// bare, unspawned <see cref="WorldSettlementFC"/> objects that only carry the comps a test adds.
/// </summary>
public static class NeedResolverTests
{
    private static ResourceTypeDef Res() => SCTestHelper.MakeResourceType("SCTest_Need");

    private static WorldSettlementFC BareSettlement(params WorldObjectComp[] comps)
    {
        WorldSettlementFC s = new();
        foreach (WorldObjectComp c in comps)
        {
            c.parent = s;
            s.AllComps.Add(c);
        }
        return s;
    }

    private static List<(INeedProvider provider, List<NeedEntry> needs)> Collect(WorldSettlementFC s)
    {
        // The helper reuses its list between providers, so each call's entries are copied out.
        List<(INeedProvider, List<NeedEntry>)> calls = [];
        NeedResolver.ForEachProviderNeeds(s, (p, needs) => calls.Add((p, [.. needs])));
        return calls;
    }

    /*-*-*- ForEachProviderNeeds -*-*-*/

    [EmpireTest("SC.Needs")]
    public static void ForEachProviderNeeds_NoComps_NeverCallsBack()
    {
        TestAssert.IsEmpty(Collect(BareSettlement()), "A settlement with no comps has no providers");
    }

    [EmpireTest("SC.Needs")]
    public static void ForEachProviderNeeds_SkipsCompsThatAreNotProviders()
    {
        ResourceTypeDef r = Res();
        TestNeedProvider provider = new() { needs = [TestNeedProvider.Need("a", r, 5.0)] };
        var calls = Collect(BareSettlement(new TestPlainComp(), provider, new TestPlainComp()));

        TestAssert.AreEqual(1, calls.Count, "Only the provider comp should be called back");
        TestAssert.IsTrue(ReferenceEquals(calls[0].provider, provider), "The callback receives the provider itself");
        TestAssert.AreEqual(1, calls[0].needs.Count);
    }

    [EmpireTest("SC.Needs")]
    public static void ForEachProviderNeeds_DropsEntriesWithoutResourceOrPositiveAmount()
    {
        ResourceTypeDef r = Res();
        TestNeedProvider provider = new()
        {
            needs =
            [
                TestNeedProvider.Need("valid", r, 5.0),
                TestNeedProvider.Need("noResource", null, 5.0),
                TestNeedProvider.Need("zero", r, 0.0),
                TestNeedProvider.Need("negative", r, -3.0),
                TestNeedProvider.Need("tiny", r, 1e-9),
            ]
        };
        var calls = Collect(BareSettlement(provider));

        TestAssert.AreEqual(1, calls.Count);
        List<string> ids = calls[0].needs.Select(n => n.needId).ToList();
        TestAssert.AreEqual(2, ids.Count, $"Expected only the positive, resourced entries, got [{string.Join(", ", ids)}]");
        TestAssert.Contains(ids, "valid");
        TestAssert.Contains(ids, "tiny");
    }

    [EmpireTest("SC.Needs")]
    public static void ForEachProviderNeeds_ProviderWithNoValidNeeds_StillCalledWithEmptyList()
    {
        // The Complex resolver reports back to every provider, even one whose needs were all dropped.
        TestNeedProvider provider = new() { needs = [TestNeedProvider.Need("zero", Res(), 0.0)] };
        var calls = Collect(BareSettlement(provider));

        TestAssert.AreEqual(1, calls.Count, "The provider is still visited");
        TestAssert.IsEmpty(calls[0].needs, "...with no entries");
    }

    [EmpireTest("SC.Needs")]
    public static void ForEachProviderNeeds_EachProviderSeesOnlyItsOwnNeeds()
    {
        ResourceTypeDef r = Res();
        TestNeedProvider first = new() { needs = [TestNeedProvider.Need("a1", r, 1.0), TestNeedProvider.Need("a2", r, 2.0)] };
        TestNeedProvider second = new() { needs = [TestNeedProvider.Need("b1", r, 3.0)] };
        var calls = Collect(BareSettlement(first, second));

        TestAssert.AreEqual(2, calls.Count);
        TestAssert.IsTrue(ReferenceEquals(calls[0].provider, first), "Providers are visited in comp order");
        TestAssert.AreEqual(2, calls[0].needs.Count);
        TestAssert.IsTrue(ReferenceEquals(calls[1].provider, second));
        TestAssert.AreEqual(1, calls[1].needs.Count, "The reused list must be cleared between providers");
        TestAssert.AreEqual((object)"b1", calls[1].needs[0].needId);
    }

    [EmpireTest("SC.Needs")]
    public static void ForEachProviderNeeds_ThrowingProvider_IsSkippedAndOthersStillRun()
    {
        // The throwing provider logs one error (once per provider type) by design.
        ResourceTypeDef r = Res();
        TestNeedProvider before = new() { needs = [TestNeedProvider.Need("before", r, 1.0)] };
        TestNeedProvider thrower = new() { needs = [TestNeedProvider.Need("partial", r, 9.0)], throwOnCollect = true };
        TestNeedProvider after = new() { needs = [TestNeedProvider.Need("after", r, 2.0)] };

        List<(INeedProvider provider, List<NeedEntry> needs)> calls = null;
        TestAssert.DoesNotThrow(() => calls = Collect(BareSettlement(before, thrower, after)),
            "A provider that throws must not escape ForEachProviderNeeds");

        TestAssert.AreEqual(1, thrower.collectCalls, "The throwing provider was asked");
        TestAssert.AreEqual(2, calls.Count, "Only the two healthy providers are called back");
        TestAssert.IsFalse(calls.Any(c => ReferenceEquals(c.provider, thrower)), "The throwing provider is skipped");
        TestAssert.IsTrue(ReferenceEquals(calls[1].provider, after), "The provider after the throw still runs");
        TestAssert.AreEqual(1, calls[1].needs.Count,
            "Entries the throwing provider added before throwing must not leak into the next provider's list");
        TestAssert.AreEqual((object)"after", calls[1].needs[0].needId);
    }

    /*-*-*- ApplySurplusRatios -*-*-*/

    private static NeedState MetState(ResourceTypeDef r, double demand, List<NeedSurplusBonus> bonuses = null) =>
        new("need", r, demand, demand, "Need", NeedCategory.Base, surplusBonuses: bonuses ?? []);

    [EmpireTest("SC.Needs")]
    public static void ApplySurplusRatios_OwnDemand_IsRemainingOverDemand()
    {
        ResourceTypeDef r = Res();
        NeedState ns = MetState(r, 10.0);
        NeedResolver.ApplySurplusRatios([ns], SCTestHelper.MakeStockpile(r, 30.0, 1000.0), null);
        TestAssert.AreEqual(3.0, ns.surplusRatio, 1e-9);
    }

    [EmpireTest("SC.Needs")]
    public static void ApplySurplusRatios_SharedDemand_UsesTotalForEveryState()
    {
        // Two settlements sharing one pile: demands 10 and 30 (total 40), 80 left after the draws.
        // Each must see 80 / 40 = 2, not 8 and 2.67.
        ResourceTypeDef r = Res();
        NeedState small = MetState(r, 10.0);
        NeedState large = MetState(r, 30.0);
        Dictionary<ResourceTypeDef, double> shared = new() { [r] = 40.0 };

        NeedResolver.ApplySurplusRatios([small, large], SCTestHelper.MakeStockpile(r, 80.0, 1000.0), shared);

        TestAssert.AreEqual(2.0, small.surplusRatio, 1e-9, "Small consumer measured against faction-wide demand");
        TestAssert.AreEqual(2.0, large.surplusRatio, 1e-9, "Large consumer measured against faction-wide demand");
    }

    [EmpireTest("SC.Needs")]
    public static void ApplySurplusRatios_SharedDemandWithoutResource_FallsBackToOwnDemand()
    {
        ResourceTypeDef r = Res();
        ResourceTypeDef other = SCTestHelper.MakeResourceType("SCTest_NeedOther");
        NeedState ns = MetState(r, 10.0);
        Dictionary<ResourceTypeDef, double> shared = new() { [other] = 500.0 };

        NeedResolver.ApplySurplusRatios([ns], SCTestHelper.MakeStockpile(r, 30.0, 1000.0), shared);

        TestAssert.AreEqual(3.0, ns.surplusRatio, 1e-9);
    }

    [EmpireTest("SC.Needs")]
    public static void ApplySurplusRatios_ShortNeed_GetsNoSurplus()
    {
        ResourceTypeDef r = Res();
        NeedState ns = new("need", r, 10.0, 5.0, "Need", NeedCategory.Base, surplusBonuses: []);
        NeedResolver.ApplySurplusRatios([ns], SCTestHelper.MakeStockpile(r, 30.0, 1000.0), null);
        TestAssert.AreEqual(0.0, ns.surplusRatio, 1e-12, "A need that is short has no surplus, whatever is stocked");
    }

    [EmpireTest("SC.Needs")]
    public static void ApplySurplusRatios_MetWithinTolerance_GetsSurplus()
    {
        ResourceTypeDef r = Res();
        NeedState ns = new("need", r, 10.0, 10.0 - 1e-12, "Need", NeedCategory.Base, surplusBonuses: []);
        NeedResolver.ApplySurplusRatios([ns], SCTestHelper.MakeStockpile(r, 20.0, 1000.0), null);
        TestAssert.AreEqual(2.0, ns.surplusRatio, 1e-9, "Float error below the tolerance must not cancel the surplus");
    }

    [EmpireTest("SC.Needs")]
    public static void ApplySurplusRatios_NoSurplusBonuses_IsLeftAlone()
    {
        ResourceTypeDef r = Res();
        NeedState ns = new("need", r, 10.0, 10.0, "Need", NeedCategory.Base, surplusBonuses: null);
        NeedResolver.ApplySurplusRatios([ns], SCTestHelper.MakeStockpile(r, 30.0, 1000.0), null);
        TestAssert.AreEqual(0.0, ns.surplusRatio, 1e-12);
    }

    [EmpireTest("SC.Needs")]
    public static void ApplySurplusRatios_ZeroDemand_IsLeftAlone()
    {
        ResourceTypeDef r = Res();
        NeedState ns = MetState(r, 0.0);
        Dictionary<ResourceTypeDef, double> shared = new() { [r] = 0.0 };
        NeedResolver.ApplySurplusRatios([ns], SCTestHelper.MakeStockpile(r, 30.0, 1000.0), shared);
        TestAssert.AreEqual(0.0, ns.surplusRatio, 1e-12, "No division by a zero demand");
    }

    [EmpireTest("SC.Needs")]
    public static void ApplySurplusRatios_EmptyStockpile_IsZero()
    {
        ResourceTypeDef r = Res();
        NeedState ns = MetState(r, 10.0);
        ns.surplusRatio = 5.0;
        NeedResolver.ApplySurplusRatios([ns], SCTestHelper.MakeEmptyStockpile(), null);
        TestAssert.AreEqual(0.0, ns.surplusRatio, 1e-12, "Nothing left over means no surplus, replacing any older ratio");
    }
}
