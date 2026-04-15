using RimWorld.Planet;
using System.Collections.Generic;
using System.Text;
using Verse;

namespace FactionColonies.SupplyChain
{
    /// <summary>
    /// Validates resource availability for settlement founding and consumes resources on success.
    /// Silver cost is handled separately by <see cref="SCSettlementTypeExtension"/>.
    /// </summary>
    public class FoundingCostValidator : ISettlementFoundingValidator
    {
        private readonly WorldComponent_SupplyChain worldComp;

        /// <summary>
        /// The settlement the player has chosen as a resource source (Complex mode).
        /// Null means auto-select nearest.
        /// </summary>
        public WorldSettlementFC SelectedSource;

        /// <summary>
        /// True when the player has explicitly picked a source settlement.
        /// When false, <see cref="SelectedSource"/> auto-follows the nearest settlement.
        /// </summary>
        public bool UserSelectedSource;

        public FoundingCostValidator(WorldComponent_SupplyChain worldComp)
        {
            this.worldComp = worldComp;
        }

        public bool CanFoundSettlement(PlanetTile tile, WorldSettlementDef type, out string reason)
        {
            reason = null;

            List<FCResourceCost> costs = FoundingCostUtil.GetFoundingResourceCosts(type);
            if (costs is null || costs.Count == 0) return true;
            if (IsBelowThreshold()) return true;

            StringBuilder sb = null;
            double distMult = FoundingCostUtil.ComputeDistanceMultiplier(tile);
            IStockpile stockpile = GetStockpile(tile);
            if (stockpile is null)
            {
                sb = new StringBuilder();
                sb.Append("SC_NoStockpile".Translate());
                reason = sb.ToString();
                return false;
            }

            bool allowed = true;

            foreach (FCResourceCost entry in costs)
            {
                double needed = FormulaUtil.ResourceCost(entry.amount, distMult);
                double have = stockpile.GetAmount(entry.resource);

                if (have < needed)
                {
                    if (sb is null) sb = new StringBuilder();
                    else sb.AppendLine();
                    sb.Append("SC_FoundingCostInsufficient".Translate(
                        entry.resource.LabelCap,
                        have.ToString("F0"),
                        needed.ToString("F0"),
                        GetSourceName(tile)));
                    allowed = false;
                }
            }

            if (!allowed)
                reason = sb.ToString();
            return allowed;
        }

        public string GetAdditionalCostDescription(PlanetTile tile, WorldSettlementDef type)
        {
            List<FCResourceCost> costs = FoundingCostUtil.GetFoundingResourceCosts(type);
            if (costs is null || costs.Count == 0) return null;
            if (IsBelowThreshold()) return null;

            double distMult = FoundingCostUtil.ComputeDistanceMultiplier(tile);

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < costs.Count; i++)
            {
                FCResourceCost entry = costs[i];
                double needed = FormulaUtil.ResourceCost(entry.amount, distMult);
                if (i > 0) sb.Append(", ");
                sb.Append(needed.ToString("F0"));
                sb.Append(" ");
                sb.Append(entry.resource.LabelCap);
            }

            return "SC_FoundingCostFrom".Translate(sb.ToString(), GetSourceName(tile));
        }

        public void OnSettlementFounded(PlanetTile tile, WorldSettlementDef type)
        {
            List<FCResourceCost> costs = FoundingCostUtil.GetFoundingResourceCosts(type);
            if (costs is null || costs.Count == 0) return;
            if (IsBelowThreshold()) return;

            double distMult = FoundingCostUtil.ComputeDistanceMultiplier(tile);
            IStockpile stockpile = GetStockpile(tile);
            if (stockpile is null) return;

            foreach (FCResourceCost entry in costs)
            {
                double needed = FormulaUtil.ResourceCost(entry.amount, distMult);
                stockpile.TryDraw(entry.resource, needed, out _);
            }
        }

        /// <summary>
        /// Resolves the effective source settlement for Complex mode.
        /// Returns <see cref="SelectedSource"/> if set, otherwise nearest.
        /// </summary>
        public WorldSettlementFC GetEffectiveSource(PlanetTile tile)
        {
            if (UserSelectedSource && SelectedSource != null) return SelectedSource;
            return FoundingCostUtil.FindNearestSettlement(tile);
        }

        /// <summary>
        /// Resets user source selection (called when companion window opens).
        /// </summary>
        public void ResetSourceSelection()
        {
            SelectedSource = null;
            UserSelectedSource = false;
        }

        private bool IsBelowThreshold()
        {
            FactionFC faction = FactionCache.FactionComp;
            if (faction is null) return true;
            int count = faction.settlements.Count + faction.settlementCaravansList.Count;
            return count < SupplyChainSettings.freeSettlementThreshold;
        }

        private IStockpile GetStockpile(PlanetTile tile)
        {
            if (worldComp.Mode == SupplyChainMode.Simple)
                return worldComp.Stockpile;

            WorldSettlementFC source = GetEffectiveSource(tile);
            if (source is null) return null;

            WorldObjectComp_SupplyChain comp = SupplyChainCache.GetSettlementComp(source);
            return comp?.GetStockpile();
        }

        private string GetSourceName(PlanetTile tile)
        {
            if (worldComp.Mode == SupplyChainMode.Simple)
                return "SC_FoundingCostFactionStockpile".Translate();

            WorldSettlementFC source = GetEffectiveSource(tile);
            return source?.Name ?? "SC_FoundingCostFactionStockpile".Translate();
        }
    }
}
