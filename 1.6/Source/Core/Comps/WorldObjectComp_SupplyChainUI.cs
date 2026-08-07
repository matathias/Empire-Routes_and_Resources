using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace FactionColonies.SupplyChain
{
    public class WorldObjectCompProperties_SupplyChainUI : WorldObjectCompProperties
    {
        public WorldObjectCompProperties_SupplyChainUI()
        {
            compClass = typeof(WorldObjectComp_SupplyChainUI);
        }
    }

    /*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*/
    /* Settlement supply-chain UI comp.                                       */
    /*                                                                        */
    /* The single "Supply Chain" tab in the settlement window. Holds no       */
    /* persisted data — it is a pure view over the resource-ledger comp       */
    /* (WorldObjectComp_SupplyChain) and the needs comp                       */
    /* (WorldObjectComp_SettlementNeeds), both fetched via SupplyChainCache.  */
    /*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*/
    public class WorldObjectComp_SupplyChainUI : WorldObjectComp, ISettlementWindowOverview
    {
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
        private Vector2 scrollPosDeliveries;

        // Route creation state
        private WorldSettlementFC newRouteOther;
        private ResourceTypeDef newRouteResource;
        private string newRouteAmountBuffer = "";
        private float newRouteAmount;
        private int newRouteFrequency = SupplyChainSettings.defaultRouteFrequencyDays;
        private string newRouteFreqBuffer = "";
        private bool newRouteIsOutgoing = true;
        private ResourceTypeDef routeFilterResource;

        // Persistent per-route text buffers for the in-row editable amount field.
        private Dictionary<SupplyRoute, string> routeAmountBuffers = new Dictionary<SupplyRoute, string>();

        public WorldSettlementFC WorldSettlement
        {
            get
            {
                if (cachedSettlement is null)
                    cachedSettlement = parent as WorldSettlementFC;
                return cachedSettlement;
            }
        }

        // --- Sibling comp access (cached in SupplyChainCache) ---

        private WorldObjectComp_SupplyChain Ledger => SupplyChainCache.GetSettlementComp(WorldSettlement);
        private WorldObjectComp_SettlementNeeds Needs => SupplyChainCache.GetNeedsComp(WorldSettlement);

        // Data proxies so the view body reads the ledger/needs comps transparently.
        private static readonly List<NeedState> EmptyNeedStates = new List<NeedState>();
        private List<NeedState> needStates => Needs?.NeedStates ?? EmptyNeedStates;
        private IStockpile localStockpileDict => Ledger?.GetStockpile();
        private List<SellOrder> localSellOrders => Ledger?.LocalSellOrders;
        private Dictionary<ResourceTypeDef, double> titheInjections => Ledger?.TitheInjections;

        private double GetAllocation(ResourceTypeDef def) => Ledger?.GetAllocation(def) ?? 0.0;
        private bool IsAutoMax(ResourceTypeDef def) => Ledger is object && Ledger.IsAutoMax(def);
        private void SetAllocation(ResourceTypeDef def, double amount) => Ledger?.SetAllocation(def, amount);
        private void SetAutoMax(ResourceTypeDef def, bool enabled) => Ledger?.SetAutoMax(def, enabled);
        private double GetTitheInjection(ResourceTypeDef def) => Ledger?.GetTitheInjection(def) ?? 0.0;
        private void SetTitheInjection(ResourceTypeDef def, double amount) => Ledger?.SetTitheInjection(def, amount);

        // --- ISettlementWindowOverview ---

        private WorldSettlementFC uiSettlement;

        public void PreOpenWindow(WorldSettlementFC settlement)
        {
            uiSettlement = settlement;
            sliderBuffers.Clear();
            routeAmountBuffers.Clear(); // else a route edited via the faction tab shows a stale amount here
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
            scrollPosDeliveries = Vector2.zero;
            newRouteOther = null;
            newRouteResource = null;
            newRouteAmountBuffer = "";
            newRouteAmount = 0;
            newRouteFrequency = SupplyChainSettings.defaultRouteFrequencyDays;
            newRouteFreqBuffer = "";
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

        public bool ShouldShowOverviewTab(WorldSettlementFC settlement) => true;

        public void DrawOverviewTab(Rect boundingBox)
        {
            if (uiSettlement is null) return;

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
            Rect scrollRect = new Rect(boundingBox.x, y, boundingBox.width, boundingBox.height - (y - boundingBox.y));

            Rect viewRect = ScrollUtil.BeginScrollView(scrollRect, ref scrollPos, totalHeight);
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

            ScrollUtil.EndScrollView();
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
            if (localStockpileDict is null) return 0f;

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
                if (wc is object && ws is object)
                    flow = wc.GetCachedFlow(ws, Ledger, def);

                // Cell width must be computed identically to DrawStockpileStatusBar (two separate
                // CalcSize calls), or the predicted wrap diverges from the drawn wrap by a row.
                string amtStr = amount.ToString("F1");
                string netStr = flow.DailyNet >= 0 ? "(+" + flow.DailyNet.ToString("F1") + ")" : "(" + flow.DailyNet.ToString("F1") + ")";
                float cellW = StatusIconSize + 2f + Text.CalcSize(amtStr).x + Text.CalcSize(netStr).x + StatusCellPad;

                if (curX + cellW > width && curX > 0f)
                {
                    rowCount++;
                    curX = 0f;
                }
                curX += cellW;
            }

            Text.Font = GameFont.Small;
            // +2f mirrors the draw's top offset (curY = rect.y + 2f) so the last row isn't clipped.
            return any ? rowCount * StatusRowH + 2f : 0f;
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
                if (wc is object && ws is object)
                    flow = wc.GetCachedFlow(ws, Ledger, def);

                string amtStr = amount.ToString("F1");
                string netStr = flow.DailyNet >= 0 ? "(+" + flow.DailyNet.ToString("F1") + ")" : "(" + flow.DailyNet.ToString("F1") + ")";
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
                if (def.Icon is object)
                    GUI.DrawTexture(new Rect(curX, iconY, StatusIconSize, StatusIconSize), def.Icon);

                // Amount text (white)
                GUI.color = Color.white;
                Rect amtRect = new Rect(curX + StatusIconSize + 2f, curY, amtW, StatusRowH);
                Widgets.Label(amtRect, amtStr);

                // Net change text (colored)
                Color netColor = flow.DailyNet > 0.01 ? AccentUtil.Income
                    : flow.DailyNet < -0.01 ? AccentUtil.Expense
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
            float tabW = boundingBox.width / 6f;
            string[] tabLabels =
            {
                "SC_SubStockpile".Translate(),
                "SC_SubNeeds".Translate(),
                "SC_SubProduction".Translate(),
                "SC_SubOrders".Translate(),
                "SC_SubRoutes".Translate(),
                "SC_SubDeliveries".Translate()
            };

            Rect chosenRect = new Rect();
            for (int i = 0; i < 6; i++)
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
            else if (complexSubTab == 4)
                DrawComplexRoutes(contentRect);
            else
                DrawComplexDeliveries(contentRect);

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

            Rect viewRect = ScrollUtil.BeginScrollView(rect, ref scrollPosStockpile, totalHeight);
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
                if (flowWc is object && flowSettlement is object)
                    flow = flowWc.GetCachedFlow(flowSettlement, Ledger, def);

                // Row highlight: alternating gray + flow-based red/green
                if (idx % 2 == 0) Widgets.DrawHighlight(rowRect);
                UIUtilSC.DrawFlowHighlight(rowRect, flow.DailyNet);

                // Left accent bar (colored by flow)
                Color accentColor = flow.DailyNet > 0.01 ? AccentPositive : flow.DailyNet < -0.01 ? AccentNegative : AccentNeutral;
                Widgets.DrawBoxSolid(new Rect(0f, curY, AccentW, barHeight), accentColor);

                if (def.Icon is object)
                    GUI.DrawTexture(new Rect(contentX, curY + 2f, 24f, 24f), def.Icon);

                Text.Anchor = TextAnchor.MiddleLeft;
                Rect reslabel = new Rect(contentX + 28f, curY, 100f, barHeight);
                string reslabelstr = TextUtil.ClampWithEllipsis(reslabel, def.label.CapitalizeFirst());
                Widgets.Label(reslabel, reslabelstr);

                float barX = contentX + 130f;
                Rect barRect = new Rect(barX, curY + 4f, barWidth, barHeight - 8f);
                Widgets.FillableBar(barRect, fillPct);

                // Arrow indicator (between bar and amount text)
                float arrowX = barRect.xMax + 2f;
                if (flow.DailyNet > 0.01)
                {
                    GUI.color = AccentUtil.Income;
                    GUI.DrawTexture(new Rect(arrowX, curY + (barHeight - arrowSize) / 2f, arrowSize, arrowSize), TexUI.ArrowTexRight);
                    GUI.color = Color.white;
                }
                else if (flow.DailyNet < -0.01)
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
                    IStockpile capturedStockpile = localStockpileDict;
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

            ScrollUtil.EndScrollView();
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

            Rect viewRect = ScrollUtil.BeginScrollView(rect, ref scrollPosNeeds, totalHeight);
            float curY = 4f;

            DrawNeedsSection(viewRect, ref curY);

            ScrollUtil.EndScrollView();
        }

        // --- Complex Sub-Tab 2: Production (allocation sliders) ---

        private void DrawComplexProduction(Rect rect)
        {
            const float rowHeight = 35f;
            const float sectionPad = 8f;

            int resourceCount = uiSettlement.Resources.Count;

            float allocH = 36f + resourceCount * rowHeight + sectionPad;
            float totalHeight = allocH + 16f;

            Rect viewRect = ScrollUtil.BeginScrollView(rect, ref scrollPosProduction, totalHeight);
            float curY = 4f;

            // --- Allocation sliders section ---
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(AccentW + 6f, curY, viewRect.width, 30f), "SC_ProductionAllocations".Translate());
            Text.Font = GameFont.Small;
            curY += 34f;

            DrawAllocationSliders(viewRect, ref curY, rowHeight);

            ScrollUtil.EndScrollView();
        }

        // --- Complex Sub-Tab 3: Orders (sell orders + tithe injection) ---

        private void DrawComplexOrders(Rect rect)
        {
            if (Ledger is null) return; // localSellOrders/titheInjections deref the ledger comp unguarded below

            const float sectionPad = 8f;

            float sellH = 36f + localSellOrders.Count * 28f + 32f + sectionPad;
            float titheH = 36f + titheInjections.Count * 28f + 32f + sectionPad;
            float totalHeight = sellH + titheH + 16f;

            Rect viewRect = ScrollUtil.BeginScrollView(rect, ref scrollPosOrders, totalHeight);
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
                if (order.resource.Icon is object)
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
            if (toRemove is object)
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
                if (kv.Key.Icon is object)
                    GUI.DrawTexture(new Rect(cx, curY + 3f, 20f, 20f), kv.Key.Icon);

                Widgets.Label(new Rect(cx + 24f, curY, 120f, 26f),
                    kv.Key.label.CapitalizeFirst());
                Widgets.Label(new Rect(cx + 148f, curY, 130f, 26f),
                    "SC_UnitsPerDay".Translate(kv.Value.ToString("F1")));

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
            if (titheToRemove is object)
            {
                foreach (ResourceTypeDef def in titheToRemove)
                    SetTitheInjection(def, 0);
            }

            ScrollUtil.EndScrollView();
        }

        // --- Complex Sub-Tab 4: Routes ---

        private void DrawComplexRoutes(Rect rect)
        {
            WorldComponent_SupplyChain wc = SupplyChainCache.Comp;
            if (wc is null) return;

            WorldSettlementFC ws = WorldSettlement;
            if (ws is null) return;

            GameFont prevFont = Text.Font;

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

            bool allActive = routeFilterResource is null;
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
                float btnW = (filterDef.Icon is object ? 20f : 0f) + Text.CalcSize(btnLabel).x + 10f;
                if (UIUtil.ButtonFlatIcon(new Rect(fbX, filterY, btnW, fbH), btnLabel,
                    filterDef.Icon, labelColor: filterDef.color, highlighted: active))
                    routeFilterResource = captured;
                fbX += btnW + 4f;
            }

            if (routeFilterResource is object && !routeResources.Contains(routeFilterResource))
                routeFilterResource = null;

            Text.Font = GameFont.Tiny;

            float fixedHeaderTotal = 82f; // toggle (26) + add form (28) + filter row (26) + gap (2)

            // --- Scrollable route list ---
            Rect scrollRect = new Rect(rect.x, rect.y + fixedHeaderTotal, rect.width, rect.height - fixedHeaderTotal);

            // Pre-build the visible (filtered) list so the reorder arrows have indexable neighbors.
            List<SupplyRoute> shown = new List<SupplyRoute>();
            foreach (SupplyRoute route in wc.SupplyRoutes)
            {
                if (!route.IsValid()) continue;
                if (routeFilterResource is object && route.resource != routeFilterResource) continue;
                if (newRouteIsOutgoing && route.source == ws) shown.Add(route);
                else if (!newRouteIsOutgoing && route.destination == ws) shown.Add(route);
            }

            float totalHeight = 4f + shown.Count * 30f + 30f;

            Rect viewRect = ScrollUtil.BeginScrollView(scrollRect, ref scrollPosRoutes, totalHeight);
            float curY = 4f;

            float dualAccentStart = (AccentW * 2) + 6f;
            SupplyRoute routeToRemove = null;
            for (int v = 0; v < shown.Count; v++)
            {
                SupplyRoute route = shown[v];
                bool isOutgoing = route.source == ws;

                // Cheap efficiency refresh only — the expensive travel-time/path pathfind is warmed off
                // the UI thread by SupplyRouteWarmer; show a placeholder until it's ready.
                route.RecacheEfficiencyIfDirty();
                bool pathReady = route.PathReady;

                Rect rowRect = new Rect(0f, curY, viewRect.width, 28f);
                if (v % 2 == 0) Widgets.DrawHighlight(rowRect);

                // Dual accent bars: resource color + efficiency color
                float eff = (float)route.CachedEfficiency;
                Color routeAccent = route.resource?.color ?? Color.gray;
                Color effAccent = pathReady ? AccentUtil.GetStatColor(eff * 100f, false) : Color.gray;
                Widgets.DrawBoxSolid(new Rect(0f, curY, AccentW, 28f), routeAccent);
                Widgets.DrawBoxSolid(new Rect(AccentW + 2f, curY, AccentW, 28f), effAccent);

                // Reorder arrows (dispatch precedence = list order). Break after a move — the master
                // list just changed underneath us; the next frame redraws the new order.
                float reorderX = dualAccentStart;
                float arrowH2 = 14f;
                if (v > 0 && Widgets.ButtonImage(new Rect(reorderX, curY, 14f, arrowH2), TexButton.ReorderUp))
                {
                    wc.MoveRouteBefore(route, shown[v - 1]);
                    break;
                }
                if (v < shown.Count - 1 && Widgets.ButtonImage(new Rect(reorderX, curY + arrowH2, 14f, arrowH2), TexButton.ReorderDown))
                {
                    wc.MoveRouteAfter(route, shown[v + 1]);
                    break;
                }
                TooltipHandler.TipRegion(new Rect(reorderX, curY, 14f, 28f), "SC_RouteReorderTooltip".Translate());

                float cx = dualAccentStart + 18f;
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
                if (route.resource is object && route.resource.Icon is object)
                    GUI.DrawTexture(new Rect(cx + 34f, curY + 4f, 20f, 20f), route.resource.Icon);

                string resName = route.resource is object ? route.resource.label.CapitalizeFirst() : "?";
                Widgets.Label(new Rect(cx + 58f, curY, 90f, 26f), resName);

                // Direction arrow + other settlement name
                string dirArrow = isOutgoing ? "→" : "←";
                GUI.color = isOutgoing ? new Color(1f, 0.85f, 0.6f) : new Color(0.6f, 0.85f, 1f);
                Widgets.Label(new Rect(cx + 150f, curY, 16f, 26f), dirArrow);
                GUI.color = Color.white;

                // Calculation pipeline: qty → eff% → net
                float pipeX = viewRect.width - 250f;
                float netVal = (float)(route.amountPerPeriod * route.CachedEfficiency);

                string otherName = isOutgoing ? route.destination.Name : route.source.Name;
                float nameW = pipeX - (cx + 168f) - 4f;
                Widgets.Label(new Rect(cx + 168f, curY, nameW, 26f), otherName);

                Text.Anchor = TextAnchor.MiddleCenter;
                // Base quantity (editable in place)
                DeliveryUIUtil.DrawAmountField(new Rect(pipeX, curY, 34f, 26f), route, routeAmountBuffers, wc.DirtyFlowCache);
                pipeX += 34f;
                Widgets.Label(new Rect(pipeX, curY, 16f, 26f), "→");
                pipeX += 16f;

                // Efficiency
                Rect efficiencyRect = new Rect(pipeX, curY, 52f, 26f);
                GUI.color = effAccent;
                if (pathReady)
                    Widgets.Label(efficiencyRect, "SC_EffLabel".Translate((eff * 100).ToString("F0")));
                else
                    Widgets.Label(efficiencyRect, "SC_RoutePending".Translate());
                GUI.color = Color.white;
                TooltipHandler.TipRegion(efficiencyRect, "SC_EffTooltip_Route".Translate());
                pipeX += 52f;

                // Arrow to net
                Widgets.Label(new Rect(pipeX, curY, 16f, 26f), "→");
                pipeX += 16f;

                // Net value
                GUI.color = effAccent;
                if (pathReady)
                    Widgets.Label(new Rect(pipeX, curY, 34f, 26f), netVal.ToString("F1"));
                else
                    Widgets.Label(new Rect(pipeX, curY, 34f, 26f), "SC_RoutePending".Translate());
                GUI.color = Color.white;

                // Frequency stepper: [-] Nd [+]
                DeliveryUIUtil.DrawFrequencyStepper(new Rect(viewRect.width - 88f, curY, 58f, 28f), route, wc.DirtyFlowCache);

                // Remove button
                if (Widgets.ButtonText(new Rect(viewRect.width - 24f, curY + 1f, 22f, 24f), "X"))
                    routeToRemove = route;

                Text.Anchor = TextAnchor.UpperLeft;
                curY += 30f;
            }

            if (routeToRemove is object)
            {
                routeAmountBuffers.Remove(routeToRemove);
                wc.UnlinkRoute(routeToRemove);
                wc.DirtyFlowCache();
            }

            if (shown.Count == 0)
            {
                GUI.color = Color.gray;
                Widgets.Label(new Rect(AccentW + 6f, curY, viewRect.width, 24f),
                    "SC_NoRoutesDirection".Translate());
                GUI.color = Color.white;
                curY += 26f;
            }

            ScrollUtil.EndScrollView();
            Text.Font = prevFont;
        }

        // --- Complex Sub-Tab 5: Deliveries (in-transit, filtered to this settlement) ---

        private void DrawComplexDeliveries(Rect rect)
        {
            WorldComponent_SupplyChain wc = SupplyChainCache.Comp;
            if (wc is null) return;

            WorldSettlementFC ws = WorldSettlement;
            if (ws is null) return;

            DeliveryUIUtil.DrawDeliveriesList(rect, ref scrollPosDeliveries, wc.PendingDeliveries, ws);
        }

        // --- Complex Mode: Add Route (settlement-contextual) ---

        /// <summary>
        /// Draws the add-route form at absolute screen coordinates (outside a scroll view).
        /// </summary>
        private void DrawAddRouteFormFixed(float x, ref float curY, float width, WorldComponent_SupplyChain wc)
        {
            FactionFC faction = FindFC.FactionComp;
            if (faction is null) return;

            WorldSettlementFC ws = WorldSettlement;

            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(x, curY, 70f, 26f), "SC_NewRoute".Translate());

            float bx = x + 74f;

            // Resource picker
            string resLabel = newRouteResource is object
                ? newRouteResource.label.CapitalizeFirst()
                : (string)"SC_ResourcePicker".Translate();
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

            // Other settlement picker
            float pickerW = width - (bx - x) - 74f - 52f - 54f - 8f;
            if (pickerW < 120f) pickerW = 120f;

            string otherLabel = newRouteOther is object
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
                    if (newRouteResource is object)
                    {
                        if (newRouteIsOutgoing)
                        {
                            foreach (SettlementNeedDef needDef in SupplyChainCache.AllNeedDefs)
                            {
                                if (!needDef.IsActiveForSettlement(captured)) continue;
                                if (needDef.UsesResource(newRouteResource))
                                {
                                    double demand = needDef.CalculateDemand(captured)
                                        * needDef.GetResourceFraction(FindFC.TechLevel, newRouteResource);
                                    label += "SC_RouteFormNeedSuffix".Translate(demand.ToString("F1"));
                                    break;
                                }
                            }
                        }
                        else
                        {
                            ResourceFC res = s.GetResource(newRouteResource);
                            if (res is object)
                                label += "SC_RouteFormProdSuffix".Translate(res.rawTotalProduction.ToString("F1"));
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

            // Frequency (days between deliveries)
            Rect freqRect = new Rect(bx, curY, 44f, 24f);
            Widgets.TextFieldNumeric(freqRect, ref newRouteFrequency, ref newRouteFreqBuffer,
                SupplyChainSettings.minRouteFrequencyDays, SupplyChainSettings.maxRouteFrequencyDays);
            TooltipHandler.TipRegion(freqRect, "SC_FrequencyTooltip".Translate());
            bx += 48f;

            // Add button
            if (Widgets.ButtonText(new Rect(bx, curY, 50f, 24f), "SC_Add".Translate()))
            {
                if (newRouteOther is object && newRouteResource is object && newRouteAmount > 0)
                {
                    WorldSettlementFC src = newRouteIsOutgoing ? ws : newRouteOther;
                    WorldSettlementFC dest = newRouteIsOutgoing ? newRouteOther : ws;
                    SupplyRoute route = new SupplyRoute(src, dest, newRouteResource, newRouteAmount);
                    route.frequencyDays = Mathf.Clamp(newRouteFrequency,
                        SupplyChainSettings.minRouteFrequencyDays, SupplyChainSettings.maxRouteFrequencyDays);
                    wc.LinkRoute(route);
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

        private const float AllocStep = 0.1f;

        /* Floor a value down to the 0.1 allocation grid, returned as an exact multiple
           of AllocStep. Reconstructing as tenths*AllocStep (the same literal passed to
           HorizontalSlider's roundTo) makes the slider's internal re-rounding return the
           identical float, so it never re-triggers the drag-slider sound each frame. */
        private static float FloorToAllocGrid(double value)
        {
            int tenths = Mathf.FloorToInt((float)value / AllocStep + 0.001f);
            if (tenths < 0) tenths = 0;
            return tenths * AllocStep;
        }

        private void DrawAllocationSliders(Rect viewRect, ref float curY, float rowHeight)
        {
            int idx = 0;
            foreach (ResourceFC resource in uiSettlement.Resources)
            {
                ResourceTypeDef def = resource.def;
                if (def is null) continue;
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

                if (def.Icon is object)
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
                        FloorToAllocGrid(currentAlloc).ToString("F1") + " / " + FloorToAllocGrid(rawProd).ToString("F1"));

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
                    // Floor both the value and the max onto the 0.1 grid as exact
                    // multiples of AllocStep. The slider re-rounds its value with the
                    // same step internally; passing a non-matching float (e.g.
                    // (float)Math.Round(0.9) == 0.89999997 vs the slider's 0.90000004)
                    // makes value != num true every frame, which fires the drag-slider
                    // sound continuously even when idle.
                    float sliderVal = FloorToAllocGrid(currentAlloc);
                    float maxSlider = FloorToAllocGrid(maxAlloc);

                    float newVal = Widgets.HorizontalSlider(
                        new Rect(cx + 150f, curY + 8f, 240f, rowHeight - 16f),
                        sliderVal, 0f, maxSlider, false,
                        null, null, null, AllocStep);

                    // newVal is already a multiple of AllocStep and clamped to
                    // [0, maxSlider]; clamp once more to correct a stale over-allocation.
                    if (newVal > maxSlider)
                        newVal = maxSlider;

                    if (Math.Abs(newVal - sliderVal) > 0.01f)
                    {
                        SetAllocation(def, newVal);
                    }

                    Widgets.Label(new Rect(cx + 400f, curY, 90f, rowHeight),
                        FloorToAllocGrid(currentAlloc).ToString("F1") + " / " + FloorToAllocGrid(rawProd).ToString("F1"));

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
            bool isComplex = wc is object && wc.Mode == SupplyChainMode.Complex;
            FactionFC faction = FindFC.FactionComp;
            WorldSettlementFC ws = WorldSettlement;

            Dictionary<ResourceTypeDef, float> projectedRates = new Dictionary<ResourceTypeDef, float>();
            foreach (NeedState state in needStates)
            {
                if (state.resource is null || projectedRates.ContainsKey(state.resource))
                    continue;

                float rate;
                if (isComplex)
                    rate = NeedProjection.ProjectedFillRate(wc, ws, Ledger, state.resource);
                else
                    rate = NeedProjection.ProjectedFillRateSimple(wc, faction, state.resource);

                projectedRates[state.resource] = rate;
            }

            Text.Font = GameFont.Medium;
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(new Rect(AccentW + 6f, curY, viewRect.width, 30f), "SC_SettlementNeeds".Translate());
            Text.Font = GameFont.Small;
            curY += 34f;

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

                if (state.resource.Icon is object)
                    GUI.DrawTexture(new Rect(cx, topY + 1f, 20f, 20f), state.resource.Icon);

                Text.Anchor = TextAnchor.MiddleLeft;
                Rect labelRect = new Rect(cx + 24f, topY, 140f, NeedTopLineH);
                Widgets.Label(labelRect, TextUtil.ClampWithEllipsis(labelRect, state.label));

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
                    if (penaltyText is object)
                    {
                        Rect penaltyRect = new Rect(cx + 228f, botY, viewRect.width - cx - 232f, NeedBotLineH);
                        Text.Anchor = TextAnchor.MiddleRight;
                        Widgets.Label(penaltyRect, TextUtil.ClampWithEllipsis(penaltyRect, penaltyText));
                    }
                    GUI.color = Color.white;
                }

                Text.Font = GameFont.Small;

                // Tooltip
                string tooltip = BuildNeedTooltip(state, satisfaction, actual);
                if (tooltip is object)
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
                if (needDef is object)
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
            if (projected < 1f && state.penalties is object && state.demanded > 0)
            {
                tip += "\n\n" + (string)"SC_NeedProjectionExplain".Translate(
                    (projected * 100f).ToString("F0"));
                double projectedShortfall = state.demanded * (1.0 - projected);
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

            string resLabel = newLocalSellResource is object ? newLocalSellResource.label.CapitalizeFirst() : (string)"SC_PickResource".Translate();
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
                if (newLocalSellResource is object && newLocalSellAmount > 0)
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

            string resLabel = newTitheInjResource is object ? newTitheInjResource.label.CapitalizeFirst() : (string)"SC_PickResource".Translate();
            if (Widgets.ButtonText(new Rect(sx + 44f, curY, 130f, 24f), resLabel))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>();
                // Only tithable resources: SetTitheInjection silently no-ops on the rest.
                foreach (ResourceTypeDef def in FactionCache.TitheableResourceTypeDefs)
                {
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
                if (newTitheInjResource is object && newTitheInjAmount > 0)
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
