using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using System;

namespace FactionColonies.SupplyChain
{
    public class WorldComponent_SupplyChain : WorldComponent, ITaxTickParticipant, IDailyAccrualParticipant, IMainTabWindowOverview, ISettlementListener, IResearchListener
    {
        private SupplyChainMode mode = SupplyChainMode.Simple;
        private Dictionary<ResourceTypeDef, double> factionStockpile = new Dictionary<ResourceTypeDef, double>();
        private Dictionary<ResourceTypeDef, double> factionCaps = new Dictionary<ResourceTypeDef, double>();
        private List<SellOrder> globalSellOrders = new List<SellOrder>();

        // Complex mode
        private List<SupplyRoute> supplyRoutes = new List<SupplyRoute>();
        private List<SupplyRoute> dormantRoutes = new List<SupplyRoute>();

        // In-transit deliveries (Complex mode). Stored here rather than as FCEvents so they never
        // appear in the base mod's Events tab; ticked down daily in DailyConsume_Complex.
        private List<PendingDelivery> pendingDeliveries = new List<PendingDelivery>();
        public List<PendingDelivery> PendingDeliveries => pendingDeliveries;

        // Monotonic id source giving each PendingDelivery a stable unique load id (for the caravan cross-ref).
        private int nextDeliveryId;

        // Background recompute of route travel times/paths (transient). Fed each tick with the active
        // routes; keeps their caches warm off the UI thread. lastSeenRoadVersion detects road changes.
        private readonly SupplyRouteWarmer routeWarmer = new SupplyRouteWarmer();
        private int lastSeenRoadVersion;

        // Route visualization (transient, not saved)
        public bool showAllRoutes;
        public bool showSelectedRoutes;
        public bool showRouteLabels;

        // Pair caches: one representative route + one combined label per directed settlement pair
        private Dictionary<DirectedPlanetTilePair, SupplyRoute> pairRouteCache = new Dictionary<DirectedPlanetTilePair, SupplyRoute>();
        private Dictionary<DirectedPlanetTilePair, string> pairLabelCache = new Dictionary<DirectedPlanetTilePair, string>();
        private bool pairCacheDirty = true;

        private bool capsAndStockpilesDirty = true;
        private DictionaryStockpile stockpile;

        // Flow cache: keyed by (settlement PlanetTile, resourceDefIndex)
        private Dictionary<PlanetTileResourceKey, FlowBreakdown> flowCache = new Dictionary<PlanetTileResourceKey, FlowBreakdown>();
        private bool flowCacheDirty = true;

        // Simple-mode flow cache: keyed by resource def index
        private Dictionary<ushort, FlowBreakdown> simpleFlowCache = new Dictionary<ushort, FlowBreakdown>();

        // Resource columns cache for DrawComplexStockpiles
        private List<ResourceTypeDef> cachedResourceColumns;
        private bool resourceColumnsDirty = true;

        // UI state (not saved)
        private FactionFC uiFaction;
        private Vector2 scrollPos;
        private ResourceTypeDef newSellOrderResource;
        private string newSellOrderAmountBuffer = "";
        private float newSellOrderAmount;

        // Simple mode tithe injection UI state
        private Vector2 scrollPosSimple;
        private WorldSettlementFC newTitheSettlement;
        private ResourceTypeDef newTitheResource;
        private string newTitheAmountBuffer = "";
        private float newTitheAmount;

        // Complex mode tab state
        private int complexTab = 0; // 0 = Stockpiles, 1 = Routes, 2 = Deliveries
        private Vector2 scrollPosStockpiles;
        private Vector2 scrollPosRoutes;
        private Vector2 scrollPosDeliveries;

        // Route creation UI state
        private WorldSettlementFC newRouteSource;
        private WorldSettlementFC newRouteDest;
        private ResourceTypeDef newRouteResource;
        private string newRouteAmountBuffer = "";
        private float newRouteAmount;
        private int newRouteFrequency = SupplyChainSettings.defaultRouteFrequencyDays;
        private string newRouteFreqBuffer = "";
        private ResourceTypeDef routeFilterResource;

        private bool thresholdLetterSent;

        public WorldComponent_SupplyChain(World world) : base(world)
        {
        }

        private FoundingCostValidator foundingValidator;

        public SupplyChainMode Mode => mode;
        public IStockpile Stockpile => stockpile;
        public IReadOnlyList<SupplyRoute> SupplyRoutes => supplyRoutes;
        public FoundingCostValidator FoundingValidator => foundingValidator;
        
        // --- Lifecycle ---

        public override void FinalizeInit(bool fromLoad)
        {
            base.FinalizeInit(fromLoad);

            if (factionStockpile == null)
                factionStockpile = new Dictionary<ResourceTypeDef, double>();
            if (factionCaps == null)
                factionCaps = new Dictionary<ResourceTypeDef, double>();
            if (globalSellOrders == null)
                globalSellOrders = new List<SellOrder>();
            if (supplyRoutes == null)
                supplyRoutes = new List<SupplyRoute>();
            if (dormantRoutes == null)
                dormantRoutes = new List<SupplyRoute>();
            if (pendingDeliveries == null)
                pendingDeliveries = new List<PendingDelivery>();

            stockpile = new DictionaryStockpile(factionStockpile, factionCaps);

            RegisterWithRegistries();

            SupplyChainCache.ClearCompCache();
            capsAndStockpilesDirty = true;

            // Reconcile with global settings (mode may have changed while this save was unloaded).
            // Only the new-game path runs here: on load, SwitchMode redistributes stockpiles across
            // settlements whose cross-refs aren't resolved until after FinalizeInit, so the load-path
            // reconciliation runs from ExposeData's PostLoadInit pass instead.
            if (!fromLoad && mode != SupplyChainSettings.mode)
            {
                LogSC.MessageForce("Mode mismatch: current=" + mode + ", settings=" + SupplyChainSettings.mode + ". Switching.");
                SwitchMode(SupplyChainSettings.mode);
            }

            // Handle adding Routes & Resources mid-save
            if (fromLoad && !loadedFromSave && Scribe.mode == LoadSaveMode.LoadingVars)
            {
                LogSC.MessageForce("Detected mid-save add (component not in save). Registering for PostLoadInit reconciliation.");
                Scribe.loader.initer.RegisterForPostLoadInit(this);
            }

            // Warm up local stockpile wrappers / faction caps, the transient trade-network partner
            // sets, and the delivery caravans. This call covers the new-game path; on load these are
            // no-ops here because the settlement/route cross-refs aren't resolved until after
            // FinalizeInit, so the load-path warm-up runs from ExposeData's PostLoadInit pass instead.
            EnsureCapsAndStockpiles();
            if (mode == SupplyChainMode.Complex)
                RebuildAllPartnerSets(FindFC.FactionComp);
            ReconcileDeliveryCaravans();

            // Routes are already path-dirty from load (PostLoadInit); align the road-version baseline so the
            // first tick doesn't treat load as a spurious "roads changed" (the warmer will warm them anyway).
            lastSeenRoadVersion = RouteRoadChangeTracker.Version;

            LogSC.MessageForce("WorldComponent_SupplyChain initialized (mode=" + mode + ", fromLoad=" + fromLoad + ")");
        }

        private bool firstTick = true;
        private BuildingFilter filterStockpileCap;
        private BuildingFilter filterBuildingNeeds;

        // True when THIS component was present in the save (its ExposeData ran during LoadingVars).
        // A component freshly added to an existing save leaves this false; that's how FinalizeInit
        // detects a mid-save-add. Transient — never scribed.
        private bool loadedFromSave;

        private void RegisterWithRegistries()
        {
            EmpireRegistry.Register(this);

            if (filterStockpileCap == null)
            {
                filterStockpileCap = new BuildingFilter(
                    "SC_FilterStockpileCap".Translate(),
                    null,
                    def =>
                    {
                        BuildingNeedExtension ext = def.GetModExtension<BuildingNeedExtension>();
                        return ext?.capBonuses?.Count > 0;
                    }
                );
            }
            if (filterBuildingNeeds == null)
            {
                filterBuildingNeeds = new BuildingFilter(
                    "SC_FilterBuildingNeeds".Translate(),
                    null,
                    def =>
                    {
                        BuildingNeedExtension ext = def.GetModExtension<BuildingNeedExtension>();
                        return ext?.inputs?.Count > 0;
                    }
                );
            }
            EmpireRegistry.Register(filterStockpileCap);
            EmpireRegistry.Register(filterBuildingNeeds);

            if (foundingValidator is null)
                foundingValidator = new FoundingCostValidator(this);
            EmpireRegistry.Register(foundingValidator);
        }

        public override void WorldComponentTick()
        {
            base.WorldComponentTick();
            if (firstTick)
            {
                firstTick = false;
                // Re-register in case ClearCaches ran after FinalizeInit (new game race condition)
                RegisterWithRegistries();
            }

            // Keep route travel-time/path caches warm off the UI thread. Roads change incrementally, so
            // poll the road-change counter and re-dirty every route's path when the network changes (a new
            // road can shorten the best path for any route). Then let the warmer (background) reconcile.
            if (mode == SupplyChainMode.Complex)
            {
                int roadVersion = RouteRoadChangeTracker.Version;
                if (roadVersion != lastSeenRoadVersion)
                {
                    lastSeenRoadVersion = roadVersion;
                    foreach (SupplyRoute route in supplyRoutes)
                        route.MarkPathDirty();
                    routeWarmer.Invalidate();
                }
                routeWarmer.Tick(supplyRoutes);
            }
        }

        // --- World Map Route Visualization ---

        internal void EnsurePairCaches()
        {
            if (!pairCacheDirty) return;
            pairCacheDirty = false;
            pairRouteCache.Clear();
            pairLabelCache.Clear();

            foreach (SupplyRoute route in supplyRoutes)
            {
                if (!route.IsValid()) continue;
                DirectedPlanetTilePair key = new DirectedPlanetTilePair(route.source.Tile, route.destination.Tile);

                pairRouteCache.TryAdd(key, route);

                string line = route.amountPerPeriod.ToString("F0") + " " + route.resource.label;
                if (pairLabelCache.TryGetValue(key, out string existing))
                    pairLabelCache[key] = existing + "\n" + line;
                else
                    pairLabelCache[key] = line;
            }

            // Append destination name to each label
            foreach (DirectedPlanetTilePair key in new List<DirectedPlanetTilePair>(pairLabelCache.Keys))
            {
                SupplyRoute rep = pairRouteCache[key];
                pairLabelCache[key] = pairLabelCache[key] + "\n\u2192 " + rep.destination.Name;
            }
        }

        public override void WorldComponentUpdate()
        {
            base.WorldComponentUpdate();
            if (!showAllRoutes) return;
            EnsurePairCaches();

            WorldGrid grid = Find.WorldGrid;
            foreach (SupplyRoute route in pairRouteCache.Values)
            {
                RouteOverlayUtil.DrawRoute(route, grid);
            }
        }

        public override void WorldComponentOnGUI()
        {
            if (!showRouteLabels) return;
            if (!showAllRoutes && !showSelectedRoutes) return;
            if (!RouteOverlayUtil.ShouldDrawLabels()) return;
            EnsurePairCaches();

            WorldGrid grid = Find.WorldGrid;
            GameFont prev = Text.Font;
            Text.Font = GameFont.Tiny;

            foreach (KeyValuePair<DirectedPlanetTilePair, SupplyRoute> kvp in pairRouteCache)
            {
                SupplyRoute route = kvp.Value;

                if (!showAllRoutes && showSelectedRoutes)
                {
                    bool relevant = false;
                    foreach (WorldObject obj in Find.WorldSelector.SelectedObjects)
                    {
                        if (obj == route.source || obj == route.destination)
                        { relevant = true; break; }
                    }
                    if (!relevant) continue;
                }

                pairLabelCache.TryGetValue(kvp.Key, out string label);
                if (label != null)
                    RouteOverlayUtil.DrawRouteLabel(route, grid, label);
            }

            GUI.color = Color.white;
            Text.Font = prev;
        }

        public void DrawRoutesForSettlement(WorldSettlementFC ws)
        {
            if (ws is null) return;
            EnsurePairCaches();
            WorldGrid grid = Find.WorldGrid;

            foreach (SupplyRoute route in pairRouteCache.Values)
            {
                if (route.source != ws && route.destination != ws) continue;
                RouteOverlayUtil.DrawRoute(route, grid);
            }
        }

        // --- Save/Load ---

        public override void ExposeData()
        {
            base.ExposeData();

            // Presence in the save is signalled by reaching ExposeData during LoadingVars. Record it so
            // FinalizeInit can tell a loaded component from one freshly added to an existing save (which
            // never gets a LoadingVars pass) and avoid double-registering it for PostLoadInit.
            if (Scribe.mode == LoadSaveMode.LoadingVars)
                loadedFromSave = true;

            Scribe_Values.Look(ref mode, "mode", SupplyChainMode.Simple);
            Scribe_Collections.Look(ref factionStockpile, "factionStockpile", LookMode.Def, LookMode.Value);
            Scribe_Collections.Look(ref factionCaps, "factionCaps", LookMode.Def, LookMode.Value);
            Scribe_Collections.Look(ref globalSellOrders, "globalSellOrders", LookMode.Deep);

            Scribe_Collections.Look(ref supplyRoutes, "supplyRoutes", LookMode.Deep);
            Scribe_Collections.Look(ref dormantRoutes, "dormantRoutes", LookMode.Deep);
            Scribe_Collections.Look(ref pendingDeliveries, "pendingDeliveries", LookMode.Deep);
            Scribe_Values.Look(ref nextDeliveryId, "nextDeliveryId", 0);
            Scribe_Values.Look(ref thresholdLetterSent, "thresholdLetterSent", false);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (factionStockpile == null)
                    factionStockpile = new Dictionary<ResourceTypeDef, double>();
                if (factionCaps == null)
                    factionCaps = new Dictionary<ResourceTypeDef, double>();
                if (globalSellOrders == null)
                    globalSellOrders = new List<SellOrder>();
                if (supplyRoutes == null)
                    supplyRoutes = new List<SupplyRoute>();
                if (dormantRoutes == null)
                    dormantRoutes = new List<SupplyRoute>();
                if (pendingDeliveries == null)
                    pendingDeliveries = new List<PendingDelivery>();

                // Cross-refs (faction.settlements, route endpoints) resolve before this PostLoadInit
                // pass but AFTER World.FinalizeInit, where the load-time init first runs against
                // still-empty lists. Redo it here now that the references are live: reconcile the mode,
                // then rebuild the per-settlement stockpile wrappers and trade-network partner sets.
                // (New games don't reach this branch; their references are already live at FinalizeInit,
                // which handles them.)
                SupplyChainCache.ClearCompCache();

                // Reconcile the loaded mode with the global setting (it may have changed while this save
                // was unloaded). SwitchMode redistributes stockpiles across settlements, so running it at
                // FinalizeInit against an empty settlement list would clear the faction stockpile without
                // distributing it (Complex) or strand the local stockpiles (Simple) — hence it runs here.
                if (mode != SupplyChainSettings.mode)
                {
                    LogSC.MessageForce("Mode mismatch on load: save=" + mode + ", settings=" + SupplyChainSettings.mode + ". Switching.");
                    SwitchMode(SupplyChainSettings.mode);
                }

                capsAndStockpilesDirty = true;
                EnsureCapsAndStockpiles();
                if (mode == SupplyChainMode.Complex)
                    RebuildAllPartnerSets(FindFC.FactionComp);
                ReconcileDeliveryCaravans();
            }
        }

        // --- Helpers ---

        private void RecalculateCaps()
        {
            FactionFC faction = FindFC.FactionComp;
            int numSettlements = faction?.settlements.Count ?? 0;

            foreach (ResourceTypeDef def in SupplyChainCache.AllResourceTypeDefs)
            {
                factionCaps[def] = numSettlements * SupplyChainSettings.baseCapPerSettlement;
            }

            if (faction == null) return;
            foreach (WorldSettlementFC settlement in faction.settlements)
            {
                if (settlement.BuildingsComp is null) continue;
                foreach (BuildingFC building in settlement.BuildingsComp.Buildings)
                {
                    if (building.def is null || building.def == BuildingFCDefOf.Empty) continue;
                    BuildingNeedExtension ext = SupplyChainCache.GetBuildingNeedExt(building.def);
                    if (ext?.capBonuses is null) continue;
                    foreach (BuildingCapBonus bonus in ext.capBonuses)
                    {
                        if (bonus.resource != null && factionCaps.ContainsKey(bonus.resource))
                            factionCaps[bonus.resource] += bonus.amount;
                    }
                }
            }
        }

        private void InitAllLocalStockpiles()
        {
            FactionFC faction = FindFC.FactionComp;
            if (faction is null) return;

            foreach (WorldSettlementFC settlement in faction.settlements)
            {
                WorldObjectComp_SupplyChain comp = GetComp(settlement);
                comp?.RecalculateLocalCaps();
                comp?.InitLocalStockpile();
            }
        }

        internal void EnsureCapsAndStockpiles()
        {
            if (!capsAndStockpilesDirty) return;
            if (mode == SupplyChainMode.Simple)
                RecalculateCaps();
            else
                InitAllLocalStockpiles();
            capsAndStockpilesDirty = false;
        }

        internal static WorldObjectComp_SupplyChain GetComp(WorldSettlementFC settlement)
        {
            return SupplyChainCache.GetSettlementComp(settlement);
        }

        // --- Flow Calculation & Cache ---

        internal struct CompNeedLine
        {
            public string label;
            public double amount;
        }

        internal struct FlowBreakdown
        {
            public double production;
            public double routeIn;
            public double baseNeeds;
            public double buildingNeeds;
            public double compNeeds;
            public List<CompNeedLine> compNeedLines;
            public double routeOut;
            public double sellOrders;
            public double titheInjection;
            public double needs => baseNeeds + buildingNeeds + compNeeds;
            // The single net used everywhere: need fill-rate projection, the displayed net, and
            // cell coloring. Daily terms only (diverted production in, needs + tithe out). Routes
            // and sell orders are per-tax-cycle stockpile events, surfaced separately in the
            // tooltip's "Per tax cycle" section, and are never folded into this rate.
            public double DailyNet => production - needs - titheInjection;
        }

        internal FlowBreakdown GetCachedFlow(WorldSettlementFC settlement, WorldObjectComp_SupplyChain comp, ResourceTypeDef def)
        {
            if (flowCacheDirty)
            {
                flowCache.Clear();
                simpleFlowCache.Clear();
                flowCacheDirty = false;
            }

            PlanetTileResourceKey key = new PlanetTileResourceKey(settlement.Tile, def.index);
            if (!flowCache.TryGetValue(key, out FlowBreakdown flow))
            {
                flow = CalculateFlow(settlement, comp, def);
                flowCache[key] = flow;
            }
            return flow;
        }

        internal FlowBreakdown GetCachedSimpleFlow(FactionFC faction, ResourceTypeDef def)
        {
            if (flowCacheDirty)
            {
                flowCache.Clear();
                simpleFlowCache.Clear();
                flowCacheDirty = false;
            }

            if (!simpleFlowCache.TryGetValue(def.index, out FlowBreakdown flow))
            {
                flow = CalculateSimpleFlow(faction, def);
                simpleFlowCache[def.index] = flow;
            }
            return flow;
        }

        internal void DirtyFlowCache()
        {
            flowCacheDirty = true;
            pairCacheDirty = true;
        }

        internal FlowBreakdown CalculateFlow(WorldSettlementFC settlement, WorldObjectComp_SupplyChain comp, ResourceTypeDef def)
        {
            FlowBreakdown flow = new FlowBreakdown
            {
                production = comp.GetAllocation(def)
            };

            foreach (SupplyRoute route in supplyRoutes)
            {
                if (!route.IsValid() || route.resource != def) continue;
                // Normalize each route's per-delivery amount to a daily average so the flow display
                // is comparable across routes with different delivery frequencies.
                double perDay = route.frequencyDays > 0 ? 1.0 / route.frequencyDays : 1.0;
                if (route.destination == settlement)
                    flow.routeIn += route.amountPerPeriod * route.CachedEfficiency * perDay;
                if (route.source == settlement)
                    flow.routeOut += route.amountPerPeriod * perDay;
            }

            AccumulateSettlementFlow(settlement, def, ref flow);

            foreach (SellOrder order in comp.LocalSellOrders)
            {
                if (order.resource == def)
                    flow.sellOrders += order.amountPerPeriod;
            }

            flow.titheInjection += comp.GetTitheInjection(def);

            return flow;
        }

        /// <summary>
        /// Calculates faction-level flow for Simple mode by aggregating across all settlements.
        /// </summary>
        private FlowBreakdown CalculateSimpleFlow(FactionFC faction, ResourceTypeDef def)
        {
            FlowBreakdown flow = new FlowBreakdown();
            if (faction is null) return flow;

            foreach (WorldSettlementFC settlement in faction.settlements)
            {
                WorldObjectComp_SupplyChain comp = GetComp(settlement);
                if (comp != null)
                    flow.production += comp.GetAllocation(def);

                AccumulateSettlementFlow(settlement, def, ref flow);
            }

            foreach (SellOrder order in globalSellOrders)
            {
                if (order.resource == def)
                    flow.sellOrders += order.amountPerPeriod;
            }

            // Aggregate tithe injections across all settlements
            foreach (WorldSettlementFC s in faction.settlements)
            {
                WorldObjectComp_SupplyChain c = GetComp(s);
                if (c != null)
                    flow.titheInjection += c.GetTitheInjection(def);
            }

            return flow;
        }

        /// <summary>
        /// Accumulates base needs and building needs for a single settlement into the flow breakdown.
        /// Shared by both CalculateFlow (Complex) and CalculateSimpleFlow (Simple).
        /// </summary>
        private static void AccumulateSettlementFlow(WorldSettlementFC settlement, ResourceTypeDef def, ref FlowBreakdown flow)
        {
            FactionFC faction = FindFC.FactionComp;
            if (faction is null || settlement is null) return;
            TechLevel tech = faction.techLevel;
            foreach (SettlementNeedDef needDef in SupplyChainCache.AllNeedDefs)
            {
                if (!needDef.IsActiveForSettlement(settlement)) continue;
                if (needDef.UsesResource(def))
                    flow.baseNeeds += needDef.CalculateDemand(settlement) * needDef.GetResourceFraction(tech, def);
            }

            if (settlement.BuildingsComp != null)
            {
                foreach (BuildingFC building in settlement.BuildingsComp.Buildings)
                {
                    if (building.def is null || building.def == BuildingFCDefOf.Empty) continue;
                    if (!building.active) continue; // dormant building consumes nothing
                    BuildingNeedExtension ext = SupplyChainCache.GetBuildingNeedExt(building.def);
                    if (ext?.inputs is null) continue;
                    foreach (BuildingResourceInput input in ext.inputs)
                    {
                        if (input.resource == def)
                            flow.buildingNeeds += input.amount;
                    }
                }
            }

            foreach (WorldObjectComp woc in settlement.AllComps)
            {
                INeedProvider provider = woc as INeedProvider;
                if (provider == null) continue;
                List<NeedEntry> entries = new List<NeedEntry>();
                provider.CollectNeeds(settlement, entries);
                foreach (NeedEntry entry in entries)
                {
                    if (entry.resource != def) continue;
                    flow.compNeeds += entry.amount;

                    // Store labeled line for tooltip display
                    string lineLabel = entry.label ?? entry.needId;
                    if (flow.compNeedLines is null)
                    {
                        flow.compNeedLines = new List<CompNeedLine>();
                        flow.compNeedLines.Add(new CompNeedLine { label = lineLabel, amount = entry.amount });
                    }
                    else
                    {
                        // Merge entries with the same label
                        bool merged = false;
                        for (int i = 0; i < flow.compNeedLines.Count; i++)
                        {
                            if (flow.compNeedLines[i].label == lineLabel)
                            {
                                CompNeedLine line = flow.compNeedLines[i];
                                line.amount += entry.amount;
                                flow.compNeedLines[i] = line;
                                merged = true;
                                break;
                            }
                        }
                        if (!merged)
                            flow.compNeedLines.Add(new CompNeedLine { label = lineLabel, amount = entry.amount });
                    }
                }
            }
        }

        // --- Mode Switching ---

        public void SwitchMode(SupplyChainMode newMode)
        {
            if (newMode == mode) return;

            FactionFC faction = FindFC.FactionComp;
            if (faction is null) return;

            if (mode == SupplyChainMode.Simple && newMode == SupplyChainMode.Complex)
            {
                SwitchToComplex(faction);
            }
            else if (mode == SupplyChainMode.Complex && newMode == SupplyChainMode.Simple)
            {
                SwitchToSimple(faction);
            }

            mode = newMode;
            capsAndStockpilesDirty = false;
            DirtyFlowCache();
            resourceColumnsDirty = true;
            LogSC.MessageForce("Supply chain mode switched to " + newMode);
        }

        private void SwitchToComplex(FactionFC faction)
        {
            // 1. Calculate total production share per settlement for proportional distribution
            Dictionary<WorldSettlementFC, double> productionShares = new Dictionary<WorldSettlementFC, double>();
            double totalProduction = 0;
            foreach (WorldSettlementFC settlement in faction.settlements)
            {
                double settlementProd = 0;
                foreach (ResourceFC resource in settlement.Resources)
                {
                    settlementProd += resource.rawTotalProduction;
                }
                productionShares[settlement] = settlementProd;
                totalProduction += settlementProd;
            }

            // 2. Distribute faction stockpile proportionally
            foreach (WorldSettlementFC settlement in faction.settlements)
            {
                WorldObjectComp_SupplyChain comp = GetComp(settlement);
                if (comp is null) continue;

                comp.RecalculateLocalCaps();
                comp.InitLocalStockpile();

                double share = totalProduction > 0 ? productionShares[settlement] / totalProduction : 1.0 / faction.settlements.Count;

                foreach (KeyValuePair<ResourceTypeDef, double> kv in factionStockpile)
                {
                    double amount = kv.Value * share;
                    if (amount > 0)
                    {
                        // A settlement can receive more of a resource than its local cap holds; sell the
                        // over-cap remainder as silver instead of dropping it (one-time reconciliation of
                        // the redistributed faction pile, at the same overflow penalty as the daily sweep).
                        double excess = comp.GetStockpile().Credit(kv.Key, amount);
                        if (excess > 0 && !kv.Key.isPoolResource)
                            settlement.AddOneTimeSilverIncome(FormulaUtil.OverflowSilver(excess));
                    }
                }
            }

            // 3. Restore valid dormant routes
            foreach (SupplyRoute route in dormantRoutes)
            {
                if (route.IsValid())
                {
                    route.MarkPathDirty();  // roads/tech may have changed while dormant
                    route.nextDispatchTick = -1; // re-stagger dispatch from the switch moment
                    LinkRoute(route);
                }
                else
                {
                    LogSC.Message("Discarding invalid dormant route (settlement destroyed).");
                }
            }
            dormantRoutes.Clear();

            // 4. Clear faction stockpile
            factionStockpile.Clear();
        }

        private void SwitchToSimple(FactionFC faction)
        {
            // 1. Sum all local stockpiles into faction stockpile
            foreach (WorldSettlementFC settlement in faction.settlements)
            {
                WorldObjectComp_SupplyChain comp = GetComp(settlement);
                if (comp is null) continue;

                foreach (KeyValuePair<ResourceTypeDef, double> kv in comp.LocalStockpile)
                {
                    factionStockpile.TryGetValue(kv.Key, out double current);
                    factionStockpile[kv.Key] = current + kv.Value;
                }

                comp.ClearLocalData();
            }

            // 2. Land any in-transit deliveries into the faction stockpile so goods that already
            // left their source aren't silently destroyed by the mode toggle. Efficiency is applied
            // here for parity with a normal arrival; caps clamp on the next EnsureCaps.
            foreach (PendingDelivery d in pendingDeliveries)
            {
                DestroyCaravanOf(d);
                if (d.resource is null) continue;
                double credited = d.amount * d.efficiency;
                if (credited <= 0) continue;
                factionStockpile.TryGetValue(d.resource, out double cur);
                factionStockpile[d.resource] = cur + credited;
            }
            pendingDeliveries.Clear();

            // 3. Stash routes as dormant and drop all trade-network partner links.
            dormantRoutes.AddRange(supplyRoutes);
            supplyRoutes.Clear();
            foreach (WorldSettlementFC settlement in faction.settlements)
                GetComp(settlement)?.ClearPartners();

            // 4. Reconstruct faction stockpile and recalculate caps
            RecalculateCaps();
            stockpile = new DictionaryStockpile(factionStockpile, factionCaps);
        }

        // --- ITaxTickParticipant ---

        public void PreTaxResolution(FactionFC faction)
        {
            if (mode == SupplyChainMode.Simple)
                PreTaxResolution_Simple(faction);
            else
                PreTaxResolution_Complex(faction);
        }

        private void PreTaxResolution_Simple(FactionFC faction)
        {
            // Deposits, needs, dormancy, and tithe injection now run daily in PostDailyAccrual.
            // The tax tick only liquidates voluntary sell orders against the shared stockpile.
            foreach (SellOrder order in globalSellOrders)
            {
                float silver = order.Execute(stockpile);
                if (silver > 0)
                {
                    DistributeSilverEvenly(silver, faction);
                    LogSC.Message($"Sell order: {order.amountPerPeriod} {order.resource.label} -> {silver} silver");
                }
            }
        }

        private void PreTaxResolution_Complex(FactionFC faction)
        {
            // Route movement, arrivals, and the trade network now run daily in DailyConsume_Complex.
            // The tax tick only runs the silver-generating steps: a cap-safety overflow sweep and
            // per-settlement sell orders.

            // Refresh local caps (only if buildings changed) before the overflow/sell sweep.
            foreach (WorldSettlementFC settlement in faction.settlements)
            {
                GetComp(settlement)?.RecalculateLocalCapsIfDirty();
            }

            // 6. PER-SETTLEMENT OVERFLOW cap-safety net. The daily consume pass already sweeps true
            //    surplus after routes/needs; this catches any residual over-cap (e.g. a capacity
            //    reduction between daily runs) so the tax tick still lands stockpiles at cap.
            foreach (WorldSettlementFC settlement in faction.settlements)
            {
                WorldObjectComp_SupplyChain comp = GetComp(settlement);
                if (comp is null) continue;

                IStockpile localStockpile = comp.GetStockpile();
                if (localStockpile is null) continue;

                float silver = SweepOverflow(localStockpile);
                if (silver > 0)
                    settlement.AddOneTimeSilverIncome(silver);
            }

            // 7. PER-SETTLEMENT SELL ORDERS
            foreach (WorldSettlementFC settlement in faction.settlements)
            {
                WorldObjectComp_SupplyChain comp = GetComp(settlement);
                if (comp is null) continue;

                IStockpile localStockpile = comp.GetStockpile();
                if (localStockpile is null) continue;

                foreach (SellOrder order in comp.LocalSellOrders)
                {
                    float silver = order.Execute(localStockpile, settlement);
                    if (silver > 0)
                    {
                        settlement.AddOneTimeSilverIncome(silver);
                        LogSC.Message($"Local sell order at {settlement.Name}: {order.amountPerPeriod} {order.resource.label} -> {silver} silver");
                    }
                }
            }
            capsAndStockpilesDirty = false;
        }

        /// <summary>
        /// Clamps every resource a stockpile holds back to its cap and returns the total silver
        /// generated. This is the "sell only true surplus" step of produce -> consume -> sell: run it
        /// AFTER the daily consume pass so routes and needs draw from the day's production first and only
        /// genuinely unstorable surplus is reconciled. Also doubles as a cap-safety net for capacity
        /// reductions. Non-pool overflow sells at the overflow penalty; pool resources (power/research)
        /// have no silver value, so their overflow is dropped silently — but they are still clamped like
        /// every other resource, or an auto-maxed pool allocation would accumulate over cap forever.
        /// Internal (not private) so the daily-accrual regression tests can drive it directly.
        /// </summary>
        internal float SweepOverflow(IStockpile sp)
        {
            if (sp is null) return 0f;

            float total = 0f;
            foreach (ResourceTypeDef def in SupplyChainCache.AllResourceTypeDefs)
            {
                double amount = sp.GetAmount(def);
                double cap = sp.GetCap(def);
                if (amount > cap && cap > 0)
                {
                    double excess = amount - cap;
                    sp.TryDraw(def, excess, out double drawn);
                    if (drawn > 0 && !def.isPoolResource)
                        total += FormulaUtil.OverflowSilver(drawn);
                }
            }
            return total;
        }

        public void PostTaxResolution(FactionFC faction)
        {
        }

        public void PreSettlementCreateTax(WorldSettlementFC settlement)
        {
        }

        public void PostSettlementCreateTax(WorldSettlementFC settlement, ref int silverAmount, List<Thing> titheThings)
        {
            if (silverAmount <= 0 || FindFC.FactionComp == null) return;

            FCStatDef taxEffStat = SCStatDefOf.SC_TaxEfficiency;
            if (taxEffStat is null) return;

            double mult = FindFC.FactionComp.GetStatValue(taxEffStat, settlement);
            if (mult > 0 && mult != 1.0)
                silverAmount = (int)(silverAmount * mult);
        }

        // --- IDailyAccrualParticipant ---

        /// <summary>
        /// Runs once per day after every settlement has accrued the day's production and the
        /// per-allocation realize() deposits have landed uncapped (produce-then-consume). Consumes from
        /// the now-filled stockpile: settlement/comp needs (proportional), per-building dormancy
        /// (all-or-nothing), and tithe injection; then sells only the true surplus (whatever storage
        /// still cannot hold after consumption) at the overflow penalty; then re-syncs auto-max so
        /// tomorrow's deposit tracks today's production rate (the one-day predictive lag matching
        /// dormancy/tithe).
        /// </summary>
        public void PostDailyAccrual(FactionFC faction)
        {
            if (faction is null) return;
            if (mode == SupplyChainMode.Simple)
                DailyConsume_Simple(faction);
            else
                DailyConsume_Complex(faction);
        }

        private void DailyConsume_Simple(FactionFC faction)
        {
            EnsureCapsAndStockpiles();

            // No trade network in Simple mode — clear any stale network info.
            foreach (WorldSettlementFC settlement in faction.settlements)
                GetComp(settlement)?.SetNetworkInfo(0, 0);

            // 1. Settlement + comp needs (proportional draw from the shared stockpile).
            NeedResolver.ResolveSettlementNeedsFair(faction, stockpile);

            // 2. Per-building dormancy (all-or-nothing; deterministic settlement + slot order).
            foreach (WorldSettlementFC settlement in faction.settlements)
                NeedResolver.ResolveBuildingDormancy(settlement, stockpile);

            // 3. Tithe injection (per-day draw).
            foreach (WorldSettlementFC settlement in faction.settlements)
                GetComp(settlement)?.ResolveTitheInjections(stockpile);

            // 4. Sell only the true surplus: after needs/dormancy/tithe have drawn from the day's
            //    production, liquidate whatever the shared pile still cannot store. Silver is shared
            //    evenly since the pool has no per-settlement attribution.
            float overflowSilver = SweepOverflow(stockpile);
            if (overflowSilver > 0)
                DistributeSilverEvenly(overflowSilver, faction);

            // 5. Re-sync auto-max allocations to current production for tomorrow's deposit.
            foreach (WorldSettlementFC settlement in faction.settlements)
                GetComp(settlement)?.SyncAllAutoMaxAllocations();

            DirtyFlowCache();
        }

        private void DailyConsume_Complex(FactionFC faction)
        {
            // Route movement is now daily. The day's production has already been deposited (uncapped)
            // into each source stockpile, so routes and needs draw from it before the end-of-pass
            // overflow sweep sells whatever storage still cannot hold. Refresh caps first, then land
            // arrivals, then dispatch due routes, so goods that arrive today can satisfy today's needs.
            foreach (WorldSettlementFC settlement in faction.settlements)
                GetComp(settlement)?.RecalculateLocalCapsIfDirty();

            ProcessArrivals(faction);
            DispatchDueRoutes(faction);

            foreach (WorldSettlementFC settlement in faction.settlements)
            {
                WorldObjectComp_SupplyChain dataComp = GetComp(settlement);
                if (dataComp is null) continue;

                IStockpile local = dataComp.EnsureLocalStockpile();
                if (local is null) continue;

                WorldObjectComp_SettlementNeeds needComp = SupplyChainCache.GetNeedsComp(settlement);

                // 1. Settlement + comp needs (proportional draw from the local stockpile).
                if (needComp != null)
                    NeedResolver.ResolveSettlementNeeds(settlement, local, needComp);

                // 2. Per-building dormancy (all-or-nothing; deterministic slot order).
                NeedResolver.ResolveBuildingDormancy(settlement, local);

                // 3. Tithe injection (per-day draw).
                dataComp.ResolveTitheInjections(local);

                // 4. Sell only the true surplus: after this settlement's routes/needs/dormancy/tithe
                //    have drawn from the day's production, liquidate whatever storage still cannot hold.
                float overflowSilver = SweepOverflow(local);
                if (overflowSilver > 0)
                    settlement.AddOneTimeSilverIncome(overflowSilver);

                // 5. Re-sync auto-max allocations for tomorrow's deposit.
                dataComp.SyncAllAutoMaxAllocations();
            }
            DirtyFlowCache();
        }

        /// <summary>
        /// Daily safety net: credits any delivery that has NO live world object (straight-line
        /// pod/shuttle deliveries, or a road delivery whose caravan was lost) once its arrival tick
        /// passes. Road deliveries with a live caravan are completed by the caravan on physical
        /// arrival (see <see cref="CompleteDelivery"/>), so they are skipped here.
        /// </summary>
        private void ProcessArrivals(FactionFC faction)
        {
            int now = Find.TickManager.TicksGame;
            List<PendingDelivery> arrived = null;
            foreach (PendingDelivery d in pendingDeliveries)
            {
                if (d.caravan is object && !d.caravan.Destroyed) continue;   // a live caravan drives this delivery's arrival
                if (now < d.arrivalTick) continue;
                if (arrived is null) arrived = new List<PendingDelivery>();
                arrived.Add(d);
            }
            if (arrived is null) return;

            foreach (PendingDelivery d in arrived)
                CompleteDelivery(d);
        }

        /// <summary>
        /// Credits a single in-transit delivery to its destination stockpile (efficiency applied here)
        /// and removes it from the pending list. Deliveries to a destroyed/missing settlement are
        /// dropped gracefully — the source already spent the goods at dispatch. Idempotent: a delivery
        /// no longer in the pending list is ignored. Called both by <see cref="ProcessArrivals"/> and by
        /// a <see cref="DeliveryCaravan"/> when it physically reaches the destination.
        /// </summary>
        public void CompleteDelivery(PendingDelivery d)
        {
            if (d is null) return;
            if (!pendingDeliveries.Remove(d)) return;   // already completed
            d.caravan = null;

            if (d.destination is object && !d.destination.Destroyed && d.resource is object)
            {
                WorldObjectComp_SupplyChain destComp = GetComp(d.destination);
                IStockpile destStockpile = destComp?.GetStockpile();
                if (destStockpile is object)
                {
                    double credited = d.amount * d.efficiency;   // efficiency snapshot applied on arrival
                    if (credited > 0)
                        // Deposit uncapped like the day's production (produce -> consume -> sweep): arrivals
                        // land before needs draw, so the goods can cover today's consumption instead of being
                        // clipped at the cap and lost. The daily overflow sweep sells any true surplus.
                        destStockpile.Add(d.resource, credited);
                }
            }
            DirtyFlowCache();
        }

        /// <summary>Destroys the world object following <paramref name="d"/>, if any (does not credit).</summary>
        private static void DestroyCaravanOf(PendingDelivery d)
        {
            if (d?.caravan is object)
            {
                if (!d.caravan.Destroyed) d.caravan.Destroy();
                d.caravan = null;
            }
        }

        private void ReconcileDeliveryCaravans()
        {
            if (pendingDeliveries is null) return;

            // The delivery <-> caravan links are serialized cross-references, so no value-matching is
            // needed. Only two fixups: destroy caravans whose delivery didn't survive the load (orphans),
            // and — when the feature is enabled — spawn a caravan for any road delivery still missing one.
            List<DeliveryCaravan> orphans = null;
            foreach (WorldObject wo in Find.WorldObjects.AllWorldObjects)
            {
                if (wo is DeliveryCaravan dc && dc.LinkedDelivery is null)
                {
                    if (orphans is null) orphans = new List<DeliveryCaravan>();
                    orphans.Add(dc);
                }
            }
            if (orphans != null)
            {
                foreach (DeliveryCaravan dc in orphans)
                    if (!dc.Destroyed) dc.Destroy();
            }

            if (SupplyChainSettings.useDeliveryCaravans)
            {
                foreach (PendingDelivery d in pendingDeliveries)
                {
                    if (d.caravan is object && !d.caravan.Destroyed) continue;
                    if (d.pathTiles != null && d.pathTiles.Count >= 2)
                        DeliveryCaravan.Spawn(d);
                }
            }
        }

        /// <summary>
        /// Dispatches every route whose scheduled dispatch tick has passed, drawing from the source
        /// stockpile and enqueuing an in-transit <see cref="PendingDelivery"/>. Each route reschedules
        /// its next dispatch relative to now (never a backlog), and newly created/loaded routes dispatch
        /// immediately on the first daily tick after they appear, then every frequency period thereafter.
        /// Routes are processed in <see cref="supplyRoutes"/> order, so a route earlier in the list wins
        /// the draw when several share a source stockpile that can't satisfy them all this period.
        /// </summary>
        private void DispatchDueRoutes(FactionFC faction)
        {
            int now = Find.TickManager.TicksGame;
            List<SupplyRoute> invalidRoutes = null;

            foreach (SupplyRoute route in supplyRoutes)
            {
                if (!route.IsValid())
                {
                    if (invalidRoutes is null) invalidRoutes = new List<SupplyRoute>();
                    invalidRoutes.Add(route);
                    continue;
                }

                // A freshly created/restored route (sentinel -1) is due immediately on this first
                // daily tick; thereafter it dispatches once per frequency period.
                if (route.nextDispatchTick < 0)
                    route.nextDispatchTick = now;

                if (now < route.nextDispatchTick) continue;

                // Due now — ensure travel time / path / efficiency are ready. Normally the background
                // warmer has already computed them; this is the synchronous fallback (a no-op once warm),
                // and only ever runs for the few routes actually dispatching this tick.
                route.RecacheIfDirty();

                WorldObjectComp_SupplyChain sourceComp = GetComp(route.source);
                IStockpile sourceStockpile = sourceComp?.GetStockpile();
                if (sourceStockpile != null)
                {
                    PendingDelivery d = route.TryDispatch(sourceStockpile);
                    if (d != null)
                    {
                        d.loadId = nextDeliveryId++;
                        pendingDeliveries.Add(d);
                        // With the feature enabled, road-routed deliveries get a world object that follows the
                        // path and drives arrival; straight-line (pod/shuttle) deliveries and the disabled
                        // feature have no path/object and arrive via ProcessArrivals.
                        if (SupplyChainSettings.useDeliveryCaravans && d.pathTiles != null && d.pathTiles.Count >= 2)
                            DeliveryCaravan.Spawn(d);
                        LogSC.Message($"Dispatched {d.amount} {route.resource.label} from {route.source.Name} -> {route.destination.Name}, arriving in {(d.arrivalTick - now).ToStringTicksToPeriod()}");
                    }
                }

                route.nextDispatchTick = now + route.frequencyDays * GenDate.TicksPerDay;
            }

            if (invalidRoutes != null)
            {
                foreach (SupplyRoute route in invalidRoutes)
                    UnlinkRoute(route);
                DirtyFlowCache();
            }
        }

        // --- Trade-network partner-set maintenance ---
        // Each settlement's in/out partner sets are maintained incrementally as routes are created
        // and deleted; the daily tick never rebuilds them. A pair may be joined by more than one
        // route (e.g. different resources), so a set entry is only dropped once the LAST route
        // connecting that directed pair is gone.

        /// <summary>
        /// Adds a route to the list AND registers its endpoints as partners, atomically. The only
        /// sanctioned way to add to <see cref="supplyRoutes"/>. Idempotent.
        /// </summary>
        public void LinkRoute(SupplyRoute r)
        {
            if (r is null || supplyRoutes.Contains(r)) return;
            supplyRoutes.Add(r);
            LinkPartners(r);
        }

        /// <summary>
        /// Removes a route from the list AND drops its partner link (if no remaining route still
        /// connects the pair), atomically. The only sanctioned way to remove from
        /// <see cref="supplyRoutes"/>.
        /// </summary>
        public void UnlinkRoute(SupplyRoute r)
        {
            if (r is null || !supplyRoutes.Remove(r)) return; // remove first, then reassess the pair
            UnlinkPartners(r.source, r.destination);
        }

        /// <summary>
        /// Repositions <paramref name="route"/> to sit just ahead of <paramref name="target"/> in
        /// <see cref="supplyRoutes"/> (dispatch order = list order). Callers pass the visible neighbor
        /// so reordering stays correct under the UI's direction/resource filters. Partner links are
        /// unaffected — only the ordering changes.
        /// </summary>
        public void MoveRouteBefore(SupplyRoute route, SupplyRoute target)
        {
            if (!ReorderRoute(route, target, false)) return;
            DirtyFlowCache();
        }

        /// <summary>Repositions <paramref name="route"/> to sit just behind <paramref name="target"/>
        /// in <see cref="supplyRoutes"/>. Companion to <see cref="MoveRouteBefore"/>.</summary>
        public void MoveRouteAfter(SupplyRoute route, SupplyRoute target)
        {
            if (!ReorderRoute(route, target, true)) return;
            DirtyFlowCache();
        }

        private bool ReorderRoute(SupplyRoute route, SupplyRoute target, bool after)
        {
            if (route is null || target is null || route == target) return false;
            int from = supplyRoutes.IndexOf(route);
            if (from < 0) return false;
            supplyRoutes.RemoveAt(from);
            int to = supplyRoutes.IndexOf(target); // recomputed after the removal shifts indices
            if (to < 0) { supplyRoutes.Insert(from, route); return false; } // target gone — restore
            supplyRoutes.Insert(after ? to + 1 : to, route);
            return true;
        }

        /// <summary>Registers a route's endpoints as partners. Partner-set only — does not touch the
        /// route list, so it is safe to call over routes already in the list (see RebuildAllPartnerSets).</summary>
        private void LinkPartners(SupplyRoute r)
        {
            if (r is null || !r.IsValid()) return;
            GetComp(r.source)?.AddOutPartner(r.destination);
            GetComp(r.destination)?.AddInPartner(r.source);
        }

        /// <summary>Drops the partner link for a directed pair, but only if no remaining route still
        /// connects it. Partner-set only; call AFTER the route has left <see cref="supplyRoutes"/>.</summary>
        private void UnlinkPartners(WorldSettlementFC src, WorldSettlementFC dst)
        {
            if (src is null || dst is null) return;
            if (AnyRouteConnects(src, dst)) return;
            GetComp(src)?.RemoveOutPartner(dst);
            GetComp(dst)?.RemoveInPartner(src);
        }

        private bool AnyRouteConnects(WorldSettlementFC src, WorldSettlementFC dst)
        {
            foreach (SupplyRoute r in supplyRoutes)
                if (r.IsValid() && r.source == src && r.destination == dst) return true;
            return false;
        }

        /// <summary>Full rebuild of every settlement's partner sets from the route list (load only).</summary>
        private void RebuildAllPartnerSets(FactionFC faction)
        {
            if (faction is null) return;
            foreach (WorldSettlementFC settlement in faction.settlements)
                GetComp(settlement)?.ClearPartners();
            foreach (SupplyRoute route in supplyRoutes)
                LinkPartners(route);
        }

        /// <summary>Debug: dispatch every route now, ignoring its schedule.</summary>
        public void DebugForceDispatchAllRoutes()
        {
            FactionFC faction = FindFC.FactionComp;
            if (faction is null || mode != SupplyChainMode.Complex) return;
            int now = Find.TickManager.TicksGame;
            foreach (SupplyRoute route in supplyRoutes)
                route.nextDispatchTick = now;
            DispatchDueRoutes(faction);
            DirtyFlowCache();
        }

        /// <summary>
        /// Debug: force every in-transit delivery to complete now — credit its goods to the destination
        /// and remove its on-map caravan — including caravan-driven road deliveries (which
        /// <see cref="ProcessArrivals"/> deliberately skips, since a live caravan drives their arrival).
        /// </summary>
        public void DebugForceArriveAllDeliveries()
        {
            FactionFC faction = FindFC.FactionComp;
            if (faction is null || mode != SupplyChainMode.Complex) return;

            // Snapshot first: CompleteDelivery mutates pendingDeliveries.
            List<PendingDelivery> all = new List<PendingDelivery>(pendingDeliveries);
            foreach (PendingDelivery d in all)
            {
                DestroyCaravanOf(d);   // remove the on-map caravan (no-op for straight-line deliveries)
                CompleteDelivery(d);   // credit destination + remove from pendingDeliveries
            }
            DirtyFlowCache();
        }

        /// <summary>
        /// Uncapped deposit into the shared faction stockpile (Simple mode). Mirrors the local-stockpile
        /// <see cref="IStockpile.Add"/> path used by the produce-then-consume deposit: the day's production
        /// lands in full (possibly over cap) so needs can draw from it before the daily overflow sweep
        /// liquidates whatever storage still cannot hold.
        /// </summary>
        public void AddToFactionStockpile(ResourceTypeDef def, double amount)
        {
            if (def is null || amount <= 0) return;
            EnsureCapsAndStockpiles();
            stockpile?.Add(def, amount);
        }

        private void DistributeSilverEvenly(float silver, FactionFC faction)
        {
            if (faction.settlements.Count == 0) return;
            float share = silver / faction.settlements.Count;
            foreach (WorldSettlementFC settlement in faction.settlements)
            {
                settlement.AddOneTimeSilverIncome(share);
            }
        }

        // --- ISettlementListener + IResearchListener ---

        public void OnSettlementCreated(WorldSettlementFC settlement)
        {
            SupplyChainCache.ClearCompCache();
            InvalidateAllRoutes();
            capsAndStockpilesDirty = true;
            DirtyFlowCache();
            resourceColumnsDirty = true;

            SupplyChainCache.GetNeedsComp(settlement)?.RebuildNeedStates();

            // Caps scale with settlement count (Simple) / building set (Complex); the new settlement is
            // already in faction.settlements at this point, so recompute before seeding — otherwise the
            // starting buffer is clamped against stale caps and the first settlement's buffer is dropped.
            EnsureCapsAndStockpiles();

            // Seed the new settlement with the configured per-resource starting buffer so it isn't
            // immediately in penalty on its first day.
            // Stockpile resolved lazily so an all-zero configuration touches nothing.
            IStockpile startSp = null;
            foreach (ResourceTypeDef def in SupplyChainCache.AllResourceTypeDefs)
            {
                if (def.isPoolResource) continue;
                int amt = SupplyChainSettings.GetStartingAmount(def.defName);
                if (amt <= 0) continue;
                if (startSp == null)
                    startSp = mode == SupplyChainMode.Simple ? Stockpile : GetComp(settlement)?.EnsureLocalStockpile();
                if (startSp == null) break;
                startSp.Credit(def, amt);
            }
        }

        /// <summary>
        /// Fires the one-time "resource costs now apply" letter on the founding that reaches
        /// the free-settlement gate, warning that the next founding will be the first to cost
        /// resources. Called from FoundingCostValidator.OnSettlementFounded, where the founding
        /// being committed is not yet in settlements or settlementCaravansList — so +1 counts it.
        /// </summary>
        public void MaybeAnnounceUpcomingFoundingCosts()
        {
            if (thresholdLetterSent) return;
            FactionFC faction = FindFC.FactionComp;
            if (faction is null) return;
            int committed = faction.settlements.Count + faction.settlementCaravansList.Count;
            if (committed + 1 >= SupplyChainSettings.freeSettlementThreshold)
            {
                thresholdLetterSent = true;
                Find.LetterStack.ReceiveLetter(
                    "SC_ThresholdLetterTitle".Translate(),
                    "SC_ThresholdLetterBody".Translate(
                        FindFC.EmpireTitle, SupplyChainSettings.freeSettlementThreshold.ToString()),
                    LetterDefOf.NeutralEvent);
            }
        }

        public void OnSettlementRemoved(WorldSettlementFC settlement)
        {
            // Drop every active route touching this settlement through the atomic UnlinkRoute so the
            // route list and partner sets stay consistent. Snapshot first (can't mutate while iterating).
            List<SupplyRoute> touching = null;
            foreach (SupplyRoute r in supplyRoutes)
            {
                if (r.source != settlement && r.destination != settlement) continue;
                if (touching is null) touching = new List<SupplyRoute>();
                touching.Add(r);
            }
            if (touching != null)
                foreach (SupplyRoute r in touching)
                    UnlinkRoute(r);

            SupplyChainCache.ClearCompCache();
            // Dormant routes are never partner-linked, so drop them directly.
            dormantRoutes.RemoveAll(r => r.source == settlement || r.destination == settlement);
            // Drop in-transit deliveries bound for this settlement (can no longer be credited).
            // Leave deliveries that merely originated here — the goods already left and are en route.
            pendingDeliveries.RemoveAll(d =>
            {
                if (d.destination != settlement) return false;
                DestroyCaravanOf(d);
                return true;
            });
            InvalidateAllRoutes();
            capsAndStockpilesDirty = true;
            DirtyFlowCache();
            resourceColumnsDirty = true;
        }

        public void OnSettlementUpgraded(WorldSettlementFC settlement, int oldLevel, int newLevel)
        {
            DirtyFlowCache();
            WorldObjectComp_SupplyChain comp = GetComp(settlement);
            SupplyChainCache.GetNeedsComp(settlement)?.RebuildNeedStates();
            comp?.SyncAllAutoMaxAllocations();
        }

        public void OnSettlementTypeChanged(WorldSettlementFC settlement, WorldSettlementDef oldDef, WorldSettlementDef newDef)
        {
            capsAndStockpilesDirty = true;
            DirtyFlowCache();
            resourceColumnsDirty = true;
            GetComp(settlement)?.SyncAllAutoMaxAllocations();
        }

        public void OnBuildingConstructed(WorldSettlementFC settlement, BuildingFCDef building, int slot)
        {
            capsAndStockpilesDirty = true;
            DirtyFlowCache();
            resourceColumnsDirty = true;
            WorldObjectComp_SupplyChain comp = GetComp(settlement);
            comp?.DirtyLocalCaps();
            SupplyChainCache.GetNeedsComp(settlement)?.RebuildNeedStates();
            comp?.SyncAllAutoMaxAllocations();
        }

        public void OnBuildingDeconstructed(WorldSettlementFC settlement, BuildingFCDef building, int slot)
        {
            capsAndStockpilesDirty = true;
            DirtyFlowCache();
            resourceColumnsDirty = true;
            WorldObjectComp_SupplyChain comp = GetComp(settlement);
            comp?.DirtyLocalCaps();
            SupplyChainCache.GetNeedsComp(settlement)?.RebuildNeedStates();
            comp?.SyncAllAutoMaxAllocations();
        }

        public void OnResearchCompleted(ResearchProjectDef project)
        {
            FactionFC faction = FindFC.FactionComp;
            if (faction is null) return;
            DirtyFlowCache();
            foreach (WorldSettlementFC settlement in faction.settlements)
                GetComp(settlement)?.SyncAllAutoMaxAllocations();

            // Transport-pods research changes the travel-time branch, so route paths may change. Re-warm
            // them off the UI (and supersede any in-flight computation).
            foreach (SupplyRoute route in supplyRoutes)
                route.MarkPathDirty();
            routeWarmer.Invalidate();
        }

        // Settlement create/remove can change faction stats (hence route efficiency) but never moves an
        // existing route's endpoints or roads — so only the cheap efficiency is invalidated here, not the
        // expensive path. If founding a settlement kicks off road building, the OverlayRoad hook
        // (RouteRoadChangeTracker) will mark paths dirty when roads actually change.
        private void InvalidateAllRoutes()
        {
            foreach (SupplyRoute route in supplyRoutes)
                route.MarkEfficiencyDirty();
        }

        // --- IMainTabWindowOverview (Faction Tab) ---

        public string TabName()
        {
            return "SC_TabName".Translate();
        }

        public void PreOpenWindow(FactionFC faction)
        {
            uiFaction = faction;
            scrollPos = Vector2.zero;
            scrollPosStockpiles = Vector2.zero;
            scrollPosRoutes = Vector2.zero;
            newSellOrderResource = null;
            newSellOrderAmountBuffer = "";
            newSellOrderAmount = 0;
            newRouteSource = null;
            newRouteDest = null;
            newRouteResource = null;
            newRouteAmountBuffer = "";
            newRouteAmount = 0;
        }

        public void OnTabSwitch()
        {
        }

        public void PostCloseWindow()
        {
            uiFaction = null;
        }

        public void DrawOverviewTab(Rect boundingBox)
        {
            if (uiFaction == null) return;

            if (mode == SupplyChainMode.Simple)
                DrawFactionTab_Simple(boundingBox);
            else
                DrawFactionTab_Complex(boundingBox);
        }

        // --- Simple Mode Faction Tab ---

        private void DrawFactionTab_Simple(Rect boundingBox)
        {
            Rect inner = boundingBox.ContractedBy(10f);
            float curY = inner.y;

            EnsureCapsAndStockpiles();

            // Header (pinned outside scroll)
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inner.x, curY, 300f, 30f), "SC_FactionStockpile".Translate());
            Text.Font = GameFont.Tiny;
            GUI.color = Color.gray;
            Widgets.Label(new Rect(inner.x + 310f, curY + 4f, 100f, 26f), "SC_ModeSimple".Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            curY += 38f;

            // Pre-calculate counts for scroll height
            FactionFC simpleFaction = FindFC.FactionComp;
            int resourceCount = 0;
            foreach (ResourceTypeDef def in SupplyChainCache.AllResourceTypeDefs)
            {
                resourceCount++;
            }

            int totalTitheInjections = 0;
            if (simpleFaction != null)
            {
                foreach (WorldSettlementFC s in simpleFaction.settlements)
                {
                    WorldObjectComp_SupplyChain comp = GetComp(s);
                    if (comp is null) continue;
                    foreach (KeyValuePair<ResourceTypeDef, double> kv in comp.TitheInjections)
                    {
                        if (kv.Key != null && kv.Value > 0) totalTitheInjections++;
                    }
                }
            }

            float totalHeight = resourceCount * 30f  // resource bars
                + 12f                                 // gap
                + 34f + 32f                           // sell orders header + add row
                + globalSellOrders.Count * 28f        // sell order rows
                + 56f                                 // overflow info
                + 16f                                 // gap
                + 34f + 32f                           // tithe header + add row
                + (totalTitheInjections > 0 ? totalTitheInjections * 28f : 24f)
                + 20f;                                // bottom padding

            Rect scrollArea = new Rect(inner.x, curY, inner.width, inner.yMax - curY);
            Rect viewRect = ScrollUtil.BeginScrollView(scrollArea, ref scrollPosSimple, totalHeight);
            float drawY = 0f;

            // Resource bars — scale bar to fill available width
            const float barHeight = 28f;
            const float accentW = 4f;
            const float arrowSize = 16f;
            float contentX = accentW + 4f;
            float labelEndX = contentX + 172f;
            float amountTextW = 90f;
            float netFlowW = 60f;
            const float buyBtnW = 22f;
            float barWidth = viewRect.width - labelEndX - arrowSize - 8f - amountTextW - netFlowW - buyBtnW - 8f;
            if (barWidth < 100f) barWidth = 100f;

            int resIdx = 0;
            foreach (ResourceTypeDef def in SupplyChainCache.AllResourceTypeDefs)
            {
                double amount = stockpile.GetAmount(def);
                double cap = stockpile.GetCap(def);
                float fillPct = cap > 0 ? (float)(amount / cap) : 0f;
                FlowBreakdown simpleFlow = GetCachedSimpleFlow(simpleFaction, def);

                Rect rowRect = new Rect(0f, drawY, viewRect.width, barHeight);
                if (resIdx % 2 == 0) Widgets.DrawHighlight(rowRect);
                UIUtilSC.DrawFlowHighlight(rowRect, simpleFlow.DailyNet);

                // Left accent bar colored by flow
                Color accentColor = simpleFlow.DailyNet > 0.01 ? AccentUtil.Income
                    : simpleFlow.DailyNet < -0.01 ? AccentUtil.Expense : Color.gray;
                Widgets.DrawBoxSolid(new Rect(0f, drawY, accentW, barHeight), accentColor);

                if (def.Icon != null)
                    GUI.DrawTexture(new Rect(contentX, drawY + 2f, 24f, 24f), def.Icon);

                Text.Anchor = TextAnchor.MiddleLeft;
                bool prevWordWrap = Text.WordWrap;
                Text.WordWrap = false;
                Rect labelRect = new Rect(contentX + 28f, drawY, 140f, barHeight);
                Widgets.Label(labelRect, TextUtil.ClampWithEllipsis(labelRect, def.label.CapitalizeFirst()));
                Text.WordWrap = prevWordWrap;

                Rect barRect = new Rect(labelEndX, drawY + 4f, barWidth, barHeight - 8f);
                Widgets.FillableBar(barRect, fillPct);

                // Arrow indicator (between bar and amount text)
                float arrowX = barRect.xMax + 2f;
                if (simpleFlow.DailyNet > 0.01)
                {
                    GUI.color = AccentUtil.Income;
                    GUI.DrawTexture(new Rect(arrowX, drawY + (barHeight - arrowSize) / 2f, arrowSize, arrowSize), TexUI.ArrowTexRight);
                    GUI.color = Color.white;
                }
                else if (simpleFlow.DailyNet < -0.01)
                {
                    GUI.color = AccentUtil.Expense;
                    GUI.DrawTexture(new Rect(arrowX, drawY + (barHeight - arrowSize) / 2f, arrowSize, arrowSize), TexUI.ArrowTexLeft);
                    GUI.color = Color.white;
                }

                float amountX = arrowX + arrowSize + 4f;
                Widgets.Label(new Rect(amountX, drawY, amountTextW, barHeight),
                    "SC_StockpileAmount".Translate(amount.ToString("F1"), cap.ToString("F0")));

                // Net flow readout
                double net = simpleFlow.DailyNet;
                if (net > 0.01 || net < -0.01)
                {
                    Text.Anchor = TextAnchor.MiddleRight;
                    Widgets.Label(new Rect(amountX + amountTextW, drawY, netFlowW, barHeight), TextUtil.ColorizeAdditiveBonus(Math.Round(net, 2)));
                    Text.Anchor = TextAnchor.MiddleLeft;
                }

                // Buy button
                float buyX = viewRect.width - buyBtnW - 2f;
                Rect buyRect = new Rect(buyX, drawY + 3f, buyBtnW, barHeight - 6f);
                Text.Font = GameFont.Tiny;
                if (Widgets.ButtonText(buyRect, "$"))
                {
                    ResourceTypeDef capturedDef = def;
                    UIUtilSC.ShowBuyMenu(capturedDef, stockpile, delegate { DirtyFlowCache(); });
                }
                TooltipHandler.TipRegion(buyRect, "SC_BuyTooltip".Translate());
                Text.Font = GameFont.Small;

                int numSettlements = simpleFaction != null ? simpleFaction.settlements.Count : 0;
                double baseCap = numSettlements * SupplyChainSettings.baseCapPerSettlement;
                double buildingCapBonus = cap - baseCap;

                TooltipHandler.TipRegion(rowRect, UIUtilSC.BuildFlowTooltip(def, amount, cap, simpleFlow,
                    numSettlements, SupplyChainSettings.baseCapPerSettlement, buildingCapBonus));

                Text.Anchor = TextAnchor.UpperLeft;
                drawY += barHeight + 2f;
                resIdx++;
            }

            drawY += 12f;

            // Sell Orders section
            Text.Font = GameFont.Medium;
            Rect sellHeaderRect = new Rect(0f, drawY, 300f, 30f);
            Widgets.Label(sellHeaderRect, "SC_StandingSellOrders".Translate());
            TooltipHandler.TipRegion(sellHeaderRect, (string)"SC_SellOrdersTooltip".Translate(
                SupplyChainSettings.overflowPenaltyRate.ToString("P0")));
            Text.Font = GameFont.Small;
            drawY += 34f;

            DrawAddSellOrderRow(viewRect, ref drawY);
            drawY += 4f;

            const float sellRowH = 28f;
            const float sellAccentW = 4f;

            List<SellOrder> toRemove = null;
            int sellIdx = 0;
            foreach (SellOrder order in globalSellOrders)
            {
                if (order.resource is null) continue;

                Rect rowRect = new Rect(0f, drawY, viewRect.width, sellRowH);
                if (sellIdx % 2 == 0) Widgets.DrawHighlight(rowRect);
                Widgets.DrawBoxSolid(new Rect(0f, drawY, sellAccentW, sellRowH), AccentUtil.Income);

                float cx = sellAccentW + 6f;

                Text.Anchor = TextAnchor.MiddleLeft;
                if (order.resource.Icon != null)
                    GUI.DrawTexture(new Rect(cx, drawY + 4f, 20f, 20f), order.resource.Icon);

                Widgets.Label(new Rect(cx + 24f, drawY, 120f, sellRowH),
                    order.resource.label.CapitalizeFirst());

                Widgets.Label(new Rect(cx + 150f, drawY, 130f, sellRowH),
                    "SC_UnitsPerPeriod".Translate(order.amountPerPeriod.ToString("F1")));

                float expectedSilver = (float)(order.amountPerPeriod * FCSettings.silverPerResource
                    * SupplyChainSettings.overflowPenaltyRate);
                GUI.color = new Color(0.7f, 1f, 0.7f);
                Widgets.Label(new Rect(cx + 290f, drawY, 100f, sellRowH),
                    "SC_ExpectedSilver".Translate(expectedSilver.ToString("F0")));
                GUI.color = Color.white;

                float removeX = viewRect.width - 64f;
                if (Widgets.ButtonText(new Rect(removeX, drawY + 2f, 60f, sellRowH - 4f), "SC_Remove".Translate()))
                {
                    if (toRemove is null) toRemove = new List<SellOrder>();
                    toRemove.Add(order);
                }

                Text.Anchor = TextAnchor.UpperLeft;
                drawY += sellRowH;
                sellIdx++;
            }
            if (toRemove != null)
            {
                foreach (SellOrder order in toRemove)
                    globalSellOrders.Remove(order);
                DirtyFlowCache();
            }

            // Overflow info
            drawY += 16f;
            Text.Font = GameFont.Tiny;
            GUI.color = Color.gray;
            Widgets.Label(new Rect(0f, drawY, viewRect.width, 40f),
                "SC_OverflowInfo".Translate(
                    SupplyChainSettings.overflowPenaltyRate.ToString("P0"),
                    FormulaUtil.OverflowSilver(1).ToString("0.##")));
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            drawY += 16f;

            // Tithe Injection section
            Text.Font = GameFont.Medium;
            Rect titheHeaderRect = new Rect(0f, drawY, 300f, 30f);
            Widgets.Label(titheHeaderRect, "SC_TitheInjection".Translate());
            TooltipHandler.TipRegion(titheHeaderRect, (string)"SC_TitheInjectionTooltip".Translate());
            Text.Font = GameFont.Small;
            drawY += 34f;

            DrawAddTitheInjectionRow_Simple(viewRect, ref drawY, simpleFaction);
            drawY += 4f;

            // Flat list of all tithe injections across all settlements
            const float titheRowH = 28f;
            List<KeyValuePair<WorldObjectComp_SupplyChain, ResourceTypeDef>> titheToRemove = null;
            int titheIdx = 0;

            if (simpleFaction != null)
            {
                foreach (WorldSettlementFC settlement in simpleFaction.settlements)
                {
                    WorldObjectComp_SupplyChain comp = GetComp(settlement);
                    if (comp == null) continue;
                    foreach (KeyValuePair<ResourceTypeDef, double> kv in comp.TitheInjections)
                    {
                        if (kv.Key == null || kv.Value <= 0) continue;

                        Rect titheRow = new Rect(0f, drawY, viewRect.width, titheRowH);
                        if (titheIdx % 2 == 0) Widgets.DrawHighlight(titheRow);
                        Widgets.DrawBoxSolid(new Rect(0f, drawY, accentW, titheRowH), AccentUtil.Expense);

                        float cx = accentW + 6f;
                        Text.Anchor = TextAnchor.MiddleLeft;

                        // Settlement name
                        Widgets.Label(new Rect(cx, drawY, 200f, titheRowH), settlement.Name);

                        // Resource icon + name
                        if (kv.Key.Icon != null)
                            GUI.DrawTexture(new Rect(cx + 204f, drawY + 4f, 20f, 20f), kv.Key.Icon);
                        Widgets.Label(new Rect(cx + 228f, drawY, 100f, titheRowH),
                            kv.Key.label.CapitalizeFirst());

                        // Units per day
                        Widgets.Label(new Rect(cx + 334f, drawY, 130f, titheRowH),
                            "SC_UnitsPerDay".Translate(kv.Value.ToString("F1")));

                        // Budget value (blue tint)
                        double silverBudget = kv.Value * FCSettings.silverPerResource;
                        float xBtnX = viewRect.width - 28f;
                        GUI.color = new Color(0.7f, 0.85f, 1f);
                        Widgets.Label(new Rect(cx + 470f, drawY, xBtnX - (cx + 470f) - 4f, titheRowH),
                            "SC_TitheBudgetValue".Translate(silverBudget.ToString("F0")));
                        GUI.color = Color.white;

                        // Remove button
                        if (Widgets.ButtonText(new Rect(xBtnX, drawY + 2f, 24f, 24f), "X"))
                        {
                            if (titheToRemove is null)
                                titheToRemove = new List<KeyValuePair<WorldObjectComp_SupplyChain, ResourceTypeDef>>();
                            titheToRemove.Add(new KeyValuePair<WorldObjectComp_SupplyChain, ResourceTypeDef>(comp, kv.Key));
                        }

                        Text.Anchor = TextAnchor.UpperLeft;
                        drawY += titheRowH;
                        titheIdx++;
                    }
                }
            }
            if (titheToRemove != null)
            {
                foreach (KeyValuePair<WorldObjectComp_SupplyChain, ResourceTypeDef> pair in titheToRemove)
                    pair.Key.SetTitheInjection(pair.Value, 0);
            }

            // Empty state
            if (titheIdx == 0)
            {
                Text.Font = GameFont.Tiny;
                GUI.color = Color.gray;
                Widgets.Label(new Rect(0f, drawY, viewRect.width, 24f),
                    "SC_NoTitheInjections".Translate());
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
            }

            ScrollUtil.EndScrollView();
        }

        // --- Complex Mode Faction Tab ---

        private void DrawFactionTab_Complex(Rect boundingBox)
        {
            Rect inner = boundingBox.ContractedBy(10f);

            EnsureCapsAndStockpiles();

            // Header
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inner.x, inner.y, 300f, 30f), "SC_EmpireSupplyNetwork".Translate(FindFC.EmpireTitle));
            Text.Font = GameFont.Tiny;
            GUI.color = Color.gray;
            Widgets.Label(new Rect(inner.x + 310f, inner.y + 4f, 100f, 26f), "SC_ModeComplex".Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            // Tab bar
            float tabY = inner.y + 38f;
            float tabH = 24f;
            float tabW = inner.width / 3f;
            string[] tabLabels =
            {
                "SC_TabStockpiles".Translate(),
                "SC_TabRoutes".Translate(),
                "SC_TabDeliveries".Translate()
            };

            Rect chosenRect = new Rect();
            for (int i = 0; i < 3; i++)
            {
                Rect tabRect = new Rect(inner.x + tabW * i, tabY, tabW, tabH);
                if (UIUtil.ButtonFlat(tabRect, tabLabels[i], highlighted: complexTab == i))
                    complexTab = i;
                if (complexTab == i)
                    chosenRect = tabRect;
            }

            // Tab underline decoration
            UIUtil.DrawTabDecoratorHorizontalTop(chosenRect, inner, Color.gray);

            // Content area below tabs
            float contentY = tabY + tabH;
            Rect contentRect = new Rect(inner.x, contentY, inner.width, inner.yMax - contentY);

            if (complexTab == 0)
                DrawComplexStockpiles(contentRect);
            else if (complexTab == 1)
                DrawComplexRoutes(contentRect);
            else
                DrawComplexDeliveries(contentRect);
        }

        private void DrawComplexStockpiles(Rect rect)
        {
            const float settRowH = 28f;
            const float accentW = 4f;
            const float rowGap = 2f;
            const float nameColW = 200f;
            const float headerH = 30f;
            const float barH = 16f;
            const float cellPad = 2f;
            const float arrowSize = 16f;

            FactionFC faction = FindFC.FactionComp;
            if (faction is null) return;

            // Resource columns cache (non-poolResource with cap > 0 in any settlement)
            if (resourceColumnsDirty || cachedResourceColumns is null)
            {
                cachedResourceColumns = new List<ResourceTypeDef>();
                foreach (ResourceTypeDef def in SupplyChainCache.AllResourceTypeDefs)
                {
                    bool anyHasCap = false;
                    foreach (WorldSettlementFC s in faction.settlements)
                    {
                        WorldObjectComp_SupplyChain c = GetComp(s);
                        IStockpile p = c?.GetStockpile();
                        if (p?.GetCap(def) > 0)
                        {
                            anyHasCap = true;
                            break;
                        }
                    }
                    if (anyHasCap) cachedResourceColumns.Add(def);
                }
                resourceColumnsDirty = false;
            }
            List<ResourceTypeDef> columns = cachedResourceColumns;

            int resCount = columns.Count;
            float availableW = rect.width - 16f; // account for scrollbar
            float colW = resCount > 0 ? (availableW - nameColW) / resCount : 0f;

            // --- Pinned header row (outside scroll) ---
            Rect headerRect = new Rect(rect.x, rect.y, rect.width, headerH);
            Widgets.DrawBoxSolid(headerRect, new Color(0.1f, 0.1f, 0.1f, 0.5f));

            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(rect.x + accentW + 6f, rect.y, nameColW - accentW - 6f, headerH), "SC_Settlement".Translate());
            Text.Anchor = TextAnchor.UpperLeft;

            for (int i = 0; i < resCount; i++)
            {
                ResourceTypeDef def = columns[i];
                float colX = rect.x + nameColW + colW * i;
                // Center icon in column
                float iconSize = 24f;
                float iconX = colX + (colW - iconSize) / 2f;
                float iconY = rect.y + (headerH - iconSize) / 2f;
                if (def.Icon != null)
                    GUI.DrawTexture(new Rect(iconX, iconY, iconSize, iconSize), def.Icon);
                // Tooltip on header
                TooltipHandler.TipRegion(new Rect(colX, rect.y, colW, headerH), def.label.CapitalizeFirst());
            }

            Text.Font = GameFont.Small;

            // --- Scrollable settlement rows ---
            Rect scrollRect = new Rect(rect.x, rect.y + headerH, rect.width, rect.height - headerH);
            int settlementCount = faction.settlements.Count;
            float totalHeight = settlementCount * (settRowH + rowGap) + 20f;

            Rect viewRect = ScrollUtil.BeginScrollView(scrollRect, ref scrollPosStockpiles, totalHeight);
            float curY = 4f;

            int sIdx = 0;
            foreach (WorldSettlementFC settlement in faction.settlements)
            {
                WorldObjectComp_SupplyChain comp = GetComp(settlement);

                Rect sRow = new Rect(0f, curY, viewRect.width, settRowH);
                if (sIdx % 2 == 0) Widgets.DrawHighlight(sRow);
                if (Mouse.IsOver(sRow)) Widgets.DrawHighlight(sRow);

                // Determine accent from flow state
                Color accent = Color.gray;
                if (comp != null)
                {
                    bool hasDeficit = false;
                    bool hasSurplus = false;
                    foreach (ResourceTypeDef flowDef in columns)
                    {
                        IStockpile checkStockpile = comp.GetStockpile();
                        if (checkStockpile == null || checkStockpile.GetCap(flowDef) <= 0) continue;
                        FlowBreakdown flow = GetCachedFlow(settlement, comp, flowDef);
                        if (flow.DailyNet < -0.01)
                            hasDeficit = true;
                        else if (flow.DailyNet > 0.01)
                            hasSurplus = true;
                    }
                    if (hasDeficit)
                        accent = AccentUtil.Expense;
                    else if (hasSurplus)
                        accent = AccentUtil.Income;
                }
                Widgets.DrawBoxSolid(new Rect(0f, curY, accentW, settRowH), accent);

                // Settlement name (clickable)
                Text.Anchor = TextAnchor.MiddleLeft;
                bool prevWordWrap = Text.WordWrap;
                Text.WordWrap = false;
                float nameWidth = nameColW - accentW - 10f;
                Rect nameRect = new Rect(accentW + 6f, curY, nameWidth, settRowH);
                Widgets.Label(nameRect, TextUtil.ClampWithEllipsis(nameRect, settlement.Name));
                Text.WordWrap = prevWordWrap;
                if (Mouse.IsOver(nameRect))
                    Widgets.DrawHighlight(nameRect);
                if (Widgets.ButtonInvisible(nameRect))
                    Find.WindowStack.Add(new SettlementWindowFc(settlement));

                // Resource cells
                IStockpile sStockpile = comp?.GetStockpile();
                for (int i = 0; i < resCount; i++)
                {
                    ResourceTypeDef def = columns[i];
                    float cellX = nameColW + colW * i;
                    Rect cellRect = new Rect(cellX, curY, colW, settRowH);

                    double amt = sStockpile?.GetAmount(def) ?? 0;
                    double cap = sStockpile?.GetCap(def) ?? 0;

                    if (cap <= 0)
                    {
                        // No capacity for this resource in this settlement — draw dash
                        Text.Anchor = TextAnchor.MiddleCenter;
                        UIUtil.DrawColoredLabel(cellRect, "-", Color.gray);
                        continue;
                    }

                    float fill = (float)(amt / cap);
                    FlowBreakdown flow = GetCachedFlow(settlement, comp, def);

                    // Flow highlight on cell background
                    UIUtilSC.DrawFlowHighlight(cellRect, flow.DailyNet);

                    // Fill bar centered vertically in cell
                    float barY = curY + (settRowH - barH) / 2f;
                    Rect barRect = new Rect(cellX + cellPad, barY, colW - cellPad * 2f, barH);
                    Widgets.FillableBar(barRect, fill);

                    // Arrow indicator (top-right corner of cell)
                    if (flow.DailyNet > 0.01)
                    {
                        GUI.color = AccentUtil.Income;
                        GUI.DrawTexture(new Rect(cellX + colW - arrowSize - 1f, curY + 1f, arrowSize, arrowSize), TexUI.ArrowTexRight);
                        GUI.color = Color.white;
                    }
                    else if (flow.DailyNet < -0.01)
                    {
                        GUI.color = AccentUtil.Expense;
                        GUI.DrawTexture(new Rect(cellX + colW - arrowSize - 1f, curY + 1f, arrowSize, arrowSize), TexUI.ArrowTexLeft);
                        GUI.color = Color.white;
                    }

                    // Tooltip
                    TooltipHandler.TipRegion(cellRect, UIUtilSC.BuildFlowTooltip(def, amt, cap, flow));
                }

                Text.Anchor = TextAnchor.UpperLeft;

                curY += settRowH + rowGap;
                sIdx++;
            }

            ScrollUtil.EndScrollView();
        }

        private void DrawComplexRoutes(Rect rect)
        {
            const float routeRowH = 32f;
            const float accentW = 4f;
            const float rowGap = 2f;

            FactionFC faction = FindFC.FactionComp;
            float totalHeight = supplyRoutes.Count * (routeRowH + rowGap) + 150f;

            Rect viewRect = ScrollUtil.BeginScrollView(rect, ref scrollPosRoutes, totalHeight);
            float curY = 4f;
            float rowW = viewRect.width;

            // Add new route (above list)
            DrawAddRouteRow(viewRect, ref curY, faction);
            curY += 4f;

            // Resource filter buttons
            float fbX = 0f;
            float fbH = 22f;
            Text.Font = GameFont.Tiny;

            bool allActive = routeFilterResource == null;
            if (UIUtil.ButtonFlat(new Rect(fbX, curY, 40f, fbH), (string)"SC_All".Translate(), highlighted: allActive))
                routeFilterResource = null;
            fbX += 44f;

            HashSet<ResourceTypeDef> routeResources = new HashSet<ResourceTypeDef>();
            foreach (SupplyRoute r in supplyRoutes)
            {
                if (r.IsValid() && r.resource != null)
                    routeResources.Add(r.resource);
            }
            foreach (ResourceTypeDef filterDef in routeResources)
            {
                bool active = routeFilterResource == filterDef;
                string btnLabel = filterDef.label.CapitalizeFirst();
                float btnW = Text.CalcSize(btnLabel).x + 28f;
                if (filterDef.Icon != null)
                    GUI.DrawTexture(new Rect(fbX + 4f, curY + 3f, 16f, 16f), filterDef.Icon);
                if (UIUtil.ButtonFlat(new Rect(fbX, curY, btnW, fbH), "   " + btnLabel, labelColor: filterDef.color,
                        highlighted: active))
                {
                    routeFilterResource = filterDef;
                    break;
                }

                fbX += btnW + 4f;
            }

            // Reset filter if filtered resource has no routes
            if (routeFilterResource != null && !routeResources.Contains(routeFilterResource))
                routeFilterResource = null;

            Text.Font = GameFont.Small;
            curY += fbH + 4f;

            // Pre-build the visible (filtered) list so the reorder arrows have indexable neighbors;
            // cull invalid routes while we walk the master list.
            List<SupplyRoute> routesToRemove = null;
            List<SupplyRoute> shown = new List<SupplyRoute>();
            foreach (SupplyRoute route in supplyRoutes)
            {
                if (!route.IsValid())
                {
                    if (routesToRemove is null) routesToRemove = new List<SupplyRoute>();
                    routesToRemove.Add(route);
                    continue;
                }
                if (routeFilterResource != null && route.resource != routeFilterResource) continue;
                shown.Add(route);
            }

            for (int v = 0; v < shown.Count; v++)
            {
                SupplyRoute route = shown[v];

                // Cheap efficiency refresh only — the expensive travel-time/path pathfind is warmed off
                // the UI thread by SupplyRouteWarmer; show a placeholder until it's ready.
                route.RecacheEfficiencyIfDirty();
                bool pathReady = route.PathReady;

                Rect rRow = new Rect(0f, curY, rowW, routeRowH);
                if (v % 2 == 0) Widgets.DrawHighlight(rRow);

                float eff = (float)route.CachedEfficiency;
                Color routeAccent = route.resource != null ? route.resource.color : Color.gray;
                Color effAccent = pathReady ? AccentUtil.GetStatColor(eff * 100f, false) : Color.gray;
                Widgets.DrawBoxSolid(new Rect(0f, curY, accentW, routeRowH), routeAccent);
                Widgets.DrawBoxSolid(new Rect(accentW + 2f, curY, accentW, routeRowH), effAccent);

                // Reorder arrows (dispatch precedence = list order). Break after a move — the master
                // list just changed underneath us; the next frame redraws the new order.
                float reorderX = accentW * 2 + 2f + 4f;
                float arrowH2 = routeRowH / 2f;
                if (v > 0 && Widgets.ButtonImage(new Rect(reorderX, curY, 14f, arrowH2), TexButton.ReorderUp))
                {
                    MoveRouteBefore(route, shown[v - 1]);
                    break;
                }
                if (v < shown.Count - 1 && Widgets.ButtonImage(new Rect(reorderX, curY + arrowH2, 14f, arrowH2), TexButton.ReorderDown))
                {
                    MoveRouteAfter(route, shown[v + 1]);
                    break;
                }
                TooltipHandler.TipRegion(new Rect(reorderX, curY, 14f, routeRowH), "SC_RouteReorderTooltip".Translate());

                float cx = reorderX + 14f + 6f;

                Text.Anchor = TextAnchor.MiddleLeft;

                if (route.resource != null && route.resource.Icon != null)
                    GUI.DrawTexture(new Rect(cx, curY + 6f, 20f, 20f), route.resource.Icon);

                string resName = route.resource != null ? route.resource.label.CapitalizeFirst() : "?";
                Widgets.Label(new Rect(cx + 24f, curY, 80f, routeRowH), resName);

                // Right-anchored elements
                float removeX = rowW - 64f;
                float effX = removeX - 70f;
                float amtX = effX - 110f;
                float freqX = amtX - 60f;

                // Source / Arrow / Dest columns
                float routeTextX = cx + 108f;
                float routeTextW = freqX - routeTextX - 4f;
                float arrowW = 24f;
                float nameColW = (routeTextW - arrowW) / 2f;

                Text.Anchor = TextAnchor.MiddleRight;
                Widgets.Label(new Rect(routeTextX, curY, nameColW, routeRowH), route.source.Name);
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(new Rect(routeTextX + nameColW, curY, arrowW, routeRowH), "\u2192");
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(new Rect(routeTextX + nameColW + arrowW, curY, nameColW, routeRowH), route.destination.Name);

                // Frequency stepper: [-] Nd [+]
                DeliveryUIUtil.DrawFrequencyStepper(new Rect(freqX, curY, 58f, routeRowH), route, DirtyFlowCache);

                Widgets.Label(new Rect(amtX, curY, 106f, routeRowH),
                    "SC_PerPeriod".Translate(route.amountPerPeriod.ToString("F1")));

                GUI.color = effAccent;
                Rect effRect = new Rect(effX, curY, 66f, routeRowH);
                if (pathReady)
                {
                    Widgets.Label(effRect, "SC_EfficiencyPercent".Translate((eff * 100).ToString("F0")));
                    GUI.color = Color.white;

                    double travelDays = route.CachedTravelTicks / (double)GenDate.TicksPerDay;
                    TooltipHandler.TipRegion(effRect,
                        "SC_EfficiencyTooltip".Translate(
                            travelDays.ToString("F1"),
                            SupplyChainSettings.routeDecayPerDay.ToString("F2"),
                            (eff * 100).ToString("F1")));
                }
                else
                {
                    Widgets.Label(effRect, "SC_RoutePending".Translate());
                    GUI.color = Color.white;
                }

                if (Widgets.ButtonText(new Rect(removeX, curY + 4f, 60f, routeRowH - 8f), "SC_Remove".Translate()))
                {
                    if (routesToRemove is null) routesToRemove = new List<SupplyRoute>();
                    routesToRemove.Add(route);
                }

                Text.Anchor = TextAnchor.UpperLeft;
                curY += routeRowH + rowGap;
            }

            if (routesToRemove != null)
            {
                foreach (SupplyRoute route in routesToRemove)
                    UnlinkRoute(route);
                DirtyFlowCache();
            }

            ScrollUtil.EndScrollView();
        }

        // --- Deliveries (Complex mode, faction-wide) ---

        private void DrawComplexDeliveries(Rect rect)
        {
            DeliveryUIUtil.DrawDeliveriesList(rect, ref scrollPosDeliveries, pendingDeliveries, null);
        }

        // --- Add Route Row (Complex mode) ---

        private void DrawAddRouteRow(Rect viewRect, ref float curY, FactionFC faction)
        {
            if (faction is null) return;

            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(0f, curY, 70f, 26f), "SC_NewRoute".Translate());

            // Calculate dynamic picker widths
            float fixedW = 74f + 114f + 78f + 52f + 54f; // label + resource+gap + amount+gap + freq+gap + add
            float remainW = viewRect.width - fixedW - 8f;
            float pickerW = remainW / 2f;
            if (pickerW < 140f) pickerW = 140f;

            float bx = 74f;

            // Resource picker (first)
            string resLabel = newRouteResource != null ? newRouteResource.label.CapitalizeFirst() : (string)"SC_ResourcePicker".Translate();
            if (Widgets.ButtonText(new Rect(bx, curY, 110f, 24f), resLabel))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>();
                foreach (ResourceTypeDef def in SupplyChainCache.AllResourceTypeDefs)
                {
                    ResourceTypeDef captured = def;
                    options.Add(new FloatMenuOption(def.label.CapitalizeFirst(), delegate { newRouteResource = captured; }));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }
            bx += 114f;

            // Source picker (with production info if resource selected)
            string srcLabel = newRouteSource != null ? newRouteSource.Name : (string)"SC_SourcePicker".Translate();
            if (Widgets.ButtonText(new Rect(bx, curY, pickerW, 24f), srcLabel))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>();
                foreach (WorldSettlementFC s in faction.settlements)
                {
                    WorldSettlementFC captured = s;
                    string label = s.Name;
                    if (newRouteResource != null)
                    {
                        ResourceFC res = s.GetResource(newRouteResource);
                        if (res != null)
                            label += " (" + res.rawTotalProduction.ToString("F1") + "/day)";
                    }
                    options.Add(new FloatMenuOption(label, delegate { newRouteSource = captured; }));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }
            bx += pickerW + 4f;

            // Dest picker (with need info if resource selected)
            string destLabel = newRouteDest != null ? newRouteDest.Name : (string)"SC_DestPicker".Translate();
            if (Widgets.ButtonText(new Rect(bx, curY, pickerW, 24f), destLabel))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>();
                foreach (WorldSettlementFC s in faction.settlements)
                {
                    WorldSettlementFC captured = s;
                    string label = s.Name;
                    if (newRouteResource != null)
                    {
                        foreach (SettlementNeedDef needDef in SupplyChainCache.AllNeedDefs)
                        {
                            if (!needDef.IsActiveForSettlement(captured)) continue;
                            if (needDef.UsesResource(newRouteResource))
                            {
                                double demand = needDef.CalculateDemand(captured)
                                    * needDef.GetResourceFraction(FindFC.TechLevel, newRouteResource);
                                label += " (need: " + demand.ToString("F1") + "/day)";
                                break;
                            }
                        }
                    }
                    options.Add(new FloatMenuOption(label, delegate { newRouteDest = captured; }));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }
            bx += pickerW + 4f;

            // Amount
            Widgets.TextFieldNumeric(new Rect(bx, curY, 70f, 24f),
                ref newRouteAmount, ref newRouteAmountBuffer, 0f, 9999f);
            bx += 74f;

            // Frequency (days between deliveries)
            Rect freqRect = new Rect(bx, curY, 44f, 24f);
            Widgets.TextFieldNumeric(freqRect, ref newRouteFrequency, ref newRouteFreqBuffer,
                SupplyChainSettings.minRouteFrequencyDays, SupplyChainSettings.maxRouteFrequencyDays);
            TooltipHandler.TipRegion(freqRect, "SC_FrequencyTooltip".Translate());
            bx += 48f;

            // Confirm
            if (Widgets.ButtonText(new Rect(bx, curY, 50f, 24f), "SC_Add".Translate()))
            {
                if (newRouteSource != null && newRouteDest != null && newRouteResource != null
                    && newRouteAmount > 0 && newRouteSource != newRouteDest)
                {
                    SupplyRoute route = new SupplyRoute(newRouteSource, newRouteDest, newRouteResource, newRouteAmount);
                    route.frequencyDays = Mathf.Clamp(newRouteFrequency,
                        SupplyChainSettings.minRouteFrequencyDays, SupplyChainSettings.maxRouteFrequencyDays);
                    LinkRoute(route);
                    DirtyFlowCache();

                    newRouteSource = null;
                    newRouteDest = null;
                    newRouteResource = null;
                    newRouteAmount = 0;
                    newRouteAmountBuffer = "";
                }
            }

            Text.Anchor = TextAnchor.UpperLeft;
            curY += 28f;

            // Hint if source == dest
            if (newRouteSource != null && newRouteDest != null && newRouteSource == newRouteDest)
            {
                Text.Font = GameFont.Tiny;
                GUI.color = new Color(1f, 0.5f, 0.5f);
                Widgets.Label(new Rect(0f, curY, viewRect.width, 20f), "SC_SameSettlementError".Translate());
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                curY += 22f;
            }
        }

        // --- Add Sell Order Row (Simple mode) ---

        private void DrawAddSellOrderRow(Rect inner, ref float curY)
        {
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(inner.x, curY, 40f, 26f), "SC_AddColon".Translate());

            string resLabel = newSellOrderResource != null ? newSellOrderResource.label.CapitalizeFirst() : (string)"SC_PickResource".Translate();
            if (Widgets.ButtonText(new Rect(inner.x + 44f, curY, 130f, 24f), resLabel))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>();
                foreach (ResourceTypeDef def in SupplyChainCache.AllResourceTypeDefs)
                {
                    if (def.isPoolResource) continue;
                    ResourceTypeDef captured = def;
                    options.Add(new FloatMenuOption(def.label.CapitalizeFirst(), delegate
                    {
                        newSellOrderResource = captured;
                    }));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }

            Widgets.TextFieldNumeric(new Rect(inner.x + 180f, curY, 80f, 24f),
                ref newSellOrderAmount, ref newSellOrderAmountBuffer, 0f, 9999f);

            if (Widgets.ButtonText(new Rect(inner.x + 268f, curY, 60f, 24f), "SC_Add".Translate()))
            {
                if (newSellOrderResource != null && newSellOrderAmount > 0)
                {
                    globalSellOrders.Add(new SellOrder(newSellOrderResource, newSellOrderAmount));
                    DirtyFlowCache();
                    newSellOrderResource = null;
                    newSellOrderAmount = 0;
                    newSellOrderAmountBuffer = "";
                }
            }

            Text.Anchor = TextAnchor.UpperLeft;
            curY += 28f;
        }

        // --- Add Tithe Injection Row (Simple mode main tab) ---

        private void DrawAddTitheInjectionRow_Simple(Rect viewRect, ref float curY, FactionFC faction)
        {
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(0f, curY, 40f, 26f), "SC_AddColon".Translate());

            // Settlement picker
            string settLabel = newTitheSettlement != null ? newTitheSettlement.Name : (string)"SC_PickSettlement".Translate();
            if (Widgets.ButtonText(new Rect(44f, curY, 200f, 24f), settLabel))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>();
                if (faction != null)
                {
                    foreach (WorldSettlementFC s in faction.settlements)
                    {
                        WorldSettlementFC captured = s;
                        options.Add(new FloatMenuOption(s.Name, delegate
                        {
                            newTitheSettlement = captured;
                        }));
                    }
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }

            // Resource picker
            string resLabel = newTitheResource != null ? newTitheResource.label.CapitalizeFirst() : (string)"SC_PickResource".Translate();
            if (Widgets.ButtonText(new Rect(250f, curY, 130f, 24f), resLabel))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>();
                // Only tithable resources: SetTitheInjection silently no-ops on the rest.
                foreach (ResourceTypeDef def in FactionCache.TitheableResourceTypeDefs)
                {
                    ResourceTypeDef captured = def;
                    options.Add(new FloatMenuOption(def.label.CapitalizeFirst(), delegate
                    {
                        newTitheResource = captured;
                    }));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }

            // Amount
            Widgets.TextFieldNumeric(new Rect(386f, curY, 80f, 24f),
                ref newTitheAmount, ref newTitheAmountBuffer, 0f, 9999f);

            // Add button
            if (Widgets.ButtonText(new Rect(474f, curY, 60f, 24f), "SC_Add".Translate()))
            {
                if (newTitheSettlement != null && newTitheResource != null && newTitheAmount > 0)
                {
                    WorldObjectComp_SupplyChain comp = GetComp(newTitheSettlement);
                    if (comp != null)
                    {
                        comp.SetTitheInjection(newTitheResource, newTitheAmount);
                    }
                    else
                    {
                        newTitheSettlement = null;
                    }
                    newTitheResource = null;
                    newTitheAmount = 0;
                    newTitheAmountBuffer = "";
                }
            }

            Text.Anchor = TextAnchor.UpperLeft;
            curY += 28f;
        }
    }
}
