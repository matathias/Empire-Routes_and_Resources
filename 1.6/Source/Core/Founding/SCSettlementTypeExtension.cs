using System.Collections.Generic;
using RimWorld.Planet;
using Verse;

namespace FactionColonies.SupplyChain
{
    public class FCResourceCost
    {
        public ResourceTypeDef resource;
        public double amount;
    }

    /// <summary>
    /// Replaces the base mod's count-based settlement cost scaling with distance-based scaling.
    /// XML-patched onto settlement defs in place of the base <see cref="SettlementTypeExtension"/>.
    /// </summary>
    public class SCSettlementTypeExtension : SettlementTypeExtension
    {
        public List<FCResourceCost> foundingResourceCosts = new List<FCResourceCost>();

        public virtual List<FCResourceCost> GetFoundingResourceCosts()
        {
            return foundingResourceCosts;
        }

        public override int GetCreationCost()
        {
            int baseCost = (int)FCSettings.silverToCreateSettlement;

            if (faction is null)
                return baseCost;

            CreateColonyWindowFc window = Find.WindowStack.WindowOfType<CreateColonyWindowFc>();
            PlanetTile tile = window?.currentTileSelected ?? PlanetTile.Invalid;

            if (!tile.Valid)
                return baseCost;

            // Distance measured from nearest settlement, or player's capital if none exist
            return baseCost + FoundingCostUtil.ComputeSilverSurcharge(tile);
        }

        public override int GetCreationTime(PlanetTile destination)
        {
            WorldSettlementFC nearest = FoundingCostUtil.FindNearestSettlement(destination);
            PlanetTile origin = FoundingCostUtil.GetSourceTile(nearest);

            return TravelUtil.ReturnTicksToArrive(origin, destination);
        }
    }
}
