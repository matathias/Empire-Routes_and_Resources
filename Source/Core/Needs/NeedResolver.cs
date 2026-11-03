using System;
using System.Collections.Generic;
using RimWorld.Planet;
using Verse;

namespace FactionColonies.SupplyChain
{
    /// <summary>
    /// Static utility for resolving settlement needs by drawing from stockpiles.
    /// </summary>
    public static class NeedResolver
    {
        /// <summary>
        /// Resolves Base + Comp needs for a single settlement by drawing from the given stockpile.
        /// Building inputs are handled separately by ResolveBuildingDormancy (all-or-nothing).
        /// Used in Complex mode (each settlement draws from its own local stockpile).
        /// </summary>
        public static void ResolveSettlementNeeds(WorldSettlementFC settlement, IStockpile stockpile, WorldObjectComp_SettlementNeeds comp)
        {
            if (stockpile == null || comp == null) return;

            List<NeedState> states = new List<NeedState>();

            // 1. Base settlement needs
            FactionFC faction = FindFC.FactionComp;
            foreach (SettlementNeedDef needDef in SupplyChainCache.AllNeedDefs)
            {
                if (!needDef.IsActiveForSettlement(settlement)) continue;

                needDef.BuildNeedStates(settlement, faction, 0.0, delegate(NeedState ns)
                {
                    double drawn;
                    stockpile.TryDraw(ns.resource, ns.demanded, out drawn);
                    ns.fulfilled = drawn;
                    states.Add(ns);
                });
            }

            // 2. Comp-provided needs (e.g., specialist needs via INeedProvider)
            ResolveCompNeeds(settlement, stockpile, states);

            // 3. Compute surplus ratios (post-all-draws)
            foreach (NeedState state in states)
            {
                if (state.surplusBonuses == null || state.demanded <= 0 || state.fulfilled < state.demanded)
                    continue;
                state.surplusRatio = stockpile.GetAmount(state.resource) / state.demanded;
            }

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
                WorldObjectComp_SettlementNeeds comp = SupplyChainCache.GetNeedsComp(settlement);
                if (comp == null) continue;

                // Base needs
                foreach (SettlementNeedDef needDef in SupplyChainCache.AllNeedDefs)
                {
                    if (!needDef.IsActiveForSettlement(settlement)) continue;

                    WorldSettlementFC capturedSettlement = settlement;
                    WorldObjectComp_SettlementNeeds capturedComp = comp;
                    needDef.BuildNeedStates(settlement, faction, 0.0, delegate(NeedState ns)
                    {
                        allDemands.Add(new NeedDemandEntry
                        {
                            settlement = capturedSettlement,
                            comp = capturedComp,
                            needId = ns.needId,
                            resource = ns.resource,
                            demand = ns.demanded,
                            label = ns.label,
                            category = NeedCategory.Base,
                            penalties = ns.penalties,
                            surplusBonuses = ns.surplusBonuses,
                            maxSurplusRatio = ns.maxSurplusRatio
                        });
                    });
                }

                // Building inputs are NOT resolved here — they drive per-building dormancy
                // (all-or-nothing) via ResolveBuildingDormancy, not the proportional need model.

                // Comp-provided needs (e.g., specialist needs via INeedProvider)
                foreach (WorldObjectComp woc in settlement.AllComps)
                {
                    INeedProvider provider = woc as INeedProvider;
                    if (provider == null) continue;

                    List<NeedEntry> compNeeds = new List<NeedEntry>();
                    provider.CollectNeeds(settlement, compNeeds);

                    foreach (NeedEntry entry in compNeeds)
                    {
                        if (entry.resource == null || entry.amount <= 0) continue;

                        allDemands.Add(new NeedDemandEntry
                        {
                            settlement = settlement,
                            comp = comp,
                            needId = entry.needId,
                            resource = entry.resource,
                            demand = entry.amount,
                            penalties = entry.penalties,
                            label = entry.label,
                            category = NeedCategory.Comp,
                            provider = provider,
                            surplusBonuses = entry.surplusBonuses,
                            maxSurplusRatio = entry.maxSurplusRatio
                        });
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
            Dictionary<WorldObjectComp_SettlementNeeds, List<NeedState>> compStates =
                new Dictionary<WorldObjectComp_SettlementNeeds, List<NeedState>>();

            // Track provider resolutions for OnNeedsResolved callbacks
            Dictionary<INeedProvider, List<NeedResolution>> providerResolutions =
                new Dictionary<INeedProvider, List<NeedResolution>>();

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

                states.Add(new NeedState(entry.needId, entry.resource, entry.demand, drawn,
                    entry.label, entry.category, entry.penalties,
                    entry.surplusBonuses, entry.maxSurplusRatio));

                // Track provider resolutions
                if (entry.provider != null)
                {
                    List<NeedResolution> resolutions;
                    if (!providerResolutions.TryGetValue(entry.provider, out resolutions))
                    {
                        resolutions = new List<NeedResolution>();
                        providerResolutions[entry.provider] = resolutions;
                    }
                    resolutions.Add(new NeedResolution
                    {
                        needId = entry.needId,
                        demanded = entry.demand,
                        fulfilled = drawn
                    });
                }
            }

            // Compute surplus ratios (post-all-draws, faction-wide shared stockpile)
            foreach (KeyValuePair<WorldObjectComp_SettlementNeeds, List<NeedState>> kv in compStates)
            {
                foreach (NeedState state in kv.Value)
                {
                    if (state.surplusBonuses == null || state.demanded <= 0 || state.fulfilled < state.demanded)
                        continue;
                    state.surplusRatio = stockpile.GetAmount(state.resource) / state.demanded;
                }
            }

            // Apply results
            foreach (KeyValuePair<WorldObjectComp_SettlementNeeds, List<NeedState>> kv in compStates)
            {
                kv.Key.SetNeedStates(kv.Value);
                WorldSettlementFC ws = kv.Key.WorldSettlement;
                if (ws != null)
                    ws.InvalidateStatCache();
            }

            // Notify providers of their resolution results
            foreach (KeyValuePair<INeedProvider, List<NeedResolution>> kv in providerResolutions)
            {
                kv.Key.OnNeedsResolved(kv.Value);
            }

            // Settlements with no demands still need cleared states
            foreach (WorldSettlementFC settlement in faction.settlements)
            {
                WorldObjectComp_SettlementNeeds comp = SupplyChainCache.GetNeedsComp(settlement);
                if (comp == null) continue;
                if (!compStates.ContainsKey(comp))
                {
                    comp.SetNeedStates(new List<NeedState>());
                    settlement.InvalidateStatCache();
                }
            }
        }

        /// <summary>
        /// Settles per-building dormancy for one settlement, drawing building inputs from the
        /// given (post-deposit) stockpile. All-or-nothing per building instance: if the stockpile
        /// holds ALL of a building's inputs in full, draw them and mark the building active;
        /// otherwise draw nothing (inputs stay for others) and mark it dormant. Deterministic
        /// slot order.
        /// </summary>
        public static void ResolveBuildingDormancy(WorldSettlementFC settlement, IStockpile stockpile)
        {
            if (settlement?.BuildingsComp == null || stockpile == null) return;

            WorldObjectComp_SettlementBuildings bComp = settlement.BuildingsComp;
            List<BuildingFC> buildings = bComp.Buildings;

            for (int slot = 0; slot < buildings.Count; slot++)
            {
                BuildingFC building = buildings[slot];
                if (building.def is null || building.def == BuildingFCDefOf.Empty)
                    continue;

                BuildingNeedExtension ext = SupplyChainCache.GetBuildingNeedExt(building.def);

                // Buildings with no inputs (e.g. cap-only) are always active.
                if (ext?.inputs is null || ext.inputs.Count == 0)
                {
                    bComp.SetBuildingActive(slot, true);
                    continue;
                }

                // Can the stockpile cover ALL of this building's inputs in full?
                bool canAfford = true;
                foreach (BuildingResourceInput input in ext.inputs)
                {
                    if (input.resource is null || input.amount <= 0) continue;
                    if (stockpile.GetAmount(input.resource) < input.amount)
                    {
                        canAfford = false;
                        break;
                    }
                }

                if (canAfford)
                {
                    foreach (BuildingResourceInput input in ext.inputs)
                    {
                        if (input.resource is null || input.amount <= 0) continue;
                        stockpile.TryDraw(input.resource, input.amount, out _);
                    }
                }

                bComp.SetBuildingActive(slot, canAfford);
            }
        }

        private static void ResolveCompNeeds(WorldSettlementFC settlement, IStockpile stockpile, List<NeedState> states)
        {
            foreach (WorldObjectComp comp in settlement.AllComps)
            {
                INeedProvider provider = comp as INeedProvider;
                if (provider == null) continue;

                try
                {
                    List<NeedEntry> compNeeds = new List<NeedEntry>();
                    provider.CollectNeeds(settlement, compNeeds);

                    List<NeedResolution> resolutions = new List<NeedResolution>();
                    foreach (NeedEntry entry in compNeeds)
                    {
                        if (entry.resource == null || entry.amount <= 0) continue;

                        double drawn;
                        stockpile.TryDraw(entry.resource, entry.amount, out drawn);

                        states.Add(new NeedState(entry.needId, entry.resource, entry.amount, drawn,
                            entry.label, NeedCategory.Comp, entry.penalties,
                            entry.surplusBonuses, entry.maxSurplusRatio));

                        resolutions.Add(new NeedResolution
                        {
                            needId = entry.needId,
                            demanded = entry.amount,
                            fulfilled = drawn
                        });
                    }

                    provider.OnNeedsResolved(resolutions);
                }
                catch (Exception e)
                {
                    LogSC.Error("INeedProvider " + comp.GetType().Name + " threw during need resolution: " + e);
                }
            }
        }

        private struct NeedDemandEntry
        {
            public WorldSettlementFC settlement;
            public WorldObjectComp_SettlementNeeds comp;
            public string needId;
            public ResourceTypeDef resource;
            public double demand;
            public List<NeedPenalty> penalties;
            public string label;
            public NeedCategory category;
            public INeedProvider provider;
            public List<NeedSurplusBonus> surplusBonuses;
            public double maxSurplusRatio;
        }
    }
}
