using RimWorld.Planet;
using Verse;

namespace FactionColonies.SupplyChain
{
    /// <summary>
    /// Replaces the base mod's count-based settlement cost scaling with distance-based scaling.
    /// XML-patched onto settlement defs in place of the base <see cref="SettlementTypeExtension"/>.
    /// </summary>
    public class SCSettlementTypeExtension : SettlementTypeExtension
    {
        public override int GetCreationCost()
        {
            int baseCost = (int)FCSettings.silverToCreateSettlement;

            // Only check completed settlements — caravans in transit have no tile to measure distance from
            if (faction is null || faction.settlements.Count == 0)
                return baseCost;

            CreateColonyWindowFc window = Find.WindowStack.WindowOfType<CreateColonyWindowFc>();
            PlanetTile tile = window?.currentTileSelected ?? PlanetTile.Invalid;

            if (!tile.Valid)
                return baseCost;

            return baseCost + FoundingCostUtil.ComputeSilverSurcharge(tile);
        }
    }
}
