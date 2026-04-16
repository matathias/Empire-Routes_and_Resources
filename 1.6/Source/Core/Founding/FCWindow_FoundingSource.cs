using System.Collections.Generic;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace FactionColonies.SupplyChain
{
    /// <summary>
    /// Companion window to <see cref="CreateColonyWindowFc"/> that shows the source settlement
    /// picker and resource availability for founding costs (Complex mode only).
    /// </summary>
    public class FCWindow_FoundingSource : Window
    {
        private const float WindowWidth = 250f;
        private const float RowHeight = 22f;
        private const float ButtonHeight = 30f;
        private const float Padding = 8f;

        private PlanetTile lastTile = PlanetTile.Invalid;
        private WorldSettlementDef lastSettlementType;
        private FoundingCostValidator validator;
        private WorldComponent_SupplyChain worldComp;
        private List<FCResourceCost> cachedCosts;
        private bool lastModifiersWindowOpen;

        public override Vector2 InitialSize => new Vector2(WindowWidth, 350f);

        public FCWindow_FoundingSource()
        {
            draggable = true;
            doCloseX = true;
            preventCameraMotion = false;
            forcePause = false;
            closeOnAccept = false;
            closeOnCancel = false;
        }

        public override void PreOpen()
        {
            base.PreOpen();
            worldComp = SupplyChainCache.Comp;
            validator = worldComp?.FoundingValidator;
            if (validator != null)
                validator.ResetSourceSelection();
        }

        protected override void SetInitialSizeAndPosition()
        {
            base.SetInitialSizeAndPosition();
            // Position to the left of the tile modifiers window if open, otherwise left of the create window
            FCWindow_CreateColonyStatModifiers modifiersWindow = Find.WindowStack.WindowOfType<FCWindow_CreateColonyStatModifiers>();
            if (modifiersWindow != null)
            {
                windowRect.x = modifiersWindow.windowRect.x - WindowWidth - 10f;
                windowRect.y = modifiersWindow.windowRect.y;
            }
            else
            {
                CreateColonyWindowFc createWindow = Find.WindowStack.WindowOfType<CreateColonyWindowFc>();
                if (createWindow != null)
                {
                    windowRect.x = createWindow.windowRect.x - WindowWidth - 10f;
                    windowRect.y = createWindow.windowRect.y;
                }
            }
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (validator is null || worldComp is null || worldComp.Mode == SupplyChainMode.Simple)
            {
                Close();
                return;
            }

            CreateColonyWindowFc createWindow = Find.WindowStack.WindowOfType<CreateColonyWindowFc>();
            if (createWindow is null)
            {
                Close();
                return;
            }

            // Reposition when the tile modifiers companion window opens or closes
            FCWindow_CreateColonyStatModifiers modWindow = Find.WindowStack.WindowOfType<FCWindow_CreateColonyStatModifiers>();
            bool modifiersOpen = modWindow is object;
            if (modifiersOpen != lastModifiersWindowOpen)
            {
                lastModifiersWindowOpen = modifiersOpen;
                if (modWindow is object)
                    windowRect.x = modWindow.windowRect.x - WindowWidth - 10f;
                else
                    windowRect.x = createWindow.windowRect.x - WindowWidth - 10f;
            }

            PlanetTile currentTile = createWindow.currentTileSelected;
            WorldSettlementDef settlementType = createWindow.currentSettlementType;

            // Handle settlement type changes — close if type no longer has founding costs
            if (settlementType != lastSettlementType)
            {
                lastSettlementType = settlementType;
                cachedCosts = FoundingCostUtil.GetFoundingResourceCosts(settlementType);
                if (cachedCosts is null || cachedCosts.Count == 0)
                {
                    Close();
                    return;
                }
            }
            else if (cachedCosts is null)
            {
                cachedCosts = FoundingCostUtil.GetFoundingResourceCosts(settlementType);
            }

            if (cachedCosts is null || cachedCosts.Count == 0)
            {
                Close();
                return;
            }

            // Auto-select nearest source when tile changes (only if user hasn't explicitly picked)
            if (currentTile != lastTile)
            {
                lastTile = currentTile;
                if (!validator.UserSelectedSource && currentTile.Valid)
                    validator.SelectedSource = FoundingCostUtil.FindNearestSettlement(currentTile);
            }

            float curY = 0f;
            
            WorldSettlementFC source = validator.GetEffectiveSource(currentTile);
            string sourceName = source?.Name ?? "SC_FoundingCostNone".Translate();

            // Title
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleCenter;
            Rect titleRect = new Rect(0, curY, inRect.width, ButtonHeight);
            UIUtil.DrawColoredHighlight(titleRect, source?.settlementDef?.accentColor ?? Color.white);
            Widgets.Label(titleRect, "SC_FoundingSourceTitle".Translate());
            curY += ButtonHeight + 5f;

            // Source settlement selector button

            Widgets.Label(new Rect(0, curY, inRect.width, 25f), "SC_FoundingSourceButton".Translate());
            curY += 30f;
            Text.Anchor = TextAnchor.MiddleCenter;
            if (Widgets.ButtonText(new Rect(0, curY, inRect.width, ButtonHeight), sourceName))
            {
                ShowSourceMenu(currentTile);
            }
            curY += ButtonHeight + Padding;

            // Divider
            Widgets.DrawLineHorizontal(0, curY, inRect.width);
            curY += Padding;

            // Resource availability
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleLeft;

            double distMult = FoundingCostUtil.ComputeDistanceMultiplier(currentTile);
            IStockpile stockpile = GetCurrentStockpile(currentTile);

            for (int i = 0; i < cachedCosts.Count; i++)
            {
                FCResourceCost entry = cachedCosts[i];
                double needed = FormulaUtil.ResourceCost(entry.amount, distMult);
                double have = stockpile?.GetAmount(entry.resource) ?? 0;
                bool sufficient = have >= needed;

                // Resource label
                float resourceImgSize = RowHeight;
                float resourceImxgX = 2f;
                Rect resourceImgRect = new Rect(resourceImxgX, curY, resourceImgSize, resourceImgSize);
                Widgets.ButtonImage(resourceImgRect, entry.resource.Icon);
                GUI.color = sufficient ? AccentUtil.Income : AccentUtil.Expense;
                Widgets.Label(new Rect(resourceImgRect.xMax + Padding, curY, inRect.width * 0.45f, RowHeight), entry.resource.LabelCap);

                // Amount: have / needed
                Text.Anchor = TextAnchor.MiddleRight;
                Widgets.Label(new Rect(inRect.width * 0.45f, curY, inRect.width * 0.55f - Padding, RowHeight),
                    have.ToString("F0") + " / " + needed.ToString("F0"));
                Text.Anchor = TextAnchor.MiddleLeft;

                GUI.color = Color.white;
                curY += RowHeight;
            }

            curY += Padding;

            // Distance info
            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleCenter;
            GUI.color = new Color(0.7f, 0.7f, 0.7f);
            Widgets.Label(new Rect(0, curY, inRect.width, RowHeight),
                "SC_FoundingDistanceMult".Translate(distMult.ToString("F1")));
            GUI.color = Color.white;
            curY += RowHeight + Padding;

            // Resize window to fit content
            windowRect.height = curY + Window.StandardMargin * 2;

            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;
        }

        private void ShowSourceMenu(PlanetTile targetTile)
        {
            FactionFC faction = FactionCache.FactionComp;
            if (faction == null) return;

            List<FloatMenuOption> options = new List<FloatMenuOption>();

            for (int i = 0; i < faction.settlements.Count; i++)
            {
                WorldSettlementFC settlement = faction.settlements[i];
                WorldObjectComp_SupplyChain comp = SupplyChainCache.GetSettlementComp(settlement);
                IStockpile stockpile = comp?.GetStockpile();

                string label = settlement.Name;
                if (stockpile != null && cachedCosts != null)
                {
                    double menuDistMult = FoundingCostUtil.ComputeDistanceMultiplier(targetTile);
                    int sufficient = 0;
                    foreach (FCResourceCost entry in cachedCosts)
                    {
                        double needed = FormulaUtil.ResourceCost(entry.amount, menuDistMult);
                        if (stockpile.GetAmount(entry.resource) >= needed) sufficient++;
                    }
                    label += $" ({sufficient}/{cachedCosts.Count})";
                }

                WorldSettlementFC capturedSettlement = settlement;
                options.Add(new FloatMenuOption(label, delegate
                {
                    validator.SelectedSource = capturedSettlement;
                    validator.UserSelectedSource = true;
                }));
            }

            if (options.Count > 0)
                Find.WindowStack.Add(new FloatMenu(options));
        }

        private IStockpile GetCurrentStockpile(PlanetTile tile)
        {
            if (worldComp.Mode == SupplyChainMode.Simple)
                return worldComp.Stockpile;

            WorldSettlementFC source = validator.GetEffectiveSource(tile);
            if (source is null) return null;

            WorldObjectComp_SupplyChain comp = SupplyChainCache.GetSettlementComp(source);
            return comp?.GetStockpile();
        }

        /// <summary>
        /// Opens the companion window if conditions are met (Complex mode, above threshold, has extension).
        /// </summary>
        public static void TryOpen()
        {
            WorldComponent_SupplyChain wc = SupplyChainCache.Comp;
            if (wc is null || wc.Mode != SupplyChainMode.Complex) return;
            if (wc.FoundingValidator is null) return;

            FactionFC faction = FactionCache.FactionComp;
            if (faction is null) return;
            int count = faction.settlements.Count + faction.settlementCaravansList.Count;
            if (count < SupplyChainSettings.freeSettlementThreshold) return;

            CreateColonyWindowFc createWindow = Find.WindowStack.WindowOfType<CreateColonyWindowFc>();

            List<FCResourceCost> costs = FoundingCostUtil.GetFoundingResourceCosts(createWindow?.currentSettlementType);
            if (costs is null || costs.Count == 0) return;

            // Don't open duplicate
            if (Find.WindowStack.WindowOfType<FCWindow_FoundingSource>() != null) return;

            Find.WindowStack.Add(new FCWindow_FoundingSource());
        }

        /// <summary>
        /// Closes the companion window if it's open.
        /// </summary>
        public static void TryClose()
        {
            FCWindow_FoundingSource window = Find.WindowStack.WindowOfType<FCWindow_FoundingSource>();
            if (window != null)
                window.Close();
        }
    }
}
