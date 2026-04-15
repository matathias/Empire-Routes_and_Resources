using System;
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
    /// One entry in a SettlementNeedResourceInput.weightsByTech list.
    /// </summary>
    public class TechLevelWeight
    {
        public TechLevel level;
        public float weight;
    }

    /// <summary>
    /// One resource entry inside a SettlementNeedDef. Optional weightsByTech lets a need
    /// shift its consumption mix as the faction's tech level changes (e.g. wood -> metal).
    /// </summary>
    public class SettlementNeedResourceInput
    {
        public ResourceTypeDef resource;
        public float weight = 1f;
        public List<TechLevelWeight> weightsByTech;

        public float GetWeightAt(TechLevel tech)
        {
            if (weightsByTech is null || weightsByTech.Count == 0)
                return weight;

            // Exact match
            foreach (TechLevelWeight t in weightsByTech)
            {
                if (t.level == tech) return t.weight;
            }

            // Fall back to the nearest lower defined key.
            float fallback = -1f;
            TechLevel bestKey = TechLevel.Undefined;
            foreach (TechLevelWeight t in weightsByTech)
            {
                if (t.level <= tech && t.level > bestKey)
                {
                    bestKey = t.level;
                    fallback = t.weight;
                }
            }
            if (fallback >= 0f) return fallback;

            // No lower key — use the smallest defined key as a floor.
            TechLevel smallestKey = TechLevel.Archotech;
            float smallestVal = weight;
            foreach (TechLevelWeight t in weightsByTech)
            {
                if (t.level < smallestKey)
                {
                    smallestKey = t.level;
                    smallestVal = t.weight;
                }
            }
            return smallestVal;
        }
    }

    /// <summary>
    /// Defines a base settlement need that consumes resources from the stockpile each tax period.
    /// Unmet needs apply stat penalties proportional to the satisfaction deficit. A need can
    /// draw from multiple resources at once with tech-level-weighted splits.
    /// </summary>
    public class SettlementNeedDef : Def
    {
        public List<SettlementNeedResourceInput> resources;
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
            return minTechLevel == TechLevel.Undefined || faction?.techLevel >= minTechLevel;
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
                    double pop = SupplyChainSettings.useMaxWorkersForNeeds
                        ? Math.Max(settlement.workers, settlement.workersMax)
                        : settlement.workers;
                    return baseAmount * pop;
                case NeedScaling.PerLevel:
                    return baseAmount * settlement.settlementLevel;
                default:
                    return baseAmount;
            }
        }

        /// <summary>
        /// Returns the (resource, fraction) split for the given tech level, normalized to sum to 1.0.
        /// Zero-weight entries are dropped. Defensive fallback returns the first resource at fraction 1.0
        /// if all weights are zero.
        /// </summary>
        public List<KeyValuePair<ResourceTypeDef, double>> GetResourceSplit(TechLevel tech)
        {
            List<KeyValuePair<ResourceTypeDef, double>> result = new List<KeyValuePair<ResourceTypeDef, double>>();
            if (resources is null || resources.Count == 0) return result;

            double total = 0.0;
            List<double> weights = new List<double>(resources.Count);
            foreach (SettlementNeedResourceInput entry in resources)
            {
                if (entry?.resource is null) { weights.Add(0.0); continue; }
                double w = entry.GetWeightAt(tech);
                if (w < 0.0) w = 0.0;
                weights.Add(w);
                total += w;
            }

            if (total <= 0.0)
            {
                foreach (SettlementNeedResourceInput t in resources)
                {
                    if (t?.resource != null)
                    {
                        result.Add(new KeyValuePair<ResourceTypeDef, double>(t.resource, 1.0));
                        return result;
                    }
                }

                return result;
            }

            for (int i = 0; i < resources.Count; i++)
            {
                if (weights[i] <= 0.0 || resources[i]?.resource is null) continue;
                result.Add(new KeyValuePair<ResourceTypeDef, double>(resources[i].resource, weights[i] / total));
            }
            return result;
        }

        /// <summary>
        /// Returns this need's fraction of total demand assigned to the given resource at the given tech level.
        /// 0.0 if the need does not consume the resource at this tech.
        /// </summary>
        public double GetResourceFraction(TechLevel tech, ResourceTypeDef r)
        {
            List<KeyValuePair<ResourceTypeDef, double>> split = GetResourceSplit(tech);
            foreach (KeyValuePair<ResourceTypeDef, double> t in split)
            {
                if (t.Key == r) return t.Value;
            }
            return 0.0;
        }

        public bool UsesResource(ResourceTypeDef r)
        {
            if (resources is null) return false;
            foreach (SettlementNeedResourceInput t in resources)
            {
                if (t?.resource == r) return true;
            }
            return false;
        }

        /// <summary>
        /// Builds NeedState entries for this need, splitting demand across resources according
        /// to the faction's tech level. Used by both NeedResolver and WorldObjectComp_SupplyChain.
        /// Surplus bonuses are scaled per-fraction so the total max bonus across sub-states equals
        /// the original maxBonus.
        /// </summary>
        public void BuildNeedStates(WorldSettlementFC settlement, FactionFC faction, double prevFulfilledFallback,
            Action<NeedState> emit)
        {
            double demand = CalculateDemand(settlement);
            TechLevel tech = faction?.techLevel ?? TechLevel.Undefined;
            List<KeyValuePair<ResourceTypeDef, double>> split = GetResourceSplit(tech);
            if (split.Count == 0) return;

            bool single = split.Count == 1;
            for (int i = 0; i < split.Count; i++)
            {
                ResourceTypeDef res = split[i].Key;
                double fraction = split[i].Value;
                double subDemand = demand * fraction;

                string needId = single ? defName : (defName + "." + res.defName);
                string subLabel = single
                    ? label.CapitalizeFirst()
                    : (label.CapitalizeFirst() + " - " + res.label.CapitalizeFirst());

                List<NeedSurplusBonus> scaledBonuses = null;
                if (surplusBonuses != null && surplusBonuses.Count > 0)
                {
                    if (single)
                    {
                        scaledBonuses = surplusBonuses;
                    }
                    else
                    {
                        scaledBonuses = new List<NeedSurplusBonus>(surplusBonuses.Count);
                        for (int b = 0; b < surplusBonuses.Count; b++)
                        {
                            NeedSurplusBonus src = surplusBonuses[b];
                            scaledBonuses.Add(new NeedSurplusBonus
                            {
                                stat = src.stat,
                                maxBonus = src.maxBonus * fraction,
                                label = src.label
                            });
                        }
                    }
                }

                NeedState ns = new NeedState(needId, res, subDemand, prevFulfilledFallback,
                    subLabel, NeedCategory.Base, penalties,
                    scaledBonuses, maxSurplusRatio, this);
                emit(ns);
            }
        }

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string e in base.ConfigErrors())
                yield return e;
            if (resources is null || resources.Count == 0)
            {
                yield return "SettlementNeedDef " + defName + " has no resources";
            }
            else
            {
                bool anyValid = false;
                bool anyNonzero = false;
                for (int i = 0; i < resources.Count; i++)
                {
                    SettlementNeedResourceInput entry = resources[i];
                    if (entry?.resource is null)
                    {
                        yield return "SettlementNeedDef " + defName + " resource entry " + i + " is null";
                        continue;
                    }
                    anyValid = true;
                    if (entry.weightsByTech != null && entry.weightsByTech.Count > 0)
                    {
                        foreach (var t in entry.weightsByTech)
                            if (t.weight > 0f) { anyNonzero = true; break; }
                    }
                    else if (entry.weight > 0f)
                    {
                        anyNonzero = true;
                    }
                }
                if (anyValid && !anyNonzero)
                    yield return "SettlementNeedDef " + defName + " has no nonzero resource weight at any tech level";
            }
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
