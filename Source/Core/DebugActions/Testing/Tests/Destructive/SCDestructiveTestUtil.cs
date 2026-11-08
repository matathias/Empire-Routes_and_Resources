namespace FactionColonies.SupplyChain;

/// <summary>
/// SupplyChain-specific helpers for the destructive test tier. Builds on the base mod's
/// <see cref="DestructiveTestUtil"/> (forced preconditions, transient-settlement create/teardown,
/// invariant battery) and adds the fixtures the Routes &amp; Resources destructive tests share.
/// Like the base helpers, these FORCE missing state into existence and fail the test when that is
/// impossible; they never skip.
/// </summary>
public static class SCDestructiveTestUtil
{
    /*-*-*-*-*/
    /* Components and defs */
    /*-*-*-*-*/

    /// <summary>The SupplyChain world component. RimWorld instantiates every WorldComponent subclass
    /// for each world, so a null here in a loaded game is a real failure.</summary>
    public static WorldComponent_SupplyChain RequireWorldComp() =>
        TestAssert.NotNull(SupplyChainCache.Comp, "No SupplyChain world component in a loaded game");

    /// <summary>The settlement's SupplyChain data comp (injected into every settlement def by this mod).</summary>
    public static WorldObjectComp_SupplyChain RequireSettlementComp(WorldSettlementFC s) =>
        TestAssert.NotNull(SupplyChainCache.GetSettlementComp(s), $"Settlement '{s?.Name}' has no SupplyChain comp");

    /// <summary>The settlement's needs comp (injected into every settlement def by this mod).</summary>
    public static WorldObjectComp_SettlementNeeds RequireNeedsComp(WorldSettlementFC s) =>
        TestAssert.NotNull(SupplyChainCache.GetNeedsComp(s), $"Settlement '{s?.Name}' has no settlement-needs comp");

    /// <summary>The first resource type def. The base mod ships resource types, so none is a failure.</summary>
    public static ResourceTypeDef RequireAnyResource() =>
        TestAssert.NotNull(FactionCache.AllResourceTypeDefs.FirstOrDefault(), "No resource types defined");

    /// <summary>The first non-pool resource type def (the base mod ships several).</summary>
    public static ResourceTypeDef RequireNonPoolResource() =>
        TestAssert.NotNull(FactionCache.AllResourceTypeDefs.FirstOrDefault(def => !def.isPoolResource),
            "No non-pool resource types defined");

    /// <summary>The first pool resource type def (the base mod ships research and power).</summary>
    public static ResourceTypeDef RequirePoolResource() =>
        TestAssert.NotNull(FactionCache.AllResourceTypeDefs.FirstOrDefault(def => def.isPoolResource),
            "No pool resource types defined");

    /*-*-*-*-*/
    /* Settlements */
    /*-*-*-*-*/

    private static bool HasSCComps(WorldSettlementFC s) =>
        SupplyChainCache.GetSettlementComp(s) is not null && SupplyChainCache.GetNeedsComp(s) is not null;

    /// <summary>
    /// The first settlement carrying the SupplyChain comps that also matches <paramref name="match"/>,
    /// creating a transient one when none does. A new settlement that still fails the predicate fails
    /// the test.
    /// </summary>
    public static WorldSettlementFC EnsureSCSettlement(Predicate<WorldSettlementFC> match = null) =>
        DestructiveTestUtil.EnsureSettlement(s => HasSCComps(s) && (match is null || match(s)));

    /// <summary>A settlement that tracks at least one resource matching <paramref name="filter"/>,
    /// returned together with the first such resource.</summary>
    public static WorldSettlementFC EnsureSettlementTracking(Predicate<ResourceTypeDef> filter, out ResourceTypeDef resource)
    {
        WorldSettlementFC s = EnsureSCSettlement(x => FirstTracked(x, filter) is not null);
        resource = FirstTracked(s, filter);
        return s;
    }

    private static ResourceTypeDef FirstTracked(WorldSettlementFC s, Predicate<ResourceTypeDef> filter) =>
        s.Resources?.FirstOrDefault(rfc => rfc?.def is not null && filter(rfc.def))?.def;

    /// <summary>Two distinct settlements carrying the SupplyChain comps, creating transient ones to
    /// make up any shortfall.</summary>
    public static (WorldSettlementFC src, WorldSettlementFC dst) EnsureTwoSettlements()
    {
        FactionFC f = DestructiveTestUtil.RequireFaction();
        List<WorldSettlementFC> found = f.settlements.Where(s => s is not null && HasSCComps(s)).Take(2).ToList();
        while (found.Count < 2)
        {
            WorldSettlementFC created = DestructiveTestUtil.RequireTransientSettlement();
            TestAssert.IsTrue(HasSCComps(created), $"New settlement '{created.Name}' has no SupplyChain comps");
            found.Add(created);
        }
        TestAssert.IsTrue(found[0] != found[1], "EnsureTwoSettlements returned the same settlement twice");
        return (found[0], found[1]);
    }

    /// <summary>
    /// Two distinct settlements where <c>dst</c> is NOT yet a trade-network out-partner of <c>src</c>.
    /// When every existing pair is already partnered, a fresh transient settlement (which has no
    /// routes yet) is paired with the first one.
    /// </summary>
    public static (WorldSettlementFC src, WorldSettlementFC dst) EnsureUnpartneredPair()
    {
        EnsureTwoSettlements();
        List<WorldSettlementFC> all = DestructiveTestUtil.RequireFaction().settlements
            .Where(s => s is not null && HasSCComps(s)).ToList();

        foreach (WorldSettlementFC a in all)
        {
            WorldObjectComp_SupplyChain aComp = SupplyChainCache.GetSettlementComp(a);
            foreach (WorldSettlementFC b in all)
                if (a != b && !aComp.OutPartners.Contains(b)) return (a, b);
        }

        WorldSettlementFC fresh = DestructiveTestUtil.RequireTransientSettlement();
        RequireSettlementComp(fresh);
        return (all[0], fresh);
    }

    /*-*-*-*-*/
    /* Stockpiles and mode */
    /*-*-*-*-*/

    /// <summary>
    /// The live stockpile a settlement draws from: the shared faction pile in Simple mode, the
    /// settlement's local pile (initialized on demand) in Complex mode. Caps are refreshed first.
    /// </summary>
    public static IStockpile LiveStockpileFor(WorldComponent_SupplyChain wc, WorldSettlementFC s)
    {
        wc.EnsureCapsAndStockpiles();
        IStockpile sp = wc.Mode == SupplyChainMode.Simple
            ? wc.Stockpile
            : RequireSettlementComp(s).EnsureLocalStockpile();
        return TestAssert.NotNull(sp, $"No live {wc.Mode} stockpile for settlement '{s.Name}'");
    }

    /// <summary>
    /// Draws <paramref name="r"/> down to at most half its cap in <paramref name="sp"/> so a credit
    /// is observable. Fails if the pile has no positive cap for the resource.
    /// </summary>
    public static void EnsureHeadroom(IStockpile sp, ResourceTypeDef r)
    {
        double cap = sp.GetCap(r);
        TestAssert.GreaterThan(cap, 0.0, $"Stockpile has no positive cap for {r.defName}");
        double excess = sp.GetAmount(r) - cap * 0.5;
        if (excess > 0) sp.TryDraw(r, excess, out _);
    }

    /// <summary>
    /// Switches the faction-wide supply-chain mode to <paramref name="mode"/> and returns the
    /// previous mode, so the caller can restore it with <see cref="RestoreMode"/> in a finally block.
    /// A switch redistributes every settlement's stockpiles.
    /// </summary>
    public static SupplyChainMode ForceMode(WorldComponent_SupplyChain wc, SupplyChainMode mode)
    {
        SupplyChainMode original = wc.Mode;
        if (wc.Mode != mode) wc.SwitchMode(mode);
        TestAssert.IsTrue(wc.Mode == mode, $"Could not switch the supply-chain mode to {mode}");
        return original;
    }

    /// <summary>Switches back to <paramref name="original"/> if a test changed the mode.</summary>
    public static void RestoreMode(WorldComponent_SupplyChain wc, SupplyChainMode original)
    {
        if (wc.Mode != original) wc.SwitchMode(original);
    }

    /// <summary>
    /// The usual Complex-mode local-stockpile fixture: a settlement's comp, its local stockpile, and
    /// the first non-pool resource with its (positive) local cap. Local caps start at localCapBase,
    /// which the settings UI keeps at 10 or more, so a non-positive cap is a failure.
    /// </summary>
    public static IStockpile RequireLocalStockpile(WorldSettlementFC s, out WorldObjectComp_SupplyChain sc,
        out ResourceTypeDef r, out double cap)
    {
        sc = RequireSettlementComp(s);
        r = RequireNonPoolResource();
        IStockpile sp = TestAssert.NotNull(sc.EnsureLocalStockpile(), $"No local stockpile for '{s.Name}'");
        cap = sp.GetCap(r);
        TestAssert.GreaterThan(cap, 0.0, $"Local cap for {r.defName} at '{s.Name}' must be positive");
        return sp;
    }

    /// <summary>
    /// A stockpile seeded with every resource type at a very large amount and cap — used to
    /// drive need resolution into the "fully supplied" regime.
    /// </summary>
    public static DictionaryStockpile AbundantStockpile()
    {
        var amounts = new Dictionary<ResourceTypeDef, double>();
        var caps = new Dictionary<ResourceTypeDef, double>();
        foreach (ResourceTypeDef r in FactionCache.AllResourceTypeDefs)
        {
            amounts[r] = 1e9;
            caps[r] = 1e9;
        }
        return new DictionaryStockpile(amounts, caps);
    }

    /*-*-*-*-*/
    /* Buildings */
    /*-*-*-*-*/

    private static bool HasPositiveInput(BuildingNeedExtension ext) =>
        ext?.inputs is not null && ext.inputs.Any(i => i.resource is not null && i.amount > 0);

    private static bool IsInputBuilding(BuildingFC b) =>
        b?.def is not null && b.def != BuildingFCDefOf.Empty && b.def != BuildingFCDefOf.Construction
        && HasPositiveInput(SupplyChainCache.GetBuildingNeedExt(b.def));

    /// <summary>
    /// A built settlement building that consumes at least one resource input and is the ONLY such
    /// building on its settlement, so a dormancy pass over the settlement draws inputs for it alone.
    /// Uses an existing one when any settlement qualifies; otherwise instantly constructs the first
    /// input-consuming <see cref="BuildingFCDef"/> (this mod ships several) on a settlement with no
    /// input buildings (creating a transient one if needed), into an empty slot or over slot 0.
    /// </summary>
    public static WorldSettlementFC EnsureInputBuilding(out int slot, out BuildingNeedExtension ext)
    {
        FactionFC f = DestructiveTestUtil.RequireFaction();
        foreach (WorldSettlementFC s in f.settlements)
        {
            List<BuildingFC> buildings = s?.BuildingsComp?.Buildings;
            if (buildings is null) continue;
            List<int> inputSlots = [.. Enumerable.Range(0, buildings.Count).Where(i => IsInputBuilding(buildings[i]))];
            if (inputSlots.Count != 1) continue;
            slot = inputSlots[0];
            ext = SupplyChainCache.GetBuildingNeedExt(buildings[slot].def);
            return s;
        }

        BuildingFCDef inputDef = TestAssert.NotNull(
            DefDatabase<BuildingFCDef>.AllDefsListForReading.FirstOrDefault(d =>
                d != BuildingFCDefOf.Empty && d != BuildingFCDefOf.Construction
                && HasPositiveInput(SupplyChainCache.GetBuildingNeedExt(d))),
            "No BuildingFCDef consumes a resource input");

        WorldSettlementFC target = EnsureSCSettlement(x =>
            x.BuildingsComp?.Buildings?.Count > 0 && !x.BuildingsComp.Buildings.Any(IsInputBuilding));
        WorldObjectComp_SettlementBuildings bComp = target.BuildingsComp;
        int usable = Math.Min(bComp.Buildings.Count, Math.Max(1, bComp.NumBuildingSlots));
        slot = 0;
        for (int i = 0; i < usable; i++)
        {
            if (bComp.BuildingSlotIsEmpty(i)) { slot = i; break; }
        }

        bComp.ConstructBuilding(inputDef, slot);
        TestAssert.IsTrue(bComp.Buildings[slot].def == inputDef,
            $"Constructing {inputDef.defName} in slot {slot} of '{target.Name}' did not take");
        ext = SupplyChainCache.GetBuildingNeedExt(inputDef);
        return target;
    }

    /*-*-*-*-*/
    /* Assertions */
    /*-*-*-*-*/

    /// <summary>
    /// Asserts every live stockpile amount is non-negative (a small -0.001 tolerance absorbs float
    /// noise). In Simple mode there is one shared faction stockpile; in Complex mode each settlement
    /// has its own. Shared by the daily-accrual and tax-cycle destructive tests, which both run real
    /// consume passes that must never drive a stockpile below zero.
    /// </summary>
    public static void AssertStockpilesNonNegative(FactionFC f, WorldComponent_SupplyChain comp, string ctx)
    {
        if (comp.Mode == SupplyChainMode.Simple)
        {
            AssertOneStockpileNonNegative(comp.Stockpile, ctx + ":Faction");
            return;
        }
        foreach (WorldSettlementFC s in f.settlements)
        {
            WorldObjectComp_SupplyChain sc = SupplyChainCache.GetSettlementComp(s);
            if (sc is null) continue;
            AssertOneStockpileNonNegative(sc.GetStockpile(), ctx + ":" + s.Name);
        }
    }

    private static void AssertOneStockpileNonNegative(IStockpile sp, string ctx)
    {
        if (sp is null) return;
        foreach (ResourceTypeDef r in FactionCache.AllResourceTypeDefs)
            TestAssert.GreaterThan(sp.GetAmount(r), -0.001, ctx + ": negative stockpile amount for " + r.defName);
    }
}
