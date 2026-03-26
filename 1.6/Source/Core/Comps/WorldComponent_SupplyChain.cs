using System;
using System.Collections.Generic;
using System.Linq;
using FactionColonies.util;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace FactionColonies.SupplyChain
{
    public class WorldComponent_SupplyChain : WorldComponent, ITaxTickParticipant, IMainTabWindowOverview, ILifecycleParticipant
    {
        private SupplyChainMode mode = SupplyChainMode.Simple;
        private Dictionary<ResourceTypeDef, double> factionStockpile = new Dictionary<ResourceTypeDef, double>();
        private Dictionary<ResourceTypeDef, double> factionCaps = new Dictionary<ResourceTypeDef, double>();
        private List<SellOrder> globalSellOrders = new List<SellOrder>();

        // Complex mode
        private List<SupplyRoute> supplyRoutes = new List<SupplyRoute>();
        private List<SupplyRoute> dormantRoutes = new List<SupplyRoute>();

        // Route visualization (transient, not saved)
        public bool showAllRoutes;
        public bool showSelectedRoutes;
        public bool showRouteLabels;

        // Pair caches: one representative route + one combined label per directed settlement pair
        private Dictionary<long, SupplyRoute> pairRouteCache = new Dictionary<long, SupplyRoute>();
        private Dictionary<long, string> pairLabelCache = new Dictionary<long, string>();
        private bool pairCacheDirty = true;

        private bool capsAndStockpilesDirty = true;
        private DictionaryStockpile stockpile;

        // Flow cache: keyed by (settlementTile << 16 | resourceDefIndex)
        private Dictionary<ulong, FlowBreakdown> flowCache = new Dictionary<ulong, FlowBreakdown>();
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

        // Complex mode tab state
        private int complexTab = 0; // 0 = Stockpiles, 1 = Routes
        private Vector2 scrollPosStockpiles;
        private Vector2 scrollPosRoutes;

        // Route creation UI state
        private WorldSettlementFC newRouteSource;
        private WorldSettlementFC newRouteDest;
        private ResourceTypeDef newRouteResource;
        private string newRouteAmountBuffer = "";
        private float newRouteAmount;
        private ResourceTypeDef routeFilterResource;

        public WorldComponent_SupplyChain(World world) : base(world)
        {
        }

        public SupplyChainMode Mode
        {
            get { return mode; }
        }

        public IStockpile Stockpile
        {
            get { return stockpile; }
        }

        public List<SupplyRoute> SupplyRoutes
        {
            get { return supplyRoutes; }
        }

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

            stockpile = new DictionaryStockpile(factionStockpile, factionCaps);

            TaxTickRegistry.Register(this);
            MainTableRegistry.Register(this);
            LifecycleRegistry.Register(this);

            BuildingFilterRegistry.Register(new BuildingFilter(
                "SC_FilterStockpileCap".Translate(),
                null,
                def =>
                {
                    BuildingNeedExtension ext = def.GetModExtension<BuildingNeedExtension>();
                    return ext != null && ext.capBonuses != null && ext.capBonuses.Count > 0;
                }
            ));
            BuildingFilterRegistry.Register(new BuildingFilter(
                "SC_FilterBuildingNeeds".Translate(),
                null,
                def =>
                {
                    BuildingNeedExtension ext = def.GetModExtension<BuildingNeedExtension>();
                    return ext != null && ext.inputs != null && ext.inputs.Count > 0;
                }
            ));

            SupplyChainCache.ClearCompCache();
            capsAndStockpilesDirty = true;

            // Reconcile with global settings (mode may have changed while this save was unloaded)
            if (mode != SupplyChainSettings.mode)
            {
                LogUtil.Message("Mode mismatch: save=" + mode + ", settings=" + SupplyChainSettings.mode + ". Switching.");
                SwitchMode(SupplyChainSettings.mode);
            }

            LogUtil.Message("WorldComponent_SupplyChain initialized (mode=" + mode + ", fromLoad=" + fromLoad + ")");
        }

        // --- World Map Route Visualization ---

        private static long MakePairKey(SupplyRoute route)
        {
            return ((long)route.source.Tile.tileId << 32) | ((long)route.destination.Tile.tileId & 0xFFFFFFFFL);
        }

        internal void EnsurePairCaches()
        {
            if (!pairCacheDirty) return;
            pairCacheDirty = false;
            pairRouteCache.Clear();
            pairLabelCache.Clear();

            foreach (SupplyRoute route in supplyRoutes)
            {
                if (!route.IsValid()) continue;
                long key = MakePairKey(route);

                if (!pairRouteCache.ContainsKey(key))
                    pairRouteCache[key] = route;

                string line = route.amountPerPeriod.ToString("F0") + " " + route.resource.label;
                string existing;
                if (pairLabelCache.TryGetValue(key, out existing))
                    pairLabelCache[key] = existing + "\n" + line;
                else
                    pairLabelCache[key] = line;
            }

            // Append destination name to each label
            foreach (long key in new List<long>(pairLabelCache.Keys))
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

            foreach (KeyValuePair<long, SupplyRoute> kvp in pairRouteCache)
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

                string label;
                pairLabelCache.TryGetValue(kvp.Key, out label);
                if (label != null)
                    RouteOverlayUtil.DrawRouteLabel(route, grid, label);
            }

            GUI.color = Color.white;
            Text.Font = prev;
        }

        public static void DrawRoutesForSettlement(WorldComponent_SupplyChain wc, WorldSettlementFC ws)
        {
            if (ws == null || wc == null) return;
            wc.EnsurePairCaches();
            WorldGrid grid = Find.WorldGrid;

            foreach (SupplyRoute route in wc.pairRouteCache.Values)
            {
                if (route.source != ws && route.destination != ws) continue;
                RouteOverlayUtil.DrawRoute(route, grid);
            }
        }

        // --- Save/Load ---

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref mode, "mode", SupplyChainMode.Simple);
            Scribe_Collections.Look(ref factionStockpile, "factionStockpile", LookMode.Def, LookMode.Value);
            Scribe_Collections.Look(ref factionCaps, "factionCaps", LookMode.Def, LookMode.Value);
            Scribe_Collections.Look(ref globalSellOrders, "globalSellOrders", LookMode.Deep);

            Scribe_Collections.Look(ref supplyRoutes, "supplyRoutes", LookMode.Deep);
            Scribe_Collections.Look(ref dormantRoutes, "dormantRoutes", LookMode.Deep);

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
            }
        }

        // --- Helpers ---

        private void RecalculateCaps()
        {
            FactionFC faction = FactionCache.FactionComp;
            int numSettlements = faction != null ? faction.settlements.Count : 0;

            foreach (ResourceTypeDef def in SupplyChainCache.AllResourceTypeDefs)
            {
                if (def.isPoolResource)
                    continue;
                factionCaps[def] = numSettlements * SupplyChainSettings.baseCapPerSettlement;
            }

            if (faction == null) return;
            foreach (WorldSettlementFC settlement in faction.settlements)
            {
                if (settlement.BuildingsComp == null) continue;
                foreach (BuildingFC building in settlement.BuildingsComp.Buildings)
                {
                    if (building.def == null || building.def == BuildingFCDefOf.Empty) continue;
                    BuildingNeedExtension ext = SupplyChainCache.GetBuildingNeedExt(building.def);
                    if (ext?.capBonuses == null) continue;
                    foreach (BuildingCapBonus bonus in ext.capBonuses)
                    {
                        if (bonus.resource != null && !bonus.resource.isPoolResource && factionCaps.ContainsKey(bonus.resource))
                            factionCaps[bonus.resource] += bonus.amount;
                    }
                }
            }
        }

        private void InitAllLocalStockpiles()
        {
            FactionFC faction = FactionCache.FactionComp;
            if (faction == null) return;

            foreach (WorldSettlementFC settlement in faction.settlements)
            {
                WorldObjectComp_SupplyChain comp = GetComp(settlement);
                if (comp != null)
                {
                    comp.RecalculateLocalCaps();
                    comp.InitLocalStockpile();
                }
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

        internal struct FlowBreakdown
        {
            public double production;
            public double routeIn;
            public double baseNeeds;
            public double buildingNeeds;
            public double routeOut;
            public double sellOrders;
            public double titheInjection;
            public double needs { get { return baseNeeds + buildingNeeds; } }
            public double Net { get { return production + routeIn - needs - routeOut - sellOrders - titheInjection; } }
        }

        private static ulong FlowKey(int settlementTile, ushort resourceIndex)
        {
            return ((ulong)(uint)settlementTile << 16) | resourceIndex;
        }

        internal FlowBreakdown GetCachedFlow(WorldSettlementFC settlement, WorldObjectComp_SupplyChain comp, ResourceTypeDef def)
        {
            if (flowCacheDirty)
            {
                flowCache.Clear();
                simpleFlowCache.Clear();
                flowCacheDirty = false;
            }

            ulong key = FlowKey(settlement.Tile, def.index);
            FlowBreakdown flow;
            if (!flowCache.TryGetValue(key, out flow))
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

            FlowBreakdown flow;
            if (!simpleFlowCache.TryGetValue(def.index, out flow))
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
            FlowBreakdown flow = new FlowBreakdown();
            flow.production = comp.GetAllocation(def);

            foreach (SupplyRoute route in supplyRoutes)
            {
                if (!route.IsValid() || route.resource != def) continue;
                if (route.destination == settlement)
                    flow.routeIn += route.amountPerPeriod * route.CachedEfficiency;
                if (route.source == settlement)
                    flow.routeOut += route.amountPerPeriod;
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
            if (faction == null) return flow;

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
            FactionFC faction = FactionCache.FactionComp;
            foreach (SettlementNeedDef needDef in SupplyChainCache.AllNeedDefs)
            {
                if (faction != null && !needDef.IsActiveForFaction(faction)) continue;
                if (needDef.resource == def)
                    flow.baseNeeds += needDef.CalculateDemand(settlement);
            }

            if (settlement.BuildingsComp != null)
            {
                foreach (BuildingFC building in settlement.BuildingsComp.Buildings)
                {
                    if (building.def == null || building.def == BuildingFCDefOf.Empty) continue;
                    BuildingNeedExtension ext = SupplyChainCache.GetBuildingNeedExt(building.def);
                    if (ext == null || ext.inputs == null) continue;
                    foreach (BuildingResourceInput input in ext.inputs)
                    {
                        if (input.resource == def)
                            flow.buildingNeeds += input.amount;
                    }
                }
            }
        }

        // --- Mode Switching ---

        public void SwitchMode(SupplyChainMode newMode)
        {
            if (newMode == mode) return;

            FactionFC faction = FactionCache.FactionComp;
            if (faction == null) return;

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
            LogUtil.Message("Supply chain mode switched to " + newMode);
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
                    if (!resource.def.isPoolResource)
                        settlementProd += resource.rawTotalProduction;
                }
                productionShares[settlement] = settlementProd;
                totalProduction += settlementProd;
            }

            // 2. Distribute faction stockpile proportionally
            foreach (WorldSettlementFC settlement in faction.settlements)
            {
                WorldObjectComp_SupplyChain comp = GetComp(settlement);
                if (comp == null) continue;

                comp.RecalculateLocalCaps();
                comp.InitLocalStockpile();

                double share = totalProduction > 0 ? productionShares[settlement] / totalProduction : 1.0 / faction.settlements.Count;

                foreach (KeyValuePair<ResourceTypeDef, double> kv in factionStockpile)
                {
                    double amount = kv.Value * share;
                    if (amount > 0)
                        comp.GetStockpile().Credit(kv.Key, amount);
                }
            }

            // 3. Restore valid dormant routes
            foreach (SupplyRoute route in dormantRoutes)
            {
                if (route.IsValid())
                {
                    route.Invalidate();
                    supplyRoutes.Add(route);
                }
                else
                {
                    LogUtil.Message("Discarding invalid dormant route (settlement destroyed).");
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
                if (comp == null) continue;

                foreach (KeyValuePair<ResourceTypeDef, double> kv in comp.LocalStockpile)
                {
                    double current;
                    factionStockpile.TryGetValue(kv.Key, out current);
                    factionStockpile[kv.Key] = current + kv.Value;
                }

                comp.ClearLocalData();
            }

            // 2. Stash routes as dormant
            dormantRoutes.AddRange(supplyRoutes);
            supplyRoutes.Clear();

            // 3. Reconstruct faction stockpile and recalculate caps
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
            RecalculateCaps();

            Dictionary<ResourceTypeDef, double> totalOverflow = new Dictionary<ResourceTypeDef, double>();
            Dictionary<ResourceTypeDef, Dictionary<WorldSettlementFC, double>> contributions =
                new Dictionary<ResourceTypeDef, Dictionary<WorldSettlementFC, double>>();

            // 1. ACCUMULATE
            foreach (WorldSettlementFC settlement in faction.settlements)
            {
                WorldObjectComp_SupplyChain comp = GetComp(settlement);
                if (comp == null) continue;

                foreach (ResourceFC resource in settlement.Resources)
                {
                    if (resource.def.isPoolResource) continue;

                    double allocated = comp.GetAllocation(resource.def);
                    if (allocated <= 0) continue;

                    double excess = stockpile.Credit(resource.def, allocated);

                    Dictionary<WorldSettlementFC, double> contribMap;
                    if (!contributions.TryGetValue(resource.def, out contribMap))
                    {
                        contribMap = new Dictionary<WorldSettlementFC, double>();
                        contributions[resource.def] = contribMap;
                    }
                    contribMap[settlement] = allocated;

                    if (excess > 0)
                    {
                        double current;
                        totalOverflow.TryGetValue(resource.def, out current);
                        totalOverflow[resource.def] = current + excess;
                    }
                }
            }

            // 2. RESOLVE TITHE INJECTIONS (draw from shared stockpile, set externalTitheBudget)
            foreach (WorldSettlementFC settlement in faction.settlements)
            {
                WorldObjectComp_SupplyChain comp = GetComp(settlement);
                if (comp != null)
                    comp.ResolveTitheInjections(stockpile);
            }

            // 3. RESOLVE NEEDS (fair distribution from shared stockpile)
            NeedResolver.ResolveSettlementNeedsFair(faction, stockpile);
            DirtyFlowCache();

            // 4. OVERFLOW
            foreach (KeyValuePair<ResourceTypeDef, double> kv in totalOverflow)
            {
                if (kv.Value <= 0) continue;

                float silver = (float)(kv.Value * FCSettings.silverPerResource * SupplyChainSettings.overflowPenaltyRate);
                DistributeSilver(silver, kv.Key, contributions, faction);

                LogUtil.Message("Overflow auto-sell: " + kv.Value.ToString("F1") + " " + kv.Key.label
                    + " -> " + silver.ToString("F0") + " silver");
            }

            // 5. SELL ORDERS
            foreach (SellOrder order in globalSellOrders)
            {
                float silver = order.Execute(stockpile);
                if (silver > 0)
                {
                    DistributeSilverEvenly(silver, faction);
                    LogUtil.Message("Sell order: " + order.amountPerPeriod.ToString("F1") + " " + order.resource.label
                        + " -> " + silver.ToString("F0") + " silver");
                }
            }
            capsAndStockpilesDirty = false;
        }

        private void PreTaxResolution_Complex(FactionFC faction)
        {
            // 1. Recalculate local caps (only if buildings changed)
            foreach (WorldSettlementFC settlement in faction.settlements)
            {
                WorldObjectComp_SupplyChain comp = GetComp(settlement);
                if (comp != null)
                    comp.RecalculateLocalCapsIfDirty();
            }

            // 2. ACCUMULATE to local stockpiles
            foreach (WorldSettlementFC settlement in faction.settlements)
            {
                WorldObjectComp_SupplyChain comp = GetComp(settlement);
                if (comp == null) continue;

                IStockpile localStockpile = comp.GetStockpile();
                if (localStockpile == null) continue;

                foreach (ResourceFC resource in settlement.Resources)
                {
                    if (resource.def.isPoolResource) continue;

                    double allocated = comp.GetAllocation(resource.def);
                    if (allocated <= 0) continue;

                    double excess = localStockpile.Credit(resource.def, allocated);

                    // Overflow: auto-sell excess at penalty rate
                    if (excess > 0)
                    {
                        float silver = (float)(excess * FCSettings.silverPerResource * SupplyChainSettings.overflowPenaltyRate);
                        settlement.AddOneTimeSilverIncome(silver);
                        LogUtil.Message("Local overflow at " + settlement.Name + ": "
                            + excess.ToString("F1") + " " + resource.def.label
                            + " -> " + silver.ToString("F0") + " silver");
                    }
                }
            }

            // 3. RESOLVE ROUTES (ordered by priority)
            supplyRoutes.Sort((a, b) => a.priority.CompareTo(b.priority));
            List<SupplyRoute> invalidRoutes = null;

            foreach (SupplyRoute route in supplyRoutes)
            {
                if (!route.IsValid())
                {
                    if (invalidRoutes == null) invalidRoutes = new List<SupplyRoute>();
                    invalidRoutes.Add(route);
                    continue;
                }

                route.RecacheIfDirty();

                WorldObjectComp_SupplyChain sourceComp = GetComp(route.source);
                WorldObjectComp_SupplyChain destComp = GetComp(route.destination);

                if (sourceComp == null || destComp == null) continue;

                IStockpile sourceStockpile = sourceComp.GetStockpile();
                IStockpile destStockpile = destComp.GetStockpile();

                if (sourceStockpile == null || destStockpile == null) continue;

                double transferred = route.Execute(sourceStockpile, destStockpile);
                if (transferred > 0)
                {
                    LogUtil.Message("Route " + route.source.Name + " -> " + route.destination.Name
                        + ": " + transferred.ToString("F1") + " " + route.resource.label + " transferred");
                }
            }

            // Clean up invalid routes
            if (invalidRoutes != null)
            {
                foreach (SupplyRoute route in invalidRoutes)
                    supplyRoutes.Remove(route);
                DirtyFlowCache();
            }

            // 4. RESOLVE TITHE INJECTIONS (draw from local stockpiles, set externalTitheBudget)
            foreach (WorldSettlementFC settlement in faction.settlements)
            {
                WorldObjectComp_SupplyChain titheComp = GetComp(settlement);
                if (titheComp == null) continue;

                IStockpile titheStockpile = titheComp.GetStockpile();
                if (titheStockpile == null) continue;

                titheComp.ResolveTitheInjections(titheStockpile);
            }

            // 5. RESOLVE NEEDS (per-settlement, from local stockpiles)
            foreach (WorldSettlementFC settlement in faction.settlements)
            {
                WorldObjectComp_SupplyChain needComp = GetComp(settlement);
                if (needComp == null) continue;

                IStockpile needStockpile = needComp.GetStockpile();
                if (needStockpile == null) continue;

                NeedResolver.ResolveSettlementNeeds(settlement, needStockpile, needComp);
            }
            DirtyFlowCache();

            // 6. PER-SETTLEMENT OVERFLOW (anything over cap after route transfers)
            foreach (WorldSettlementFC settlement in faction.settlements)
            {
                WorldObjectComp_SupplyChain comp = GetComp(settlement);
                if (comp == null) continue;

                IStockpile localStockpile = comp.GetStockpile();
                if (localStockpile == null) continue;

                foreach (ResourceTypeDef def in SupplyChainCache.AllResourceTypeDefs)
                {
                    if (def.isPoolResource) continue;

                    double amount = localStockpile.GetAmount(def);
                    double cap = localStockpile.GetCap(def);
                    if (amount > cap && cap > 0)
                    {
                        double excess = amount - cap;
                        double drawn;
                        localStockpile.TryDraw(def, excess, out drawn);

                        if (drawn > 0)
                        {
                            float silver = (float)(drawn * FCSettings.silverPerResource * SupplyChainSettings.overflowPenaltyRate);
                            settlement.AddOneTimeSilverIncome(silver);
                        }
                    }
                }
            }

            // 7. PER-SETTLEMENT SELL ORDERS
            foreach (WorldSettlementFC settlement in faction.settlements)
            {
                WorldObjectComp_SupplyChain comp = GetComp(settlement);
                if (comp == null) continue;

                IStockpile localStockpile = comp.GetStockpile();
                if (localStockpile == null) continue;

                foreach (SellOrder order in comp.LocalSellOrders)
                {
                    float silver = order.Execute(localStockpile, settlement);
                    if (silver > 0)
                    {
                        settlement.AddOneTimeSilverIncome(silver);
                        LogUtil.Message("Local sell order at " + settlement.Name + ": "
                            + order.amountPerPeriod.ToString("F1") + " " + order.resource.label
                            + " -> " + silver.ToString("F0") + " silver");
                    }
                }
            }
            capsAndStockpilesDirty = false;
        }

        public void PostTaxResolution(FactionFC faction)
        {
            if (mode == SupplyChainMode.Simple)
                PostTaxResolution_Simple(faction);
            else
                PostTaxResolution_Complex(faction);
        }

        private void PostTaxResolution_Simple(FactionFC faction)
        {
            // Clean up tithe injection state after tax resolution
            foreach (WorldSettlementFC settlement in faction.settlements)
            {
                WorldObjectComp_SupplyChain comp = GetComp(settlement);
                if (comp != null)
                    comp.PostTaxCleanup();
            }
        }

        private void PostTaxResolution_Complex(FactionFC faction)
        {
            // Clean up tithe injection state after tax resolution
            foreach (WorldSettlementFC settlement in faction.settlements)
            {
                WorldObjectComp_SupplyChain comp = GetComp(settlement);
                if (comp != null)
                    comp.PostTaxCleanup();
            }
        }

        public void PreSettlementCreateTax(WorldSettlementFC settlement)
        {
        }

        public void PostSettlementCreateTax(WorldSettlementFC settlement, ref int silverAmount, List<Thing> titheThings)
        {
        }

        private void DistributeSilver(float silver, ResourceTypeDef resource,
            Dictionary<ResourceTypeDef, Dictionary<WorldSettlementFC, double>> contributions,
            FactionFC faction)
        {
            Dictionary<WorldSettlementFC, double> contribMap;
            if (!contributions.TryGetValue(resource, out contribMap) || contribMap.Count == 0)
            {
                DistributeSilverEvenly(silver, faction);
                return;
            }

            double totalContrib = 0;
            foreach (double v in contribMap.Values)
                totalContrib += v;

            if (totalContrib <= 0)
            {
                DistributeSilverEvenly(silver, faction);
                return;
            }

            foreach (KeyValuePair<WorldSettlementFC, double> kv in contribMap)
            {
                float share = silver * (float)(kv.Value / totalContrib);
                if (share > 0)
                    kv.Key.AddOneTimeSilverIncome(share);
            }
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

        // --- ILifecycleParticipant ---

        public void OnSettlementCreated(WorldSettlementFC settlement)
        {
            SupplyChainCache.ClearCompCache();
            InvalidateAllRoutes();
            capsAndStockpilesDirty = true;
            DirtyFlowCache();
            resourceColumnsDirty = true;
        }

        public void OnSettlementRemoved(WorldSettlementFC settlement)
        {
            SupplyChainCache.ClearCompCache();
            // Remove routes referencing this settlement
            supplyRoutes.RemoveAll(r => r.source == settlement || r.destination == settlement);
            dormantRoutes.RemoveAll(r => r.source == settlement || r.destination == settlement);
            InvalidateAllRoutes();
            capsAndStockpilesDirty = true;
            DirtyFlowCache();
            resourceColumnsDirty = true;
        }

        public void OnSettlementUpgraded(WorldSettlementFC settlement, int oldLevel, int newLevel)
        {
            DirtyFlowCache();
        }

        public void OnSettlementTypeChanged(WorldSettlementFC settlement, WorldSettlementDef oldDef, WorldSettlementDef newDef)
        {
            capsAndStockpilesDirty = true;
            DirtyFlowCache();
            resourceColumnsDirty = true;
        }

        public void OnBuildingConstructed(WorldSettlementFC settlement, BuildingFCDef building, int slot)
        {
            capsAndStockpilesDirty = true;
            DirtyFlowCache();
            resourceColumnsDirty = true;
            WorldObjectComp_SupplyChain comp = GetComp(settlement);
            if (comp != null)
                comp.DirtyLocalCaps();
        }

        public void OnBuildingDeconstructed(WorldSettlementFC settlement, BuildingFCDef building, int slot)
        {
            capsAndStockpilesDirty = true;
            DirtyFlowCache();
            resourceColumnsDirty = true;
            WorldObjectComp_SupplyChain comp = GetComp(settlement);
            if (comp != null)
                comp.DirtyLocalCaps();
        }

        public void OnSquadDeployed(WorldSettlementFC settlement, MilitaryJobDef job, bool isExtraSquad) { }
        public void OnSquadRecalled(WorldSettlementFC settlement) { }
        public void OnBattleResolved(WorldSettlementFC settlement, MilitaryJobDef job, bool victory, BattleResult result) { }
        public void OnMercenaryDeath(MercenaryDeathEvent evt) { }

        public void OnResearchCompleted(ResearchProjectDef project) { }

        private void InvalidateAllRoutes()
        {
            foreach (SupplyRoute route in supplyRoutes)
                route.Invalidate();
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

            // Header
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inner.x, curY, 300f, 30f), "SC_FactionStockpile".Translate());
            Text.Font = GameFont.Tiny;
            GUI.color = Color.gray;
            Widgets.Label(new Rect(inner.x + 310f, curY + 4f, 100f, 26f), "SC_ModeSimple".Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            curY += 38f;

            // Resource bars — scale bar to fill available width
            FactionFC simpleFaction = FactionCache.FactionComp;
            const float barHeight = 28f;
            const float accentW = 4f;
            const float arrowSize = 16f;
            float contentX = inner.x + accentW + 4f;
            float labelEndX = contentX + 130f;
            float amountTextW = 150f;
            float barWidth = inner.width - (labelEndX - inner.x) - arrowSize - 8f - amountTextW - 4f;
            if (barWidth < 100f) barWidth = 100f;

            int resIdx = 0;
            foreach (ResourceTypeDef def in SupplyChainCache.AllResourceTypeDefs)
            {
                if (def.isPoolResource) continue;

                double amount = stockpile.GetAmount(def);
                double cap = stockpile.GetCap(def);
                if (cap <= 0) continue;

                float fillPct = cap > 0 ? (float)(amount / cap) : 0f;
                FlowBreakdown simpleFlow = GetCachedSimpleFlow(simpleFaction, def);

                Rect rowRect = new Rect(inner.x, curY, inner.width, barHeight);
                if (resIdx % 2 == 0) Widgets.DrawHighlight(rowRect);
                UIUtilSC.DrawFlowHighlight(rowRect, simpleFlow.Net);

                // Left accent bar colored by flow
                Color accentColor = simpleFlow.Net > 0.01 ? AccentUtil.Income
                    : simpleFlow.Net < -0.01 ? AccentUtil.Expense : Color.gray;
                Widgets.DrawBoxSolid(new Rect(inner.x, curY, accentW, barHeight), accentColor);

                if (def.Icon != null)
                    GUI.DrawTexture(new Rect(contentX, curY + 2f, 24f, 24f), def.Icon);

                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(new Rect(contentX + 28f, curY, 100f, barHeight), def.label.CapitalizeFirst());

                Rect barRect = new Rect(labelEndX, curY + 4f, barWidth, barHeight - 8f);
                Widgets.FillableBar(barRect, fillPct);

                // Arrow indicator (between bar and amount text)
                float arrowX = barRect.xMax + 2f;
                if (simpleFlow.Net > 0.01)
                {
                    GUI.color = AccentUtil.Income;
                    GUI.DrawTexture(new Rect(arrowX, curY + (barHeight - arrowSize) / 2f, arrowSize, arrowSize), TexUI.ArrowTexRight);
                    GUI.color = Color.white;
                }
                else if (simpleFlow.Net < -0.01)
                {
                    GUI.color = AccentUtil.Expense;
                    GUI.DrawTexture(new Rect(arrowX, curY + (barHeight - arrowSize) / 2f, arrowSize, arrowSize), TexUI.ArrowTexLeft);
                    GUI.color = Color.white;
                }

                Widgets.Label(new Rect(arrowX + arrowSize + 4f, curY, amountTextW, barHeight),
                    "SC_StockpileAmount".Translate(amount.ToString("F1"), cap.ToString("F0")));

                int numSettlements = simpleFaction != null ? simpleFaction.settlements.Count : 0;
                double baseCap = numSettlements * SupplyChainSettings.baseCapPerSettlement;
                double buildingCapBonus = cap - baseCap;

                TooltipHandler.TipRegion(rowRect, UIUtilSC.BuildFlowTooltip(def, amount, cap, simpleFlow,
                    numSettlements, SupplyChainSettings.baseCapPerSettlement, buildingCapBonus));

                Text.Anchor = TextAnchor.UpperLeft;
                curY += barHeight + 2f;
                resIdx++;
            }

            curY += 12f;

            // Sell Orders section
            Text.Font = GameFont.Medium;
            Rect sellHeaderRect = new Rect(inner.x, curY, 300f, 30f);
            Widgets.Label(sellHeaderRect, "SC_StandingSellOrders".Translate());
            TooltipHandler.TipRegion(sellHeaderRect, (string)"SC_SellOrdersTooltip".Translate(
                SupplyChainSettings.overflowPenaltyRate.ToString("P0")));
            Text.Font = GameFont.Small;
            curY += 34f;

            DrawAddSellOrderRow(inner, ref curY);
            curY += 4f;

            const float sellRowH = 28f;
            const float sellAccentW = 4f;
            float sellRowW = inner.width;

            List<SellOrder> toRemove = null;
            int sellIdx = 0;
            foreach (SellOrder order in globalSellOrders)
            {
                if (order.resource == null) continue;

                Rect rowRect = new Rect(inner.x, curY, sellRowW, sellRowH);
                if (sellIdx % 2 == 0) Widgets.DrawHighlight(rowRect);
                Widgets.DrawBoxSolid(new Rect(inner.x, curY, sellAccentW, sellRowH), AccentUtil.Income);

                float cx = inner.x + sellAccentW + 6f;
                float cw = sellRowW - sellAccentW - 10f;

                Text.Anchor = TextAnchor.MiddleLeft;
                if (order.resource.Icon != null)
                    GUI.DrawTexture(new Rect(cx, curY + 4f, 20f, 20f), order.resource.Icon);

                Widgets.Label(new Rect(cx + 24f, curY, 120f, sellRowH),
                    order.resource.label.CapitalizeFirst());

                Widgets.Label(new Rect(cx + 150f, curY, 130f, sellRowH),
                    "SC_UnitsPerPeriod".Translate(order.amountPerPeriod.ToString("F1")));

                float expectedSilver = (float)(order.amountPerPeriod * FCSettings.silverPerResource
                    * SupplyChainSettings.overflowPenaltyRate);
                GUI.color = new Color(0.7f, 1f, 0.7f);
                Widgets.Label(new Rect(cx + 290f, curY, 100f, sellRowH),
                    "SC_ExpectedSilver".Translate(expectedSilver.ToString("F0")));
                GUI.color = Color.white;

                float removeX = inner.x + sellRowW - 64f;
                if (Widgets.ButtonText(new Rect(removeX, curY + 2f, 60f, sellRowH - 4f), "SC_Remove".Translate()))
                {
                    if (toRemove == null) toRemove = new List<SellOrder>();
                    toRemove.Add(order);
                }

                Text.Anchor = TextAnchor.UpperLeft;
                curY += sellRowH;
                sellIdx++;
            }
            if (toRemove != null)
            {
                foreach (SellOrder order in toRemove)
                    globalSellOrders.Remove(order);
                DirtyFlowCache();
            }

            // Overflow info
            curY += 16f;
            Text.Font = GameFont.Tiny;
            GUI.color = Color.gray;
            Widgets.Label(new Rect(inner.x, curY, inner.width, 40f),
                "SC_OverflowInfo".Translate(
                    SupplyChainSettings.overflowPenaltyRate.ToString("P0"),
                    (FCSettings.silverPerResource * SupplyChainSettings.overflowPenaltyRate).ToString("F0")));
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            curY += 16f;

            // Tithe Injection info
            Text.Font = GameFont.Medium;
            Rect titheHeaderRect = new Rect(inner.x, curY, 300f, 30f);
            Widgets.Label(titheHeaderRect, "SC_TitheInjection".Translate());
            TooltipHandler.TipRegion(titheHeaderRect, (string)"SC_TitheInjectionTooltip".Translate());
            Text.Font = GameFont.Small;
            curY += 34f;

            GUI.color = Color.gray;
            Widgets.Label(new Rect(inner.x, curY, inner.width, 24f),
                "SC_TitheInjectionPerSettlement".Translate());
            GUI.color = Color.white;
            curY += 28f;
        }

        // --- Complex Mode Faction Tab ---

        private void DrawFactionTab_Complex(Rect boundingBox)
        {
            Rect inner = boundingBox.ContractedBy(10f);

            EnsureCapsAndStockpiles();

            // Header
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inner.x, inner.y, 300f, 30f), "SC_EmpireSupplyNetwork".Translate());
            Text.Font = GameFont.Tiny;
            GUI.color = Color.gray;
            Widgets.Label(new Rect(inner.x + 310f, inner.y + 4f, 100f, 26f), "SC_ModeComplex".Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            // Tab bar
            float tabY = inner.y + 38f;
            float tabH = 24f;
            float tabW = inner.width / 2f;
            string[] tabLabels = new string[]
            {
                (string)"SC_TabStockpiles".Translate(),
                (string)"SC_TabRoutes".Translate()
            };

            Rect chosenRect = new Rect();
            for (int i = 0; i < 2; i++)
            {
                Rect tabRect = new Rect(inner.x + tabW * i, tabY, tabW, tabH);
                if (UIUtil.ButtonFlat(tabRect, tabLabels[i], highlighted: complexTab == i))
                    complexTab = i;
                if (complexTab == i)
                    chosenRect = tabRect;
            }

            // Tab underline decoration
            Color origColor = GUI.color;
            GUI.color = Color.gray;
            Widgets.DrawLineHorizontal(inner.x, chosenRect.yMax, chosenRect.x - inner.x);
            Widgets.DrawLineVertical(chosenRect.x, chosenRect.y, chosenRect.height);
            Widgets.DrawLineHorizontal(chosenRect.x, chosenRect.y, chosenRect.width);
            Widgets.DrawLineVertical(chosenRect.xMax, chosenRect.y, chosenRect.height);
            Widgets.DrawLineHorizontal(chosenRect.xMax, chosenRect.yMax, inner.xMax - chosenRect.xMax);
            GUI.color = origColor;

            // Content area below tabs
            float contentY = tabY + tabH;
            Rect contentRect = new Rect(inner.x, contentY, inner.width, inner.yMax - contentY);

            if (complexTab == 0)
                DrawComplexStockpiles(contentRect);
            else
                DrawComplexRoutes(contentRect);
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

            FactionFC faction = FactionCache.FactionComp;
            if (faction == null) return;

            // Resource columns cache (non-poolResource with cap > 0 in any settlement)
            if (resourceColumnsDirty || cachedResourceColumns == null)
            {
                cachedResourceColumns = new List<ResourceTypeDef>();
                foreach (ResourceTypeDef def in SupplyChainCache.AllResourceTypeDefs)
                {
                    if (def.isPoolResource) continue;
                    bool anyHasCap = false;
                    foreach (WorldSettlementFC s in faction.settlements)
                    {
                        WorldObjectComp_SupplyChain c = GetComp(s);
                        IStockpile p = c != null ? c.GetStockpile() : null;
                        if (p != null && p.GetCap(def) > 0)
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
            Rect viewRect = new Rect(0f, 0f, scrollRect.width - 16f, totalHeight);

            Widgets.BeginScrollView(scrollRect, ref scrollPosStockpiles, viewRect);
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
                        if (flow.Net < -0.01)
                            hasDeficit = true;
                        else if (flow.Net > 0.01)
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
                Widgets.Label(nameRect, settlement.Name.Truncate(nameWidth));
                Text.WordWrap = prevWordWrap;
                if (Mouse.IsOver(nameRect))
                    Widgets.DrawHighlight(nameRect);
                if (Widgets.ButtonInvisible(nameRect))
                    Find.WindowStack.Add(new SettlementWindowFc(settlement));

                // Resource cells
                IStockpile sStockpile = comp != null ? comp.GetStockpile() : null;
                for (int i = 0; i < resCount; i++)
                {
                    ResourceTypeDef def = columns[i];
                    float cellX = nameColW + colW * i;
                    Rect cellRect = new Rect(cellX, curY, colW, settRowH);

                    double amt = sStockpile != null ? sStockpile.GetAmount(def) : 0;
                    double cap = sStockpile != null ? sStockpile.GetCap(def) : 0;

                    if (cap <= 0)
                    {
                        // No capacity for this resource in this settlement — draw dash
                        Text.Anchor = TextAnchor.MiddleCenter;
                        GUI.color = Color.gray;
                        Widgets.Label(cellRect, "-");
                        GUI.color = Color.white;
                        continue;
                    }

                    float fill = (float)(amt / cap);
                    FlowBreakdown flow = GetCachedFlow(settlement, comp, def);

                    // Flow highlight on cell background
                    UIUtilSC.DrawFlowHighlight(cellRect, flow.Net);

                    // Fill bar centered vertically in cell
                    float barY = curY + (settRowH - barH) / 2f;
                    Rect barRect = new Rect(cellX + cellPad, barY, colW - cellPad * 2f, barH);
                    Widgets.FillableBar(barRect, fill);

                    // Arrow indicator (top-right corner of cell)
                    if (flow.Net > 0.01)
                    {
                        GUI.color = AccentUtil.Income;
                        GUI.DrawTexture(new Rect(cellX + colW - arrowSize - 1f, curY + 1f, arrowSize, arrowSize), TexUI.ArrowTexRight);
                        GUI.color = Color.white;
                    }
                    else if (flow.Net < -0.01)
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

            Widgets.EndScrollView();
        }

        private void DrawComplexRoutes(Rect rect)
        {
            const float routeRowH = 32f;
            const float accentW = 4f;
            const float rowGap = 2f;

            FactionFC faction = FactionCache.FactionComp;
            float totalHeight = supplyRoutes.Count * (routeRowH + rowGap) + 150f;

            Rect viewRect = new Rect(0f, 0f, rect.width - 16f, totalHeight);
            Widgets.BeginScrollView(rect, ref scrollPosRoutes, viewRect);
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
                ResourceTypeDef captured = filterDef;
                string btnLabel = filterDef.label.CapitalizeFirst();
                float btnW = Text.CalcSize(btnLabel).x + 28f;
                if (filterDef.Icon != null)
                    GUI.DrawTexture(new Rect(fbX + 4f, curY + 3f, 16f, 16f), filterDef.Icon);
                if (UIUtil.ButtonFlat(new Rect(fbX, curY, btnW, fbH), "   " + btnLabel, labelColor: filterDef.color, highlighted: active))
                    routeFilterResource = captured;
                fbX += btnW + 4f;
            }

            // Reset filter if filtered resource has no routes
            if (routeFilterResource != null && !routeResources.Contains(routeFilterResource))
                routeFilterResource = null;

            Text.Font = GameFont.Small;
            curY += fbH + 4f;

            List<SupplyRoute> routesToRemove = null;
            int rIdx = 0;
            foreach (SupplyRoute route in supplyRoutes)
            {
                if (!route.IsValid())
                {
                    if (routesToRemove == null) routesToRemove = new List<SupplyRoute>();
                    routesToRemove.Add(route);
                    continue;
                }

                if (routeFilterResource != null && route.resource != routeFilterResource)
                    continue;

                route.RecacheIfDirty();

                Rect rRow = new Rect(0f, curY, rowW, routeRowH);
                if (rIdx % 2 == 0) Widgets.DrawHighlight(rRow);

                float eff = (float)route.CachedEfficiency;
                Color routeAccent = route.resource != null ? route.resource.color : Color.gray;
                Color effAccent = AccentUtil.GetStatColor(eff * 100f, false);
                Widgets.DrawBoxSolid(new Rect(0f, curY, accentW, routeRowH), routeAccent);
                Widgets.DrawBoxSolid(new Rect(accentW + 2f, curY, accentW, routeRowH), effAccent);

                float cx = accentW * 2 + 2f + 6f;

                Text.Anchor = TextAnchor.MiddleLeft;

                if (route.resource != null && route.resource.Icon != null)
                    GUI.DrawTexture(new Rect(cx, curY + 6f, 20f, 20f), route.resource.Icon);

                string resName = route.resource != null ? route.resource.label.CapitalizeFirst() : "?";
                Widgets.Label(new Rect(cx + 24f, curY, 80f, routeRowH), resName);

                // Right-anchored elements
                float removeX = rowW - 64f;
                float effX = removeX - 70f;
                float amtX = effX - 110f;

                // Source / Arrow / Dest columns
                float routeTextX = cx + 108f;
                float routeTextW = amtX - routeTextX - 4f;
                float arrowW = 24f;
                float nameColW = (routeTextW - arrowW) / 2f;

                Text.Anchor = TextAnchor.MiddleRight;
                Widgets.Label(new Rect(routeTextX, curY, nameColW, routeRowH), route.source.Name);
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(new Rect(routeTextX + nameColW, curY, arrowW, routeRowH), "\u2192");
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(new Rect(routeTextX + nameColW + arrowW, curY, nameColW, routeRowH), route.destination.Name);

                Widgets.Label(new Rect(amtX, curY, 106f, routeRowH),
                    "SC_PerPeriod".Translate(route.amountPerPeriod.ToString("F1")));

                GUI.color = effAccent;
                Rect effRect = new Rect(effX, curY, 66f, routeRowH);
                Widgets.Label(effRect, "SC_EfficiencyPercent".Translate((eff * 100).ToString("F0")));
                GUI.color = Color.white;

                double travelDays = route.CachedTravelTicks / (double)GenDate.TicksPerDay;
                TooltipHandler.TipRegion(effRect,
                    "SC_EfficiencyTooltip".Translate(
                        travelDays.ToString("F1"),
                        SupplyChainSettings.routeDecayPerDay.ToString("F2"),
                        (eff * 100).ToString("F1")));

                if (Widgets.ButtonText(new Rect(removeX, curY + 4f, 60f, routeRowH - 8f), "SC_Remove".Translate()))
                {
                    if (routesToRemove == null) routesToRemove = new List<SupplyRoute>();
                    routesToRemove.Add(route);
                }

                Text.Anchor = TextAnchor.UpperLeft;
                curY += routeRowH + rowGap;
                rIdx++;
            }

            if (routesToRemove != null)
            {
                foreach (SupplyRoute route in routesToRemove)
                    supplyRoutes.Remove(route);
                DirtyFlowCache();
            }

            Widgets.EndScrollView();
        }

        // --- Add Route Row (Complex mode) ---

        private void DrawAddRouteRow(Rect viewRect, ref float curY, FactionFC faction)
        {
            if (faction == null) return;

            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(0f, curY, 70f, 26f), "SC_NewRoute".Translate());

            // Calculate dynamic picker widths
            float fixedW = 74f + 114f + 78f + 54f; // label + resource+gap + amount+gap + add
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
                    if (def.isPoolResource) continue;
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
                            label += " (" + res.rawTotalProduction.ToString("F1") + "/period)";
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
                            if (needDef.resource == newRouteResource)
                            {
                                double demand = needDef.CalculateDemand(captured);
                                label += " (need: " + demand.ToString("F1") + "/period)";
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

            // Confirm
            if (Widgets.ButtonText(new Rect(bx, curY, 50f, 24f), "SC_Add".Translate()))
            {
                if (newRouteSource != null && newRouteDest != null && newRouteResource != null
                    && newRouteAmount > 0 && newRouteSource != newRouteDest)
                {
                    SupplyRoute route = new SupplyRoute(newRouteSource, newRouteDest, newRouteResource, newRouteAmount);
                    supplyRoutes.Add(route);
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
    }
}
