using HarmonyLib;
using RimWorld.Planet;

namespace FactionColonies.SupplyChain
{
    /// <summary>
    /// Monotonic counter bumped whenever the world road network changes. Supply routes poll this to
    /// know when their cached travel time / path may be stale and needs re-pathfinding.
    /// </summary>
    public static class RouteRoadChangeTracker
    {
        public static int Version;
    }

    /// <summary>
    /// Observes every road addition at the vanilla mutation point, so road changes from ANY source
    /// (Empire's road builder, other road mods, vanilla) invalidate route travel times. OverlayRoad is
    /// the single canonical road-add API and cannot remove roads, so this catches all additions/upgrades.
    /// </summary>
    [HarmonyPatch(typeof(WorldGrid))]
    [HarmonyPatch("OverlayRoad")]
    public static class Patch_WorldGrid_OverlayRoad
    {
        public static void Postfix()
        {
            RouteRoadChangeTracker.Version++;
        }
    }
}
