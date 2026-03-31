using System.Collections.Generic;
using RimWorld;
using Verse;

namespace FactionColonies.SupplyChain
{
    public enum NeedScaling : byte
    {
        Flat,
        PerWorker,
        PerLevel
    }

    public class NeedPenalty
    {
        public FCStatDef stat;
        public double penaltyPerUnit;
        public string label;
    }

    public class NeedSurplusBonus
    {
        public FCStatDef stat;
        public double maxBonus;
        public string label;
    }

    /// <summary>
    /// Defines a base settlement need that consumes resources from the stockpile each tax period.
    /// Unmet needs apply stat penalties proportional to the satisfaction deficit.
    /// </summary>
    public class SettlementNeedDef : Def
    {
        public ResourceTypeDef resource;
        public double baseAmount;
        public NeedScaling scaling = NeedScaling.Flat;
        public TechLevel minTechLevel = TechLevel.Undefined;
        public List<WorldSettlementDef> allowedSettlementTypes;
        public List<WorldSettlementDef> blockedSettlementTypes;
        public List<NeedPenalty> penalties;
        public List<NeedSurplusBonus> surplusBonuses;
        public double maxSurplusRatio = 2.0;

        public bool IsActiveForFaction(FactionFC faction)
        {
            return minTechLevel == TechLevel.Undefined || faction.techLevel >= minTechLevel;
        }

        public bool IsActiveForSettlement(WorldSettlementFC settlement)
        {
            bool allowed = allowedSettlementTypes is null || allowedSettlementTypes.Count == 0
                || settlement.settlementDef.IsInList(allowedSettlementTypes);
            bool blocked = settlement.settlementDef.IsInList(blockedSettlementTypes);

            return allowed && !blocked;
        }

        public double CalculateDemand(WorldSettlementFC settlement)
        {
            switch (scaling)
            {
                case NeedScaling.PerWorker:
                    return baseAmount * settlement.workers;
                case NeedScaling.PerLevel:
                    return baseAmount * settlement.settlementLevel;
                default:
                    return baseAmount;
            }
        }

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string e in base.ConfigErrors())
                yield return e;
            if (resource == null)
                yield return "SettlementNeedDef " + defName + " has no resource";
            if (penalties == null || penalties.Count == 0)
                yield return "SettlementNeedDef " + defName + " has no penalties";
            else
            {
                for (int i = 0; i < penalties.Count; i++)
                {
                    if (penalties[i].stat == null)
                        yield return "SettlementNeedDef " + defName + " penalty " + i + " has null stat";
                }
            }
            if (surplusBonuses != null)
            {
                for (int i = 0; i < surplusBonuses.Count; i++)
                {
                    if (surplusBonuses[i].stat == null)
                        yield return "SettlementNeedDef " + defName + " surplusBonus " + i + " has null stat";
                }
            }
        }
    }
}
