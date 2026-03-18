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

    public class WorldObjectComp_SupplyChain : WorldObjectComp, ISettlementWindowOverview, IStatModifierProvider, ITitheBudgetModifier
    {
        private const string ALLOC_KEY_PREFIX = "SupplyChain.";

        private Dictionary<ResourceTypeDef, double> allocations = new Dictionary<ResourceTypeDef, double>();

        // Tithe injection: how many stockpile units per resource to convert to tithe budget
        private Dictionary<ResourceTypeDef, double> titheInjections = new Dictionary<ResourceTypeDef, double>();
        // At tax time, stores actual drawn amounts (may be less than configured if stockpile insufficient)
        private Dictionary<ResourceTypeDef, double> actualTitheDrawn = new Dictionary<ResourceTypeDef, double>();
        private bool isTaxTime;

        // Complex mode fields
        private Dictionary<ResourceTypeDef, double> localStockpile = new Dictionary<ResourceTypeDef, double>();
        private Dictionary<ResourceTypeDef, double> localCaps = new Dictionary<ResourceTypeDef, double>();
        private List<SellOrder> localSellOrders = new List<SellOrder>();
        private DictionaryStockpilePool localPool;

        private bool localCapsDirty = true;

        // Needs
        private List<NeedState> needStates = new List<NeedState>();

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
        private Vector2 scrollPosOverview;
        private Vector2 scrollPosProduction;
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

        public Dictionary<ResourceTypeDef, double> TitheInjections
        {
            get { return titheInjections; }
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
            localPool = new DictionaryStockpilePool(localStockpile, localCaps);
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

            WorldSettlementFC ws = WorldSettlement;
            if (ws?.BuildingsComp == null) return;
            foreach (BuildingFC building in ws.BuildingsComp.Buildings)
            {
                if (building.def == null || building.def == BuildingFCDefOf.Empty) continue;
                BuildingNeedExtension ext = SupplyChainCache.GetBuildingNeedExt(building.def);
                if (ext?.capBonuses == null) continue;
                foreach (BuildingCapBonus bonus in ext.capBonuses)
                {
                    if (bonus.resource != null && !bonus.resource.isPoolResource && localCaps.ContainsKey(bonus.resource))
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

        public List<NeedState> NeedStates
        {
            get { return needStates; }
        }

        public void SetNeedStates(List<NeedState> states)
        {
            needStates = states ?? new List<NeedState>();
            statModsDirty = true;
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

        private Dictionary<FCStatDef, double> cachedStatMods;
        private bool statModsDirty = true;

        public double GetStatModifier(FCStatDef stat)
        {
            if (statModsDirty || cachedStatMods == null)
            {
                if (cachedStatMods == null)
                    cachedStatMods = new Dictionary<FCStatDef, double>();
                else
                    cachedStatMods.Clear();
                statModsDirty = false;
            }

            double val;
            if (cachedStatMods.TryGetValue(stat, out val))
                return val;

            val = ComputeStatModifier(stat);
            cachedStatMods[stat] = val;
            return val;
        }

        private double ComputeStatModifier(FCStatDef stat)
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

                    BuildingNeedExtension ext = SupplyChainCache.GetBuildingNeedExt(building.def);
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

            return total != 0.0 ? total : stat.IdentityValue;
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

                    string line = "SC_UnmetNeedPenalty".Translate(needDef.label, val.ToString("F1"));
                    desc = desc == null ? line : desc + "\n" + line;
                }
            }

            return desc;
        }

        // --- ITitheBudgetModifier ---

        public double GetExternalTitheBudget(ResourceFC resource)
        {
            if (resource == null || resource.def == null || resource.def.isPoolResource)
                return 0;

            // At tax time, use actual drawn amounts; otherwise use configured injection (optimistic)
            if (isTaxTime)
            {
                double drawn;
                return actualTitheDrawn.TryGetValue(resource.def, out drawn) ? drawn * FCSettings.silverPerResource : 0;
            }

            double injection;
            return titheInjections.TryGetValue(resource.def, out injection) && injection > 0
                ? injection * FCSettings.silverPerResource
                : 0;
        }

        public string GetExternalTitheBudgetDesc(ResourceFC resource)
        {
            if (resource == null || resource.def == null) return null;

            double injection;
            if (!titheInjections.TryGetValue(resource.def, out injection) || injection <= 0)
                return null;

            double silverValue = injection * FCSettings.silverPerResource;
            return "SC_TitheInjectionDesc".Translate(
                injection.ToString("F1"), resource.def.LabelCap, silverValue.ToString("F0"));
        }

        // --- Tithe Injection Management ---

        public double GetTitheInjection(ResourceTypeDef def)
        {
            double val;
            return titheInjections.TryGetValue(def, out val) ? val : 0;
        }

        public void SetTitheInjection(ResourceTypeDef def, double amount)
        {
            if (def.isPoolResource) return;

            if (amount <= 0)
                titheInjections.Remove(def);
            else
                titheInjections[def] = amount;

            WorldSettlementFC ws = WorldSettlement;
            if (ws != null)
                ws.DirtyProfitCache();
            SupplyChainCache.Comp?.DirtyFlowCache();
        }

        /// <summary>
        /// Called by WorldComponent_SupplyChain during PreTaxResolution.
        /// Draws from the pool and records actual amounts for GetExternalTitheBudget.
        /// </summary>
        public void ResolveTitheInjections(IStockpilePool pool)
        {
            actualTitheDrawn.Clear();
            isTaxTime = true;
            WorldSettlementFC ws = WorldSettlement;

            foreach (KeyValuePair<ResourceTypeDef, double> kv in titheInjections)
            {
                if (kv.Key == null || kv.Key.isPoolResource || kv.Value <= 0) continue;

                double drawn;
                pool.TryDraw(kv.Key, kv.Value, out drawn);

                if (drawn > 0)
                    actualTitheDrawn[kv.Key] = drawn;

                string settleName = ws != null ? ws.Name : "unknown";
                if (SupplyChainSettings.PrintDebug)
                {
                    if (drawn < kv.Value && drawn > 0)
                    {
                        LogUtil.Message("Tithe injection shortfall at " + settleName + ": "
                            + kv.Key.label + " wanted " + kv.Value.ToString("F1")
                            + ", only " + drawn.ToString("F1") + " available (budget reduced to "
                            + (drawn * FCSettings.silverPerResource).ToString("F0") + " silver)");
                    }
                    else if (drawn <= 0)
                    {
                        LogUtil.Message("Tithe injection at " + settleName + ": "
                            + kv.Key.label + " wanted " + kv.Value.ToString("F1")
                            + ", stockpile empty — skipped");
                    }
                    else
                    {
                        LogUtil.Message("Tithe injection at " + settleName + ": "
                            + drawn.ToString("F1") + "/" + kv.Value.ToString("F1") + " "
                            + kv.Key.label + " ("
                            + (drawn * FCSettings.silverPerResource).ToString("F0") + " silver budget)");
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
            string name = ws != null ? ws.Name : "unknown";
            LogUtil.Warning("Stockpile allocation for " + def.label + " at " + name
                + " was evicted due to insufficient production.");
        }

        /// <summary>
        /// Re-registers all saved allocations with the base mod's SetStockpileAllocation API.
        /// Called from PostExposeData(PostLoadInit) to restore transient state after load.
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

        // --- Gizmos & World Map Overlay ---

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos())
                yield return g;

            WorldComponent_SupplyChain wc = SupplyChainCache.Comp;
            if (wc == null) yield break;

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
        }

        public override void PostDrawExtraSelectionOverlays()
        {
            WorldComponent_SupplyChain wc = SupplyChainCache.Comp;
            if (wc == null || !wc.showSelectedRoutes) return;
            WorldComponent_SupplyChain.DrawRoutesForSettlement(wc, WorldSettlement);
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

            Scribe_Collections.Look(ref titheInjections, "titheInjections", LookMode.Def, LookMode.Value);
            if (titheInjections == null)
                titheInjections = new Dictionary<ResourceTypeDef, double>();

            Scribe_Collections.Look(ref needStates, "needStates", LookMode.Deep);
            if (needStates == null)
                needStates = new List<NeedState>();

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (allocations.Count > 0)
                    ReRegisterAllocations();

                // Ensure any newly added SettlementNeedDefs have placeholder NeedStates
                WorldSettlementFC ws = WorldSettlement;
                FactionFC faction = FactionCache.FactionComp;
                if (ws != null)
                {
                    HashSet<string> existingIds = new HashSet<string>();
                    for (int i = 0; i < needStates.Count; i++)
                        existingIds.Add(needStates[i].needId);

                    foreach (SettlementNeedDef needDef in DefDatabase<SettlementNeedDef>.AllDefs)
                    {
                        if (faction != null && !needDef.IsActiveForFaction(faction)) continue;
                        if (!existingIds.Contains(needDef.defName))
                        {
                            double demand = needDef.CalculateDemand(ws);
                            needStates.Add(new NeedState(needDef.defName, needDef.resource, demand, 0));
                        }
                    }
                }
            }
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
            scrollPosOverview = Vector2.zero;
            scrollPosProduction = Vector2.zero;
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
            if (wc != null)
                wc.EnsureCapsAndPools();
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
            Widgets.Label(new Rect(inner.x, inner.y, inner.width, 30f), "SC_StockpileAllocations".Translate());
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

        private float MeasureStockpileStatusBar(float width)
        {
            if (localPool == null) return 0f;

            Text.Font = GameFont.Tiny;
            int rowCount = 0;
            float curX = 0f;
            bool any = false;

            foreach (ResourceTypeDef def in DefDatabase<ResourceTypeDef>.AllDefs)
            {
                if (def.isPoolResource) continue;
                double cap = localPool.GetCap(def);
                if (cap <= 0) continue;

                if (!any)
                {
                    rowCount = 1;
                    any = true;
                }

                double amount = localPool.GetAmount(def);
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
            if (localPool == null) return;

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

            foreach (ResourceTypeDef def in DefDatabase<ResourceTypeDef>.AllDefs)
            {
                if (def.isPoolResource) continue;
                double cap = localPool.GetCap(def);
                if (cap <= 0) continue;

                double amount = localPool.GetAmount(def);
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
            Rect inner = boundingBox.ContractedBy(5f);

            // Sub-tab bar
            float tabH = 24f;
            float tabW = inner.width / 3f;
            string[] tabLabels = new string[]
            {
                (string)"SC_SubOverview".Translate(),
                (string)"SC_SubProduction".Translate(),
                (string)"SC_SubRoutes".Translate()
            };

            Rect chosenRect = new Rect();
            for (int i = 0; i < 3; i++)
            {
                Rect tabRect = new Rect(inner.x + tabW * i, inner.y, tabW, tabH);
                if (UIUtil.ButtonFlat(tabRect, tabLabels[i], highlighted: complexSubTab == i))
                    complexSubTab = i;
                if (complexSubTab == i)
                    chosenRect = tabRect;
            }

            UIUtil.DrawTabDecoratorHorizontalTop(chosenRect, inner, Color.gray);

            // Measure status bar (dynamic height based on row wrapping)
            float statusBarH = MeasureStockpileStatusBar(inner.width);
            float statusGap = statusBarH > 0f ? StatusBarGap : 0f;

            // Content area below tabs, above status bar
            float contentY = inner.y + tabH;
            float contentH = inner.yMax - contentY - statusBarH - statusGap;
            Rect contentRect = new Rect(inner.x, contentY, inner.width, contentH);

            if (complexSubTab == 0)
                DrawComplexOverview(contentRect);
            else if (complexSubTab == 1)
                DrawComplexProduction(contentRect);
            else
                DrawComplexRoutes(contentRect);

            // Bottom status bar
            if (statusBarH > 0f)
            {
                Rect statusRect = new Rect(inner.x, inner.yMax - statusBarH, inner.width, statusBarH);
                DrawStockpileStatusBar(statusRect);
            }
        }

        // --- Complex Sub-Tab 0: Overview (stockpile + needs) ---

        private void DrawComplexOverview(Rect rect)
        {
            const float barHeight = 28f;
            const float sectionPad = 8f;

            WorldComponent_SupplyChain flowWc = SupplyChainCache.Comp;
            WorldSettlementFC flowSettlement = WorldSettlement;

            // Count resources for height calculation
            int resourceCount = 0;
            foreach (ResourceTypeDef def in DefDatabase<ResourceTypeDef>.AllDefs)
            {
                if (def.isPoolResource) continue;
                double cap = localPool != null ? localPool.GetCap(def) : 0;
                if (cap > 0) resourceCount++;
            }

            float stockpileH = 36f + resourceCount * (barHeight + 2f) + sectionPad;
            float needsH = needStates.Count > 0 ? 36f + needStates.Count * 26f + sectionPad : 0f;
            float totalHeight = stockpileH + needsH + 16f;
            float scrollMargin = totalHeight > rect.height ? 16f : 0f;

            Rect viewRect = new Rect(0f, 0f, rect.width - scrollMargin, totalHeight);
            Widgets.BeginScrollView(rect, ref scrollPosOverview, viewRect);
            float curY = 4f;

            // --- Local Stockpile section ---
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(AccentW + 6f, curY, viewRect.width, 30f), "SC_LocalStockpile".Translate());
            Text.Font = GameFont.Small;
            curY += 34f;

            const float arrowSize = 16f;
            float contentX = AccentW + 4f;
            float barWidth = viewRect.width - contentX - 28f - 100f - arrowSize - 8f - 150f - 4f;
            if (barWidth < 100f) barWidth = 100f;

            int idx = 0;
            foreach (ResourceTypeDef def in DefDatabase<ResourceTypeDef>.AllDefs)
            {
                if (def.isPoolResource) continue;

                double amount = localPool != null ? localPool.GetAmount(def) : 0;
                double cap = localPool != null ? localPool.GetCap(def) : 0;
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
                Widgets.Label(new Rect(contentX + 28f, curY, 100f, barHeight), def.label.CapitalizeFirst());

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

                TooltipHandler.TipRegion(rowRect, UIUtilSC.BuildFlowTooltip(def, amount, cap, flow));
                Text.Anchor = TextAnchor.UpperLeft;

                curY += barHeight + 2f;
                idx++;
            }

            // Separator between stockpile and needs
            if (needStates.Count > 0)
            {
                curY += sectionPad;

                DrawNeedsSection(viewRect, ref curY);
            }

            Widgets.EndScrollView();
        }

        // --- Complex Sub-Tab 1: Production (sliders + sell orders) ---

        private void DrawComplexProduction(Rect rect)
        {
            const float rowHeight = 35f;
            const float sectionPad = 8f;

            int resourceCount = 0;
            foreach (ResourceFC resource in uiSettlement.Resources)
            {
                if (!resource.def.isPoolResource) resourceCount++;
            }

            float allocH = 36f + resourceCount * rowHeight + sectionPad;
            float sellH = 36f + localSellOrders.Count * 28f + 32f + sectionPad;
            float titheH = 36f + titheInjections.Count * 28f + 32f + sectionPad;
            float totalHeight = allocH + sellH + titheH + 16f;
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
            curY += sectionPad;

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
                if (order.resource == null) continue;

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

                if (Widgets.ButtonText(new Rect(cx + 390f, curY, 60f, 24f), "SC_Remove".Translate()))
                {
                    if (toRemove == null) toRemove = new List<SellOrder>();
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
                if (kv.Key == null || kv.Value <= 0) continue;

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
                    if (titheToRemove == null) titheToRemove = new List<ResourceTypeDef>();
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

        // --- Complex Sub-Tab 2: Routes ---

        private void DrawComplexRoutes(Rect rect)
        {
            WorldComponent_SupplyChain wc = SupplyChainCache.Comp;
            if (wc == null) return;

            WorldSettlementFC ws = WorldSettlement;
            if (ws == null) return;

            // --- Direction toggle (fixed above scroll) ---
            Text.Font = GameFont.Tiny;
            float toggleW = rect.width / 2f;
            Rect fromRect = new Rect(rect.x, rect.y + 3f, toggleW, 24f);
            Rect toRect = new Rect(rect.x + toggleW, rect.y + 3f, toggleW, 24f);
            Rect currentRect = newRouteIsOutgoing ? fromRect : toRect;
            if (UIUtil.ButtonFlat(fromRect, (string)"SC_DirectionFrom".Translate(), highlighted: newRouteIsOutgoing))
                newRouteIsOutgoing = true;
            if (UIUtil.ButtonFlat(toRect, (string)"SC_DirectionTo".Translate(), highlighted: !newRouteIsOutgoing))
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
                if (!r.IsValid() || r.resource == null) continue;
                if (newRouteIsOutgoing && r.source == ws) routeResources.Add(r.resource);
                else if (!newRouteIsOutgoing && r.destination == ws) routeResources.Add(r.resource);
            }
            foreach (ResourceTypeDef filterDef in routeResources)
            {
                bool active = routeFilterResource == filterDef;
                ResourceTypeDef captured = filterDef;
                string btnLabel = filterDef.label.CapitalizeFirst();
                float btnW = Text.CalcSize(btnLabel).x + 28f;
                if (filterDef.Icon != null)
                    GUI.DrawTexture(new Rect(fbX + 4f, filterY + 3f, 16f, 16f), filterDef.Icon);
                if (UIUtil.ButtonFlat(new Rect(fbX, filterY, btnW, fbH), "   " + btnLabel, labelColor: filterDef.color, highlighted: active))
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
                Color routeAccent = route.resource != null ? route.resource.color : Color.gray;
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

                Rect efficiencyRect = new Rect(viewRect.width - 130f, curY, 66f, 26f);
                Rect otherLabel = new Rect(cx + 152f, curY, efficiencyRect.x - (cx + 152f) - 3f, 26f);
                // Other settlement + amount (wider column)
                string otherName = isOutgoing ? route.destination.Name : route.source.Name;
                string detail = isOutgoing
                    ? (string)"SC_RouteOutDetail".Translate(otherName, route.amountPerPeriod.ToString("F1"))
                    : (string)"SC_RouteInDetail".Translate(otherName, route.amountPerPeriod.ToString("F1"));
                Widgets.Label(otherLabel, detail);

                // Efficiency (right-aligned, color matches accent)
                GUI.color = effAccent;
                Widgets.Label(efficiencyRect, "SC_EffLabel".Translate((eff * 100).ToString("F0")));
                GUI.color = Color.white;

                // Remove button
                if (Widgets.ButtonText(new Rect(viewRect.width - 60f, curY + 1f, 56f, 24f), "SC_Remove".Translate()))
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
            if (faction == null) return;

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
                foreach (ResourceTypeDef def in DefDatabase<ResourceTypeDef>.AllDefs)
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
                            foreach (SettlementNeedDef needDef in DefDatabase<SettlementNeedDef>.AllDefs)
                            {
                                if (needDef.resource == newRouteResource)
                                {
                                    double demand = needDef.CalculateDemand(captured);
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
            if (faction == null) return;

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
                foreach (ResourceTypeDef def in DefDatabase<ResourceTypeDef>.AllDefs)
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
                            foreach (SettlementNeedDef needDef in DefDatabase<SettlementNeedDef>.AllDefs)
                            {
                                if (needDef.resource == newRouteResource)
                                {
                                    double demand = needDef.CalculateDemand(captured);
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
                Widgets.Label(new Rect(cx + 150f, curY, 80f, rowHeight),
                    "SC_ProdLabel".Translate(rawProd.ToString("F1")));

                float sliderVal = (float)currentAlloc;
                float newVal = Widgets.HorizontalSlider(
                    new Rect(cx + 235f, curY + 8f, 200f, rowHeight - 16f),
                    sliderVal, 0f, (float)maxAlloc, false,
                    null, null, null, 0.5f);

                if (Math.Abs(newVal - sliderVal) > 0.01f)
                {
                    SetAllocation(def, newVal);
                }

                Widgets.Label(new Rect(cx + 445f, curY, 80f, rowHeight),
                    "SC_Units".Translate(currentAlloc.ToString("F1")));

                float silverDiverted = (float)(currentAlloc * FCSettings.silverPerResource);
                if (silverDiverted >= 0.5f)
                {
                    Text.Font = GameFont.Tiny;
                    GUI.color = new Color(1f, 0.7f, 0.3f);
                    Widgets.Label(new Rect(cx + 530f, curY, 100f, rowHeight),
                        "SC_SilverDiverted".Translate(silverDiverted.ToString("F0")));
                    GUI.color = Color.white;
                    Text.Font = GameFont.Small;
                }

                Text.Anchor = TextAnchor.UpperLeft;
                curY += rowHeight;
                idx++;
            }
        }

        // --- Shared: Needs Display ---

        private void DrawNeedsSection(Rect viewRect, ref float curY)
        {
            if (needStates.Count == 0) return;

            Text.Font = GameFont.Medium;
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(new Rect(AccentW + 6f, curY, viewRect.width, 30f), "SC_SettlementNeeds".Translate());
            Text.Font = GameFont.Small;
            curY += 34f;

            int idx = 0;
            foreach (NeedState state in needStates)
            {
                if (state.resource == null) continue;

                float satisfaction = state.Satisfaction;

                Rect rowRect = new Rect(0f, curY, viewRect.width, 24f);
                if (idx % 2 == 0) Widgets.DrawHighlight(rowRect);

                // Left accent bar colored by satisfaction
                Color needAccent = satisfaction > 0.8f ? AccentPositive
                    : satisfaction > 0.4f ? new Color(0.9f, 0.8f, 0.2f)
                    : AccentNegative;
                Widgets.DrawBoxSolid(new Rect(0f, curY, AccentW, 24f), needAccent);

                float cx = AccentW + 4f;

                if (state.resource.Icon != null)
                    GUI.DrawTexture(new Rect(cx, curY + 2f, 20f, 20f), state.resource.Icon);

                Text.Anchor = TextAnchor.MiddleLeft;
                string label;
                if (state.needId.StartsWith("bldg."))
                    label = state.needId.Replace("bldg.", "").Replace(".", " - ");
                else
                {
                    SettlementNeedDef needDef = DefDatabase<SettlementNeedDef>.GetNamedSilentFail(state.needId);
                    label = needDef != null ? needDef.label.CapitalizeFirst() : state.needId;
                }
                Widgets.Label(new Rect(cx + 24f, curY, 130f, 24f), label);

                Rect barRect = new Rect(cx + 158f, curY + 4f, 150f, 16f);
                if (satisfaction > 0.8f)
                    GUI.color = new Color(0.4f, 0.8f, 0.4f);
                else if (satisfaction > 0.4f)
                    GUI.color = new Color(0.9f, 0.8f, 0.2f);
                else
                    GUI.color = new Color(0.9f, 0.3f, 0.3f);
                Widgets.FillableBar(barRect, satisfaction);
                GUI.color = Color.white;

                Widgets.Label(new Rect(cx + 314f, curY, 110f, 24f),
                    "SC_SatisfactionDisplay".Translate(
                        (satisfaction * 100f).ToString("F0"),
                        state.fulfilled.ToString("F1"),
                        state.demanded.ToString("F1")));

                if (satisfaction < 1f)
                {
                    Text.Font = GameFont.Tiny;
                    GUI.color = new Color(1f, 0.5f, 0.5f);
                    string penaltyText = GetPenaltySummary(state);
                    Rect penaltyRect = new Rect(cx + 430f, curY, 200f, 24f);
                    if (penaltyText != null)
                        Widgets.Label(penaltyRect, Text.ClampTextWithEllipsis(penaltyRect, penaltyText));
                    GUI.color = Color.white;
                    Text.Font = GameFont.Small;
                }

                // Tooltip explaining demand source
                string tooltip = BuildNeedTooltip(state);
                if (tooltip != null)
                    TooltipHandler.TipRegion(rowRect, tooltip);

                Text.Anchor = TextAnchor.UpperLeft;
                curY += 26f;
                idx++;
            }
        }

        private string BuildNeedTooltip(NeedState state)
        {
            WorldSettlementFC ws = WorldSettlement;
            if (ws == null) return null;

            if (state.needId.StartsWith("bldg."))
            {
                // Building need: show building name + input amount
                string bldgInfo = state.needId.Replace("bldg.", "").Replace(".", " - ");
                return bldgInfo + ": " + state.demanded.ToString("F1") + " " + state.resource.label;
            }

            SettlementNeedDef needDef = DefDatabase<SettlementNeedDef>.GetNamedSilentFail(state.needId);
            if (needDef == null) return null;

            string scalingDesc;
            switch (needDef.scaling)
            {
                case NeedScaling.PerWorker:
                    scalingDesc = needDef.baseAmount.ToString("F1") + " per worker x " + ws.workers + " = " + state.demanded.ToString("F1");
                    break;
                case NeedScaling.PerLevel:
                    scalingDesc = needDef.baseAmount.ToString("F1") + " per level x " + ws.settlementLevel + " = " + state.demanded.ToString("F1");
                    break;
                default:
                    scalingDesc = needDef.baseAmount.ToString("F1") + " (flat)";
                    break;
            }

            string tip = needDef.label.CapitalizeFirst() + "\n" + scalingDesc;

            if (state.Satisfaction < 1f && needDef.penalties != null)
            {
                tip += "\n\nPenalties:";
                float deficit = 1f - state.Satisfaction;
                foreach (NeedPenalty penalty in needDef.penalties)
                {
                    double penaltyVal = penalty.maxValue * deficit;
                    tip += "\n  " + (penalty.label ?? penalty.stat.label) + ": -" + penaltyVal.ToString("F1");
                }
            }

            return tip;
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
                    string displayLabel = penalty.label ?? penalty.stat.label;
                    string part = "SC_PenaltyLine".Translate(val.ToString("F1"), displayLabel);
                    result = result == null ? part : result + ", " + part;
                }
                return result;
            }

            return null;
        }

        // --- Complex Mode: Routes Summary ---

        private void DrawRoutesSummary(Rect viewRect, ref float curY)
        {
            WorldComponent_SupplyChain wc = SupplyChainCache.Comp;
            if (wc == null) return;

            WorldSettlementFC ws = WorldSettlement;
            if (ws == null) return;

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, curY, viewRect.width, 30f), "SC_SupplyRoutes".Translate());
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
                Widgets.Label(new Rect(0f, curY, 30f, 24f), "SC_RouteOut".Translate());
                GUI.color = Color.white;

                if (route.resource != null && route.resource.Icon != null)
                    GUI.DrawTexture(new Rect(34f, curY + 2f, 20f, 20f), route.resource.Icon);

                string resName = route.resource != null ? route.resource.label.CapitalizeFirst() : "?";
                Widgets.Label(new Rect(58f, curY, 100f, 24f), resName);
                Widgets.Label(new Rect(162f, curY, 160f, 24f),
                    "SC_RouteOutDetail".Translate(route.destination.Name, route.amountPerPeriod.ToString("F1")));

                GUI.color = new Color(0.7f, 1f, 0.7f);
                Widgets.Label(new Rect(326f, curY, 80f, 24f),
                    "SC_EffLabel".Translate((route.CachedEfficiency * 100).ToString("F0")));
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
                Widgets.Label(new Rect(0f, curY, 30f, 24f), "SC_RouteIn".Translate());
                GUI.color = Color.white;

                if (route.resource != null && route.resource.Icon != null)
                    GUI.DrawTexture(new Rect(34f, curY + 2f, 20f, 20f), route.resource.Icon);

                string resName = route.resource != null ? route.resource.label.CapitalizeFirst() : "?";
                Widgets.Label(new Rect(58f, curY, 100f, 24f), resName);
                Widgets.Label(new Rect(162f, curY, 160f, 24f),
                    "SC_RouteInDetail".Translate(route.source.Name, route.amountPerPeriod.ToString("F1")));

                GUI.color = new Color(0.7f, 1f, 0.7f);
                Widgets.Label(new Rect(326f, curY, 80f, 24f),
                    "SC_EffLabel".Translate((route.CachedEfficiency * 100).ToString("F0")));
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
                curY += 26f;
            }

            if (!hasRoutes)
            {
                Text.Font = GameFont.Tiny;
                GUI.color = Color.gray;
                Widgets.Label(new Rect(0f, curY, viewRect.width, 24f),
                    "SC_NoRoutes".Translate());
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                curY += 26f;
            }
        }

        // --- Complex Mode: Add Local Sell Order ---

        private void DrawAddLocalSellOrderRow(Rect viewRect, ref float curY)
        {
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(0f, curY, 40f, 26f), "SC_AddColon".Translate());

            string resLabel = newLocalSellResource != null ? newLocalSellResource.label.CapitalizeFirst() : (string)"SC_PickResource".Translate();
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

            if (Widgets.ButtonText(new Rect(268f, curY, 60f, 24f), "SC_Add".Translate()))
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
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(0f, curY, 40f, 26f), "SC_AddColon".Translate());

            string resLabel = newTitheInjResource != null ? newTitheInjResource.label.CapitalizeFirst() : (string)"SC_PickResource".Translate();
            if (Widgets.ButtonText(new Rect(44f, curY, 130f, 24f), resLabel))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>();
                foreach (ResourceTypeDef def in DefDatabase<ResourceTypeDef>.AllDefs)
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

            Widgets.TextFieldNumeric(new Rect(180f, curY, 80f, 24f),
                ref newTitheInjAmount, ref newTitheInjAmountBuffer, 0f, 9999f);

            if (Widgets.ButtonText(new Rect(268f, curY, 60f, 24f), "SC_Add".Translate()))
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
