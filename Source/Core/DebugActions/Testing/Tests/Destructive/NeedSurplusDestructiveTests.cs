namespace FactionColonies.SupplyChain;

/* DESTRUCTIVE: attaches scripted need-provider comps to live settlements, runs the real need
   resolvers against throwaway stockpiles (overwriting the settlements' need-states), then removes
   the comps and rebuilds the need-states so the throwaway resource does not linger. May create
   transient settlements. Not otherwise reverted. */
public static class NeedSurplusDestructiveTests
{
    private static ResourceTypeDef TestResource() => SCTestHelper.MakeResourceType("SCTest_SurplusRes");

    private static TestNeedProvider Attach(WorldSettlementFC s, params NeedEntry[] needs)
    {
        TestNeedProvider p = new() { parent = s, needs = [.. needs] };
        s.AllComps.Add(p);
        return p;
    }

    private static void Detach(WorldSettlementFC s, TestNeedProvider p)
    {
        if (s is null || p is null) return;
        s.AllComps.Remove(p);
        SupplyChainCache.GetNeedsComp(s)?.RebuildNeedStates();
    }

    /// <summary>Every real resource abundant, plus <paramref name="extra"/> at <paramref name="extraAmount"/>.</summary>
    private static DictionaryStockpile AbundantPlus(ResourceTypeDef extra, double extraAmount)
    {
        Dictionary<ResourceTypeDef, double> amounts = [];
        Dictionary<ResourceTypeDef, double> caps = [];
        foreach (ResourceTypeDef r in FactionCache.AllResourceTypeDefs)
        {
            amounts[r] = 1e9;
            caps[r] = 1e9;
        }
        amounts[extra] = extraAmount;
        caps[extra] = 1e12;
        return new DictionaryStockpile(amounts, caps);
    }

    private static NeedState StateFor(WorldSettlementFC s, string needId) =>
        SCDestructiveTestUtil.RequireNeedsComp(s).NeedStates?.FirstOrDefault(ns => ns.needId == needId);

    [EmpireDestructiveTest("SC.Destructive.Needs")]
    public static void ResolveFair_SharedPile_SurplusMeasuredAgainstFactionWideDemand()
    {
        // Two settlements need 10 and 30 of a resource from one shared pile holding 120. After the
        // draws 80 are left, which is twice the faction-wide demand of 40, so both settlements must
        // see a surplus ratio of 2 (measuring against each settlement's own demand would give 8 and
        // 2.67).
        FactionFC f = DestructiveTestUtil.RequireFaction();
        (WorldSettlementFC a, WorldSettlementFC b) = SCDestructiveTestUtil.EnsureTwoSettlements();
        ResourceTypeDef r = TestResource();
        TestNeedProvider pa = null, pb = null;
        try
        {
            pa = Attach(a, TestNeedProvider.Need("SCTest_SurplusA", r, 10.0));
            pb = Attach(b, TestNeedProvider.Need("SCTest_SurplusB", r, 30.0));
            DictionaryStockpile sp = AbundantPlus(r, 120.0);

            NeedResolver.ResolveSettlementNeedsFair(f, sp);

            NeedState sa = TestAssert.NotNull(StateFor(a, "SCTest_SurplusA"), "First settlement's need was not resolved");
            NeedState sb = TestAssert.NotNull(StateFor(b, "SCTest_SurplusB"), "Second settlement's need was not resolved");
            TestAssert.AreEqual(10.0, sa.fulfilled, 1e-6, "Abundant supply fills the small need");
            TestAssert.AreEqual(30.0, sb.fulfilled, 1e-6, "Abundant supply fills the large need");
            TestAssert.AreEqual(80.0, sp.GetAmount(r), 1e-6, "Only the demanded 40 is drawn from the shared pile");
            TestAssert.AreEqual(2.0, sa.surplusRatio, 1e-6, "Small consumer: 80 left over 40 faction-wide demand");
            TestAssert.AreEqual(2.0, sb.surplusRatio, 1e-6, "Large consumer: 80 left over 40 faction-wide demand");

            TestAssert.AreEqual(1, pa.resolvedCalls, "The first provider is told its result once");
            TestAssert.AreEqual(10.0, pa.lastResolved.Single().fulfilled, 1e-6);
            TestAssert.AreEqual(1, pb.resolvedCalls, "The second provider is told its result once");
            TestAssert.AreEqual(30.0, pb.lastResolved.Single().fulfilled, 1e-6);
        }
        finally
        {
            Detach(a, pa);
            Detach(b, pb);
        }

        DestructiveTestUtil.AssertEmpireInvariants(f, "ResolveFair_SharedSurplus");
    }

    [EmpireDestructiveTest("SC.Destructive.Needs")]
    public static void ResolveFair_ScarcePile_SplitsProportionallyWithNoSurplus()
    {
        // 20 available against 10 + 30 demanded: each need gets half, nothing is left, no surplus.
        FactionFC f = DestructiveTestUtil.RequireFaction();
        (WorldSettlementFC a, WorldSettlementFC b) = SCDestructiveTestUtil.EnsureTwoSettlements();
        ResourceTypeDef r = TestResource();
        TestNeedProvider pa = null, pb = null;
        try
        {
            pa = Attach(a, TestNeedProvider.Need("SCTest_ScarceA", r, 10.0));
            pb = Attach(b, TestNeedProvider.Need("SCTest_ScarceB", r, 30.0));
            DictionaryStockpile sp = AbundantPlus(r, 20.0);

            NeedResolver.ResolveSettlementNeedsFair(f, sp);

            NeedState sa = TestAssert.NotNull(StateFor(a, "SCTest_ScarceA"), "First settlement's need was not resolved");
            NeedState sb = TestAssert.NotNull(StateFor(b, "SCTest_ScarceB"), "Second settlement's need was not resolved");
            TestAssert.AreEqual(5.0, sa.fulfilled, 1e-6, "Half of 10");
            TestAssert.AreEqual(15.0, sb.fulfilled, 1e-6, "Half of 30");
            TestAssert.IsTrue(sa.IsShort && sb.IsShort, "Both needs are genuinely short");
            TestAssert.AreEqual(0.0, sa.surplusRatio, 1e-12, "A short need has no surplus");
            TestAssert.AreEqual(0.0, sb.surplusRatio, 1e-12, "A short need has no surplus");
        }
        finally
        {
            Detach(a, pa);
            Detach(b, pb);
        }

        DestructiveTestUtil.AssertEmpireInvariants(f, "ResolveFair_Scarce");
    }

    [EmpireDestructiveTest("SC.Destructive.Needs")]
    public static void ResolveFair_SupplyExactlyMatchesDemand_NoNeedIsShort()
    {
        // Demands that do not sum exactly in binary: the sequential draws may leave the last need a
        // few ulps under its demand, which must still count as met.
        FactionFC f = DestructiveTestUtil.RequireFaction();
        (WorldSettlementFC a, WorldSettlementFC b) = SCDestructiveTestUtil.EnsureTwoSettlements();
        ResourceTypeDef r = TestResource();
        TestNeedProvider pa = null, pb = null;
        double[] demands = [0.1, 0.7, 0.2, 0.3];
        try
        {
            pa = Attach(a, TestNeedProvider.Need("SCTest_ExactA0", r, demands[0]), TestNeedProvider.Need("SCTest_ExactA1", r, demands[1]));
            pb = Attach(b, TestNeedProvider.Need("SCTest_ExactB0", r, demands[2]), TestNeedProvider.Need("SCTest_ExactB1", r, demands[3]));
            double total = 0;
            foreach (double d in demands) total += d;

            NeedResolver.ResolveSettlementNeedsFair(f, AbundantPlus(r, total));

            foreach (string id in new[] { "SCTest_ExactA0", "SCTest_ExactA1" })
            {
                NeedState ns = TestAssert.NotNull(StateFor(a, id), $"Need {id} was not resolved");
                TestAssert.IsFalse(ns.IsShort, $"Need {id} filled to {ns.fulfilled:R} of {ns.demanded:R} must count as met");
            }
            foreach (string id in new[] { "SCTest_ExactB0", "SCTest_ExactB1" })
            {
                NeedState ns = TestAssert.NotNull(StateFor(b, id), $"Need {id} was not resolved");
                TestAssert.IsFalse(ns.IsShort, $"Need {id} filled to {ns.fulfilled:R} of {ns.demanded:R} must count as met");
            }
        }
        finally
        {
            Detach(a, pa);
            Detach(b, pb);
        }

        DestructiveTestUtil.AssertEmpireInvariants(f, "ResolveFair_Exact");
    }

    [EmpireDestructiveTest("SC.Destructive.Needs")]
    public static void ResolveFair_ThrowingProvider_DoesNotAbortTheRest()
    {
        // One provider throws while collecting and another while being told its result; the rest of
        // the faction's needs must still resolve. Both throws are logged by design.
        FactionFC f = DestructiveTestUtil.RequireFaction();
        (WorldSettlementFC a, WorldSettlementFC b) = SCDestructiveTestUtil.EnsureTwoSettlements();
        ResourceTypeDef r = TestResource();
        TestNeedProvider thrower = null, resolveThrower = null, healthy = null;
        try
        {
            thrower = Attach(a, TestNeedProvider.Need("SCTest_Thrower", r, 5.0));
            thrower.throwOnCollect = true;
            resolveThrower = Attach(a, TestNeedProvider.Need("SCTest_ResolveThrower", r, 5.0));
            resolveThrower.throwOnResolved = true;
            healthy = Attach(b, TestNeedProvider.Need("SCTest_Healthy", r, 5.0));

            TestAssert.DoesNotThrow(() => NeedResolver.ResolveSettlementNeedsFair(f, AbundantPlus(r, 100.0)),
                "A throwing provider must not abort the Simple-mode resolution");

            TestAssert.IsNull(StateFor(a, "SCTest_Thrower"), "The provider that threw while collecting contributes no need");
            TestAssert.NotNull(StateFor(a, "SCTest_ResolveThrower"), "A provider that throws on its result still had its need resolved");
            NeedState hs = TestAssert.NotNull(StateFor(b, "SCTest_Healthy"), "The healthy provider's need must resolve");
            TestAssert.AreEqual(5.0, hs.fulfilled, 1e-6);
            TestAssert.AreEqual(1, healthy.resolvedCalls, "The healthy provider is still told its result");
        }
        finally
        {
            if (a is not null && thrower is not null) a.AllComps.Remove(thrower);
            Detach(a, resolveThrower);
            Detach(b, healthy);
        }

        DestructiveTestUtil.AssertEmpireInvariants(f, "ResolveFair_Throwing");
    }

    [EmpireDestructiveTest("SC.Destructive.Needs")]
    public static void ResolveComplex_OwnPile_SurplusMeasuredAgainstOwnDemand()
    {
        // Complex mode: a settlement draws from its own pile, so the surplus is measured against its
        // own demand. 30 stocked, 10 demanded: 20 left over 10 = 2.
        FactionFC f = DestructiveTestUtil.RequireFaction();
        WorldSettlementFC s = SCDestructiveTestUtil.EnsureSCSettlement();
        WorldObjectComp_SettlementNeeds needsComp = SCDestructiveTestUtil.RequireNeedsComp(s);
        ResourceTypeDef r = TestResource();
        TestNeedProvider p = null;
        try
        {
            p = Attach(s, TestNeedProvider.Need("SCTest_Own", r, 10.0));
            DictionaryStockpile sp = AbundantPlus(r, 30.0);

            NeedResolver.ResolveSettlementNeeds(s, sp, needsComp);

            NeedState ns = TestAssert.NotNull(StateFor(s, "SCTest_Own"), "The provider's need was not resolved");
            TestAssert.AreEqual(10.0, ns.fulfilled, 1e-6);
            TestAssert.AreEqual(2.0, ns.surplusRatio, 1e-6, "20 left over 10 demanded");
            TestAssert.AreEqual(1, p.resolvedCalls, "The provider is told its result once");
        }
        finally
        {
            Detach(s, p);
        }

        DestructiveTestUtil.AssertEmpireInvariants(f, "ResolveComplex_OwnSurplus");
    }

    [EmpireDestructiveTest("SC.Destructive.Needs")]
    public static void RebuildNeedStates_KeepsSurplusRatio()
    {
        // The rebuild that runs on load and on settlement changes must carry the last surplus ratio
        // over, or surplus bonuses drop out until the next daily resolution.
        FactionFC f = DestructiveTestUtil.RequireFaction();
        WorldSettlementFC s = SCDestructiveTestUtil.EnsureSCSettlement();
        WorldObjectComp_SettlementNeeds needsComp = SCDestructiveTestUtil.RequireNeedsComp(s);
        ResourceTypeDef r = TestResource();
        TestNeedProvider p = null;
        try
        {
            p = Attach(s, TestNeedProvider.Need("SCTest_Keep", r, 10.0));
            NeedResolver.ResolveSettlementNeeds(s, AbundantPlus(r, 40.0), needsComp);
            TestAssert.AreEqual(3.0, TestAssert.NotNull(StateFor(s, "SCTest_Keep"), "Need not resolved").surplusRatio, 1e-6);

            needsComp.RebuildNeedStates();

            NeedState rebuilt = TestAssert.NotNull(StateFor(s, "SCTest_Keep"), "Need missing after rebuild");
            TestAssert.AreEqual(3.0, rebuilt.surplusRatio, 1e-6, "Surplus ratio survives the rebuild");
            TestAssert.IsFalse(rebuilt.IsShort, "A met need stays met across the rebuild");
        }
        finally
        {
            Detach(s, p);
        }

        DestructiveTestUtil.AssertEmpireInvariants(f, "RebuildNeedStates_Surplus");
    }
}
