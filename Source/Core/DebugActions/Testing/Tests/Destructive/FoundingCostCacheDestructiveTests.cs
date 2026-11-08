namespace FactionColonies.SupplyChain;

/* DESTRUCTIVE: overwrites FoundingCostUtil's cached nearest settlement to detect whether a query was
   served from the cache, bumps the global road-change counter (every route then re-warms its path),
   and creates and removes transient settlements. The cache is invalidated again at the end of each
   test. Not otherwise reverted. */
public static class FoundingCostCacheDestructiveTests
{
    /// <summary>
    /// Primes the cache at <paramref name="tile"/>, then replaces the cached nearest settlement with
    /// <paramref name="marker"/>. A later query that returns the marker was served from the cache;
    /// one that returns anything else recomputed.
    /// </summary>
    private static void PrimeAndMark(PlanetTile tile, WorldSettlementFC marker)
    {
        FoundingCostUtil.InvalidateCache();
        TestAssert.NotNull(FoundingCostUtil.FindNearestSettlement(tile), "Priming the cache found no nearest settlement");
        FoundingCostUtil.cachedNearest = marker;
    }

    private static WorldSettlementFC RequireSettlementWithTile() =>
        SCDestructiveTestUtil.EnsureSCSettlement(x => x.Tile.Valid);

    [EmpireDestructiveTest("SC.Destructive.Founding")]
    public static void NearestCache_NothingChanged_IsServedFromCache()
    {
        // Control for the tests below: with no change, the marker comes back.
        FactionFC f = DestructiveTestUtil.RequireFaction();
        WorldSettlementFC s = RequireSettlementWithTile();
        try
        {
            PrimeAndMark(s.Tile, null);
            TestAssert.IsNull(FoundingCostUtil.FindNearestSettlement(s.Tile),
                "Same tile, settlement count and road version must reuse the cached result");
        }
        finally
        {
            FoundingCostUtil.InvalidateCache();
        }

        DestructiveTestUtil.AssertEmpireInvariants(f, "NearestCache_NothingChanged");
    }

    [EmpireDestructiveTest("SC.Destructive.Founding")]
    public static void NearestCache_RoadVersionChange_Recomputes()
    {
        FactionFC f = DestructiveTestUtil.RequireFaction();
        WorldSettlementFC s = RequireSettlementWithTile();
        try
        {
            PrimeAndMark(s.Tile, null);
            RouteRoadChangeTracker.Version++;
            TestAssert.NotNull(FoundingCostUtil.FindNearestSettlement(s.Tile),
                "A road change must invalidate the cached nearest settlement and travel time");
        }
        finally
        {
            FoundingCostUtil.InvalidateCache();
        }

        DestructiveTestUtil.AssertEmpireInvariants(f, "NearestCache_RoadVersion");
    }

    [EmpireDestructiveTest("SC.Destructive.Founding")]
    public static void NearestCache_InvalidateCache_Recomputes()
    {
        FactionFC f = DestructiveTestUtil.RequireFaction();
        WorldSettlementFC s = RequireSettlementWithTile();
        try
        {
            PrimeAndMark(s.Tile, null);
            FoundingCostUtil.InvalidateCache();
            TestAssert.NotNull(FoundingCostUtil.FindNearestSettlement(s.Tile), "InvalidateCache must force a recompute");
        }
        finally
        {
            FoundingCostUtil.InvalidateCache();
        }

        DestructiveTestUtil.AssertEmpireInvariants(f, "NearestCache_Invalidate");
    }

    [EmpireDestructiveTest("SC.Destructive.Founding")]
    public static void NearestCache_SettlementCreated_Recomputes()
    {
        FactionFC f = DestructiveTestUtil.RequireFaction();
        WorldSettlementFC s = RequireSettlementWithTile();
        WorldSettlementFC created = null;
        try
        {
            PrimeAndMark(s.Tile, null);
            created = DestructiveTestUtil.RequireTransientSettlement();
            TestAssert.NotNull(FoundingCostUtil.FindNearestSettlement(s.Tile),
                "Founding a settlement must invalidate the cached nearest settlement");
        }
        finally
        {
            DestructiveTestUtil.SafeRemoveSettlement(created);
            FoundingCostUtil.InvalidateCache();
        }

        DestructiveTestUtil.AssertEmpireInvariants(f, "NearestCache_Created");
    }

    [EmpireDestructiveTest("SC.Destructive.Founding")]
    public static void NearestCache_DestroyedCachedSettlement_IsNotReturned()
    {
        // Isolates the Destroyed check: the tile, settlement count and road version all match the
        // cache, but the cached settlement is a destroyed one.
        FactionFC f = DestructiveTestUtil.RequireFaction();
        WorldSettlementFC s = RequireSettlementWithTile();
        WorldSettlementFC doomed = DestructiveTestUtil.RequireTransientSettlement();
        TestAssert.IsTrue(doomed != s, "The transient settlement must differ from the query settlement");
        bool removed = false;
        try
        {
            DestructiveTestUtil.SafeRemoveSettlement(doomed);
            removed = true;
            TestAssert.IsTrue(doomed.Destroyed, $"Removing '{doomed.Name}' did not destroy its world object");

            PrimeAndMark(s.Tile, doomed);
            WorldSettlementFC nearest = FoundingCostUtil.FindNearestSettlement(s.Tile);
            TestAssert.IsTrue(nearest != doomed, "A destroyed cached settlement must never be returned");
            TestAssert.NotNull(nearest, "The recompute must find a live settlement");
            TestAssert.IsFalse(nearest.Destroyed, "The recomputed nearest settlement must be live");
        }
        finally
        {
            if (!removed) DestructiveTestUtil.SafeRemoveSettlement(doomed);
            FoundingCostUtil.InvalidateCache();
        }

        DestructiveTestUtil.AssertEmpireInvariants(f, "NearestCache_Destroyed");
    }
}
