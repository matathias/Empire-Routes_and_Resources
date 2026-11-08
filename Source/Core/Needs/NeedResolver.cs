namespace FactionColonies.SupplyChain;

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
        if (stockpile is null || comp is null) return;

        List<NeedState> states = [];

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
        ApplySurplusRatios(states, stockpile, null);

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
        if (stockpile is null) return;

        // Gather all demand per resource across all settlements
        // Key: resource, Value: list of (settlement, comp, needId, demand)
        List<NeedDemandEntry> allDemands = [];

        foreach (WorldSettlementFC settlement in faction.settlements)
        {
            WorldObjectComp_SettlementNeeds comp = SupplyChainCache.GetNeedsComp(settlement);
            if (comp is null) continue;

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
            ForEachProviderNeeds(settlement, delegate(INeedProvider provider, List<NeedEntry> compNeeds)
            {
                foreach (NeedEntry entry in compNeeds)
                {
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
            });
        }

        // Calculate fill rate per resource
        Dictionary<ResourceTypeDef, double> totalDemandPerResource = [];
        foreach (NeedDemandEntry entry in allDemands)
        {
            double current;
            totalDemandPerResource.TryGetValue(entry.resource, out current);
            totalDemandPerResource[entry.resource] = current + entry.demand;
        }

        Dictionary<ResourceTypeDef, double> fillRates = [];
        foreach (KeyValuePair<ResourceTypeDef, double> kv in totalDemandPerResource)
        {
            double available = stockpile.GetAmount(kv.Key);
            fillRates[kv.Key] = kv.Value > 0 ? Math.Min(1.0, available / kv.Value) : 1.0;
        }

        // Distribute proportionally and draw
        // Group results by settlement
        Dictionary<WorldObjectComp_SettlementNeeds, List<NeedState>> compStates =
            [];

        // Track provider resolutions for OnNeedsResolved callbacks
        Dictionary<INeedProvider, List<NeedResolution>> providerResolutions =
            [];

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
                states = [];
                compStates[entry.comp] = states;
            }

            states.Add(new NeedState(entry.needId, entry.resource, entry.demand, drawn,
                entry.label, entry.category, entry.penalties,
                entry.surplusBonuses, entry.maxSurplusRatio));

            // Track provider resolutions
            if (entry.provider is not null)
            {
                List<NeedResolution> resolutions;
                if (!providerResolutions.TryGetValue(entry.provider, out resolutions))
                {
                    resolutions = [];
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

        // Compute surplus ratios (post-all-draws). The stockpile is shared by every settlement, so
        // it is measured against the faction-wide demand for the resource, not one settlement's:
        // otherwise each of N settlements would see about N times the real surplus.
        foreach (KeyValuePair<WorldObjectComp_SettlementNeeds, List<NeedState>> kv in compStates)
            ApplySurplusRatios(kv.Value, stockpile, totalDemandPerResource);

        // Apply results
        foreach (KeyValuePair<WorldObjectComp_SettlementNeeds, List<NeedState>> kv in compStates)
        {
            kv.Key.SetNeedStates(kv.Value);
            WorldSettlementFC ws = kv.Key.WorldSettlement;
            if (ws is not null)
                ws.InvalidateStatCache();
        }

        // Notify providers of their resolution results
        foreach (KeyValuePair<INeedProvider, List<NeedResolution>> kv in providerResolutions)
        {
            try
            {
                kv.Key.OnNeedsResolved(kv.Value);
            }
            catch (Exception e)
            {
                LogSC.Error($"INeedProvider {kv.Key.GetType().Name} threw in OnNeedsResolved: {e}");
            }
        }

        // Settlements with no demands still need cleared states
        foreach (WorldSettlementFC settlement in faction.settlements)
        {
            WorldObjectComp_SettlementNeeds comp = SupplyChainCache.GetNeedsComp(settlement);
            if (comp is null) continue;
            if (!compStates.ContainsKey(comp))
            {
                comp.SetNeedStates([]);
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
        if (settlement?.BuildingsComp is null || stockpile is null) return;

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

    /// <summary>
    /// Calls <paramref name="onProvider"/> once for each INeedProvider comp on the settlement, with
    /// the needs it contributes. Entries with no resource or a non-positive amount are dropped, so
    /// every caller (resolution and the flow display alike) counts the same needs. A provider that
    /// throws is skipped, so the rest of the caller's pass still runs; it is logged once per provider
    /// type, since some callers run every UI frame. The list is reused between providers, so callers
    /// must not keep it.
    /// </summary>
    internal static void ForEachProviderNeeds(WorldSettlementFC settlement, Action<INeedProvider, List<NeedEntry>> onProvider)
    {
        List<NeedEntry> needs = null;
        foreach (WorldObjectComp comp in settlement.AllComps)
        {
            if (comp is not INeedProvider provider) continue;

            needs ??= [];
            needs.Clear();
            try
            {
                provider.CollectNeeds(settlement, needs);
            }
            catch (Exception e)
            {
                string typeName = provider.GetType().FullName;
                Log.ErrorOnce($"{LogSC.DebugGate.Slug} INeedProvider {typeName} threw while collecting needs: {e}",
                    $"SC_NeedProvider_{typeName}".GetHashCode());
                continue;
            }

            needs.RemoveAll(entry => entry.resource is null || entry.amount <= 0);
            onProvider(provider, needs);
        }
    }

    /// <summary>
    /// Sets the surplus ratio of each satisfied state that carries surplus bonuses: what is left in
    /// the stockpile after all draws, over the demand. With <paramref name="sharedDemand"/> (a stockpile
    /// shared by several settlements) the demand is the total for the resource across all of them.
    /// </summary>
    internal static void ApplySurplusRatios(List<NeedState> states, IStockpile stockpile,
        Dictionary<ResourceTypeDef, double> sharedDemand)
    {
        foreach (NeedState state in states)
        {
            if (state.surplusBonuses is null || state.demanded <= 0 || state.IsShort)
                continue;
            double demand = sharedDemand is not null && sharedDemand.TryGetValue(state.resource, out double total)
                ? total
                : state.demanded;
            state.surplusRatio = stockpile.GetAmount(state.resource) / demand;
        }
    }

    private static void ResolveCompNeeds(WorldSettlementFC settlement, IStockpile stockpile, List<NeedState> states)
    {
        ForEachProviderNeeds(settlement, delegate(INeedProvider provider, List<NeedEntry> compNeeds)
        {
            List<NeedResolution> resolutions = [];
            foreach (NeedEntry entry in compNeeds)
            {
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

            try
            {
                provider.OnNeedsResolved(resolutions);
            }
            catch (Exception e)
            {
                LogSC.Error($"INeedProvider {provider.GetType().Name} threw in OnNeedsResolved: {e}");
            }
        });
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
