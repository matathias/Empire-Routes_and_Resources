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

    /*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*/
    /* Settlement resource-ledger comp.                                       */
    /*                                                                        */
    /* Owns the cohesive economic state read together by the flow & tax       */
    /* resolution: the local stockpile + caps, production allocations         */
    /* (auto-max), local sell orders, tithe injections, and trade-network     */
    /* info. The needs subsystem lives in WorldObjectComp_SettlementNeeds and */
    /* the settlement tab in WorldObjectComp_SupplyChainUI; both read this    */
    /* comp via SupplyChainCache.                                             */
    /*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*/
    public class WorldObjectComp_SupplyChain : WorldObjectComp, IStatModifierProvider, ITitheBudgetModifier, ISettlementPostLoadInit
    {
        private const string ALLOC_KEY_PREFIX = "SupplyChain.";

        private Dictionary<ResourceTypeDef, double> allocations = new Dictionary<ResourceTypeDef, double>();
        private HashSet<ResourceTypeDef> autoMaxResources = new HashSet<ResourceTypeDef>();
        // Manual value snapshot taken when auto-max is enabled; restored when the player turns it off.
        private Dictionary<ResourceTypeDef, double> autoMaxFallback = new Dictionary<ResourceTypeDef, double>();

        // Tithe injection: how many stockpile units per resource per day to convert to tithe budget
        private Dictionary<ResourceTypeDef, double> titheInjections = new Dictionary<ResourceTypeDef, double>();
        // Stores the actual amount drawn in the most recent daily consume step (may be less than
        // configured if the stockpile was short). Read by GetDailyExternalTitheBudget. Transient.
        private Dictionary<ResourceTypeDef, double> lastTitheDrawn = new Dictionary<ResourceTypeDef, double>();
        // Per-resource consecutive-shortfall day counter for owner-initiated allocation self-reduction. Transient.
        private Dictionary<ResourceTypeDef, int> shortfallDays = new Dictionary<ResourceTypeDef, int>();
        private const int SHORTFALL_REDUCE_DAYS = 5;

        // Complex mode fields
        private Dictionary<ResourceTypeDef, double> localStockpiles = new Dictionary<ResourceTypeDef, double>();
        private Dictionary<ResourceTypeDef, double> localCaps = new Dictionary<ResourceTypeDef, double>();
        private List<SellOrder> localSellOrders = new List<SellOrder>();
        private DictionaryStockpile localStockpileDict;

        private bool localCapsDirty = true;

        // Trade network. connectedPartners/hubScore are the serialized derived bonuses; the partner
        // sets are transient adjacency, rebuilt from the route list on load and maintained
        // incrementally as routes are created/deleted (see WorldComponent_SupplyChain link/unlink).
        private int connectedPartners;
        private int hubScore;
        private HashSet<WorldSettlementFC> outPartners = new HashSet<WorldSettlementFC>();
        private HashSet<WorldSettlementFC> inPartners = new HashSet<WorldSettlementFC>();
        public IEnumerable<WorldSettlementFC> OutPartners => outPartners;
        public IEnumerable<WorldSettlementFC> InPartners => inPartners;

        private WorldSettlementFC cachedSettlement;

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
        /// Complex mode: returns the local stockpile, lazily creating the wrapper (and refreshing
        /// caps) if it hasn't been initialized yet. The wrapper is transient (not serialized), so
        /// after a load the daily realize() deposit may be the first thing to touch it.
        /// </summary>
        public IStockpile EnsureLocalStockpile()
        {
            RecalculateLocalCapsIfDirty();
            if (localStockpileDict is null)
                InitLocalStockpile();
            return localStockpileDict;
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

        // --- Trade Network ---

        public void SetNetworkInfo(int partners, int hub)
        {
            // Early-out on unchanged values so daily/bulk recomputes don't needlessly thrash the
            // stat cache — only settlements whose bonuses actually changed get invalidated.
            if (partners == connectedPartners && hub == hubScore) return;
            connectedPartners = partners;
            hubScore = hub;
            statModsDirty = true;
            // The settlement bakes our IStatModifierProvider contributions into its own stat cache, so
            // the network bonuses only refresh once that cache is invalidated — do it now, not next daily pass.
            WorldSettlement?.InvalidateStatCache();
        }

        // --- Incremental partner-set maintenance ---
        // Mutators recompute only when membership actually changes (HashSet.Add/Remove report it),
        // so a second route to an already-connected partner is a no-op.

        public void AddOutPartner(WorldSettlementFC s) { if (outPartners.Add(s)) RecomputeNetwork(); }
        public void RemoveOutPartner(WorldSettlementFC s) { if (outPartners.Remove(s)) RecomputeNetwork(); }
        public void AddInPartner(WorldSettlementFC s) { if (inPartners.Add(s)) RecomputeNetwork(); }
        public void RemoveInPartner(WorldSettlementFC s) { if (inPartners.Remove(s)) RecomputeNetwork(); }

        public void ClearPartners()
        {
            bool any = outPartners.Count > 0 || inPartners.Count > 0;
            outPartners.Clear();
            inPartners.Clear();
            if (any) RecomputeNetwork();
        }

        private void RecomputeNetwork()
        {
            int outC = outPartners.Count;
            int inC = inPartners.Count;
            int partners = outC; // distinct union of in and out neighbours
            foreach (WorldSettlementFC s in inPartners)
                if (!outPartners.Contains(s)) partners++;
            int hub = outC < inC ? outC : inC;
            SetNetworkInfo(partners, hub);
        }

        // --- IStatModifierProvider (trade-network slice) ---
        // Needs-derived stat modifiers live on WorldObjectComp_SettlementNeeds; the stat
        // system sums IStatModifierProvider contributions across all comps on the settlement.

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
                // Trade network bonuses (Complex mode only — 0 in Simple)
                if (stat == FCStatDefOf.happinessGainedBase)
                    value += FormulaUtil.HappinessNetworkBonus(connectedPartners);
                else if (stat == FCStatDefOf.prosperityGainedBase)
                    value += FormulaUtil.ProsperityNetworkBonus(hubScore);
            }
            else // Multiplicative
            {
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

        /// <summary>
        /// Pure getter: the per-resource silver value drawn in the most recent daily consume step.
        /// Sampled daily by the base mod and accrued; reports the previous day's draw (a harmless
        /// one-day lag, since the cycle's accrued external budget is the sum of daily draws either way).
        /// </summary>
        public double GetDailyExternalTitheBudget(ResourceFC resource)
        {
            if (resource?.def is null || !resource.def.CanTithe)
                return 0;

            return lastTitheDrawn.TryGetValue(resource.def, out double drawn)
                ? drawn * FCSettings.silverPerResource
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
        /// Called daily by WorldComponent_SupplyChain.PostDailyAccrual. Draws this day's tithe
        /// injection (per-day units) from the stockpile and records actual amounts for
        /// GetDailyExternalTitheBudget (read the following day).
        /// </summary>
        public void ResolveTitheInjections(IStockpile stockpile)
        {
            lastTitheDrawn.Clear();
            WorldSettlementFC ws = WorldSettlement;

            foreach (KeyValuePair<ResourceTypeDef, double> kv in titheInjections)
            {
                if (kv.Key is null || !kv.Key.CanTithe || kv.Value <= 0) continue;

                stockpile.TryDraw(kv.Key, kv.Value, out double drawn);

                if (drawn > 0)
                    lastTitheDrawn[kv.Key] = drawn;

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
        /// Returns the per-day headroom this comp can claim for the given resource if its current
        /// registered amount were ignored: per-day rawProduction minus other submods' per-day
        /// allocations. Under the daily-cadence base mod both terms are per-day, so the comparison
        /// is per-day-vs-per-day (the latent cross-scale inconsistency the rework fixed).
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
                autoMaxFallback.Remove(def);
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

            resource.SetStockpileAllocation(key, live, (req, act) => Realize(def, req, act));
            allocations[def] = live;
            SupplyChainCache.Comp?.DirtyFlowCache();
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

            resource.SetStockpileAllocation(key, amount, (req, act) => Realize(def, req, act));
            allocations[def] = amount;
            SupplyChainCache.Comp?.DirtyFlowCache();
            return true;
        }

        /// <summary>
        /// Per-day deposit callback fired by the base mod for each registered allocation, with
        /// (requested, actual) where actual = min(allocation, that day's production). Deposits the
        /// full <paramref name="actual"/> into the target stockpile (Simple: faction; Complex: local)
        /// WITHOUT clamping to cap, and tracks sustained shortfall. Selling is deferred: the daily
        /// consume pass feeds needs/routes from the deposited production first, then sweeps whatever
        /// storage still cannot hold to silver (produce -> consume -> sell only true surplus).
        /// Depositing over-cap here is what lets today's production cover today's routes/needs instead
        /// of being liquidated before consumption ever runs. The base mod never evicts — a persistently
        /// unfillable manual allocation self-reduces here (owner-initiated).
        /// </summary>
        public void Realize(ResourceTypeDef def, double requested, double actual)
        {
            if (def is null) return;

            if (actual > 0)
            {
                WorldComponent_SupplyChain wc = SupplyChainCache.Comp;
                if (wc != null && wc.Mode == SupplyChainMode.Simple)
                {
                    wc.AddToFactionStockpile(def, actual);
                }
                else
                {
                    EnsureLocalStockpile().Add(def, actual);
                }
            }

            TrackShortfall(def, requested, actual);
        }

        /// <summary>
        /// Counts consecutive days a (non-auto-max) allocation under-delivers and, once the
        /// shortfall persists past SHORTFALL_REDUCE_DAYS, reduces the allocation to what actually
        /// flowed. Auto-max allocations re-sync on their own, so they are left alone.
        /// </summary>
        private void TrackShortfall(ResourceTypeDef def, double requested, double actual)
        {
            const double EPS = 0.01;
            if (requested - actual <= EPS || autoMaxResources.Contains(def))
            {
                shortfallDays.Remove(def);
                return;
            }

            shortfallDays.TryGetValue(def, out int days);
            days++;
            if (days >= SHORTFALL_REDUCE_DAYS)
            {
                shortfallDays.Remove(def);
                LogSC.Message($"Reducing persistently-short allocation for {def.label} at {WorldSettlement?.Name ?? "unknown"} to {actual:F1}/day.");
                SetAllocation(def, actual);
            }
            else
            {
                shortfallDays[def] = days;
            }
        }

        /// <summary>
        /// Re-registers all saved allocations with the base mod's SetStockpileAllocation API.
        /// Called from PostSettlementLoadInit to restore transient state after load.
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
                ResourceTypeDef capturedDef = kv.Key;
                resource.SetStockpileAllocation(key, clamped, (req, act) => Realize(capturedDef, req, act));

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
                defaultDesc = "SC_ShowAllRoutesDesc".Translate(FindFC.EmpireTitle),
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

            Scribe_Values.Look(ref connectedPartners, "connectedPartners", 0);
            Scribe_Values.Look(ref hubScore, "hubScore", 0);
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
        }
    }
}
