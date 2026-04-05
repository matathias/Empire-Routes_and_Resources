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
        private double cachedDistMult = 1.0;
        private FoundingCostValidator validator;
        private WorldComponent_SupplyChain worldComp;
        private FoundingCostExtension cachedExt;

        public override Vector2 InitialSize => new Vector2(WindowWidth, 300f);

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
            // Position to the left of the creation window (which is right-aligned)
            CreateColonyWindowFc createWindow = Find.WindowStack.WindowOfType<CreateColonyWindowFc>();
            if (createWindow != null)
            {
                windowRect.x = createWindow.windowRect.x - WindowWidth - 10f;
                windowRect.y = createWindow.windowRect.y;
            }
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (validator == null || worldComp == null)
            {
                Close();
                return;
            }

            CreateColonyWindowFc createWindow = Find.WindowStack.WindowOfType<CreateColonyWindowFc>();
            if (createWindow == null)
            {
                Close();
                return;
            }

            PlanetTile currentTile = createWindow.currentTileSelected;
            WorldSettlementDef settlementType = createWindow.currentSettlementType;

            // Handle settlement type changes — close if type no longer has founding costs
            if (settlementType != lastSettlementType)
            {
                lastSettlementType = settlementType;
                cachedExt = settlementType?.GetModExtension<FoundingCostExtension>();
                if (cachedExt == null || cachedExt.resourceCosts == null || cachedExt.resourceCosts.Count == 0)
                {
                    Close();
                    return;
                }
            }
            else if (cachedExt == null)
            {
                cachedExt = settlementType?.GetModExtension<FoundingCostExtension>();
            }

            if (cachedExt == null || cachedExt.resourceCosts == null || cachedExt.resourceCosts.Count == 0)
            {
                Close();
                return;
            }

            // Update nearest and cached distance when tile changes
            if (currentTile != lastTile)
            {
                lastTile = currentTile;
                if (currentTile.Valid)
                {
                    cachedDistMult = FoundingCostUtil.ComputeDistanceMultiplier(currentTile);
                    if (!validator.UserSelectedSource)
                        validator.SelectedSource = FoundingCostUtil.FindNearestSettlement(currentTile);
                }
                else
                {
                    cachedDistMult = 1.0;
                }
            }

            float curY = 0f;

            // Title
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(new Rect(0, curY, inRect.width, ButtonHeight), "SC_FoundingSourceTitle".Translate());
            curY += ButtonHeight + Padding;

            // Source settlement selector button
            WorldSettlementFC source = validator.GetEffectiveSource(currentTile);
            string sourceName = source?.Name ?? "SC_FoundingCostNone".Translate();

            Text.Anchor = TextAnchor.MiddleCenter;
            if (Widgets.ButtonText(new Rect(0, curY, inRect.width, ButtonHeight), "SC_FoundingSourceButton".Translate(sourceName)))
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

            IStockpile stockpile = GetCurrentStockpile(currentTile);

            for (int i = 0; i < cachedExt.resourceCosts.Count; i++)
            {
                ResourceCostEntry entry = cachedExt.resourceCosts[i];
                double needed = entry.amount * cachedDistMult;
                double have = stockpile != null ? stockpile.GetAmount(entry.resource) : 0;
                bool sufficient = have >= needed;

                // Resource label
                GUI.color = sufficient ? Color.white : Color.red;
                Widgets.Label(new Rect(Padding, curY, inRect.width * 0.45f, RowHeight), entry.resource.LabelCap);

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
                "SC_FoundingDistanceMult".Translate(cachedDistMult.ToString("F1")));
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
                IStockpile stockpile = comp != null ? comp.GetStockpile() : null;

                string label = settlement.Name;
                if (stockpile != null && cachedExt != null)
                {
                    int sufficient = 0;
                    for (int j = 0; j < cachedExt.resourceCosts.Count; j++)
                    {
                        ResourceCostEntry entry = cachedExt.resourceCosts[j];
                        double needed = entry.amount * cachedDistMult;
                        if (stockpile.GetAmount(entry.resource) >= needed) sufficient++;
                    }
                    label += " (" + sufficient + "/" + cachedExt.resourceCosts.Count + ")";
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
            if (source == null) return null;

            WorldObjectComp_SupplyChain comp = SupplyChainCache.GetSettlementComp(source);
            return comp != null ? comp.GetStockpile() : null;
        }

        /// <summary>
        /// Opens the companion window if conditions are met (Complex mode, above threshold, has extension).
        /// </summary>
        public static void TryOpen()
        {
            WorldComponent_SupplyChain wc = SupplyChainCache.Comp;
            if (wc == null || wc.Mode != SupplyChainMode.Complex) return;
            if (wc.FoundingValidator == null) return;

            FactionFC faction = FactionCache.FactionComp;
            if (faction == null) return;
            int count = faction.settlements.Count + faction.settlementCaravansList.Count;
            if (count < SupplyChainSettings.freeSettlementThreshold) return;

            CreateColonyWindowFc createWindow = Find.WindowStack.WindowOfType<CreateColonyWindowFc>();
            if (createWindow == null) return;

            FoundingCostExtension ext = createWindow.currentSettlementType?.GetModExtension<FoundingCostExtension>();
            if (ext == null || ext.resourceCosts == null || ext.resourceCosts.Count == 0) return;

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
