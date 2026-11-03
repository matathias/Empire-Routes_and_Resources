using System;
using System.Collections.Generic;
using FactionColonies;
using RimWorld;
using UnityEngine;
using Verse;

namespace FactionColonies.SupplyChain
{
    /// <summary>
    /// Shared rendering for the read-only in-transit Deliveries list and the per-route frequency
    /// stepper, used identically by the faction main tab and the per-settlement tab.
    /// </summary>
    internal static class DeliveryUIUtil
    {
        private static readonly Color DeliveryBarBg = new Color(0.12f, 0.12f, 0.12f);
        private static readonly Color DeliveryBarFill = new Color(0.35f, 0.6f, 0.9f);

        /// <summary>
        /// Draws the in-transit deliveries as a scrollable read-only list. When <paramref name="filter"/>
        /// is non-null, only deliveries touching that settlement (as source or destination) are shown.
        /// </summary>
        public static void DrawDeliveriesList(Rect rect, ref Vector2 scrollPos,
            List<PendingDelivery> deliveries, WorldSettlementFC filter)
        {
            const float rowH = 30f;
            const float accentW = 4f;
            const float rowGap = 2f;

            // Build the visible subset (respecting the optional settlement filter).
            List<PendingDelivery> visible = new List<PendingDelivery>();
            if (deliveries != null)
            {
                foreach (PendingDelivery d in deliveries)
                {
                    if (filter != null && d.source != filter && d.destination != filter) continue;
                    visible.Add(d);
                }
            }

            float totalHeight = visible.Count * (rowH + rowGap) + 20f;
            Rect viewRect = ScrollUtil.BeginScrollView(rect, ref scrollPos, totalHeight);
            float rowW = viewRect.width;
            float curY = 4f;

            if (visible.Count == 0)
            {
                Text.Font = GameFont.Tiny;
                GUI.color = Color.gray;
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(new Rect(0f, curY, rowW, 24f), "SC_NoDeliveries".Translate());
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                ScrollUtil.EndScrollView();
                return;
            }

            int idx = 0;
            foreach (PendingDelivery d in visible)
            {
                Rect row = new Rect(0f, curY, rowW, rowH);
                if (idx % 2 == 0) Widgets.DrawHighlight(row);

                Color accent = d.resource != null ? d.resource.color : Color.gray;
                Widgets.DrawBoxSolid(new Rect(0f, curY, accentW, rowH), accent);

                float x = accentW + 6f;
                Text.Anchor = TextAnchor.MiddleLeft;

                if (d.resource != null && d.resource.Icon != null)
                    GUI.DrawTexture(new Rect(x, curY + 5f, 20f, 20f), d.resource.Icon);
                x += 24f;

                // Net amount that will land on arrival (efficiency already factored in).
                double netOnArrival = d.amount * d.efficiency;
                Widgets.Label(new Rect(x, curY, 60f, rowH), netOnArrival.ToString("F1"));
                x += 64f;

                // Progress bar (right-anchored) with ETA centered over it.
                float barW = 150f;
                float barX = rowW - barW - 8f;

                // Source -> Dest text fills the space between the amount and the bar.
                string srcName = d.source != null ? d.source.Name : "?";
                string dstName = d.destination != null ? d.destination.Name : "?";
                float routeW = barX - x - 8f;
                if (routeW > 20f)
                {
                    bool prevWrap = Text.WordWrap;
                    Text.WordWrap = false;
                    Widgets.Label(new Rect(x, curY, routeW, rowH), srcName + " → " + dstName);
                    Text.WordWrap = prevWrap;
                }

                Rect barRect = new Rect(barX, curY + 7f, barW, rowH - 14f);
                UIUtil.DrawProgressBarColors(barRect, d.Progress, DeliveryBarBg, DeliveryBarFill);

                Text.Font = GameFont.Tiny;
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(barRect, d.TicksRemaining.ToStringTicksToPeriod());
                Text.Anchor = TextAnchor.UpperLeft;
                Text.Font = GameFont.Small;

                TooltipHandler.TipRegion(barRect,
                    "SC_DeliveryEta".Translate(d.TicksRemaining.ToStringTicksToPeriod(), (d.Progress * 100f).ToString("F0")));

                curY += rowH + rowGap;
                idx++;
            }

            ScrollUtil.EndScrollView();
        }

        /// <summary>
        /// Draws a compact [-] Nd [+] delivery-frequency stepper for a route (clamped to the
        /// configured bounds). <paramref name="onChanged"/> fires when the value actually changes.
        /// </summary>
        public static void DrawFrequencyStepper(Rect rect, SupplyRoute route, Action onChanged)
        {
            TextAnchor prevAnchor = Text.Anchor;
            float y = rect.y + (rect.height - 24f) / 2f;
            Rect minusRect = new Rect(rect.x, y, 16f, 24f);
            Rect labelRect = new Rect(rect.x + 17f, rect.y, 24f, rect.height);
            Rect plusRect = new Rect(rect.x + 42f, y, 16f, 24f);

            if (Widgets.ButtonText(minusRect, "-"))
            {
                route.SetFrequencyDays(route.frequencyDays - 1);
                onChanged?.Invoke();
            }

            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(labelRect, "SC_FreqDays".Translate(route.frequencyDays));
            Text.Anchor = prevAnchor;

            if (Widgets.ButtonText(plusRect, "+"))
            {
                route.SetFrequencyDays(route.frequencyDays + 1);
                onChanged?.Invoke();
            }

            TooltipHandler.TipRegion(rect, "SC_FrequencyTooltip".Translate());
        }

        /// <summary>
        /// Draws an editable numeric field for a route's base amount per period. Each route needs its
        /// own persistent <paramref name="buffers"/> entry so intermediate typing (e.g. "1.") survives
        /// across frames. <paramref name="onChanged"/> fires when the value actually changes.
        /// </summary>
        public static void DrawAmountField(Rect rect, SupplyRoute route,
            Dictionary<SupplyRoute, string> buffers, Action onChanged)
        {
            string buffer;
            if (!buffers.TryGetValue(route, out buffer))
                buffer = route.amountPerPeriod.ToString("F1");

            // amountPerPeriod is a double; edit through a float mirror (matches the add-route form) to
            // avoid relying on a double TextFieldNumeric overload, then widen back on write.
            float amount = (float)route.amountPerPeriod;
            TextAnchor prev = Text.Anchor;
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.TextFieldNumeric(rect, ref amount, ref buffer, 0f, 9999f);
            Text.Anchor = prev;

            buffers[route] = buffer;
            if (!Mathf.Approximately(amount, (float)route.amountPerPeriod))
            {
                route.amountPerPeriod = amount;
                onChanged?.Invoke();
            }
        }
    }
}
