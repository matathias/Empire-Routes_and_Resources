using RimWorld;
using RimWorld.Planet;
using System.Collections.Generic;
using Verse;

namespace FactionColonies.SupplyChain
{
    public static class FoundingCostUtil
    {
        private static PlanetTile cachedTile = PlanetTile.Invalid;
        private static WorldSettlementFC cachedNearest;
        private static double cachedTravelDays;
        private static int cachedSettlementCount;

        /// <summary>
        /// Computes the distance-based silver surcharge for founding at <paramref name="targetTile"/>.
        /// </summary>
        public static int ComputeSilverSurcharge(PlanetTile targetTile)
        {
            EnsureCacheValid(targetTile);
            if (cachedTravelDays <= 0) return 0;

            double multiplier = cachedTravelDays / SupplyChainSettings.distanceNormalizingDays;
            return (int)(SupplyChainSettings.baseSilverSurcharge * multiplier);
        }

        /// <summary>
        /// Returns the distance multiplier for resource costs at <paramref name="targetTile"/>.
        /// 1.0 means no scaling (no origin available). At normalizingDays travel -> 2.0.
        /// </summary>
        public static double ComputeDistanceMultiplier(PlanetTile targetTile)
        {
            EnsureCacheValid(targetTile);
            if (cachedTravelDays <= 0) return 1.0;

            return 1.0 + (cachedTravelDays / SupplyChainSettings.distanceNormalizingDays);
        }

        /// <summary>
        /// Finds the nearest empire settlement to <paramref name="targetTile"/> by travel time.
        /// Returns null if no settlements exist. Result is cached per tile.
        /// </summary>
        public static WorldSettlementFC FindNearestSettlement(PlanetTile targetTile)
        {
            EnsureCacheValid(targetTile);
            return cachedNearest;
        }

        /// <summary>
        /// Invalidates the distance cache. Call when settlements are created or removed.
        /// </summary>
        public static void InvalidateCache()
        {
            cachedTile = PlanetTile.Invalid;
            cachedNearest = null;
        }

        private static void EnsureCacheValid(PlanetTile targetTile)
        {
            int currentCount = FactionCache.FactionComp?.settlements?.Count ?? 0;

            if (targetTile == cachedTile && currentCount == cachedSettlementCount)
                return;

            cachedTile = targetTile;
            cachedSettlementCount = currentCount;
            cachedNearest = FindNearestSettlementUncached(targetTile);

            // Use nearest settlement tile, or fall back to player's capital
            PlanetTile originTile = GetSourceTile(cachedNearest);

            cachedTravelDays = originTile.Valid
                ? TravelDaysTo(originTile, targetTile)
                : 0.0;
        }

        public static PlanetTile GetSourceTile(WorldSettlementFC settlement)
        {
            PlanetTile tile = settlement?.Tile ?? FactionCache.FactionComp?.capitalLocation ?? PlanetTile.Invalid;
            if (tile == PlanetTile.Invalid)
                tile = Find.AnyPlayerHomeMap.Tile;

            return tile;
        }

        private static WorldSettlementFC FindNearestSettlementUncached(PlanetTile targetTile)
        {
            List<WorldSettlementFC> settlements = FactionCache.FactionComp?.settlements;
            if (settlements is null || settlements.Count == 0) return null;

            WorldSettlementFC nearest = null;
            int bestTicks = int.MaxValue;

            for (int i = 0; i < settlements.Count; i++)
            {
                WorldSettlementFC s = settlements[i];
                if (s is null || !s.Tile.Valid) continue;

                int ticks = TravelUtil.ReturnTicksToArrive(s.Tile, targetTile);
                if (ticks < bestTicks)
                {
                    bestTicks = ticks;
                    nearest = s;
                }
            }

            return nearest;
        }

        private static double TravelDaysTo(PlanetTile from, PlanetTile to)
        {
            return TravelUtil.ReturnTicksToArrive(from, to) / (double)GenDate.TicksPerDay;
        }
    }
}
