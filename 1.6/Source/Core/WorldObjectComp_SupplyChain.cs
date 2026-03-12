using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace FactionColonies.SupplyChain
{
    public class WorldObjectCompProperties_SupplyChain : WorldObjectCompProperties
    {
        public WorldObjectCompProperties_SupplyChain()
        {
            compClass = typeof(WorldObjectComp_SupplyChain);
        }
    }

    public class WorldObjectComp_SupplyChain : WorldObjectComp, ISettlementWindowOverview, IStatModifierProvider
    {
        private const string ALLOC_KEY_PREFIX = "SupplyChain.";

        private Dictionary<ResourceTypeDef, double> allocations = new Dictionary<ResourceTypeDef, double>();

        // Complex mode fields
        private Dictionary<ResourceTypeDef, double> localStockpile = new Dictionary<ResourceTypeDef, double>();
        private Dictionary<ResourceTypeDef, double> localCaps = new Dictionary<ResourceTypeDef, double>();
        private List<SellOrder> localSellOrders = new List<SellOrder>();
        private LocalStockpilePool localPool;

        // Needs
        private List<NeedState> needStates = new List<NeedState>();

        private WorldSettlementFC cachedSettlement;
        private Dictionary<ResourceTypeDef, string> sliderBuffers = new Dictionary<ResourceTypeDef, string>();
        private Vector2 scrollPos;

        // Complex mode UI state
        private ResourceTypeDef newLocalSellResource;
        private string newLocalSellAmountBuffer = "";
        private double newLocalSellAmount;

        public WorldSettlementFC WorldSettlement
        {
            get
            {
                if (cachedSettlement != null)
                    return cachedSettlement;
                cachedSettlement = parent as WorldSettlementFC;
                return cachedSettlement;
            }
        }

        // --- Pool Access ---

        /// <summary>
        /// Returns the local stockpile pool for Complex mode. Null in Simple mode.
        /// </summary>
        public IStockpilePool GetPool()
        {
            return localPool;
        }

        public List<SellOrder> LocalSellOrders
        {
            get { return localSellOrders; }
        }

        /// <summary>
        /// Initializes the local pool wrapper. Called by WorldComponent after mode switch or FinalizeInit.
        /// </summary>
        public void InitLocalPool()
        {
            if (localStockpile == null)
                localStockpile = new Dictionary<ResourceTypeDef, double>();
            if (localCaps == null)
                localCaps = new Dictionary<ResourceTypeDef, double>();
            localPool = new LocalStockpilePool(localStockpile, localCaps);
        }

        /// <summary>
        /// Clears local pool and stockpile data (used when switching to Simple mode).
        /// </summary>
        public void ClearLocalData()
        {
            localStockpile.Clear();
            localCaps.Clear();
            localPool = null;
        }

        /// <summary>
        /// Returns the sum of all local stockpile amounts (for summary display).
        /// </summary>
        public double TotalLocalStockpileValue()
        {
            double total = 0;
            foreach (double v in localStockpile.Values)
                total += v;
            return total;
        }

        /// <summary>
        /// Direct access to local stockpile dict for mode-switching (distributing faction pool).
        /// </summary>
        public Dictionary<ResourceTypeDef, double> LocalStockpile
        {
            get { return localStockpile; }
        }

        public void RecalculateLocalCaps()
        {
            foreach (ResourceTypeDef def in DefDatabase<ResourceTypeDef>.AllDefs)
            {
                if (def.isPoolResource)
                    continue;
                localCaps[def] = SupplyChainSettings.localCapBase;
            }
        }

        // --- Needs ---

        public List<NeedState> NeedStates
        {
            get { return needStates; }
        }

        public void SetNeedStates(List<NeedState> states)
        {
            needStates = states ?? new List<NeedState>();
        }

        private NeedState FindNeedState(string needId)
        {
            for (int i = 0; i < needStates.Count; i++)
            {
                if (needStates[i].needId == needId)
                    return needStates[i];
            }
            return null;
        }

        // --- IStatModifierProvider ---

        public double GetStatModifier(FCStatDef stat)
        {
            double total = 0.0;

            // Base settlement needs
            foreach (SettlementNeedDef needDef in DefDatabase<SettlementNeedDef>.AllDefs)
            {
                if (needDef.penalties == null) continue;

                NeedState state = FindNeedState(needDef.defName);
                float unsatisfied = state != null ? (1f - state.Satisfaction) : 0f;
                if (unsatisfied <= 0f) continue;

                foreach (NeedPenalty penalty in needDef.penalties)
                {
                    if (penalty.stat == stat)
                        total += penalty.maxValue * unsatisfied;
                }
            }

            // Building needs
            WorldSettlementFC ws = WorldSettlement;
            if (ws != null && ws.BuildingsComp != null)
            {
                foreach (BuildingFC building in ws.BuildingsComp.Buildings)
                {
                    if (building.def == null || building.def == BuildingFCDefOf.Empty)
                        continue;

                    BuildingNeedExtension ext = building.def.GetModExtension<BuildingNeedExtension>();
                    if (ext == null) continue;

                    List<NeedPenalty> penalties = ext.penalties;
                    if (penalties == null || penalties.Count == 0) continue;

                    // Average satisfaction across all inputs for this building
                    float avgUnsatisfied = 0f;
                    int inputCount = 0;
                    if (ext.inputs != null)
                    {
                        foreach (BuildingResourceInput input in ext.inputs)
                        {
                            if (input.resource == null) continue;
                            string needId = "bldg." + building.def.defName + "." + input.resource.defName;
                            NeedState state = FindNeedState(needId);
                            avgUnsatisfied += state != null ? (1f - state.Satisfaction) : 0f;
                            inputCount++;
                        }
                    }
                    if (inputCount > 0)
                        avgUnsatisfied /= inputCount;

                    if (avgUnsatisfied <= 0f) continue;

                    foreach (NeedPenalty penalty in penalties)
                    {
                        if (penalty.stat == stat)
                            total += penalty.maxValue * avgUnsatisfied;
                    }
                }
            }

            return total;
        }

        public string GetStatModifierDesc(FCStatDef stat)
        {
            string desc = null;

            foreach (SettlementNeedDef needDef in DefDatabase<SettlementNeedDef>.AllDefs)
            {
                if (needDef.penalties == null) continue;

                NeedState state = FindNeedState(needDef.defName);
                float unsatisfied = state != null ? (1f - state.Satisfaction) : 0f;
                if (unsatisfied <= 0f) continue;

                foreach (NeedPenalty penalty in needDef.penalties)
                {
                    if (penalty.stat != stat) continue;
                    double val = penalty.maxValue * unsatisfied;
                    if (val <= 0) continue;

                    string line = "Unmet " + needDef.label + " need: +" + val.ToString("F1");
                    desc = desc == null ? line : desc + "\n" + line;
                }
            }

            return desc;
        }

        // --- Allocation Management ---

        public double GetAllocation(ResourceTypeDef def)
        {
            double val;
            return allocations.TryGetValue(def, out val) ? val : 0.0;
        }

        public bool SetAllocation(ResourceTypeDef def, double amount)
        {
            WorldSettlementFC ws = WorldSettlement;
            if (ws == null) return false;

            ResourceFC resource = ws.GetResource(def);
            if (resource == null) return false;

            string key = ALLOC_KEY_PREFIX + def.defName;

            if (amount <= 0)
            {
                resource.ClearStockpileAllocation(key);
                allocations.Remove(def);
                return true;
            }

            bool ok = resource.SetStockpileAllocation(key, amount, () => OnEvicted(def));
            if (ok)
            {
                allocations[def] = amount;
            }
            return ok;
        }

        private void OnEvicted(ResourceTypeDef def)
        {
            allocations.Remove(def);
            WorldSettlementFC ws = WorldSettlement;
            string name = ws != null ? ws.Name : "unknown";
            LogUtil.Warning("Stockpile allocation for " + def.label + " at " + name
                + " was evicted due to insufficient production.");
        }

        /// <summary>
        /// Re-registers all saved allocations with the base mod's SetStockpileAllocation API.
        /// Called by WorldComponent_SupplyChain.FinalizeInit() after load.
        /// </summary>
        public void ReRegisterAllocations()
        {
            WorldSettlementFC ws = WorldSettlement;
            if (ws == null) return;

            List<ResourceTypeDef> toRemove = null;
            foreach (KeyValuePair<ResourceTypeDef, double> kv in allocations)
            {
                if (kv.Value <= 0) continue;
                ResourceFC resource = ws.GetResource(kv.Key);
                if (resource == null)
                {
                    if (toRemove == null) toRemove = new List<ResourceTypeDef>();
                    toRemove.Add(kv.Key);
                    continue;
                }

                string key = ALLOC_KEY_PREFIX + kv.Key.defName;
                bool ok = resource.SetStockpileAllocation(key, kv.Value, () => OnEvicted(kv.Key));
                if (!ok)
                {
                    if (toRemove == null) toRemove = new List<ResourceTypeDef>();
                    toRemove.Add(kv.Key);
                    LogUtil.Warning("Could not re-register allocation for " + kv.Key.label
                        + " at " + ws.Name + " (exceeds production). Clearing.");
                }
            }

            if (toRemove != null)
            {
                foreach (ResourceTypeDef def in toRemove)
                    allocations.Remove(def);
            }
        }

        // --- Save/Load ---

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Collections.Look(ref allocations, "scAllocations", LookMode.Def, LookMode.Value);
            if (allocations == null)
                allocations = new Dictionary<ResourceTypeDef, double>();

            Scribe_Collections.Look(ref localStockpile, "localStockpile", LookMode.Def, LookMode.Value);
            if (localStockpile == null)
                localStockpile = new Dictionary<ResourceTypeDef, double>();

            Scribe_Collections.Look(ref localCaps, "localCaps", LookMode.Def, LookMode.Value);
            if (localCaps == null)
                localCaps = new Dictionary<ResourceTypeDef, double>();

            Scribe_Collections.Look(ref localSellOrders, "localSellOrders", LookMode.Deep);
            if (localSellOrders == null)
                localSellOrders = new List<SellOrder>();

            Scribe_Collections.Look(ref needStates, "needStates", LookMode.Deep);
            if (needStates == null)
                needStates = new List<NeedState>();
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
            return "Supply Chain";
        }

        public void DrawOverviewTab(Rect boundingBox)
        {
            if (uiSettlement == null) return;

            WorldComponent_SupplyChain wc = Find.World.GetComponent<WorldComponent_SupplyChain>();
            bool isComplex = wc != null && wc.Mode == SupplyChainMode.Complex;

            if (isComplex)
                DrawComplexModeTab(boundingBox);
            else
                DrawSimpleModeTab(boundingBox);
        }

        // --- Simple Mode Tab (allocation sliders only) ---

        private void DrawSimpleModeTab(Rect boundingBox)
        {
            Rect inner = boundingBox.ContractedBy(10f);

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inner.x, inner.y, inner.width, 30f), "Stockpile Allocations");
            Text.Font = GameFont.Small;

            float y = inner.y + 40f;
            float rowHeight = 35f;

            int resourceCount = 0;
            foreach (ResourceFC resource in uiSettlement.Resources)
            {
                if (!resource.def.isPoolResource)
                    resourceCount++;
            }

            float totalHeight = resourceCount * rowHeight + 40f
                + needStates.Count * 26f + 50f;
            Rect viewRect = new Rect(0f, 0f, inner.width - 16f, totalHeight);
            Rect scrollRect = new Rect(inner.x, y, inner.width, inner.height - (y - inner.y));

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
                "This settlement contributes " + SupplyChainSettings.baseCapPerSettlement.ToString("F0")
                + " cap per resource to the faction stockpile.");
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            Widgets.EndScrollView();
        }

        // --- Complex Mode Tab (allocations + local stockpile + routes + sell orders) ---

        private void DrawComplexModeTab(Rect boundingBox)
        {
            Rect inner = boundingBox.ContractedBy(10f);

            // Calculate total height for scroll
            int resourceCount = 0;
            foreach (ResourceFC resource in uiSettlement.Resources)
            {
                if (!resource.def.isPoolResource)
                    resourceCount++;
            }

            float rowHeight = 35f;
            float barHeight = 28f;
            // Allocations + local stockpile bars + routes + sell orders
            float totalHeight = resourceCount * rowHeight + 60f  // allocations
                + resourceCount * (barHeight + 2f) + 60f          // local stockpile bars
                + 200f                                             // routes section estimate
                + needStates.Count * 26f + 50f                    // needs section
                + localSellOrders.Count * 28f + 80f;              // sell orders

            Rect viewRect = new Rect(0f, 0f, inner.width - 16f, totalHeight);
            Rect scrollRect = new Rect(inner.x, inner.y, inner.width, inner.height);

            Widgets.BeginScrollView(scrollRect, ref scrollPos, viewRect);
            float curY = 0f;

            // --- Allocation sliders ---
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, curY, viewRect.width, 30f), "Production Allocations");
            Text.Font = GameFont.Small;
            curY += 36f;

            DrawAllocationSliders(viewRect, ref curY, rowHeight);
            curY += 16f;

            // --- Local Stockpile Bars ---
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, curY, viewRect.width, 30f), "Local Stockpile");
            Text.Font = GameFont.Small;
            curY += 36f;

            float barWidth = 300f;
            foreach (ResourceTypeDef def in DefDatabase<ResourceTypeDef>.AllDefs)
            {
                if (def.isPoolResource) continue;

                double amount = localPool != null ? localPool.GetAmount(def) : 0;
                double cap = localPool != null ? localPool.GetCap(def) : 0;
                if (cap <= 0) continue;

                float fillPct = cap > 0 ? (float)(amount / cap) : 0f;

                if (def.Icon != null)
                    GUI.DrawTexture(new Rect(0f, curY + 2f, 24f, 24f), def.Icon);

                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(new Rect(28f, curY, 100f, barHeight), def.label.CapitalizeFirst());

                Rect barRect = new Rect(135f, curY + 4f, barWidth, barHeight - 8f);
                Widgets.FillableBar(barRect, fillPct);

                Widgets.Label(new Rect(135f + barWidth + 8f, curY, 150f, barHeight),
                    amount.ToString("F1") + " / " + cap.ToString("F0"));
                Text.Anchor = TextAnchor.UpperLeft;

                curY += barHeight + 2f;
            }
            curY += 16f;

            // --- Routes ---
            DrawRoutesSummary(viewRect, ref curY);
            curY += 16f;

            // --- Needs ---
            DrawNeedsSection(viewRect, ref curY);
            curY += 16f;

            // --- Local Sell Orders ---
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, curY, viewRect.width, 30f), "Local Sell Orders");
            Text.Font = GameFont.Small;
            curY += 34f;

            List<SellOrder> toRemove = null;
            foreach (SellOrder order in localSellOrders)
            {
                if (order.resource == null) continue;

                Text.Anchor = TextAnchor.MiddleLeft;
                if (order.resource.Icon != null)
                    GUI.DrawTexture(new Rect(0f, curY + 2f, 20f, 20f), order.resource.Icon);

                Widgets.Label(new Rect(24f, curY, 120f, 26f),
                    order.resource.label.CapitalizeFirst());
                Widgets.Label(new Rect(150f, curY, 100f, 26f),
                    order.amountPerPeriod.ToString("F1") + " units/period");

                float expectedSilver = (float)(order.amountPerPeriod * FCSettings.silverPerResource
                    * SupplyChainSettings.overflowPenaltyRate);
                GUI.color = new Color(0.7f, 1f, 0.7f);
                Widgets.Label(new Rect(260f, curY, 100f, 26f),
                    "~" + expectedSilver.ToString("F0") + " silver");
                GUI.color = Color.white;

                if (Widgets.ButtonText(new Rect(370f, curY, 60f, 24f), "Remove"))
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
                    localSellOrders.Remove(order);
            }

            // Add new local sell order
            curY += 4f;
            DrawAddLocalSellOrderRow(viewRect, ref curY);

            Widgets.EndScrollView();
        }

        // --- Shared: Allocation Sliders ---

        private void DrawAllocationSliders(Rect viewRect, ref float curY, float rowHeight)
        {
            foreach (ResourceFC resource in uiSettlement.Resources)
            {
                if (resource.def.isPoolResource)
                    continue;

                ResourceTypeDef def = resource.def;
                double currentAlloc = GetAllocation(def);
                double rawProd = resource.rawTotalProduction;
                double otherAllocs = resource.totalStockpileAllocation - currentAlloc;
                if (otherAllocs < 0) otherAllocs = 0;
                double maxAlloc = rawProd - otherAllocs;
                if (maxAlloc < 0) maxAlloc = 0;

                Rect row = new Rect(0f, curY, viewRect.width, rowHeight);

                if (def.Icon != null)
                    GUI.DrawTexture(new Rect(row.x, row.y + 2f, 24f, 24f), def.Icon);

                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(new Rect(row.x + 28f, row.y, 120f, rowHeight),
                    def.label.CapitalizeFirst());
                Widgets.Label(new Rect(row.x + 150f, row.y, 80f, rowHeight),
                    "Prod: " + rawProd.ToString("F1"));

                float sliderVal = (float)currentAlloc;
                float newVal = Widgets.HorizontalSlider(
                    new Rect(row.x + 235f, row.y + 8f, 200f, rowHeight - 16f),
                    sliderVal, 0f, (float)maxAlloc, false,
                    null, null, null, 0.5f);

                if (Math.Abs(newVal - sliderVal) > 0.01f)
                {
                    SetAllocation(def, newVal);
                }

                Widgets.Label(new Rect(row.x + 445f, row.y, 80f, rowHeight),
                    currentAlloc.ToString("F1") + " units");

                float silverDiverted = (float)(currentAlloc * FCSettings.silverPerResource);
                Text.Font = GameFont.Tiny;
                GUI.color = new Color(1f, 0.7f, 0.3f);
                Widgets.Label(new Rect(row.x + 530f, row.y, 100f, rowHeight),
                    "-" + silverDiverted.ToString("F0") + " silver");
                GUI.color = Color.white;
                Text.Font = GameFont.Small;

                Text.Anchor = TextAnchor.UpperLeft;
                curY += rowHeight;
            }
        }

        // --- Shared: Needs Display ---

        private void DrawNeedsSection(Rect viewRect, ref float curY)
        {
            if (needStates.Count == 0) return;

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, curY, viewRect.width, 30f), "Settlement Needs");
            Text.Font = GameFont.Small;
            curY += 34f;

            foreach (NeedState state in needStates)
            {
                if (state.resource == null) continue;

                float satisfaction = state.Satisfaction;

                // Icon
                if (state.resource.Icon != null)
                    GUI.DrawTexture(new Rect(0f, curY + 2f, 20f, 20f), state.resource.Icon);

                // Label (use need def label for base needs, building id for building needs)
                Text.Anchor = TextAnchor.MiddleLeft;
                string label;
                if (state.needId.StartsWith("bldg."))
                    label = state.needId.Replace("bldg.", "").Replace(".", " - ");
                else
                {
                    SettlementNeedDef needDef = DefDatabase<SettlementNeedDef>.GetNamedSilentFail(state.needId);
                    label = needDef != null ? needDef.label.CapitalizeFirst() : state.needId;
                }
                Widgets.Label(new Rect(24f, curY, 80f, 24f), label);

                // Satisfaction bar
                Rect barRect = new Rect(108f, curY + 4f, 150f, 16f);
                if (satisfaction > 0.8f)
                    GUI.color = new Color(0.4f, 0.8f, 0.4f);
                else if (satisfaction > 0.4f)
                    GUI.color = new Color(0.9f, 0.8f, 0.2f);
                else
                    GUI.color = new Color(0.9f, 0.3f, 0.3f);

                Widgets.FillableBar(barRect, satisfaction);
                GUI.color = Color.white;

                // Numeric
                Widgets.Label(new Rect(264f, curY, 120f, 24f),
                    (satisfaction * 100f).ToString("F0") + "% ("
                    + state.fulfilled.ToString("F1") + " / " + state.demanded.ToString("F1") + ")");

                // Penalty summary
                if (satisfaction < 1f)
                {
                    Text.Font = GameFont.Tiny;
                    GUI.color = new Color(1f, 0.5f, 0.5f);
                    string penaltyText = GetPenaltySummary(state);
                    if (penaltyText != null)
                        Widgets.Label(new Rect(390f, curY, 200f, 24f), penaltyText);
                    GUI.color = Color.white;
                    Text.Font = GameFont.Small;
                }

                Text.Anchor = TextAnchor.UpperLeft;
                curY += 26f;
            }
        }

        private string GetPenaltySummary(NeedState state)
        {
            float unsatisfied = 1f - state.Satisfaction;
            if (unsatisfied <= 0f) return null;

            // Check base need penalties
            SettlementNeedDef needDef = DefDatabase<SettlementNeedDef>.GetNamedSilentFail(state.needId);
            if (needDef != null && needDef.penalties != null)
            {
                string result = null;
                foreach (NeedPenalty penalty in needDef.penalties)
                {
                    double val = penalty.maxValue * unsatisfied;
                    string part = "+" + val.ToString("F1") + " " + penalty.stat.label;
                    result = result == null ? part : result + ", " + part;
                }
                return result;
            }

            return null;
        }

        // --- Complex Mode: Routes Summary ---

        private void DrawRoutesSummary(Rect viewRect, ref float curY)
        {
            WorldComponent_SupplyChain wc = Find.World.GetComponent<WorldComponent_SupplyChain>();
            if (wc == null) return;

            WorldSettlementFC ws = WorldSettlement;
            if (ws == null) return;

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, curY, viewRect.width, 30f), "Supply Routes");
            Text.Font = GameFont.Small;
            curY += 34f;

            bool hasRoutes = false;

            // Outgoing
            foreach (SupplyRoute route in wc.SupplyRoutes)
            {
                if (route.source != ws) continue;
                if (!route.IsValid()) continue;

                hasRoutes = true;
                route.RecacheIfDirty();

                Text.Anchor = TextAnchor.MiddleLeft;
                GUI.color = new Color(1f, 0.85f, 0.6f);
                Widgets.Label(new Rect(0f, curY, 30f, 24f), "OUT");
                GUI.color = Color.white;

                if (route.resource != null && route.resource.Icon != null)
                    GUI.DrawTexture(new Rect(34f, curY + 2f, 20f, 20f), route.resource.Icon);

                string resName = route.resource != null ? route.resource.label.CapitalizeFirst() : "?";
                Widgets.Label(new Rect(58f, curY, 100f, 24f), resName);
                Widgets.Label(new Rect(162f, curY, 160f, 24f),
                    "-> " + route.destination.Name + " (" + route.amountPerPeriod.ToString("F1") + ")");

                GUI.color = new Color(0.7f, 1f, 0.7f);
                Widgets.Label(new Rect(326f, curY, 80f, 24f),
                    (route.CachedEfficiency * 100).ToString("F0") + "% eff.");
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
                curY += 26f;
            }

            // Incoming
            foreach (SupplyRoute route in wc.SupplyRoutes)
            {
                if (route.destination != ws) continue;
                if (!route.IsValid()) continue;

                hasRoutes = true;
                route.RecacheIfDirty();

                Text.Anchor = TextAnchor.MiddleLeft;
                GUI.color = new Color(0.6f, 0.85f, 1f);
                Widgets.Label(new Rect(0f, curY, 30f, 24f), "IN");
                GUI.color = Color.white;

                if (route.resource != null && route.resource.Icon != null)
                    GUI.DrawTexture(new Rect(34f, curY + 2f, 20f, 20f), route.resource.Icon);

                string resName = route.resource != null ? route.resource.label.CapitalizeFirst() : "?";
                Widgets.Label(new Rect(58f, curY, 100f, 24f), resName);
                Widgets.Label(new Rect(162f, curY, 160f, 24f),
                    "<- " + route.source.Name + " (" + route.amountPerPeriod.ToString("F1") + ")");

                GUI.color = new Color(0.7f, 1f, 0.7f);
                Widgets.Label(new Rect(326f, curY, 80f, 24f),
                    (route.CachedEfficiency * 100).ToString("F0") + "% eff.");
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
                curY += 26f;
            }

            if (!hasRoutes)
            {
                Text.Font = GameFont.Tiny;
                GUI.color = Color.gray;
                Widgets.Label(new Rect(0f, curY, viewRect.width, 24f),
                    "No supply routes. Manage routes from the faction Supply Chain tab.");
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                curY += 26f;
            }
        }

        // --- Complex Mode: Add Local Sell Order ---

        private void DrawAddLocalSellOrderRow(Rect viewRect, ref float curY)
        {
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(0f, curY, 40f, 26f), "Add:");

            string resLabel = newLocalSellResource != null ? newLocalSellResource.label.CapitalizeFirst() : "Pick resource...";
            if (Widgets.ButtonText(new Rect(44f, curY, 130f, 24f), resLabel))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>();
                foreach (ResourceTypeDef def in DefDatabase<ResourceTypeDef>.AllDefs)
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

            Widgets.TextFieldNumeric(new Rect(180f, curY, 80f, 24f),
                ref newLocalSellAmount, ref newLocalSellAmountBuffer, 0f, 9999f);

            if (Widgets.ButtonText(new Rect(268f, curY, 60f, 24f), "Add"))
            {
                if (newLocalSellResource != null && newLocalSellAmount > 0)
                {
                    localSellOrders.Add(new SellOrder(newLocalSellResource, newLocalSellAmount));
                    newLocalSellResource = null;
                    newLocalSellAmount = 0;
                    newLocalSellAmountBuffer = "";
                }
            }

            Text.Anchor = TextAnchor.UpperLeft;
            curY += 28f;
        }
    }
}
