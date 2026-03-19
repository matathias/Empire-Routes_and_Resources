using System;
using System.Collections.Generic;
using Verse;

namespace FactionColonies.SupplyChain
{
    /// <summary>
    /// Static utility for resolving settlement needs by drawing from stockpiles.
    /// </summary>
    public static class NeedResolver
    {
        /// <summary>
        /// Resolves needs for a single settlement by drawing from the given stockpile.
        /// Used in Complex mode (each settlement draws from its own local stockpile).
        /// </summary>
        public static void ResolveSettlementNeeds(WorldSettlementFC settlement, IStockpile stockpile, WorldObjectComp_SupplyChain comp)
        {
            if (stockpile == null || comp == null) return;

            List<NeedState> states = new List<NeedState>();

            // 1. Base settlement needs
            FactionFC faction = FactionCache.FactionComp;
            foreach (SettlementNeedDef needDef in SupplyChainCache.AllNeedDefs)
            {
                if (faction != null && !needDef.IsActiveForFaction(faction)) continue;
                if (!needDef.IsActiveForSettlement(settlement)) continue;

                double demand = needDef.CalculateDemand(settlement);

                double drawn;
                stockpile.TryDraw(needDef.resource, demand, out drawn);

                states.Add(new NeedState(needDef.defName, needDef.resource, demand, drawn));
            }

            // 2. Building needs
            ResolveBuildingNeeds(settlement, stockpile, states);

            comp.SetNeedStates(states);
            settlement.InvalidateStatCache();
        }

        /// <summary>
        /// Resolves needs for all settlements drawing from a shared faction stockpile.
        /// Distributes proportionally when supply is scarce.
        /// Used in Simple mode.
        /// </summary>
        public static void ResolveSettlementNeedsFair(FactionFC faction, IStockpile stockpile)
        {
            if (stockpile == null) return;

            // Gather all demand per resource across all settlements
            // Key: resource, Value: list of (settlement, comp, needId, demand)
            List<NeedDemandEntry> allDemands = new List<NeedDemandEntry>();

            foreach (WorldSettlementFC settlement in faction.settlements)
            {
                WorldObjectComp_SupplyChain comp = SupplyChainCache.GetSettlementComp(settlement);
                if (comp == null) continue;

                // Base needs
                foreach (SettlementNeedDef needDef in SupplyChainCache.AllNeedDefs)
                {
                    if (!needDef.IsActiveForFaction(faction)) continue;
                    if (!needDef.IsActiveForSettlement(settlement)) continue;

                    double demand = needDef.CalculateDemand(settlement);

                    allDemands.Add(new NeedDemandEntry
                    {
                        settlement = settlement,
                        comp = comp,
                        needId = needDef.defName,
                        resource = needDef.resource,
                        demand = demand
                    });
                }

                // Building needs
                if (settlement.BuildingsComp != null)
                {
                    foreach (BuildingFC building in settlement.BuildingsComp.Buildings)
                    {
                        if (building.def == null || building.def == BuildingFCDefOf.Empty)
                            continue;

                        BuildingNeedExtension ext = SupplyChainCache.GetBuildingNeedExt(building.def);
                        if (ext == null || ext.inputs == null) continue;

                        foreach (BuildingResourceInput input in ext.inputs)
                        {
                            if (input.resource == null || input.amount <= 0) continue;

                            allDemands.Add(new NeedDemandEntry
                            {
                                settlement = settlement,
                                comp = comp,
                                needId = "bldg." + building.def.defName + "." + input.resource.defName,
                                resource = input.resource,
                                demand = input.amount
                            });
                        }
                    }
                }
            }

            // Calculate fill rate per resource
            Dictionary<ResourceTypeDef, double> totalDemandPerResource = new Dictionary<ResourceTypeDef, double>();
            foreach (NeedDemandEntry entry in allDemands)
            {
                double current;
                totalDemandPerResource.TryGetValue(entry.resource, out current);
                totalDemandPerResource[entry.resource] = current + entry.demand;
            }

            Dictionary<ResourceTypeDef, double> fillRates = new Dictionary<ResourceTypeDef, double>();
            foreach (KeyValuePair<ResourceTypeDef, double> kv in totalDemandPerResource)
            {
                double available = stockpile.GetAmount(kv.Key);
                fillRates[kv.Key] = kv.Value > 0 ? Math.Min(1.0, available / kv.Value) : 1.0;
            }

            // Distribute proportionally and draw
            // Group results by settlement
            Dictionary<WorldObjectComp_SupplyChain, List<NeedState>> compStates =
                new Dictionary<WorldObjectComp_SupplyChain, List<NeedState>>();

            foreach (NeedDemandEntry entry in allDemands)
            {
                double fillRate;
                fillRates.TryGetValue(entry.resource, out fillRate);

                double toDraw = entry.demand * fillRate;
                double drawn;
                stockpile.TryDraw(entry.resource, toDraw, out drawn);

                List<NeedState> states;
                if (!compStates.TryGetValue(entry.comp, out states))
                {
                    states = new List<NeedState>();
                    compStates[entry.comp] = states;
                }
                states.Add(new NeedState(entry.needId, entry.resource, entry.demand, drawn));
            }

            // Apply results
            foreach (KeyValuePair<WorldObjectComp_SupplyChain, List<NeedState>> kv in compStates)
            {
                kv.Key.SetNeedStates(kv.Value);
                WorldSettlementFC ws = kv.Key.WorldSettlement;
                if (ws != null)
                    ws.InvalidateStatCache();
            }

            // Settlements with no demands still need cleared states
            foreach (WorldSettlementFC settlement in faction.settlements)
            {
                WorldObjectComp_SupplyChain comp = SupplyChainCache.GetSettlementComp(settlement);
                if (comp == null) continue;
                if (!compStates.ContainsKey(comp))
                {
                    comp.SetNeedStates(new List<NeedState>());
                    settlement.InvalidateStatCache();
                }
            }
        }

        private static void ResolveBuildingNeeds(WorldSettlementFC settlement, IStockpile stockpile, List<NeedState> states)
        {
            if (settlement.BuildingsComp == null) return;

            foreach (BuildingFC building in settlement.BuildingsComp.Buildings)
            {
                if (building.def == null || building.def == BuildingFCDefOf.Empty)
                    continue;

                BuildingNeedExtension ext = SupplyChainCache.GetBuildingNeedExt(building.def);
                if (ext == null || ext.inputs == null) continue;

                foreach (BuildingResourceInput input in ext.inputs)
                {
                    if (input.resource == null || input.amount <= 0) continue;

                    double drawn;
                    stockpile.TryDraw(input.resource, input.amount, out drawn);

                    string needId = "bldg." + building.def.defName + "." + input.resource.defName;
                    states.Add(new NeedState(needId, input.resource, input.amount, drawn));
                }
            }
        }

        private struct NeedDemandEntry
        {
            public WorldSettlementFC settlement;
            public WorldObjectComp_SupplyChain comp;
            public string needId;
            public ResourceTypeDef resource;
            public double demand;
        }
    }
}
