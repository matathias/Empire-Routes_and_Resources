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

        private FactionStockpilePool pool;

        // UI state (not saved)
        private FactionFC uiFaction;
        private Vector2 scrollPos;
        private ResourceTypeDef newSellOrderResource;
        private string newSellOrderAmountBuffer = "";
        private double newSellOrderAmount;

        // Route creation UI state
        private WorldSettlementFC newRouteSource;
        private WorldSettlementFC newRouteDest;
        private ResourceTypeDef newRouteResource;
        private string newRouteAmountBuffer = "";
        private double newRouteAmount;

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

            if (mode == SupplyChainMode.Simple)
            {
                RecalculateCaps();
            }
            else
            {
                InitAllLocalPools();
            }

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
            if (mode == SupplyChainMode.Simple)
                RecalculateCaps();
        }

        public void OnSettlementRemoved(WorldSettlementFC settlement)
        {
            // Remove routes referencing this settlement
            supplyRoutes.RemoveAll(r => r.source == settlement || r.destination == settlement);
            dormantRoutes.RemoveAll(r => r.source == settlement || r.destination == settlement);
            InvalidateAllRoutes();
            if (mode == SupplyChainMode.Simple)
                RecalculateCaps();
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

            // Header
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inner.x, curY, 300f, 30f), "SC_FactionStockpile".Translate());
            Text.Font = GameFont.Tiny;
            GUI.color = Color.gray;
            Widgets.Label(new Rect(inner.x + 310f, curY + 4f, 100f, 26f), "SC_ModeSimple".Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            curY += 38f;

            // Resource bars
            float barHeight = 28f;
            float barWidth = 300f;

            foreach (ResourceTypeDef def in DefDatabase<ResourceTypeDef>.AllDefs)
            {
                if (def.isPoolResource) continue;

                double amount = pool.GetAmount(def);
                double cap = pool.GetCap(def);
                if (cap <= 0) continue;

                float fillPct = cap > 0 ? (float)(amount / cap) : 0f;

                if (def.Icon != null)
                    GUI.DrawTexture(new Rect(inner.x, curY + 2f, 24f, 24f), def.Icon);

                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(new Rect(inner.x + 28f, curY, 100f, barHeight), def.label.CapitalizeFirst());

                Rect barRect = new Rect(inner.x + 135f, curY + 4f, barWidth, barHeight - 8f);
                Widgets.FillableBar(barRect, fillPct);

                Widgets.Label(new Rect(inner.x + 135f + barWidth + 8f, curY, 150f, barHeight),
                    "SC_StockpileAmount".Translate(amount.ToString("F1"), cap.ToString("F0")));

                Text.Anchor = TextAnchor.UpperLeft;
                curY += barHeight + 2f;
            }

            curY += 12f;

            // Sell Orders section
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inner.x, curY, 300f, 30f), "SC_StandingSellOrders".Translate());
            Text.Font = GameFont.Small;
            curY += 34f;

            List<SellOrder> toRemove = null;
            foreach (SellOrder order in globalSellOrders)
            {
                if (order.resource == null) continue;

                Text.Anchor = TextAnchor.MiddleLeft;
                if (order.resource.Icon != null)
                    GUI.DrawTexture(new Rect(inner.x, curY + 2f, 20f, 20f), order.resource.Icon);

                Widgets.Label(new Rect(inner.x + 24f, curY, 120f, 26f),
                    order.resource.label.CapitalizeFirst());

                Widgets.Label(new Rect(inner.x + 150f, curY, 100f, 26f),
                    "SC_UnitsPerPeriod".Translate(order.amountPerPeriod.ToString("F1")));

                float expectedSilver = (float)(order.amountPerPeriod * FCSettings.silverPerResource
                    * SupplyChainSettings.overflowPenaltyRate);
                GUI.color = new Color(0.7f, 1f, 0.7f);
                Widgets.Label(new Rect(inner.x + 260f, curY, 100f, 26f),
                    "SC_ExpectedSilver".Translate(expectedSilver.ToString("F0")));
                GUI.color = Color.white;

                if (Widgets.ButtonText(new Rect(inner.x + 370f, curY, 60f, 24f), "SC_Remove".Translate()))
                {
                    if (toRemove == null) toRemove = new List<SellOrder>();
                    toRemove.Add(order);
                }

                Text.Anchor = TextAnchor.UpperLeft;
                curY += 28f;
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

            // Calculate scroll height
            FactionFC faction = FactionCache.FactionComp;
            int settlementCount = faction != null ? faction.settlements.Count : 0;
            float totalHeight = 50f                           // header
                + settlementCount * 28f + 50f                 // settlement overview
                + supplyRoutes.Count * 28f + 120f             // routes
                + 60f;                                         // footer

            Rect viewRect = new Rect(0f, 0f, inner.width - 16f, totalHeight);
            Rect scrollRect = new Rect(inner.x, inner.y, inner.width, inner.height);

            Widgets.BeginScrollView(scrollRect, ref scrollPos, viewRect);
            float curY = 0f;

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
            curY += 34f;

            if (faction != null)
            {
                foreach (WorldSettlementFC settlement in faction.settlements)
                {
                    WorldObjectComp_SupplyChain comp = GetComp(settlement);
                    double totalValue = comp != null ? comp.TotalLocalStockpileValue() : 0;

                    Text.Anchor = TextAnchor.MiddleLeft;
                    Widgets.Label(new Rect(0f, curY, 200f, 26f), settlement.Name);
                    Widgets.Label(new Rect(210f, curY, 200f, 26f),
                        "SC_TotalUnits".Translate(totalValue.ToString("F1")));
                    Text.Anchor = TextAnchor.UpperLeft;
                    curY += 28f;
                }
            }
            curY += 12f;

            // --- Routes ---
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, curY, 300f, 30f), "SC_SupplyRoutes".Translate());
            Text.Font = GameFont.Small;
            curY += 34f;

            List<SupplyRoute> routesToRemove = null;
            foreach (SupplyRoute route in supplyRoutes)
            {
                if (!route.IsValid())
                {
                    if (routesToRemove == null) routesToRemove = new List<SupplyRoute>();
                    routesToRemove.Add(route);
                    continue;
                }

                route.RecacheIfDirty();

                Text.Anchor = TextAnchor.MiddleLeft;

                if (route.resource != null && route.resource.Icon != null)
                    GUI.DrawTexture(new Rect(0f, curY + 2f, 20f, 20f), route.resource.Icon);

                string resName = route.resource != null ? route.resource.label.CapitalizeFirst() : "?";
                Widgets.Label(new Rect(24f, curY, 80f, 26f), resName);
                Widgets.Label(new Rect(108f, curY, 220f, 26f),
                    route.source.Name + " -> " + route.destination.Name);
                Widgets.Label(new Rect(332f, curY, 80f, 26f),
                    "SC_PerPeriod".Translate(route.amountPerPeriod.ToString("F1")));

                GUI.color = new Color(0.7f, 1f, 0.7f);
                Widgets.Label(new Rect(416f, curY, 60f, 26f),
                    "SC_EfficiencyPercent".Translate((route.CachedEfficiency * 100).ToString("F0")));
                GUI.color = Color.white;

                if (Widgets.ButtonText(new Rect(480f, curY, 60f, 24f), "SC_Remove".Translate()))
                {
                    if (routesToRemove == null) routesToRemove = new List<SupplyRoute>();
                    routesToRemove.Add(route);
                }

                Text.Anchor = TextAnchor.UpperLeft;
                curY += 28f;
            }

            if (routesToRemove != null)
            {
                foreach (SupplyRoute route in routesToRemove)
                    supplyRoutes.Remove(route);
            }

            // Add new route
            curY += 4f;
            DrawAddRouteRow(viewRect, ref curY, faction);

            Widgets.EndScrollView();
        }

        // --- Add Route Row (Complex mode) ---

        private void DrawAddRouteRow(Rect viewRect, ref float curY, FactionFC faction)
        {
            if (faction == null) return;

            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(0f, curY, 60f, 26f), "SC_NewRoute".Translate());

            // Source picker
            string srcLabel = newRouteSource != null ? newRouteSource.Name : (string)"SC_SourcePicker".Translate();
            if (Widgets.ButtonText(new Rect(64f, curY, 100f, 24f), srcLabel))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>();
                foreach (WorldSettlementFC s in faction.settlements)
                {
                    WorldSettlementFC captured = s;
                    options.Add(new FloatMenuOption(s.Name, delegate { newRouteSource = captured; }));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }

            // Dest picker
            string destLabel = newRouteDest != null ? newRouteDest.Name : (string)"SC_DestPicker".Translate();
            if (Widgets.ButtonText(new Rect(168f, curY, 100f, 24f), destLabel))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>();
                foreach (WorldSettlementFC s in faction.settlements)
                {
                    WorldSettlementFC captured = s;
                    options.Add(new FloatMenuOption(s.Name, delegate { newRouteDest = captured; }));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }

            // Resource picker
            string resLabel = newRouteResource != null ? newRouteResource.label.CapitalizeFirst() : (string)"SC_ResourcePicker".Translate();
            if (Widgets.ButtonText(new Rect(272f, curY, 100f, 24f), resLabel))
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

            // Amount
            Widgets.TextFieldNumeric(new Rect(376f, curY, 60f, 24f),
                ref newRouteAmount, ref newRouteAmountBuffer, 0f, 9999f);

            // Confirm
            if (Widgets.ButtonText(new Rect(440f, curY, 50f, 24f), "SC_Add".Translate()))
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
