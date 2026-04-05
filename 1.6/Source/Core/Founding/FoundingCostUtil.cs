using RimWorld;
using RimWorld.Planet;
using System.Collections.Generic;

namespace FactionColonies.SupplyChain
{
    public static class FoundingCostUtil
    {
        /// <summary>
        /// Computes the distance-based silver surcharge for founding at <paramref name="targetTile"/>.
        /// </summary>
        public static int ComputeSilverSurcharge(PlanetTile targetTile)
        {
            WorldSettlementFC nearest = FindNearestSettlement(targetTile);
            if (nearest is null) return 0;

            double travelDays = TravelDaysTo(nearest.Tile, targetTile);
            double multiplier = travelDays / SupplyChainSettings.distanceNormalizingDays;
            return (int)(SupplyChainSettings.baseSilverSurcharge * multiplier);
        }

        /// <summary>
        /// Returns the distance multiplier for resource costs at <paramref name="targetTile"/>.
        /// 1.0 means no scaling (no settlements exist). At normalizingDays travel -> 2.0.
        /// </summary>
        public static double ComputeDistanceMultiplier(PlanetTile targetTile)
        {
            WorldSettlementFC nearest = FindNearestSettlement(targetTile);
            if (nearest is null) return 1.0;

            double travelDays = TravelDaysTo(nearest.Tile, targetTile);
            return 1.0 + (travelDays / SupplyChainSettings.distanceNormalizingDays);
        }

        /// <summary>
        /// Finds the nearest empire settlement to <paramref name="targetTile"/> by travel time.
        /// Returns null if no settlements exist.
        /// </summary>
        public static WorldSettlementFC FindNearestSettlement(PlanetTile targetTile)
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
