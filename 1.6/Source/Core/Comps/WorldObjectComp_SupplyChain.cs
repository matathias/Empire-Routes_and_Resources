using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using FactionColonies;

namespace FactionColonies.SupplyChain
{
    public class WorldObjectCompProperties_SupplyChain : WorldObjectCompProperties
    {
        public WorldObjectCompProperties_SupplyChain()
        {
            compClass = typeof(WorldObjectComp_SupplyChain);
        }
    }

    public class WorldObjectComp_SupplyChain : WorldObjectComp, ISettlementWindowOverview, IStatModifierProvider, ITitheBudgetModifier, ISettlementPostLoadInit
    {
        private const string ALLOC_KEY_PREFIX = "SupplyChain.";

        private Dictionary<ResourceTypeDef, double> allocations = new Dictionary<ResourceTypeDef, double>();
        private HashSet<ResourceTypeDef> autoMaxResources = new HashSet<ResourceTypeDef>();
        // Manual value snapshot taken when auto-max is enabled; restored when the player turns it off.
        private Dictionary<ResourceTypeDef, double> autoMaxFallback = new Dictionary<ResourceTypeDef, double>();

        // Tithe injection: how many stockpile units per resource to convert to tithe budget
        private Dictionary<ResourceTypeDef, double> titheInjections = new Dictionary<ResourceTypeDef, double>();
        // At tax time, stores actual drawn amounts (may be less than configured if stockpile insufficient)
        private Dictionary<ResourceTypeDef, double> actualTitheDrawn = new Dictionary<ResourceTypeDef, double>();
        private bool isTaxTime;

        // Complex mode fields
        private Dictionary<ResourceTypeDef, double> localStockpiles = new Dictionary<ResourceTypeDef, double>();
        private Dictionary<ResourceTypeDef, double> localCaps = new Dictionary<ResourceTypeDef, double>();
        private List<SellOrder> localSellOrders = new List<SellOrder>();
        private DictionaryStockpile localStockpileDict;

        private bool localCapsDirty = true;

        // Needs
        private List<NeedState> needStates = new List<NeedState>();
        private bool hasAnyShortfall;
        private bool hasCompletedFirstTax;
        public bool HasCompletedFirstTax => hasCompletedFirstTax;

        // Trade network
        private int connectedPartners;
        private int hubScore;

        private WorldSettlementFC cachedSettlement;
        private Dictionary<ResourceTypeDef, string> sliderBuffers = new Dictionary<ResourceTypeDef, string>();
        private Vector2 scrollPos;

        // Complex mode UI state
        private ResourceTypeDef newLocalSellResource;
        private string newLocalSellAmountBuffer = "";
        private float newLocalSellAmount;

        // Tithe injection UI state
        private ResourceTypeDef newTitheInjResource;
        private string newTitheInjAmountBuffer = "";
        private float newTitheInjAmount;

        // Sub-tab state (complex mode)
        private int complexSubTab;
        private Vector2 scrollPosStockpile;
        private Vector2 scrollPosNeeds;
        private Vector2 scrollPosProduction;
        private Vector2 scrollPosOrders;
        private Vector2 scrollPosRoutes;

        // Route creation state
        private WorldSettlementFC newRouteOther;
        private ResourceTypeDef newRouteResource;
        private string newRouteAmountBuffer = "";
        private float newRouteAmount;
        private bool newRouteIsOutgoing = true;
        private ResourceTypeDef routeFilterResource;

        public WorldSettlementFC WorldSettlement
        {
            get
            {
                if (cachedSettlement is null)
                    cachedSettlement = parent as WorldSettlementFC;
                return cachedSettlement;
            }
        }

        // --- Stockpile Access ---

        /// <summary>
        /// Returns the local stockpile for Complex mode. Null in Simple mode.
        /// </summary>
        public IStockpile GetStockpile()
        {
            return localStockpileDict;
        }

        public List<SellOrder> LocalSellOrders => localSellOrders;

        public Dictionary<ResourceTypeDef, double> TitheInjections => titheInjections;

        /// <summary>
        /// Initializes the local stockpile wrapper. Called by WorldComponent after mode switch or FinalizeInit.
        /// </summary>
        public void InitLocalStockpile()
        {
            if (localStockpiles is null)
                localStockpiles = new Dictionary<ResourceTypeDef, double>();
            if (localCaps is null)
                localCaps = new Dictionary<ResourceTypeDef, double>();
            localStockpileDict = new DictionaryStockpile(localStockpiles, localCaps);
        }

        /// <summary>
        /// Clears local stockpile data (used when switching to Simple mode).
        /// </summary>
        public void ClearLocalData()
        {
            localStockpiles.Clear();
            localCaps.Clear();
            localStockpileDict = null;
        }

        /// <summary>
        /// Returns the sum of all local stockpile amounts (for summary display).
        /// </summary>
        public double TotalLocalStockpileValue()
        {
            double total = 0;
            foreach (double v in localStockpiles.Values)
                total += v;
            return total;
        }

        /// <summary>
        /// Direct access to local stockpile dict for mode-switching (distributing faction stockpile).
        /// </summary>
        public Dictionary<ResourceTypeDef, double> LocalStockpile => localStockpiles;

        public void RecalculateLocalCaps()
        {
            foreach (ResourceTypeDef def in SupplyChainCache.AllResourceTypeDefs)
            {
                localCaps[def] = SupplyChainSettings.localCapBase;
            }

            WorldSettlementFC ws = WorldSettlement;
            if (ws?.BuildingsComp is null) return;
            foreach (BuildingFC building in ws.BuildingsComp.Buildings)
            {
                if (building.def is null || building.def == BuildingFCDefOf.Empty) continue;
                BuildingNeedExtension ext = SupplyChainCache.GetBuildingNeedExt(building.def);
                if (ext?.capBonuses is null) continue;
                foreach (BuildingCapBonus bonus in ext.capBonuses)
                {
                    if (bonus.resource != null && localCaps.ContainsKey(bonus.resource))
                        localCaps[bonus.resource] += bonus.amount;
                }
            }
            localCapsDirty = false;
        }

        public void RecalculateLocalCapsIfDirty()
        {
            if (!localCapsDirty) return;
            RecalculateLocalCaps();
        }

        public void DirtyLocalCaps()
        {
            localCapsDirty = true;
        }

        // --- Needs ---

        public List<NeedState> NeedStates => needStates;

        public void SetNeedStates(List<NeedState> states)
        {
            needStates = states ?? new List<NeedState>();
            UpdateHasAnyShortfall();
            statModsDirty = true;
        }

        public void SetNetworkInfo(int partners, int hub)
        {
            connectedPartners = partners;
            hubScore = hub;
            statModsDirty = true;
        }

        private NeedState FindNeedState(string needId)
        {
            foreach (NeedState state in needStates)
            {
                if (state.needId == needId)
                    return state;
            }
            return null;
        }

        /// <summary>
        /// Fully rebuilds needStates from current settlement state — base, building, and
        /// comp-provided needs — while preserving fulfilled values from the last tax resolution.
        /// Called on settlement creation, load, worker changes, building changes, and upgrades.
        /// </summary>
        public void RebuildNeedStates()
        {
            WorldSettlementFC ws = WorldSettlement;
            FactionFC faction = FactionCache.FactionComp;
            if (ws is null || faction is null) return;

            // Preserve fulfilled values and surplus ratios from last tax resolution
            Dictionary<string, double> prevFulfilled = new Dictionary<string, double>();
            Dictionary<string, double> prevSurplusRatio = new Dictionary<string, double>();
            foreach(NeedState state in needStates)
            {
                prevFulfilled[state.needId] = state.fulfilled;
                prevSurplusRatio[state.needId] = state.surplusRatio;                
            }

            List<NeedState> newStates = new List<NeedState>();

            // 1. Base settlement needs (from SettlementNeedDefs)
            foreach (SettlementNeedDef needDef in SupplyChainCache.AllNeedDefs)
            {
                if (!needDef.IsActiveForFaction(faction)) continue;
                if (!needDef.IsActiveForSettlement(ws)) continue;

                needDef.BuildNeedStates(ws, faction, 0.0, delegate(NeedState ns)
                {
                    prevFulfilled.TryGetValue(ns.needId, out double fulfilled);
                    prevSurplusRatio.TryGetValue(ns.needId, out double prevSurplus);
                    ns.fulfilled = fulfilled;
                    ns.surplusRatio = prevSurplus;
                    newStates.Add(ns);
                });
            }

            // 2. Building needs (from BuildingNeedExtension)
            if (ws.BuildingsComp != null)
            {
                foreach (BuildingFC building in ws.BuildingsComp.Buildings)
                {
                    if (building.def is null || building.def == BuildingFCDefOf.Empty) continue;
                    BuildingNeedExtension ext = SupplyChainCache.GetBuildingNeedExt(building.def);
                    if (ext?.inputs is null) continue;
                    foreach (BuildingResourceInput input in ext.inputs)
                    {
                        if (input.resource is null || input.amount <= 0) continue;
                        string needId = $"bldg.{building.def.defName}.{input.resource.defName}";
                        string needLabel = $"{building.def.label.CapitalizeFirst()} - {input.resource.label.CapitalizeFirst()}";
                        prevFulfilled.TryGetValue(needId, out double fulfilled);
                        newStates.Add(new NeedState(needId, input.resource, input.amount, fulfilled,
                            needLabel, NeedCategory.Building, ext.penalties));
                    }
                }
            }

            // 3. Comp-provided needs (from INeedProvider)
            foreach (WorldObjectComp comp in ws.AllComps)
            {
                INeedProvider provider = comp as INeedProvider;
                if (provider is null) continue;
                List<NeedEntry> compNeeds = new List<NeedEntry>();
                provider.CollectNeeds(ws, compNeeds);
                foreach (NeedEntry entry in compNeeds)
                {
                    if (entry.resource is null || entry.amount <= 0) continue;
                    prevFulfilled.TryGetValue(entry.needId, out double fulfilled);
                    newStates.Add(new NeedState(entry.needId, entry.resource, entry.amount, fulfilled,
                        entry.label, NeedCategory.Comp, entry.penalties));
                }
            }

            needStates = newStates;
            UpdateHasAnyShortfall();
        }

        private void UpdateHasAnyShortfall()
        {
            hasAnyShortfall = false;
            foreach (NeedState state in needStates)
            {
                if (state.demanded > 0 && state.fulfilled < state.demanded)
                {
                    hasAnyShortfall = true;
                    return;
                }
            }
        }

        // --- IStatModifierProvider ---

        private Dictionary<FCStatDef, double> cachedStatMods;
        private bool statModsDirty = true;

        public double GetStatModifier(FCStatDef stat)
        {
            if (statModsDirty || cachedStatMods is null)
            {
                if (cachedStatMods is null)
                    cachedStatMods = new Dictionary<FCStatDef, double>();
                else
                    cachedStatMods.Clear();
                statModsDirty = false;
            }

            if (cachedStatMods.TryGetValue(stat, out double val))
                return val;

            val = ComputeStatModifier(stat);
            cachedStatMods[stat] = val;
            return val;
        }

        private double ComputeStatModifier(FCStatDef stat)
        {
            double value = stat.IdentityValue;

            if (stat.aggregation == FCStatAggregation.Additive)
            {
                // 0. Suppress natural stat stabilization when any need is unmet
                if (hasCompletedFirstTax && hasAnyShortfall)
                {
                    if (stat == FCStatDefOf.happinessGainedBase)
                        value -= FCSettings.happinessBaseGain;
                    else if (stat == FCStatDefOf.loyaltyGainedBase)
                        value -= FCSettings.loyaltyBaseGain;
                    else if (stat == FCStatDefOf.unrestLostBase)
                        value -= FCSettings.unrestBaseLost;
                }

                // 1. Penalties for unmet needs (waived during founding grace period)
                if (hasCompletedFirstTax)
                {
                    foreach (NeedState state in needStates)
                    {
                        if (state.penalties is null || state.demanded <= 0 || state.fulfilled >= state.demanded)
                            continue;
                        double shortfall = state.demanded - state.fulfilled;
                        foreach (NeedPenalty penalty in state.penalties)
                        {
                            if (penalty.stat == stat)
                                value += penalty.penaltyPerUnit * shortfall;
                        }
                    }
                }

                // 2. Surplus bonuses
                foreach (NeedState state in needStates)
                {
                    if (state.surplusBonuses is null || state.surplusRatio <= 0)
                        continue;
                    double maxSR = state.maxSurplusRatio > 0 ? state.maxSurplusRatio : 2.0;
                    double fraction = Math.Min(1.0, state.surplusRatio / maxSR);
                    foreach (NeedSurplusBonus bonus in state.surplusBonuses)
                    {
                        if (bonus.stat == stat)
                            value += bonus.maxBonus * fraction;
                    }
                }

                // 3. Trade network bonuses (Complex mode only — 0 in Simple)
                if (stat == FCStatDefOf.happinessGainedBase)
                    value += FormulaUtil.HappinessNetworkBonus(connectedPartners);
                else if (stat == FCStatDefOf.prosperityGainedBase)
                    value += FormulaUtil.ProsperityNetworkBonus(hubScore);
            }
            else // Multiplicative
            {
                // Tax efficiency: 1.0 + 0.20 * averageSatisfaction
                FCStatDef taxEffStat = SCStatDefOf.SC_TaxEfficiency;
                if (stat == taxEffStat && needStates.Count > 0)
                {
                    double sum = 0;
                    int count = 0;
                    foreach (NeedState state in needStates)
                    {
                        if (state.demanded > 0) { sum += state.Satisfaction; count++; }
                    }
                    if (count > 0)
                        value = FormulaUtil.TaxEfficiency(sum / count);
                }

                // Network sell rate: 1.0 + 0.10*min(partners,5) + 0.10*min(hub,3)
                FCStatDef sellStat = SCStatDefOf.SC_SellRateMultiplier;
                if (stat == sellStat && (connectedPartners > 0 || hubScore > 0))
                {
                    value = FormulaUtil.SellRateMultiplier(connectedPartners, hubScore);
                }
            }

            return value;
        }

        public string GetStatModifierDesc(FCStatDef stat)
        {
            string desc = null;

            // Stabilization suppression description
            if (hasCompletedFirstTax && hasAnyShortfall &&
                (stat == FCStatDefOf.happinessGainedBase ||
                 stat == FCStatDefOf.loyaltyGainedBase ||
                 stat == FCStatDefOf.unrestLostBase))
            {
                desc = "SC_StabilizationSuppressed".Translate();
            }

            // Penalty descriptions (waived during founding grace period)
            if (hasCompletedFirstTax)
            {
                foreach (NeedState state in needStates)
                {
                    if (state.penalties is null || state.demanded <= 0 || state.fulfilled >= state.demanded)
                        continue;
                    double shortfall = state.demanded - state.fulfilled;
                    foreach (NeedPenalty penalty in state.penalties)
                    {
                        if (penalty.stat != stat) continue;
                        double val = penalty.penaltyPerUnit * shortfall;
                        if (val <= 0) continue;
                        val = Math.Round(val, 2);

                        bool invert = stat.invertedForDisplay;
                        /* Due to the weirdness of unrest, we actually want to invert the inversion for unrest values */
                        /* Should *really* replace unrest with "stability" or something... */
                        if (stat == FCStatDefOf.unrestGainedBase ||
                            stat == FCStatDefOf.unrestGainedMultiplier ||
                            stat == FCStatDefOf.unrestLostBase ||
                            stat == FCStatDefOf.unrestLostMultiplier)
                            invert = !invert;

                        string line = "SC_UnmetNeedPenalty".Translate(state.label, TextUtil.ColorizeAdditiveBonus(val, hardinvert: invert));
                        desc = desc is null ? line : desc + "\n" + line;
                    }
                }
            }

            // Surplus bonus descriptions
            foreach (NeedState state in needStates)
            {
                if (state.surplusBonuses is null || state.surplusRatio <= 0)
                    continue;
                double maxSR = state.maxSurplusRatio > 0 ? state.maxSurplusRatio : 2.0;
                double fraction = Math.Min(1.0, state.surplusRatio / maxSR);
                foreach (NeedSurplusBonus bonus in state.surplusBonuses)
                {
                    if (bonus.stat != stat) continue;
                    double val = bonus.maxBonus * fraction;
                    if (val <= 0) continue;

                    string line = "SC_SurplusBonus".Translate(bonus.label ?? state.label, val.ToString("F1"));
                    desc = desc is null ? line : desc + "\n" + line;
                }
            }

            // Network bonus descriptions
            if (stat == FCStatDefOf.happinessGainedBase && connectedPartners > 0)
            {
                double val = FormulaUtil.HappinessNetworkBonus(connectedPartners);
                string line = "SC_NetworkPartnerBonus".Translate(connectedPartners.ToString(), val.ToString("F1"));
                desc = desc is null ? line : desc + "\n" + line;
            }
            if (stat == FCStatDefOf.prosperityGainedBase && hubScore > 0)
            {
                double val = FormulaUtil.ProsperityNetworkBonus(hubScore);
                string line = "SC_NetworkHubBonus".Translate(hubScore.ToString(), val.ToString("F1"));
                desc = desc is null ? line : desc + "\n" + line;
            }

            // Tax efficiency description
            FCStatDef taxEffStat = SCStatDefOf.SC_TaxEfficiency;
            if (stat == taxEffStat && needStates.Count > 0)
            {
                double sum = 0;
                int count = 0;
                foreach (NeedState state in needStates)
                {
                    if (state.demanded > 0) { sum += state.Satisfaction; count++; }
                }
                if (count > 0)
                {
                    double avgSat = sum / count;
                    double mult = FormulaUtil.TaxEfficiency(avgSat);
                    string line = "SC_TaxEfficiencyDesc".Translate(
                        (avgSat * 100).ToString("F0"), (mult * 100).ToString("F0"));
                    desc = desc is null ? line : desc + "\n" + line;
                }
            }

            // Network sell rate description
            FCStatDef sellStat = SCStatDefOf.SC_SellRateMultiplier;
            if (stat == sellStat && (connectedPartners > 0 || hubScore > 0))
            {
                double mult = FormulaUtil.SellRateMultiplier(connectedPartners, hubScore);
                string line = "SC_SellRateNetworkDesc".Translate((mult * 100).ToString("F0"));
                desc = desc is null ? line : desc + "\n" + line;
            }

            return desc;
        }

        // --- ITitheBudgetModifier ---

        public double GetExternalTitheBudget(ResourceFC resource)
        {
            if (resource?.def is null || !resource.def.CanTithe)
                return 0;

            // At tax time, use actual drawn amounts; otherwise use configured injection (optimistic)
            if (isTaxTime)
            {
                return actualTitheDrawn.TryGetValue(resource.def, out double drawn) ? drawn * FCSettings.silverPerResource : 0;
            }

            return titheInjections.TryGetValue(resource.def, out double injection) && injection > 0
                ? injection * FCSettings.silverPerResource
                : 0;
        }

        public string GetExternalTitheBudgetDesc(ResourceFC resource)
        {
            if (resource?.def is null || !resource.def.CanTithe)
                return null;

            if (!titheInjections.TryGetValue(resource.def, out double injection) || injection <= 0)
                return null;

            double silverValue = injection * FCSettings.silverPerResource;
            return "SC_TitheInjectionDesc".Translate(
                injection.ToString("F1"), resource.def.LabelCap, silverValue.ToString("F0"));
        }

        // --- Tithe Injection Management ---

        public double GetTitheInjection(ResourceTypeDef def)
        {
            return titheInjections.TryGetValue(def, out double val) ? val : 0;
        }

        public void SetTitheInjection(ResourceTypeDef def, double amount)
        {
            if (!def.CanTithe)
                return;

            if (amount <= 0)
                titheInjections.Remove(def);
            else
                titheInjections[def] = amount;

            WorldSettlement?.DirtyProfitCache();
            SupplyChainCache.Comp?.DirtyFlowCache();
        }

        /// <summary>
        /// Called by WorldComponent_SupplyChain during PreTaxResolution.
        /// Draws from the stockpile and records actual amounts for GetExternalTitheBudget.
        /// </summary>
        public void ResolveTitheInjections(IStockpile stockpile)
        {
            actualTitheDrawn.Clear();
            isTaxTime = true;
            WorldSettlementFC ws = WorldSettlement;

            foreach (KeyValuePair<ResourceTypeDef, double> kv in titheInjections)
            {
                if (kv.Key is null || !kv.Key.CanTithe || kv.Value <= 0) continue;

                stockpile.TryDraw(kv.Key, kv.Value, out double drawn);

                if (drawn > 0)
                    actualTitheDrawn[kv.Key] = drawn;

                string settleName = ws?.Name ?? "unknown";
                if (SupplyChainSettings.PrintDebug)
                {
                    if (drawn < kv.Value && drawn > 0)
                    {
                        LogSC.Message($"Tithe injection shortfall at {settleName}: {kv.Key.label} wanted {kv.Value}, only {drawn} available (budget reduced to {drawn * FCSettings.silverPerResource} silver)");
                    }
                    else if (drawn <= 0)
                    {
                        LogSC.Message($"Tithe injection at {settleName}: {kv.Key.label} wanted {kv.Value}, stockpile empty — skipped");
                    }
                    else
                    {
                        LogSC.Message($"Tithe injection at {settleName}: {drawn}/{kv.Value} {kv.Key.label} ({drawn * FCSettings.silverPerResource} silver budget)");
                    }
                }
            }
        }

        /// <summary>
        /// Called after tax resolution completes to reset the tax-time flag.
        /// </summary>
        public void PostTaxCleanup()
        {
            isTaxTime = false;
            actualTitheDrawn.Clear();
            hasCompletedFirstTax = true;
        }

        // --- Allocation Management ---

        public double GetAllocation(ResourceTypeDef def)
        {
            if (autoMaxResources.Contains(def))
            {
                ResourceFC resource = WorldSettlement?.GetResource(def);
                if (resource != null)
                    return LiveMaxFor(resource);
            }
            return allocations.TryGetValue(def, out double val) ? val : 0.0;
        }

        public bool IsAutoMax(ResourceTypeDef def)
        {
            return autoMaxResources.Contains(def);
        }

        /// <summary>
        /// Returns the headroom this comp can claim for the given resource if its current
        /// registered amount were ignored: rawProduction minus other submods' allocations.
        /// </summary>
        private double LiveMaxFor(ResourceFC resource)
        {
            if (resource is null) return 0;
            double ownRegistered = allocations.TryGetValue(resource.def, out double v) ? v : 0;
            double available = resource.rawTotalProduction - (resource.totalStockpileAllocation - ownRegistered);
            if (available < 0) return 0;
            return available;
        }

        /// <summary>
        /// Re-registers this comp's allocation for an auto-max resource at the current live max.
        /// Clears the registration if production has dropped to zero. Mirrors the new value
        /// into the local allocations dict for downstream UI/flow consumers.
        /// </summary>
        private void SyncAutoMaxAllocation(ResourceTypeDef def)
        {
            if (!autoMaxResources.Contains(def)) return;
            ResourceFC resource = WorldSettlement?.GetResource(def);
            if (resource is null)
            {
                autoMaxResources.Remove(def);
                return;
            }

            double live = LiveMaxFor(resource);
            string key = ALLOC_KEY_PREFIX + def.defName;

            if (live <= 0)
            {
                resource.ClearStockpileAllocation(key);
                allocations[def] = 0;
                SupplyChainCache.Comp?.DirtyFlowCache();
                return;
            }

            bool ok = resource.SetStockpileAllocation(key, live, () => OnEvicted(def));
            if (ok)
            {
                allocations[def] = live;
                SupplyChainCache.Comp?.DirtyFlowCache();
            }
        }

        /// <summary>
        /// Re-registers all auto-max allocations on this comp at the current live max.
        /// Called at the top of each tax cycle and on load.
        /// </summary>
        public void SyncAllAutoMaxAllocations()
        {
            if (autoMaxResources.Count == 0) return;
            List<ResourceTypeDef> snapshot = new List<ResourceTypeDef>(autoMaxResources);
            foreach (ResourceTypeDef def in snapshot)
                SyncAutoMaxAllocation(def);
        }

        /// <summary>
        /// Toggles auto-max for a resource. Turning on immediately syncs to the live max;
        /// turning off restores the player's last manual value (clamped by SetAllocation).
        /// </summary>
        public void SetAutoMax(ResourceTypeDef def, bool enabled)
        {
            if (def is null) return;
            if (enabled)
            {
                if (autoMaxResources.Add(def))
                {
                    autoMaxFallback[def] = allocations.TryGetValue(def, out double v) ? v : 0;
                    SyncAutoMaxAllocation(def);
                }
            }
            else
            {
                if (autoMaxResources.Remove(def))
                {
                    double fallback = autoMaxFallback.TryGetValue(def, out double v) ? v : 0;
                    autoMaxFallback.Remove(def);
                    SetAllocation(def, fallback);
                }
            }
        }

        public bool SetAllocation(ResourceTypeDef def, double amount)
        {
            ResourceFC resource = WorldSettlement?.GetResource(def);
            if (resource is null) return false;

            string key = ALLOC_KEY_PREFIX + def.defName;

            if (amount <= 0)
            {
                resource.ClearStockpileAllocation(key);
                allocations.Remove(def);
                SupplyChainCache.Comp?.DirtyFlowCache();
                return true;
            }

            bool ok = resource.SetStockpileAllocation(key, amount, () => OnEvicted(def));
            if (ok)
            {
                allocations[def] = amount;
                SupplyChainCache.Comp?.DirtyFlowCache();
            }
            return ok;
        }

        private void OnEvicted(ResourceTypeDef def)
        {
            allocations.Remove(def);
            WorldSettlementFC ws = WorldSettlement;
            string name = ws?.Name ?? "unknown";
            LogSC.Warning($"Stockpile allocation for {def.label} at {name} was evicted due to insufficient production.");
        }

        /// <summary>
        /// Re-registers all saved allocations with the base mod's SetStockpileAllocation API.
        /// Called from PostExposeData(PostLoadInit) to restore transient state after load.
        /// </summary>
        public void ReRegisterAllocations()
        {
            WorldSettlementFC ws = WorldSettlement;
            if (ws is null) return;

            List<ResourceTypeDef> toRemove = null;
            List<KeyValuePair<ResourceTypeDef, double>> toClamp = null;

            foreach (KeyValuePair<ResourceTypeDef, double> kv in allocations)
            {
                if (kv.Value <= 0) continue;
                ResourceFC resource = ws.GetResource(kv.Key);
                if (resource is null)
                {
                    if (toRemove is null) toRemove = new List<ResourceTypeDef>();
                    toRemove.Add(kv.Key);
                    continue;
                }

                double available = resource.rawTotalProduction - resource.totalStockpileAllocation;
                double clamped = Math.Min(kv.Value, Math.Max(0.0, available));

                if (clamped <= 0)
                {
                    if (toRemove is null) toRemove = new List<ResourceTypeDef>();
                    toRemove.Add(kv.Key);
                    LogSC.Warning($"Clearing allocation for {kv.Key.label} at {ws.Name}: current production is 0 (was {kv.Value:F1}).");
                    continue;
                }

                string key = ALLOC_KEY_PREFIX + kv.Key.defName;
                bool ok = resource.SetStockpileAllocation(key, clamped, () => OnEvicted(kv.Key));
                if (!ok)
                {
                    if (toRemove is null) toRemove = new List<ResourceTypeDef>();
                    toRemove.Add(kv.Key);
                    LogSC.Error($"Unexpected: could not re-register clamped allocation for {kv.Key.label} at {ws.Name} (clamped={clamped:F1}, available={available:F1}). Clearing.");
                    continue;
                }

                if (clamped < kv.Value)
                {
                    if (toClamp is null) toClamp = new List<KeyValuePair<ResourceTypeDef, double>>();
                    toClamp.Add(new KeyValuePair<ResourceTypeDef, double>(kv.Key, clamped));
                    LogSC.Warning($"Reduced allocation for {kv.Key.label} at {ws.Name} from {kv.Value:F1} to {clamped:F1} to fit current production.");
                }
            }

            if (toRemove != null)
            {
                foreach (ResourceTypeDef def in toRemove)
                    allocations.Remove(def);
            }
            if (toClamp != null)
            {
                foreach (KeyValuePair<ResourceTypeDef, double> kv in toClamp)
                    allocations[kv.Key] = kv.Value;
            }

            // Auto-max overrides any clamped value: pin to current production.
            SyncAllAutoMaxAllocations();
        }

        // --- Gizmos & World Map Overlay ---

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos())
                yield return g;

            WorldComponent_SupplyChain wc = SupplyChainCache.Comp;
            if (wc is null || wc.Mode != SupplyChainMode.Complex) yield break;

            yield return new Command_Toggle
            {
                defaultLabel = "SC_ShowSettlementRoutes".Translate(),
                defaultDesc = "SC_ShowSettlementRoutesDesc".Translate(),
                icon = TexLoad.iconTrade,
                isActive = () => wc.showSelectedRoutes,
                toggleAction = () => { wc.showSelectedRoutes = !wc.showSelectedRoutes; }
            };

            yield return new Command_Toggle
            {
                defaultLabel = "SC_ShowAllRoutes".Translate(),
                defaultDesc = "SC_ShowAllRoutesDesc".Translate(),
                icon = TexLoad.iconTrade,
                isActive = () => wc.showAllRoutes,
                toggleAction = () => { wc.showAllRoutes = !wc.showAllRoutes; }
            };

            yield return new Command_Toggle
            {
                defaultLabel = "SC_ShowRouteLabels".Translate(),
                defaultDesc = "SC_ShowRouteLabelsDesc".Translate(),
                icon = TexLoad.iconTrade,
                isActive = () => wc.showRouteLabels,
                toggleAction = () => { wc.showRouteLabels = !wc.showRouteLabels; }
            };
        }

        public override void PostDrawExtraSelectionOverlays()
        {
            WorldComponent_SupplyChain wc = SupplyChainCache.Comp;
            if (wc is null || !wc.showSelectedRoutes) return;
            wc.DrawRoutesForSettlement(WorldSettlement);
        }

        // --- Save/Load ---

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Collections.Look(ref allocations, "scAllocations", LookMode.Def, LookMode.Value);
            if (allocations is null)
                allocations = new Dictionary<ResourceTypeDef, double>();

            Scribe_Collections.Look(ref autoMaxResources, "scAutoMax", LookMode.Def);
            if (autoMaxResources is null)
                autoMaxResources = new HashSet<ResourceTypeDef>();

            Scribe_Collections.Look(ref autoMaxFallback, "scAutoMaxFallback", LookMode.Def, LookMode.Value);
            if (autoMaxFallback is null)
                autoMaxFallback = new Dictionary<ResourceTypeDef, double>();

            Scribe_Collections.Look(ref localStockpiles, "localStockpile", LookMode.Def, LookMode.Value);
            if (localStockpiles is null)
                localStockpiles = new Dictionary<ResourceTypeDef, double>();

            Scribe_Collections.Look(ref localCaps, "localCaps", LookMode.Def, LookMode.Value);
            if (localCaps is null)
                localCaps = new Dictionary<ResourceTypeDef, double>();

            Scribe_Collections.Look(ref localSellOrders, "localSellOrders", LookMode.Deep);
            if (localSellOrders is null)
                localSellOrders = new List<SellOrder>();

            Scribe_Collections.Look(ref titheInjections, "titheInjections", LookMode.Def, LookMode.Value);
            if (titheInjections is null)
                titheInjections = new Dictionary<ResourceTypeDef, double>();

            Scribe_Collections.Look(ref needStates, "needStates", LookMode.Deep);
            if (needStates is null)
                needStates = new List<NeedState>();
            UpdateHasAnyShortfall();

            Scribe_Values.Look(ref connectedPartners, "connectedPartners", 0);
            Scribe_Values.Look(ref hubScore, "hubScore", 0);
            Scribe_Values.Look(ref hasCompletedFirstTax, "hasCompletedFirstTax", false);
        }

        // --- ISettlementPostLoadInit ---

        public void PostSettlementLoadInit(WorldSettlementFC settlement)
        {
            if (settlement is null)
            {
                LogSC.Warning($"PostSettlementLoadInit encountered null settlement");
                return;
            }
            LogSC.Message($"Running PostSettlementLoadInit on settlement {settlement.Name} for Routes & Resources");
            if (allocations.Count > 0)
                ReRegisterAllocations();
            
            RebuildNeedStates();
        }

        // --- ISettlementWindowOverview ---

        private WorldSettlementFC uiSettlement;

        public void PreOpenWindow(WorldSettlementFC settlement)
        {
            uiSettlement = settlement;
            sliderBuffers.Clear();
            scrollPos = Vector2.zero;
            newLocalSellResource = null;
            newLocalSellAmountBuffer = "";
            newLocalSellAmount = 0;

            complexSubTab = 0;
            scrollPosStockpile = Vector2.zero;
            scrollPosNeeds = Vector2.zero;
            scrollPosProduction = Vector2.zero;
            scrollPosOrders = Vector2.zero;
            scrollPosRoutes = Vector2.zero;
            newRouteOther = null;
            newRouteResource = null;
            newRouteAmountBuffer = "";
            newRouteAmount = 0;
            newRouteIsOutgoing = true;
            routeFilterResource = null;
        }

        public void OnTabSwitch()
        {
        }

        public void PostCloseWindow()
        {
            uiSettlement = null;
        }

        public string OverviewTabName()
        {
            return "SC_TabName".Translate();
        }

        public void DrawOverviewTab(Rect boundingBox)
        {
            if (uiSettlement == null) return;

            WorldComponent_SupplyChain wc = SupplyChainCache.Comp;
            wc?.EnsureCapsAndStockpiles();
            bool isComplex = wc?.Mode == SupplyChainMode.Complex;

            if (isComplex)
                DrawComplexModeTab(boundingBox);
            else
                DrawSimpleModeTab(boundingBox);
        }

        // --- Simple Mode Tab (allocation sliders only) ---

        private void DrawSimpleModeTab(Rect boundingBox)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(boundingBox.x, boundingBox.y, boundingBox.width, 30f), "SC_StockpileAllocations".Translate());
            Text.Font = GameFont.Small;

            float y = boundingBox.y + 40f;
            float rowHeight = 35f;

            int resourceCount = uiSettlement.Resources.Count;

            float totalHeight = resourceCount * rowHeight + 40f + needStates.Count * NeedRowStep + 50f;
            Rect viewRect = new Rect(0f, 0f, boundingBox.width - 16f, totalHeight);
            Rect scrollRect = new Rect(boundingBox.x, y, boundingBox.width, boundingBox.height - (y - boundingBox.y));

            Widgets.BeginScrollView(scrollRect, ref scrollPos, viewRect);
            float curY = 0f;

            DrawAllocationSliders(viewRect, ref curY, rowHeight);
            curY += 12f;

            // Needs
            DrawNeedsSection(viewRect, ref curY);

            // Footer
            Text.Font = GameFont.Tiny;
            GUI.color = Color.gray;
            Widgets.Label(new Rect(0f, curY + 8f, viewRect.width, 30f),
                "SC_CapContribution".Translate(SupplyChainSettings.baseCapPerSettlement.ToString("F0")));
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            Widgets.EndScrollView();
        }

        // --- Complex Mode Tab (sub-tabs: Overview, Production, Routes) ---

        private static readonly Color AccentPositive = new Color(0.3f, 0.8f, 0.3f);
        private static readonly Color AccentNegative = new Color(0.9f, 0.3f, 0.3f);
        private static readonly Color AccentNeutral = new Color(0.5f, 0.5f, 0.5f);
        private const float AccentW = 4f;

        // Status bar constants
        private const float StatusRowH = 22f;
        private const float StatusIconSize = 16f;
        private const float StatusCellPad = 8f;
        private const float StatusBarGap = 4f;
        private static readonly Color StatusNetStable = new Color(0.5f, 0.5f, 0.5f);
        private static readonly Color GracePeriodText = new Color(0.5f, 0.85f, 1f);
        private static readonly TaggedString CachedFoundingGrace = "SC_FoundingGrace".Translate();

        private float MeasureStockpileStatusBar(float width)
        {
            if (localStockpileDict == null) return 0f;

            Text.Font = GameFont.Tiny;
            int rowCount = 0;
            float curX = 0f;
            bool any = false;

            foreach (ResourceTypeDef def in SupplyChainCache.AllResourceTypeDefs)
            {
                double cap = localStockpileDict.GetCap(def);
                if (cap <= 0) continue;

                if (!any)
                {
                    rowCount = 1;
                    any = true;
                }

                double amount = localStockpileDict.GetAmount(def);
                WorldComponent_SupplyChain.FlowBreakdown flow = default(WorldComponent_SupplyChain.FlowBreakdown);
                WorldComponent_SupplyChain wc = SupplyChainCache.Comp;
                WorldSettlementFC ws = WorldSettlement;
                if (wc != null && ws != null)
                    flow = wc.GetCachedFlow(ws, this, def);

                string netStr = flow.Net >= 0 ? "(+" + flow.Net.ToString("F1") + ")" : "(" + flow.Net.ToString("F1") + ")";
                string label = amount.ToString("F1") + netStr;
                float cellW = StatusIconSize + 2f + Text.CalcSize(label).x + StatusCellPad;

                if (curX + cellW > width && curX > 0f)
                {
                    rowCount++;
                    curX = 0f;
                }
                curX += cellW;
            }

            Text.Font = GameFont.Small;
            return any ? rowCount * StatusRowH : 0f;
        }

        private void DrawStockpileStatusBar(Rect rect)
        {
            if (localStockpileDict is null) return;

            // Separator line
            Widgets.DrawBoxSolid(new Rect(rect.x, rect.y, rect.width, 1f), new Color(0.3f, 0.3f, 0.3f));

            WorldComponent_SupplyChain wc = SupplyChainCache.Comp;
            WorldSettlementFC ws = WorldSettlement;

            GameFont prevFont = Text.Font;
            TextAnchor prevAnchor = Text.Anchor;
            Color prevColor = GUI.color;

            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleLeft;

            float curX = rect.x;
            float curY = rect.y + 2f;

            foreach (ResourceTypeDef def in SupplyChainCache.AllResourceTypeDefs)
            {
                double cap = localStockpileDict.GetCap(def);
                if (cap <= 0) continue;

                double amount = localStockpileDict.GetAmount(def);
                WorldComponent_SupplyChain.FlowBreakdown flow = default(WorldComponent_SupplyChain.FlowBreakdown);
                if (wc != null && ws != null)
                    flow = wc.GetCachedFlow(ws, this, def);

                string amtStr = amount.ToString("F1");
                string netStr = flow.Net >= 0 ? "(+" + flow.Net.ToString("F1") + ")" : "(" + flow.Net.ToString("F1") + ")";
                float amtW = Text.CalcSize(amtStr).x;
                float netW = Text.CalcSize(netStr).x;
                float cellW = StatusIconSize + 2f + amtW + netW + StatusCellPad;

                // Wrap to next row if needed
                if (curX + cellW > rect.xMax && curX > rect.x)
                {
                    curX = rect.x;
                    curY += StatusRowH;
                }

                // Icon
                float iconY = curY + (StatusRowH - StatusIconSize) / 2f;
                if (def.Icon != null)
                    GUI.DrawTexture(new Rect(curX, iconY, StatusIconSize, StatusIconSize), def.Icon);

                // Amount text (white)
                GUI.color = Color.white;
                Rect amtRect = new Rect(curX + StatusIconSize + 2f, curY, amtW, StatusRowH);
                Widgets.Label(amtRect, amtStr);

                // Net change text (colored)
                Color netColor = flow.Net > 0.01 ? AccentUtil.Income
                    : flow.Net < -0.01 ? AccentUtil.Expense
                    : StatusNetStable;
                GUI.color = netColor;
                Rect netRect = new Rect(amtRect.xMax, curY, netW, StatusRowH);
                Widgets.Label(netRect, netStr);
                GUI.color = prevColor;

                // Tooltip
                Rect cellRect = new Rect(curX, curY, cellW - StatusCellPad, StatusRowH);
                TooltipHandler.TipRegion(cellRect, UIUtilSC.BuildFlowTooltip(def, amount, cap, flow));

                curX += cellW;
            }

            Text.Font = prevFont;
            Text.Anchor = prevAnchor;
            GUI.color = prevColor;
        }

        private void DrawComplexModeTab(Rect boundingBox)
        {
            // Sub-tab bar
            float tabH = 24f;
            float tabW = boundingBox.width / 5f;
            string[] tabLabels =
            {
                "SC_SubStockpile".Translate(),
                "SC_SubNeeds".Translate(),
                "SC_SubProduction".Translate(),
                "SC_SubOrders".Translate(),
                "SC_SubRoutes".Translate()
            };

            Rect chosenRect = new Rect();
            for (int i = 0; i < 5; i++)
            {
                Rect tabRect = new Rect(boundingBox.x + tabW * i, boundingBox.y, tabW, tabH);
                if (UIUtil.ButtonFlat(tabRect, tabLabels[i], highlighted: complexSubTab == i))
                    complexSubTab = i;
                if (complexSubTab == i)
                    chosenRect = tabRect;
            }

            UIUtil.DrawTabDecoratorHorizontalTop(chosenRect, boundingBox, Color.gray);

            // Measure status bar (dynamic height based on row wrapping)
            float statusBarH = MeasureStockpileStatusBar(boundingBox.width);
            float statusGap = statusBarH > 0f ? StatusBarGap : 0f;

            // Content area below tabs, above status bar
            float contentY = boundingBox.y + tabH;
            float contentH = boundingBox.yMax - contentY - statusBarH - statusGap;
            Rect contentRect = new Rect(boundingBox.x, contentY, boundingBox.width, contentH);

            if (complexSubTab == 0)
                DrawComplexStockpile(contentRect);
            else if (complexSubTab == 1)
                DrawComplexNeeds(contentRect);
            else if (complexSubTab == 2)
                DrawComplexProduction(contentRect);
            else if (complexSubTab == 3)
                DrawComplexOrders(contentRect);
            else
                DrawComplexRoutes(contentRect);

            // Bottom status bar
            if (statusBarH > 0f)
            {
                Rect statusRect = new Rect(boundingBox.x, boundingBox.yMax - statusBarH, boundingBox.width, statusBarH);
                DrawStockpileStatusBar(statusRect);
            }
        }

        // --- Complex Sub-Tab 0: Stockpile ---

        private void DrawComplexStockpile(Rect rect)
        {
            const float barHeight = 28f;
            const float sectionPad = 8f;

            WorldComponent_SupplyChain flowWc = SupplyChainCache.Comp;
            WorldSettlementFC flowSettlement = WorldSettlement;

            // Count resources for height calculation
            int resourceCount = 0;
            foreach (ResourceTypeDef def in SupplyChainCache.AllResourceTypeDefs)
            {
                double cap = localStockpileDict?.GetCap(def) ?? 0;
                if (cap > 0) resourceCount++;
            }

            float stockpileH = 36f + resourceCount * (barHeight + 2f) + sectionPad;
            float totalHeight = stockpileH + 16f;
            float scrollMargin = totalHeight > rect.height ? 16f : 0f;

            Rect viewRect = new Rect(0f, 0f, rect.width - scrollMargin, totalHeight);
            Widgets.BeginScrollView(rect, ref scrollPosStockpile, viewRect);
            float curY = 4f;

            // --- Local Stockpile section ---
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(AccentW + 6f, curY, viewRect.width, 30f), "SC_LocalStockpile".Translate());
            Text.Font = GameFont.Small;
            curY += 34f;

            const float arrowSize = 16f;
            const float buyBtnW = 22f;
            float contentX = AccentW + 4f;
            float barWidth = viewRect.width - contentX - 28f - 100f - arrowSize - 8f - 150f - buyBtnW - 8f;
            if (barWidth < 100f) barWidth = 100f;

            int idx = 0;
            foreach (ResourceTypeDef def in SupplyChainCache.AllResourceTypeDefs)
            {
                double amount = localStockpileDict?.GetAmount(def) ?? 0;
                double cap = localStockpileDict?.GetCap(def) ?? 0;
                if (cap <= 0) continue;

                float fillPct = cap > 0 ? (float)(amount / cap) : 0f;

                Rect rowRect = new Rect(0f, curY, viewRect.width, barHeight);

                // Flow calculation
                WorldComponent_SupplyChain.FlowBreakdown flow = default(WorldComponent_SupplyChain.FlowBreakdown);
                if (flowWc != null && flowSettlement != null)
                    flow = flowWc.GetCachedFlow(flowSettlement, this, def);

                // Row highlight: alternating gray + flow-based red/green
                if (idx % 2 == 0) Widgets.DrawHighlight(rowRect);
                UIUtilSC.DrawFlowHighlight(rowRect, flow.Net);

                // Left accent bar (colored by flow)
                Color accentColor = flow.Net > 0.01 ? AccentPositive : flow.Net < -0.01 ? AccentNegative : AccentNeutral;
                Widgets.DrawBoxSolid(new Rect(0f, curY, AccentW, barHeight), accentColor);

                if (def.Icon != null)
                    GUI.DrawTexture(new Rect(contentX, curY + 2f, 24f, 24f), def.Icon);

                Text.Anchor = TextAnchor.MiddleLeft;
                Rect reslabel = new Rect(contentX + 28f, curY, 100f, barHeight);
                string reslabelstr = Text.ClampTextWithEllipsis(reslabel, def.label.CapitalizeFirst());
                Widgets.Label(reslabel, reslabelstr);

                float barX = contentX + 130f;
                Rect barRect = new Rect(barX, curY + 4f, barWidth, barHeight - 8f);
                Widgets.FillableBar(barRect, fillPct);

                // Arrow indicator (between bar and amount text)
                float arrowX = barRect.xMax + 2f;
                if (flow.Net > 0.01)
                {
                    GUI.color = AccentUtil.Income;
                    GUI.DrawTexture(new Rect(arrowX, curY + (barHeight - arrowSize) / 2f, arrowSize, arrowSize), TexUI.ArrowTexRight);
                    GUI.color = Color.white;
                }
                else if (flow.Net < -0.01)
                {
                    GUI.color = AccentUtil.Expense;
                    GUI.DrawTexture(new Rect(arrowX, curY + (barHeight - arrowSize) / 2f, arrowSize, arrowSize), TexUI.ArrowTexLeft);
                    GUI.color = Color.white;
                }

                Widgets.Label(new Rect(arrowX + arrowSize + 4f, curY, 150f, barHeight),
                    "SC_StockpileAmount".Translate(amount.ToString("F1"), cap.ToString("F0")));

                // Buy button
                float buyX = viewRect.width - buyBtnW - 2f;
                Rect buyRect = new Rect(buyX, curY + 3f, buyBtnW, barHeight - 6f);
                Text.Font = GameFont.Tiny;
                if (Widgets.ButtonText(buyRect, "$"))
                {
                    ResourceTypeDef capturedDef = def;
                    DictionaryStockpile capturedStockpile = localStockpileDict;
                    UIUtilSC.ShowBuyMenu(capturedDef, capturedStockpile,
                        delegate { SupplyChainCache.Comp?.DirtyFlowCache(); });
                }
                TooltipHandler.TipRegion(buyRect, "SC_BuyTooltip".Translate());
                Text.Font = GameFont.Small;

                TooltipHandler.TipRegion(rowRect, UIUtilSC.BuildFlowTooltip(def, amount, cap, flow));
                Text.Anchor = TextAnchor.UpperLeft;

                curY += barHeight + 2f;
                idx++;
            }

            Widgets.EndScrollView();
        }

        // --- Complex Sub-Tab 1: Needs ---

        private void DrawComplexNeeds(Rect rect)
        {
            if (needStates.Count == 0)
            {
                Text.Font = GameFont.Medium;
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(rect, "SC_NoNeeds".Translate());
                Text.Anchor = TextAnchor.UpperLeft;
                Text.Font = GameFont.Small;
                return;
            }

            float totalHeight = 36f + needStates.Count * NeedRowStep + 16f;
            float scrollMargin = totalHeight > rect.height ? 16f : 0f;

            Rect viewRect = new Rect(0f, 0f, rect.width - scrollMargin, totalHeight);
            Widgets.BeginScrollView(rect, ref scrollPosNeeds, viewRect);
            float curY = 4f;

            DrawNeedsSection(viewRect, ref curY);

            Widgets.EndScrollView();
        }

        // --- Complex Sub-Tab 2: Production (allocation sliders) ---

        private void DrawComplexProduction(Rect rect)
        {
            const float rowHeight = 35f;
            const float sectionPad = 8f;

            int resourceCount = uiSettlement.Resources.Count;

            float allocH = 36f + resourceCount * rowHeight + sectionPad;
            float totalHeight = allocH + 16f;
            float scrollMargin = totalHeight > rect.height ? 16f : 0f;

            Rect viewRect = new Rect(0f, 0f, rect.width - scrollMargin, totalHeight);
            Widgets.BeginScrollView(rect, ref scrollPosProduction, viewRect);
            float curY = 4f;

            // --- Allocation sliders section ---
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(AccentW + 6f, curY, viewRect.width, 30f), "SC_ProductionAllocations".Translate());
            Text.Font = GameFont.Small;
            curY += 34f;

            DrawAllocationSliders(viewRect, ref curY, rowHeight);

            Widgets.EndScrollView();
        }

        // --- Complex Sub-Tab 3: Orders (sell orders + tithe injection) ---

        private void DrawComplexOrders(Rect rect)
        {
            const float sectionPad = 8f;

            float sellH = 36f + localSellOrders.Count * 28f + 32f + sectionPad;
            float titheH = 36f + titheInjections.Count * 28f + 32f + sectionPad;
            float totalHeight = sellH + titheH + 16f;
            float scrollMargin = totalHeight > rect.height ? 16f : 0f;

            Rect viewRect = new Rect(0f, 0f, rect.width - scrollMargin, totalHeight);
            Widgets.BeginScrollView(rect, ref scrollPosOrders, viewRect);
            float curY = 4f;

            // --- Sell Orders section ---
            Text.Font = GameFont.Medium;
            Text.Anchor = TextAnchor.MiddleCenter;
            Rect localSellHeaderRect = new Rect(AccentW + 6f, curY, viewRect.width, 30f);
            Widgets.Label(localSellHeaderRect, "SC_LocalSellOrders".Translate());
            TooltipHandler.TipRegion(localSellHeaderRect, (string)"SC_SellOrdersTooltip".Translate(
                SupplyChainSettings.overflowPenaltyRate.ToString("P0")));
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
            curY += 34f;

            DrawAddLocalSellOrderRow(viewRect, ref curY);
            curY += 4f;

            List<SellOrder> toRemove = null;
            int sellIdx = 0;
            foreach (SellOrder order in localSellOrders)
            {
                if (order.resource is null) continue;

                Rect sellRow = new Rect(0f, curY, viewRect.width, 26f);
                if (sellIdx % 2 == 0) Widgets.DrawHighlight(sellRow);
                Widgets.DrawBoxSolid(new Rect(0f, curY, AccentW, 26f), AccentUtil.Income);

                float cx = AccentW + 4f;
                Text.Anchor = TextAnchor.MiddleLeft;
                if (order.resource.Icon != null)
                    GUI.DrawTexture(new Rect(cx, curY + 3f, 20f, 20f), order.resource.Icon);

                Widgets.Label(new Rect(cx + 24f, curY, 120f, 26f),
                    order.resource.label.CapitalizeFirst());
                Widgets.Label(new Rect(cx + 148f, curY, 130f, 26f),
                    "SC_UnitsPerPeriod".Translate(order.amountPerPeriod.ToString("F1")));

                float expectedSilver = (float)(order.amountPerPeriod * FCSettings.silverPerResource
                    * SupplyChainSettings.overflowPenaltyRate);
                GUI.color = new Color(0.7f, 1f, 0.7f);
                Widgets.Label(new Rect(cx + 284f, curY, 100f, 26f),
                    "SC_ExpectedSilver".Translate(expectedSilver.ToString("F0")));
                GUI.color = Color.white;

                if (Widgets.ButtonText(new Rect(sellRow.xMax - 28f, curY + 1f, 24f, 24f), "X"))
                {
                    if (toRemove is null) toRemove = new List<SellOrder>();
                    toRemove.Add(order);
                }

                Text.Anchor = TextAnchor.UpperLeft;
                curY += 28f;
                sellIdx++;
            }
            if (toRemove != null)
            {
                foreach (SellOrder order in toRemove)
                    localSellOrders.Remove(order);
                SupplyChainCache.Comp?.DirtyFlowCache();
            }

            curY += sectionPad;

            // --- Tithe Injection section ---
            Text.Font = GameFont.Medium;
            Text.Anchor = TextAnchor.MiddleCenter;
            Rect titheHeaderRect = new Rect(AccentW + 6f, curY, viewRect.width, 30f);
            Widgets.Label(titheHeaderRect, "SC_TitheInjection".Translate());
            TooltipHandler.TipRegion(titheHeaderRect, (string)"SC_TitheInjectionTooltip".Translate());
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
            curY += 34f;

            DrawAddTitheInjectionRow(viewRect, ref curY);
            curY += 4f;

            List<ResourceTypeDef> titheToRemove = null;
            int titheIdx = 0;
            foreach (KeyValuePair<ResourceTypeDef, double> kv in titheInjections)
            {
                if (kv.Key is null || kv.Value <= 0) continue;

                Rect titheRow = new Rect(0f, curY, viewRect.width, 26f);
                if (titheIdx % 2 == 0) Widgets.DrawHighlight(titheRow);
                Widgets.DrawBoxSolid(new Rect(0f, curY, AccentW, 26f), AccentUtil.Expense);

                float cx = AccentW + 4f;
                Text.Anchor = TextAnchor.MiddleLeft;
                if (kv.Key.Icon != null)
                    GUI.DrawTexture(new Rect(cx, curY + 3f, 20f, 20f), kv.Key.Icon);

                Widgets.Label(new Rect(cx + 24f, curY, 120f, 26f),
                    kv.Key.label.CapitalizeFirst());
                Widgets.Label(new Rect(cx + 148f, curY, 130f, 26f),
                    "SC_UnitsPerPeriod".Translate(kv.Value.ToString("F1")));

                double silverBudget = kv.Value * FCSettings.silverPerResource;
                float xBtnX = titheRow.xMax - 28f;
                GUI.color = new Color(0.7f, 0.85f, 1f);
                Widgets.Label(new Rect(cx + 284f, curY, xBtnX - (cx + 284f) - 4f, 26f),
                    "SC_TitheBudgetValue".Translate(silverBudget.ToString("F0")));
                GUI.color = Color.white;

                if (Widgets.ButtonText(new Rect(xBtnX, curY + 1f, 24f, 24f), "X"))
                {
                    if (titheToRemove is null) titheToRemove = new List<ResourceTypeDef>();
                    titheToRemove.Add(kv.Key);
                }

                Text.Anchor = TextAnchor.UpperLeft;
                curY += 28f;
                titheIdx++;
            }
            if (titheToRemove != null)
            {
                foreach (ResourceTypeDef def in titheToRemove)
                    SetTitheInjection(def, 0);
            }

            Widgets.EndScrollView();
        }

        // --- Complex Sub-Tab 4: Routes ---

        private void DrawComplexRoutes(Rect rect)
        {
            WorldComponent_SupplyChain wc = SupplyChainCache.Comp;
            if (wc is null) return;

            WorldSettlementFC ws = WorldSettlement;
            if (ws is null) return;

            // --- Direction toggle (fixed above scroll) ---
            Text.Font = GameFont.Tiny;
            float toggleW = rect.width / 2f;
            Rect fromRect = new Rect(rect.x, rect.y + 3f, toggleW, 24f);
            Rect toRect = new Rect(rect.x + toggleW, rect.y + 3f, toggleW, 24f);
            Rect currentRect = newRouteIsOutgoing ? fromRect : toRect;
            if (UIUtil.ButtonFlat(fromRect, "SC_DirectionFrom".Translate(), highlighted: newRouteIsOutgoing))
                newRouteIsOutgoing = true;
            if (UIUtil.ButtonFlat(toRect, "SC_DirectionTo".Translate(), highlighted: !newRouteIsOutgoing))
                newRouteIsOutgoing = false;

            UIUtil.DrawTabDecoratorHorizontalTop(currentRect, rect, Color.gray);

            // --- Add Route form (fixed above scroll) ---
            float addCurY = rect.y + 30f;
            DrawAddRouteFormFixed(rect.x, ref addCurY, rect.width, wc);

            // --- Resource filter buttons (fixed above scroll) ---
            float filterY = rect.y + 58f;
            float fbX = rect.x;
            float fbH = 22f;

            bool allActive = routeFilterResource == null;
            if (UIUtil.ButtonFlat(new Rect(fbX, filterY, 40f, fbH), (string)"SC_All".Translate(), highlighted: allActive))
                routeFilterResource = null;
            fbX += 44f;

            HashSet<ResourceTypeDef> routeResources = new HashSet<ResourceTypeDef>();
            foreach (SupplyRoute r in wc.SupplyRoutes)
            {
                if (!r.IsValid() || r.resource is null) continue;
                if (newRouteIsOutgoing && r.source == ws) routeResources.Add(r.resource);
                else if (!newRouteIsOutgoing && r.destination == ws) routeResources.Add(r.resource);
            }
            foreach (ResourceTypeDef filterDef in routeResources)
            {
                bool active = routeFilterResource == filterDef;
                ResourceTypeDef captured = filterDef;
                string btnLabel = filterDef.label.CapitalizeFirst();
                float btnW = (filterDef.Icon != null ? 20f : 0f) + Text.CalcSize(btnLabel).x + 10f;
                if (UIUtil.ButtonFlatIcon(new Rect(fbX, filterY, btnW, fbH), btnLabel,
                    filterDef.Icon, labelColor: filterDef.color, highlighted: active))
                    routeFilterResource = captured;
                fbX += btnW + 4f;
            }

            if (routeFilterResource != null && !routeResources.Contains(routeFilterResource))
                routeFilterResource = null;

            Text.Font = GameFont.Tiny;

            float fixedHeaderTotal = 82f; // toggle (26) + add form (28) + filter row (26) + gap (2)

            // --- Scrollable route list ---
            Rect scrollRect = new Rect(rect.x, rect.y + fixedHeaderTotal, rect.width, rect.height - fixedHeaderTotal);

            // Count routes for height estimate
            int routeCount = 0;
            foreach (SupplyRoute route in wc.SupplyRoutes)
            {
                if (!route.IsValid()) continue;
                if (routeFilterResource != null && route.resource != routeFilterResource) continue;
                if (newRouteIsOutgoing && route.source == ws) routeCount++;
                else if (!newRouteIsOutgoing && route.destination == ws) routeCount++;
            }

            float totalHeight = 4f + routeCount * 30f + 30f;
            float scrollMargin = totalHeight > scrollRect.height ? 16f : 0f;

            Rect viewRect = new Rect(0f, 0f, scrollRect.width - scrollMargin, totalHeight);
            Widgets.BeginScrollView(scrollRect, ref scrollPosRoutes, viewRect);
            float curY = 4f;

            float dualAccentStart = (AccentW * 2) + 6f;
            SupplyRoute routeToRemove = null;
            int routeIdx = 0;
            foreach (SupplyRoute route in wc.SupplyRoutes)
            {
                if (!route.IsValid()) continue;
                bool isOutgoing = route.source == ws;
                bool isIncoming = route.destination == ws;
                if (newRouteIsOutgoing && !isOutgoing) continue;
                if (!newRouteIsOutgoing && !isIncoming) continue;
                if (routeFilterResource != null && route.resource != routeFilterResource) continue;

                route.RecacheIfDirty();

                Rect rowRect = new Rect(0f, curY, viewRect.width, 28f);
                if (routeIdx % 2 == 0) Widgets.DrawHighlight(rowRect);

                // Dual accent bars: resource color + efficiency color
                float eff = (float)route.CachedEfficiency;
                Color routeAccent = route.resource?.color ?? Color.gray;
                Color effAccent = AccentUtil.GetStatColor(eff * 100f, false);
                Widgets.DrawBoxSolid(new Rect(0f, curY, AccentW, 28f), routeAccent);
                Widgets.DrawBoxSolid(new Rect(AccentW + 2f, curY, AccentW, 28f), effAccent);

                float cx = dualAccentStart;
                Text.Anchor = TextAnchor.MiddleLeft;

                // Direction label
                if (isOutgoing)
                {
                    GUI.color = new Color(1f, 0.85f, 0.6f);
                    Widgets.Label(new Rect(cx, curY, 30f, 26f), "SC_RouteOut".Translate());
                }
                else
                {
                    GUI.color = new Color(0.6f, 0.85f, 1f);
                    Widgets.Label(new Rect(cx, curY, 30f, 26f), "SC_RouteIn".Translate());
                }
                GUI.color = Color.white;

                // Resource icon + name
                if (route.resource != null && route.resource.Icon != null)
                    GUI.DrawTexture(new Rect(cx + 34f, curY + 4f, 20f, 20f), route.resource.Icon);

                string resName = route.resource != null ? route.resource.label.CapitalizeFirst() : "?";
                Widgets.Label(new Rect(cx + 58f, curY, 90f, 26f), resName);

                // Direction arrow + other settlement name
                string dirArrow = isOutgoing ? "\u2192" : "\u2190";
                GUI.color = isOutgoing ? new Color(1f, 0.85f, 0.6f) : new Color(0.6f, 0.85f, 1f);
                Widgets.Label(new Rect(cx + 150f, curY, 16f, 26f), dirArrow);
                GUI.color = Color.white;

                // Calculation pipeline: qty → eff% → net
                float pipeX = viewRect.width - 190f;
                float netVal = (float)(route.amountPerPeriod * route.CachedEfficiency);

                string otherName = isOutgoing ? route.destination.Name : route.source.Name;
                float nameW = pipeX - (cx + 168f) - 4f;
                Widgets.Label(new Rect(cx + 168f, curY, nameW, 26f), otherName);

                Text.Anchor = TextAnchor.MiddleCenter;
                // Base quantity
                Widgets.Label(new Rect(pipeX, curY, 34f, 26f), route.amountPerPeriod.ToString("F1"));
                pipeX += 34f;
                Widgets.Label(new Rect(pipeX, curY, 16f, 26f), "\u2192");
                pipeX += 16f;

                // Efficiency
                Rect efficiencyRect = new Rect(pipeX, curY, 52f, 26f);
                GUI.color = effAccent;
                Widgets.Label(efficiencyRect, "SC_EffLabel".Translate((eff * 100).ToString("F0")));
                GUI.color = Color.white;
                TooltipHandler.TipRegion(efficiencyRect, "SC_EffTooltip_Route".Translate());
                pipeX += 52f;

                // Arrow to net
                Widgets.Label(new Rect(pipeX, curY, 16f, 26f), "\u2192");
                pipeX += 16f;

                // Net value
                GUI.color = effAccent;
                Widgets.Label(new Rect(pipeX, curY, 34f, 26f), netVal.ToString("F1"));
                GUI.color = Color.white;

                // Remove button
                if (Widgets.ButtonText(new Rect(viewRect.width - 24f, curY + 1f, 22f, 24f), "X"))
                    routeToRemove = route;

                Text.Anchor = TextAnchor.UpperLeft;
                curY += 30f;
                routeIdx++;
            }

            if (routeToRemove != null)
            {
                wc.SupplyRoutes.Remove(routeToRemove);
                wc.DirtyFlowCache();
            }

            if (routeIdx == 0)
            {
                //Text.Font = GameFont.Tiny;
                GUI.color = Color.gray;
                Widgets.Label(new Rect(AccentW + 6f, curY, viewRect.width, 24f),
                    "SC_NoRoutesDirection".Translate());
                GUI.color = Color.white;
                //Text.Font = GameFont.Small;
                curY += 26f;
            }

            Widgets.EndScrollView();
        }

        // --- Complex Mode: Add Route (settlement-contextual) ---

        private void DrawAddRouteForm(Rect viewRect, ref float curY, WorldComponent_SupplyChain wc)
        {
            FactionFC faction = FactionCache.FactionComp;
            if (faction is null) return;

            WorldSettlementFC ws = WorldSettlement;

            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(0f, curY, 70f, 26f), "SC_NewRoute".Translate());

            float bx = 74f;

            // Resource picker
            string resLabel = newRouteResource != null
                ? newRouteResource.label.CapitalizeFirst()
                : (string)"SC_ResourcePicker".Translate();
            if (Widgets.ButtonText(new Rect(bx, curY, 110f, 24f), resLabel))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>();
                foreach (ResourceTypeDef def in SupplyChainCache.AllResourceTypeDefs)
                {
                    if (def.isPoolResource) continue;
                    ResourceTypeDef captured = def;
                    options.Add(new FloatMenuOption(def.label.CapitalizeFirst(), delegate { newRouteResource = captured; }));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }
            bx += 114f;

            // Other settlement picker
            float pickerW = viewRect.width - bx - 74f - 54f - 8f;
            if (pickerW < 120f) pickerW = 120f;

            string otherLabel = newRouteOther != null
                ? newRouteOther.Name
                : (string)"SC_PickSettlement".Translate();
            if (Widgets.ButtonText(new Rect(bx, curY, pickerW, 24f), otherLabel))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>();
                foreach (WorldSettlementFC s in faction.settlements)
                {
                    if (s == ws) continue;
                    WorldSettlementFC captured = s;
                    string label = s.Name;
                    if (newRouteResource != null)
                    {
                        if (newRouteIsOutgoing)
                        {
                            // Show need info for destination
                            foreach (SettlementNeedDef needDef in SupplyChainCache.AllNeedDefs)
                            {
                                if (needDef.UsesResource(newRouteResource))
                                {
                                    double demand = needDef.CalculateDemand(captured)
                                        * needDef.GetResourceFraction(FactionCache.FactionComp != null
                                            ? FactionCache.FactionComp.techLevel : TechLevel.Undefined, newRouteResource);
                                    label += " (need: " + demand.ToString("F1") + ")";
                                    break;
                                }
                            }
                        }
                        else
                        {
                            // Show production info for source
                            ResourceFC res = s.GetResource(newRouteResource);
                            if (res != null)
                                label += " (prod: " + res.rawTotalProduction.ToString("F1") + ")";
                        }
                    }
                    options.Add(new FloatMenuOption(label, delegate { newRouteOther = captured; }));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }
            bx += pickerW + 4f;

            // Amount
            Widgets.TextFieldNumeric(new Rect(bx, curY, 70f, 24f),
                ref newRouteAmount, ref newRouteAmountBuffer, 0f, 9999f);
            bx += 74f;

            // Add button
            if (Widgets.ButtonText(new Rect(bx, curY, 50f, 24f), "SC_Add".Translate()))
            {
                if (newRouteOther != null && newRouteResource != null && newRouteAmount > 0)
                {
                    WorldSettlementFC src = newRouteIsOutgoing ? ws : newRouteOther;
                    WorldSettlementFC dest = newRouteIsOutgoing ? newRouteOther : ws;
                    SupplyRoute route = new SupplyRoute(src, dest, newRouteResource, newRouteAmount);
                    wc.SupplyRoutes.Add(route);
                    wc.DirtyFlowCache();

                    newRouteOther = null;
                    newRouteResource = null;
                    newRouteAmount = 0;
                    newRouteAmountBuffer = "";
                }
            }

            Text.Anchor = TextAnchor.UpperLeft;
            curY += 28f;
        }

        /// <summary>
        /// Draws the add-route form at absolute screen coordinates (outside a scroll view).
        /// </summary>
        private void DrawAddRouteFormFixed(float x, ref float curY, float width, WorldComponent_SupplyChain wc)
        {
            FactionFC faction = FactionCache.FactionComp;
            if (faction is null) return;

            WorldSettlementFC ws = WorldSettlement;

            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(x, curY, 70f, 26f), "SC_NewRoute".Translate());

            float bx = x + 74f;

            // Resource picker
            string resLabel = newRouteResource != null
                ? newRouteResource.label.CapitalizeFirst()
                : (string)"SC_ResourcePicker".Translate();
            if (Widgets.ButtonText(new Rect(bx, curY, 110f, 24f), resLabel))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>();
                foreach (ResourceTypeDef def in SupplyChainCache.AllResourceTypeDefs)
                {
                    if (def.isPoolResource) continue;
                    ResourceTypeDef captured = def;
                    options.Add(new FloatMenuOption(def.label.CapitalizeFirst(), delegate { newRouteResource = captured; }));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }
            bx += 114f;

            // Other settlement picker
            float pickerW = width - (bx - x) - 74f - 54f - 8f;
            if (pickerW < 120f) pickerW = 120f;

            string otherLabel = newRouteOther != null
                ? newRouteOther.Name
                : (string)"SC_PickSettlement".Translate();
            if (Widgets.ButtonText(new Rect(bx, curY, pickerW, 24f), otherLabel))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>();
                foreach (WorldSettlementFC s in faction.settlements)
                {
                    if (s == ws) continue;
                    WorldSettlementFC captured = s;
                    string label = s.Name;
                    if (newRouteResource != null)
                    {
                        if (newRouteIsOutgoing)
                        {
                            foreach (SettlementNeedDef needDef in SupplyChainCache.AllNeedDefs)
                            {
                                if (needDef.UsesResource(newRouteResource))
                                {
                                    double demand = needDef.CalculateDemand(captured)
                                        * needDef.GetResourceFraction(FactionCache.FactionComp != null
                                            ? FactionCache.FactionComp.techLevel : TechLevel.Undefined, newRouteResource);
                                    label += " (need: " + demand.ToString("F1") + ")";
                                    break;
                                }
                            }
                        }
                        else
                        {
                            ResourceFC res = s.GetResource(newRouteResource);
                            if (res != null)
                                label += " (prod: " + res.rawTotalProduction.ToString("F1") + ")";
                        }
                    }
                    options.Add(new FloatMenuOption(label, delegate { newRouteOther = captured; }));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }
            bx += pickerW + 4f;

            // Amount
            Widgets.TextFieldNumeric(new Rect(bx, curY, 70f, 24f),
                ref newRouteAmount, ref newRouteAmountBuffer, 0f, 9999f);
            bx += 74f;

            // Add button
            if (Widgets.ButtonText(new Rect(bx, curY, 50f, 24f), "SC_Add".Translate()))
            {
                if (newRouteOther != null && newRouteResource != null && newRouteAmount > 0)
                {
                    WorldSettlementFC src = newRouteIsOutgoing ? ws : newRouteOther;
                    WorldSettlementFC dest = newRouteIsOutgoing ? newRouteOther : ws;
                    SupplyRoute route = new SupplyRoute(src, dest, newRouteResource, newRouteAmount);
                    wc.SupplyRoutes.Add(route);
                    wc.DirtyFlowCache();

                    newRouteOther = null;
                    newRouteResource = null;
                    newRouteAmount = 0;
                    newRouteAmountBuffer = "";
                }
            }

            Text.Anchor = TextAnchor.UpperLeft;
            curY += 28f;
        }

        // --- Shared: Allocation Sliders ---

        private void DrawAllocationSliders(Rect viewRect, ref float curY, float rowHeight)
        {
            int idx = 0;
            foreach (ResourceFC resource in uiSettlement.Resources)
            {
                ResourceTypeDef def = resource.def;
                double currentAlloc = GetAllocation(def);
                double rawProd = resource.rawTotalProduction;
                double otherAllocs = resource.totalStockpileAllocation - currentAlloc;
                if (otherAllocs < 0) otherAllocs = 0;
                double maxAlloc = rawProd - otherAllocs;
                if (maxAlloc < 0) maxAlloc = 0;

                Rect row = new Rect(0f, curY, viewRect.width, rowHeight);

                // Alternating row highlights
                if (idx % 2 == 0) Widgets.DrawHighlight(row);

                // Resource-colored accent bar
                Color resColor = def.color != default(Color) ? def.color : Color.gray;
                Widgets.DrawBoxSolid(new Rect(0f, curY, AccentW, rowHeight), resColor);

                float cx = AccentW + 4f;

                if (def.Icon != null)
                    GUI.DrawTexture(new Rect(cx, curY + 2f, 24f, 24f), def.Icon);

                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(new Rect(cx + 28f, curY, 120f, rowHeight),
                    def.label.CapitalizeFirst());

                bool autoMax = IsAutoMax(def);

                if (autoMax)
                {
                    Rect badgeRect = new Rect(cx + 150f, curY + 6f, 240f, rowHeight - 12f);
                    Color badgeBg = new Color(resColor.r * 0.45f, resColor.g * 0.45f, resColor.b * 0.45f, 0.85f);
                    Widgets.DrawBoxSolid(badgeRect, badgeBg);
                    Text.Anchor = TextAnchor.MiddleCenter;
                    Widgets.Label(badgeRect, "SC_MaxBadge".Translate());
                    Text.Anchor = TextAnchor.MiddleLeft;

                    Widgets.Label(new Rect(cx + 400f, curY, 90f, rowHeight),
                        currentAlloc.ToString("F1") + " / " + rawProd.ToString("F1"));

                    float silverDiverted = (float)(currentAlloc * FCSettings.silverPerResource);
                    if (silverDiverted >= 0.5f)
                    {
                        Text.Font = GameFont.Tiny;
                        GUI.color = new Color(1f, 0.7f, 0.3f);
                        Widgets.Label(new Rect(cx + 525f, curY, 80f, rowHeight),
                            "SC_SilverDiverted".Translate(silverDiverted.ToString("F0")));
                        GUI.color = Color.white;
                        Text.Font = GameFont.Small;
                    }
                }
                else if (rawProd > 0)
                {
                    float sliderVal = (float)Math.Round(currentAlloc,1);
                    // Snap max down to the 0.1 rounding grid so the slider's
                    // internal rounding never pushes the value above max
                    // (which causes an infinite audio-cue loop).
                    float maxSlider = Mathf.Floor((float)maxAlloc * 10f) / 10f;
                    if (maxSlider < 0f) maxSlider = 0f;

                    float newVal = Widgets.HorizontalSlider(
                        new Rect(cx + 150f, curY + 8f, 240f, rowHeight - 16f),
                        sliderVal, 0f, maxSlider, false,
                        null, null, null, 0.1f);

                    newVal = MathF.Round(newVal, 1);

                    if (newVal > maxSlider)
                        newVal = maxSlider;

                    if (Math.Abs(newVal - sliderVal) > 0.01f)
                    {
                        SetAllocation(def, newVal);
                    }

                    Widgets.Label(new Rect(cx + 400f, curY, 90f, rowHeight),
                        currentAlloc.ToString("F1") + " / " + rawProd.ToString("F1"));

                    float silverDiverted = (float)(currentAlloc * FCSettings.silverPerResource);
                    if (silverDiverted >= 0.5f)
                    {
                        Text.Font = GameFont.Tiny;
                        GUI.color = new Color(1f, 0.7f, 0.3f);
                        Widgets.Label(new Rect(cx + 525f, curY, 80f, rowHeight),
                            "SC_SilverDiverted".Translate(silverDiverted.ToString("F0")));
                        GUI.color = Color.white;
                        Text.Font = GameFont.Small;
                    }
                }
                else
                {
                    GUI.color = Color.gray;
                    Text.Anchor = TextAnchor.MiddleCenter;
                    Widgets.Label(new Rect(cx + 150f, curY, 240f, rowHeight), "SC_NoProduction".Translate());
                    Text.Anchor = TextAnchor.MiddleLeft;
                    GUI.color = Color.white;
                }

                // Auto-max toggle (between number and silver slots, inside the visible row)
                const float autoBoxSize = 22f;
                Vector2 autoBoxPos = new Vector2(cx + 495f, curY + (rowHeight - autoBoxSize) / 2f);
                Rect autoBoxRect = new Rect(autoBoxPos.x, autoBoxPos.y, autoBoxSize, autoBoxSize);
                bool autoMaxNow = autoMax;
                Widgets.Checkbox(autoBoxPos, ref autoMaxNow, autoBoxSize);
                TooltipHandler.TipRegion(autoBoxRect, "SC_AutoMaxTooltip".Translate());
                if (autoMaxNow != autoMax)
                {
                    SetAutoMax(def, autoMaxNow);
                }

                Text.Anchor = TextAnchor.UpperLeft;
                curY += rowHeight;
                idx++;
            }
        }

        // --- Shared: Needs Display ---

        private const float NeedRowH = 40f;
        private const float NeedRowStep = 42f;
        private const float NeedTopLineH = 22f;
        private const float NeedBotLineH = 16f;

        private void DrawNeedsSection(Rect viewRect, ref float curY)
        {
            if (needStates.Count == 0) return;

            // Pre-compute projected fill rates per resource
            WorldComponent_SupplyChain wc = SupplyChainCache.Comp;
            bool isComplex = wc != null && wc.Mode == SupplyChainMode.Complex;
            FactionFC faction = FactionCache.FactionComp;
            WorldSettlementFC ws = WorldSettlement;

            Dictionary<ResourceTypeDef, float> projectedRates = new Dictionary<ResourceTypeDef, float>();
            foreach (NeedState state in needStates)
            {
                if (state.resource is null || projectedRates.ContainsKey(state.resource))
                    continue;

                float rate;
                if (isComplex)
                    rate = NeedProjection.ProjectedFillRate(wc, ws, this, state.resource);
                else
                    rate = NeedProjection.ProjectedFillRateSimple(wc, faction, state.resource);

                projectedRates[state.resource] = rate;
            }

            Text.Font = GameFont.Medium;
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(new Rect(AccentW + 6f, curY, viewRect.width, 30f), "SC_SettlementNeeds".Translate());
            Text.Font = GameFont.Small;
            curY += 34f;

            if (!hasCompletedFirstTax)
            {
                Text.Anchor = TextAnchor.MiddleCenter;
                GUI.color = GracePeriodText;
                Widgets.Label(new Rect(AccentW + 6f, curY, viewRect.width - AccentW - 12f, 22f), CachedFoundingGrace);
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
                curY += 24f;
            }

            int idx = 0;
            foreach (NeedState state in needStates)
            {
                if (state.resource is null) continue;

                if (!projectedRates.TryGetValue(state.resource, out float projected))
                    projected = state.Satisfaction;
                float actual = state.Satisfaction;
                float satisfaction = projected;

                Rect rowRect = new Rect(0f, curY, viewRect.width, NeedRowH);
                if (idx % 2 == 0) Widgets.DrawHighlight(rowRect);

                // Left accent bar spans full row height
                Color needAccent = satisfaction > 0.8f ? AccentPositive
                    : satisfaction > 0.4f ? new Color(0.9f, 0.8f, 0.2f)
                    : AccentNegative;
                Widgets.DrawBoxSolid(new Rect(0f, curY, AccentW, NeedRowH), needAccent);

                float cx = AccentW + 4f;

                // --- Top line: icon + label + bar + percentage ---
                float topY = curY + 1f;

                if (state.resource.Icon != null)
                    GUI.DrawTexture(new Rect(cx, topY + 1f, 20f, 20f), state.resource.Icon);

                Text.Anchor = TextAnchor.MiddleLeft;
                Rect labelRect = new Rect(cx + 24f, topY, 140f, NeedTopLineH);
                Widgets.Label(labelRect, Text.ClampTextWithEllipsis(labelRect, state.label));

                float barX = cx + 168f;
                float barW = viewRect.width - barX - 60f;
                if (barW < 80f) barW = 80f;
                Rect barRect = new Rect(barX, topY + 3f, barW, NeedTopLineH - 6f);
                if (satisfaction > 0.8f)
                    GUI.color = new Color(0.4f, 0.8f, 0.4f);
                else if (satisfaction > 0.4f)
                    GUI.color = new Color(0.9f, 0.8f, 0.2f);
                else
                    GUI.color = new Color(0.9f, 0.3f, 0.3f);
                Widgets.FillableBar(barRect, satisfaction);
                GUI.color = Color.white;

                // Percentage right of bar
                Text.Anchor = TextAnchor.MiddleRight;
                Widgets.Label(new Rect(barRect.xMax + 4f, topY, 50f, NeedTopLineH),
                    (satisfaction * 100f).ToString("F0") + "%");

                // --- Bottom line: projection detail + penalties ---
                float botY = curY + NeedTopLineH + 2f;
                Text.Font = GameFont.Tiny;
                Text.Anchor = TextAnchor.MiddleLeft;

                string statusText;
                if (state.demanded > 0 && Math.Abs(satisfaction - actual) > 0.005f)
                {
                    statusText = "SC_SatisfactionProjected".Translate(
                        (satisfaction * 100f).ToString("F0"),
                        (actual * 100f).ToString("F0"));
                }
                else
                {
                    statusText = (string)"SC_SatisfactionDisplay".Translate(
                        (satisfaction * 100f).ToString("F0"),
                        state.fulfilled.ToString("F1"),
                        state.demanded.ToString("F1"));
                }
                GUI.color = new Color(0.7f, 0.7f, 0.7f);
                Widgets.Label(new Rect(cx + 24f, botY, 200f, NeedBotLineH), statusText);
                GUI.color = Color.white;

                // Penalty summary on bottom-right
                if (satisfaction < 1f)
                {
                    GUI.color = new Color(1f, 0.5f, 0.5f);
                    double projectedShortfall = state.demanded * (1.0 - satisfaction);
                    string penaltyText = GetProjectedPenaltySummary(state, projectedShortfall);
                    if (penaltyText != null)
                    {
                        Rect penaltyRect = new Rect(cx + 228f, botY, viewRect.width - cx - 232f, NeedBotLineH);
                        Text.Anchor = TextAnchor.MiddleRight;
                        Widgets.Label(penaltyRect, Text.ClampTextWithEllipsis(penaltyRect, penaltyText));
                    }
                    GUI.color = Color.white;
                }

                Text.Font = GameFont.Small;

                // Tooltip
                string tooltip = BuildNeedTooltip(state, satisfaction, actual);
                if (tooltip != null)
                    TooltipHandler.TipRegion(rowRect, tooltip);

                Text.Anchor = TextAnchor.UpperLeft;
                curY += NeedRowStep;
                idx++;
            }
        }

        private string BuildNeedTooltip(NeedState state, float projected, float actual)
        {
            WorldSettlementFC ws = WorldSettlement;
            if (ws is null) return null;

            string displayLabel = state.label;

            string tip;
            if (state.category == NeedCategory.Building)
            {
                tip = displayLabel + ": " + state.demanded.ToString("F1") + " " + state.resource.label;
            }
            else
            {
                // Base/comp needs: show scaling breakdown if a SettlementNeedDef exists
                SettlementNeedDef needDef = state.needDef;
                if (needDef != null)
                {
                    string scalingDesc;
                    switch (needDef.scaling)
                    {
                        case NeedScaling.PerWorker:
                            string popLabel = (SupplyChainSettings.useMaxWorkersForNeeds && ws.workersMax > ws.workers)
                                ? ws.workersMax.ToString("F0") + " " + (string)"SC_MaxWorkersSuffix".Translate()
                                : ws.workers.ToString("F0");
                            scalingDesc = needDef.baseAmount.ToString("F1") + " per worker x " + popLabel + " = " + state.demanded.ToString("F1");
                            break;
                        case NeedScaling.PerLevel:
                            scalingDesc = needDef.baseAmount.ToString("F1") + " per level x " + ws.settlementLevel + " = " + state.demanded.ToString("F1");
                            break;
                        default:
                            scalingDesc = needDef.baseAmount.ToString("F1") + " (flat)";
                            break;
                    }
                    tip = displayLabel + "\n" + scalingDesc;
                }
                else
                {
                    tip = displayLabel + ": " + state.demanded.ToString("F1") + " " + state.resource.label;
                }
            }

            // Projected penalties
            if (projected < 1f && state.penalties != null && state.demanded > 0)
            {
                tip += "\n\n" + (string)"SC_NeedProjectionExplain".Translate(
                    (projected * 100f).ToString("F0"));
                double projectedShortfall = state.demanded * (1.0 - projected);
                if (!hasCompletedFirstTax)
                    tip += "\n\n" + (string)"SC_ProjectedPenaltiesWaived".Translate();
                else
                    tip += "\n\n" + (string)"SC_ProjectedPenalties".Translate();
                foreach (NeedPenalty penalty in state.penalties)
                {
                    double penaltyVal = penalty.penaltyPerUnit * projectedShortfall;
                    tip += "\n  " + (penalty.label ?? penalty.stat.label) + ": -" + penaltyVal.ToString("F1");
                }
            }

            // Show last-cycle actual if it differs
            if (state.demanded > 0 && Math.Abs(projected - actual) > 0.005f)
            {
                tip += "\n\n" + (string)"SC_NeedActualLast".Translate(
                    (actual * 100f).ToString("F0"));
            }

            return tip;
        }

        private string GetProjectedPenaltySummary(NeedState state, double shortfall)
        {
            if (state.penalties is null || shortfall <= 0)
                return null;
            string result = null;
            foreach (NeedPenalty penalty in state.penalties)
            {
                double val = penalty.penaltyPerUnit * shortfall;
                string displayLabel = penalty.label ?? penalty.stat.label;
                string part = "SC_PenaltyLine".Translate(val.ToString("F1"), displayLabel);
                result = result is null ? part : result + ", " + part;
            }
            return result;
        }

        // --- Complex Mode: Add Local Sell Order ---

        private void DrawAddLocalSellOrderRow(Rect viewRect, ref float curY)
        {
            // Center the add-row widgets: Add: [picker] [amount] [Add]
            const float groupW = 40f + 4f + 130f + 6f + 80f + 8f + 60f; // 328
            float sx = (viewRect.width - groupW) / 2f;

            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(sx, curY, 40f, 26f), "SC_AddColon".Translate());

            string resLabel = newLocalSellResource != null ? newLocalSellResource.label.CapitalizeFirst() : (string)"SC_PickResource".Translate();
            if (Widgets.ButtonText(new Rect(sx + 44f, curY, 130f, 24f), resLabel))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>();
                foreach (ResourceTypeDef def in SupplyChainCache.AllResourceTypeDefs)
                {
                    if (def.isPoolResource) continue;
                    ResourceTypeDef captured = def;
                    options.Add(new FloatMenuOption(def.label.CapitalizeFirst(), delegate
                    {
                        newLocalSellResource = captured;
                    }));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }

            Widgets.TextFieldNumeric(new Rect(sx + 180f, curY, 80f, 24f),
                ref newLocalSellAmount, ref newLocalSellAmountBuffer, 0f, 9999f);

            if (Widgets.ButtonText(new Rect(sx + 268f, curY, 60f, 24f), "SC_Add".Translate()))
            {
                if (newLocalSellResource != null && newLocalSellAmount > 0)
                {
                    localSellOrders.Add(new SellOrder(newLocalSellResource, newLocalSellAmount));
                    SupplyChainCache.Comp?.DirtyFlowCache();
                    newLocalSellResource = null;
                    newLocalSellAmount = 0;
                    newLocalSellAmountBuffer = "";
                }
            }

            Text.Anchor = TextAnchor.UpperLeft;
            curY += 28f;
        }

        private void DrawAddTitheInjectionRow(Rect viewRect, ref float curY)
        {
            // Center the add-row widgets: Add: [picker] [amount] [Add]
            const float groupW = 40f + 4f + 130f + 6f + 80f + 8f + 60f; // 328
            float sx = (viewRect.width - groupW) / 2f;

            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(sx, curY, 40f, 26f), "SC_AddColon".Translate());

            string resLabel = newTitheInjResource != null ? newTitheInjResource.label.CapitalizeFirst() : (string)"SC_PickResource".Translate();
            if (Widgets.ButtonText(new Rect(sx + 44f, curY, 130f, 24f), resLabel))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>();
                foreach (ResourceTypeDef def in SupplyChainCache.AllResourceTypeDefs)
                {
                    if (def.isPoolResource) continue;
                    ResourceTypeDef captured = def;
                    options.Add(new FloatMenuOption(def.label.CapitalizeFirst(), delegate
                    {
                        newTitheInjResource = captured;
                    }));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }

            Widgets.TextFieldNumeric(new Rect(sx + 180f, curY, 80f, 24f),
                ref newTitheInjAmount, ref newTitheInjAmountBuffer, 0f, 9999f);

            if (Widgets.ButtonText(new Rect(sx + 268f, curY, 60f, 24f), "SC_Add".Translate()))
            {
                if (newTitheInjResource != null && newTitheInjAmount > 0)
                {
                    SetTitheInjection(newTitheInjResource, newTitheInjAmount);
                    newTitheInjResource = null;
                    newTitheInjAmount = 0;
                    newTitheInjAmountBuffer = "";
                }
            }

            Text.Anchor = TextAnchor.UpperLeft;
            curY += 28f;
        }
    }
}
