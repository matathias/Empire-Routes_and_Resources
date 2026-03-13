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

        private bool capsAndPoolsDirty = true;
        private FactionStockpilePool pool;

        // UI state (not saved)
        private FactionFC uiFaction;
        private Vector2 scrollPos;
        private ResourceTypeDef newSellOrderResource;
        private string newSellOrderAmountBuffer = "";
        private float newSellOrderAmount;

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

        public IStockpilePool Pool
        {
            get { return pool; }
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

            pool = new FactionStockpilePool(factionStockpile, factionCaps);

            TaxTickRegistry.Register(this);
            MainTableRegistry.Register(this);
            LifecycleRegistry.Register(this);

            capsAndPoolsDirty = true;

            // Re-register comp allocations after load
            if (fromLoad)
            {
                FactionFC faction = FactionCache.FactionComp;
                if (faction != null)
                {
                    foreach (WorldSettlementFC settlement in faction.settlements)
                    {
                        WorldObjectComp_SupplyChain comp = GetComp(settlement);
                        if (comp != null)
                            comp.ReRegisterAllocations();
                    }
                }
            }

            // Reconcile with global settings (mode may have changed while this save was unloaded)
            if (mode != SupplyChainSettings.mode)
            {
                LogUtil.Message("Mode mismatch: save=" + mode + ", settings=" + SupplyChainSettings.mode + ". Switching.");
                SwitchMode(SupplyChainSettings.mode);
            }

            LogUtil.Message("WorldComponent_SupplyChain initialized (mode=" + mode + ", fromLoad=" + fromLoad + ")");
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

            foreach (ResourceTypeDef def in DefDatabase<ResourceTypeDef>.AllDefs)
            {
                if (def.isPoolResource)
                    continue;
                factionCaps[def] = numSettlements * SupplyChainSettings.baseCapPerSettlement;
            }
        }

        private void InitAllLocalPools()
        {
            FactionFC faction = FactionCache.FactionComp;
            if (faction == null) return;

            foreach (WorldSettlementFC settlement in faction.settlements)
            {
                WorldObjectComp_SupplyChain comp = GetComp(settlement);
                if (comp != null)
                {
                    comp.RecalculateLocalCaps();
                    comp.InitLocalPool();
                }
            }
        }

        internal void EnsureCapsAndPools()
        {
            if (!capsAndPoolsDirty) return;
            if (mode == SupplyChainMode.Simple)
                RecalculateCaps();
            else
                InitAllLocalPools();
            capsAndPoolsDirty = false;
        }

        internal static WorldObjectComp_SupplyChain GetComp(WorldSettlementFC settlement)
        {
            if (settlement == null) return null;
            foreach (WorldObjectComp comp in settlement.AllComps)
            {
                WorldObjectComp_SupplyChain sc = comp as WorldObjectComp_SupplyChain;
                if (sc != null) return sc;
            }
            return null;
        }

        // --- Flow Calculation ---

        internal struct FlowBreakdown
        {
            public double production;
            public double routeIn;
            public double needs;
            public double routeOut;
            public double sellOrders;
            public double Net { get { return production + routeIn - needs - routeOut - sellOrders; } }
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

            foreach (SettlementNeedDef needDef in DefDatabase<SettlementNeedDef>.AllDefs)
            {
                if (needDef.resource == def)
                    flow.needs += needDef.CalculateDemand(settlement);
            }

            if (settlement.BuildingsComp != null)
            {
                foreach (BuildingFC building in settlement.BuildingsComp.Buildings)
                {
                    if (building.def == null || building.def == BuildingFCDefOf.Empty) continue;
                    BuildingNeedExtension ext = building.def.GetModExtension<BuildingNeedExtension>();
                    if (ext == null || ext.inputs == null) continue;
                    foreach (BuildingResourceInput input in ext.inputs)
                    {
                        if (input.resource == def)
                            flow.needs += input.amount;
                    }
                }
            }

            foreach (SellOrder order in comp.LocalSellOrders)
            {
                if (order.resource == def)
                    flow.sellOrders += order.amountPerPeriod;
            }

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

                foreach (SettlementNeedDef needDef in DefDatabase<SettlementNeedDef>.AllDefs)
                {
                    if (needDef.resource == def)
                        flow.needs += needDef.CalculateDemand(settlement);
                }

                if (settlement.BuildingsComp != null)
                {
                    foreach (BuildingFC building in settlement.BuildingsComp.Buildings)
                    {
                        if (building.def == null || building.def == BuildingFCDefOf.Empty) continue;
                        BuildingNeedExtension ext = building.def.GetModExtension<BuildingNeedExtension>();
                        if (ext == null || ext.inputs == null) continue;
                        foreach (BuildingResourceInput input in ext.inputs)
                        {
                            if (input.resource == def)
                                flow.needs += input.amount;
                        }
                    }
                }
            }

            foreach (SellOrder order in globalSellOrders)
            {
                if (order.resource == def)
                    flow.sellOrders += order.amountPerPeriod;
            }

            return flow;
        }

        internal static string BuildFlowTooltip(ResourceTypeDef def, double amt, double cap, FlowBreakdown flow)
        {
            string tip = def.label.CapitalizeFirst() + ": " + amt.ToString("F1") + " / " + cap.ToString("F0");

            double net = flow.Net;
            if (net > 0.01)
                tip += "\n" + (string)"SC_BarNetPositive".Translate(net.ToString("F1"));
            else if (net < -0.01)
                tip += "\n" + (string)"SC_BarNetNegative".Translate((-net).ToString("F1"));
            else
                tip += "\n" + (string)"SC_BarNetEven".Translate();

            if (flow.production > 0)
                tip += "\n" + (string)"SC_BarFlowProduction".Translate(flow.production.ToString("F1"));
            if (flow.routeIn > 0)
                tip += "\n" + (string)"SC_BarFlowRouteIn".Translate(flow.routeIn.ToString("F1"));
            if (flow.needs > 0)
                tip += "\n" + (string)"SC_BarFlowNeeds".Translate(flow.needs.ToString("F1"));
            if (flow.routeOut > 0)
                tip += "\n" + (string)"SC_BarFlowRouteOut".Translate(flow.routeOut.ToString("F1"));
            if (flow.sellOrders > 0)
                tip += "\n" + (string)"SC_BarFlowSellOrders".Translate(flow.sellOrders.ToString("F1"));

            return tip;
        }

        internal static void DrawFlowIndicator(float x, float y, double net)
        {
            GameFont prevFont = Text.Font;
            TextAnchor prevAnchor = Text.Anchor;
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleCenter;
            if (net > 0.01)
            {
                GUI.color = AccentUtil.Income;
                Widgets.Label(new Rect(x, y, 14f, 16f), "+");
            }
            else if (net < -0.01)
            {
                GUI.color = AccentUtil.Expense;
                Widgets.Label(new Rect(x, y, 14f, 16f), "-");
            }
            else
            {
                GUI.color = Color.gray;
                Widgets.Label(new Rect(x, y, 14f, 16f), "=");
            }
            GUI.color = Color.white;
            Text.Font = prevFont;
            Text.Anchor = prevAnchor;
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
            capsAndPoolsDirty = false;
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
                comp.InitLocalPool();

                double share = totalProduction > 0 ? productionShares[settlement] / totalProduction : 1.0 / faction.settlements.Count;

                foreach (KeyValuePair<ResourceTypeDef, double> kv in factionStockpile)
                {
                    double amount = kv.Value * share;
                    if (amount > 0)
                        comp.GetPool().Credit(kv.Key, amount);
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

            // 3. Reconstruct faction pool and recalculate caps
            RecalculateCaps();
            pool = new FactionStockpilePool(factionStockpile, factionCaps);
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

                    double excess = pool.Credit(resource.def, allocated);

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

            // 2. RESOLVE NEEDS (fair distribution from shared pool)
            NeedResolver.ResolveSettlementNeedsFair(faction, pool, GetComp);

            // 3. OVERFLOW
            foreach (KeyValuePair<ResourceTypeDef, double> kv in totalOverflow)
            {
                if (kv.Value <= 0) continue;

                float silver = (float)(kv.Value * FCSettings.silverPerResource * SupplyChainSettings.overflowPenaltyRate);
                DistributeSilver(silver, kv.Key, contributions, faction);

                LogUtil.Message("Overflow auto-sell: " + kv.Value.ToString("F1") + " " + kv.Key.label
                    + " -> " + silver.ToString("F0") + " silver");
            }

            // 4. SELL ORDERS
            foreach (SellOrder order in globalSellOrders)
            {
                float silver = order.Execute(pool);
                if (silver > 0)
                {
                    DistributeSilverEvenly(silver, faction);
                    LogUtil.Message("Sell order: " + order.amountPerPeriod.ToString("F1") + " " + order.resource.label
                        + " -> " + silver.ToString("F0") + " silver");
                }
            }
            capsAndPoolsDirty = false;
        }

        private void PreTaxResolution_Complex(FactionFC faction)
        {
            // 1. Recalculate all local caps
            foreach (WorldSettlementFC settlement in faction.settlements)
            {
                WorldObjectComp_SupplyChain comp = GetComp(settlement);
                if (comp != null)
                    comp.RecalculateLocalCaps();
            }

            // 2. ACCUMULATE to local pools
            foreach (WorldSettlementFC settlement in faction.settlements)
            {
                WorldObjectComp_SupplyChain comp = GetComp(settlement);
                if (comp == null) continue;

                IStockpilePool localPool = comp.GetPool();
                if (localPool == null) continue;

                foreach (ResourceFC resource in settlement.Resources)
                {
                    if (resource.def.isPoolResource) continue;

                    double allocated = comp.GetAllocation(resource.def);
                    if (allocated <= 0) continue;

                    double excess = localPool.Credit(resource.def, allocated);

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

                IStockpilePool sourcePool = sourceComp.GetPool();
                IStockpilePool destPool = destComp.GetPool();

                if (sourcePool == null || destPool == null) continue;

                double transferred = route.Execute(sourcePool, destPool);
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
            }

            // 4. RESOLVE NEEDS (per-settlement, from local pools)
            foreach (WorldSettlementFC settlement in faction.settlements)
            {
                WorldObjectComp_SupplyChain needComp = GetComp(settlement);
                if (needComp == null) continue;

                IStockpilePool needPool = needComp.GetPool();
                if (needPool == null) continue;

                NeedResolver.ResolveSettlementNeeds(settlement, needPool, needComp);
            }

            // 5. PER-SETTLEMENT OVERFLOW (anything over cap after route transfers)
            foreach (WorldSettlementFC settlement in faction.settlements)
            {
                WorldObjectComp_SupplyChain comp = GetComp(settlement);
                if (comp == null) continue;

                IStockpilePool localPool = comp.GetPool();
                if (localPool == null) continue;

                foreach (ResourceTypeDef def in DefDatabase<ResourceTypeDef>.AllDefs)
                {
                    if (def.isPoolResource) continue;

                    double amount = localPool.GetAmount(def);
                    double cap = localPool.GetCap(def);
                    if (amount > cap && cap > 0)
                    {
                        double excess = amount - cap;
                        double drawn;
                        localPool.TryDraw(def, excess, out drawn);

                        if (drawn > 0)
                        {
                            float silver = (float)(drawn * FCSettings.silverPerResource * SupplyChainSettings.overflowPenaltyRate);
                            settlement.AddOneTimeSilverIncome(silver);
                        }
                    }
                }
            }

            // 6. PER-SETTLEMENT SELL ORDERS
            foreach (WorldSettlementFC settlement in faction.settlements)
            {
                WorldObjectComp_SupplyChain comp = GetComp(settlement);
                if (comp == null) continue;

                IStockpilePool localPool = comp.GetPool();
                if (localPool == null) continue;

                foreach (SellOrder order in comp.LocalSellOrders)
                {
                    float silver = order.Execute(localPool);
                    if (silver > 0)
                    {
                        settlement.AddOneTimeSilverIncome(silver);
                        LogUtil.Message("Local sell order at " + settlement.Name + ": "
                            + order.amountPerPeriod.ToString("F1") + " " + order.resource.label
                            + " -> " + silver.ToString("F0") + " silver");
                    }
                }
            }
            capsAndPoolsDirty = false;
        }

        public void PostTaxResolution(FactionFC faction)
        {
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
            InvalidateAllRoutes();
            capsAndPoolsDirty = true;
        }

        public void OnSettlementRemoved(WorldSettlementFC settlement)
        {
            // Remove routes referencing this settlement
            supplyRoutes.RemoveAll(r => r.source == settlement || r.destination == settlement);
            dormantRoutes.RemoveAll(r => r.source == settlement || r.destination == settlement);
            InvalidateAllRoutes();
            capsAndPoolsDirty = true;
        }

        public void OnSettlementUpgraded(WorldSettlementFC settlement, int oldLevel, int newLevel) { }
        public void OnSettlementTypeChanged(WorldSettlementFC settlement, WorldSettlementDef oldDef, WorldSettlementDef newDef) { }
        public void OnBuildingConstructed(WorldSettlementFC settlement, BuildingFCDef building, int slot) { }
        public void OnBuildingDeconstructed(WorldSettlementFC settlement, BuildingFCDef building, int slot) { }
        public void OnSquadDeployed(WorldSettlementFC settlement, MilitaryJobDef job, bool isExtraSquad) { }
        public void OnSquadRecalled(WorldSettlementFC settlement) { }
        public void OnBattleResolved(WorldSettlementFC settlement, MilitaryJobDef job, bool victory, BattleResult result) { }
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

            EnsureCapsAndPools();

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
            float barHeight = 28f;
            float labelEndX = 150f;
            float amountTextW = 150f;
            float barWidth = inner.width - labelEndX - amountTextW - 16f;
            if (barWidth < 100f) barWidth = 100f;

            foreach (ResourceTypeDef def in DefDatabase<ResourceTypeDef>.AllDefs)
            {
                if (def.isPoolResource) continue;

                double amount = pool.GetAmount(def);
                double cap = pool.GetCap(def);
                if (cap <= 0) continue;

                float fillPct = cap > 0 ? (float)(amount / cap) : 0f;

                if (def.Icon != null)
                    GUI.DrawTexture(new Rect(inner.x, curY + 2f, 24f, 24f), def.Icon);

                FlowBreakdown simpleFlow = CalculateSimpleFlow(simpleFaction, def);
                DrawFlowIndicator(inner.x + 26f, curY + 6f, simpleFlow.Net);

                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(new Rect(inner.x + 42f, curY, 100f, barHeight), def.label.CapitalizeFirst());

                Rect barRect = new Rect(inner.x + labelEndX, curY + 4f, barWidth, barHeight - 8f);
                Widgets.FillableBar(barRect, fillPct);

                Widgets.Label(new Rect(inner.x + labelEndX + barWidth + 8f, curY, amountTextW, barHeight),
                    "SC_StockpileAmount".Translate(amount.ToString("F1"), cap.ToString("F0")));

                Rect rowTipRect = new Rect(inner.x, curY, inner.width, barHeight);
                UIUtil.TipRegionByText(rowTipRect, BuildFlowTooltip(def, amount, cap, simpleFlow));

                Text.Anchor = TextAnchor.UpperLeft;
                curY += barHeight + 2f;
            }

            curY += 12f;

            // Sell Orders section
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inner.x, curY, 300f, 30f), "SC_StandingSellOrders".Translate());
            Text.Font = GameFont.Small;
            curY += 34f;

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
            }

            curY += 4f;
            DrawAddSellOrderRow(inner, ref curY);

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
        }

        // --- Complex Mode Faction Tab ---

        private void DrawFactionTab_Complex(Rect boundingBox)
        {
            Rect inner = boundingBox.ContractedBy(10f);

            EnsureCapsAndPools();

            const float settRowH = 42f;
            const float routeRowH = 32f;
            const float accentW = 4f;
            const float rowGap = 2f;

            // Calculate scroll height
            FactionFC faction = FactionCache.FactionComp;
            int settlementCount = faction != null ? faction.settlements.Count : 0;
            float totalHeight = 50f
                + settlementCount * (settRowH + rowGap) + 50f
                + supplyRoutes.Count * (routeRowH + rowGap) + 150f
                + 60f;

            Rect viewRect = new Rect(0f, 0f, inner.width - 16f, totalHeight);
            Rect scrollRect = new Rect(inner.x, inner.y, inner.width, inner.height);

            Widgets.BeginScrollView(scrollRect, ref scrollPos, viewRect);
            float curY = 0f;
            float rowW = viewRect.width;

            // Header
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, curY, 300f, 30f), "SC_EmpireSupplyNetwork".Translate());
            Text.Font = GameFont.Tiny;
            GUI.color = Color.gray;
            Widgets.Label(new Rect(310f, curY + 4f, 100f, 26f), "SC_ModeComplex".Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            curY += 38f;

            // --- Settlement Overview ---
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, curY, 300f, 30f), "SC_SettlementStockpiles".Translate());
            Text.Font = GameFont.Small;
            curY += 32f;
            Widgets.DrawLineHorizontal(0f, curY, rowW);
            curY += 4f;

            if (faction != null)
            {
                int sIdx = 0;
                foreach (WorldSettlementFC settlement in faction.settlements)
                {
                    WorldObjectComp_SupplyChain comp = GetComp(settlement);

                    Rect sRow = new Rect(0f, curY, rowW, settRowH);
                    if (sIdx % 2 == 0) Widgets.DrawHighlight(sRow);

                    Color accent = AccentUtil.GetSettlementAccent(settlement);
                    Widgets.DrawBoxSolid(new Rect(0f, curY, accentW, settRowH), accent);

                    float cx = accentW + 6f;

                    // Top line: settlement name (clickable)
                    Text.Anchor = TextAnchor.MiddleLeft;
                    bool prevWordWrap = Text.WordWrap;
                    Text.WordWrap = false;
                    Rect nameRect = new Rect(cx, curY, rowW - cx - 4f, 24f);
                    Widgets.Label(nameRect, settlement.Name);
                    Text.WordWrap = prevWordWrap;
                    if (Mouse.IsOver(nameRect))
                        Widgets.DrawHighlight(nameRect);
                    if (Widgets.ButtonInvisible(nameRect))
                        Find.WindowStack.Add(new SettlementWindowFc(settlement));

                    // Bottom line: full-width stockpile bars
                    IStockpilePool sPool = comp != null ? comp.GetPool() : null;
                    float barY = curY + 22f;
                    if (sPool != null)
                    {
                        // Count valid resources for dynamic width
                        int resCount = 0;
                        foreach (ResourceTypeDef def in DefDatabase<ResourceTypeDef>.AllDefs)
                        {
                            if (def.isPoolResource) continue;
                            if (sPool.GetCap(def) > 0) resCount++;
                        }

                        if (resCount > 0)
                        {
                            float totalBarW = rowW - cx - 4f;
                            float slotW = totalBarW / resCount;
                            float barX = cx;

                            foreach (ResourceTypeDef def in DefDatabase<ResourceTypeDef>.AllDefs)
                            {
                                if (def.isPoolResource) continue;
                                double amt = sPool.GetAmount(def);
                                double cap = sPool.GetCap(def);
                                if (cap <= 0) continue;

                                float fill = (float)(amt / cap);

                                // Icon
                                if (def.Icon != null)
                                    GUI.DrawTexture(new Rect(barX, barY, 16f, 16f), def.Icon);

                                // Flow indicator (between icon and bar)
                                FlowBreakdown flow = CalculateFlow(settlement, comp, def);
                                float indicatorW = 14f;
                                float indX = barX + 18f;
                                DrawFlowIndicator(indX, barY, flow.Net);

                                // Fill bar (after indicator)
                                float barStart = indX + indicatorW + 2f;
                                float miniBarW = slotW - (barStart - barX);
                                if (miniBarW < 10f) miniBarW = 10f;
                                Rect miniBar = new Rect(barStart, barY + 3f, miniBarW, 10f);
                                Widgets.FillableBar(miniBar, fill);

                                // Tooltip
                                Rect tipRect = new Rect(barX, barY, slotW, 16f);
                                UIUtil.TipRegionByText(tipRect, BuildFlowTooltip(def, amt, cap, flow));

                                barX += slotW;
                            }
                        }
                    }

                    Text.Anchor = TextAnchor.UpperLeft;

                    curY += settRowH + rowGap;
                    sIdx++;
                }
            }
            curY += 12f;

            // --- Routes ---
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, curY, 300f, 30f), "SC_SupplyRoutes".Translate());
            Text.Font = GameFont.Small;
            curY += 32f;
            Widgets.DrawLineHorizontal(0f, curY, rowW);
            curY += 4f;

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
                if (UIUtil.ButtonFlat(new Rect(fbX, curY, btnW, fbH), "   " + btnLabel, highlighted: active))
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
                Color effAccent = AccentUtil.GetStatColor(eff * 100f, false);
                Widgets.DrawBoxSolid(new Rect(0f, curY, accentW, routeRowH), effAccent);

                float cx = accentW + 6f;

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

                GUI.color = new Color(0.7f, 1f, 0.7f);
                Rect effRect = new Rect(effX, curY, 66f, routeRowH);
                Widgets.Label(effRect, "SC_EfficiencyPercent".Translate((eff * 100).ToString("F0")));
                GUI.color = Color.white;

                double travelDays = route.CachedTravelTicks / (double)GenDate.TicksPerDay;
                UIUtil.TipRegionByText(effRect,
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
                foreach (ResourceTypeDef def in DefDatabase<ResourceTypeDef>.AllDefs)
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
                        foreach (SettlementNeedDef needDef in DefDatabase<SettlementNeedDef>.AllDefs)
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
                foreach (ResourceTypeDef def in DefDatabase<ResourceTypeDef>.AllDefs)
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
