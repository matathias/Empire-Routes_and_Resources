using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using static FactionColonies.SupplyChain.WorldComponent_SupplyChain;

namespace FactionColonies.SupplyChain
{
    internal static class UIUtilSC
    {
        internal static void DrawFlowHighlight(Rect rect, double net)
        {
            if (net > 0.01)
            {
                Widgets.DrawBoxSolid(rect, new Color(0f, 0.6f, 0f, 0.25f));
            }
            else if (net < -0.01)
            {
                Widgets.DrawBoxSolid(rect, new Color(0.6f, 0f, 0f, 0.25f));
            }
        }
        internal static string BuildFlowTooltip(ResourceTypeDef def, double amt, double cap, FlowBreakdown flow,
            int numSettlements = 0, double baseCapPerSettlement = 0, double buildingCapBonus = 0)
        {
            string tip = def.label.CapitalizeFirst() + ": " + amt.ToString("F1") + " / " + cap.ToString("F0")
                + "\n-----";

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
            if (flow.baseNeeds > 0)
                tip += "\n" + (string)"SC_BarFlowBaseNeeds".Translate(flow.baseNeeds.ToString("F1"));
            if (flow.buildingNeeds > 0)
                tip += "\n" + (string)"SC_BarFlowBuildingNeeds".Translate(flow.buildingNeeds.ToString("F1"));
            if (flow.compNeedLines != null)
            {
                foreach (CompNeedLine line in flow.compNeedLines)
                {
                    if (line.amount > 0)
                        tip += "\n" + (string)"SC_BarFlowCompNeedLine".Translate(line.label, line.amount.ToString("F1"));
                }
            }
            else if (flow.compNeeds > 0)
            {
                tip += "\n" + (string)"SC_BarFlowCompNeedLine".Translate("Other", flow.compNeeds.ToString("F1"));
            }
            if (flow.routeOut > 0)
                tip += "\n" + (string)"SC_BarFlowRouteOut".Translate(flow.routeOut.ToString("F1"));
            if (flow.sellOrders > 0)
                tip += "\n" + (string)"SC_BarFlowSellOrders".Translate(flow.sellOrders.ToString("F1"));
            if (flow.titheInjection > 0)
                tip += "\n" + (string)"SC_BarFlowTitheInjection".Translate(flow.titheInjection.ToString("F1"));

            if (numSettlements > 0)
            {
                double baseCap = numSettlements * baseCapPerSettlement;
                tip += "\n" + (string)"SC_BarCapBreakdown".Translate(
                    numSettlements.ToString(), baseCapPerSettlement.ToString("F0"), baseCap.ToString("F0"));
                if (buildingCapBonus > 0.01)
                {
                    tip += "\n" + (string)"SC_BarCapBuildings".Translate(buildingCapBonus.ToString("F0"));
                    tip += "\n" + (string)"SC_BarCapTotal".Translate(cap.ToString("F0"));
                }
            }

            return tip;
        }

        /// <summary>
        /// Shows a float menu for purchasing <paramref name="def"/> units with silver,
        /// crediting the given <paramref name="stockpile"/>. Call <paramref name="onPurchased"/>
        /// after a successful purchase to dirty caches.
        /// </summary>
        internal static void ShowBuyMenu(ResourceTypeDef def, IStockpile stockpile, Action onPurchased)
        {
            int silverAvailable = PaymentUtil.GetSilver();
            double cap = stockpile.GetCap(def);
            double current = stockpile.GetAmount(def);
            double space = Math.Max(0, cap - current);
            int maxAffordable = silverAvailable / FCSettings.silverPerResource;
            int maxBuyable = (int)Math.Min(maxAffordable, space);

            if (maxBuyable <= 0)
            {
                string reason = silverAvailable < FCSettings.silverPerResource
                    ? "SC_CannotBuyNoSilver".Translate(def.LabelCap)
                    : "SC_CannotBuyFull".Translate(def.LabelCap);
                Messages.Message(reason, MessageTypeDefOf.RejectInput, false);
                return;
            }

            List<FloatMenuOption> options = new List<FloatMenuOption>();
            HashSet<int> seen = new HashSet<int>();
            int[] presets = { 1, 5, 10, maxBuyable };

            for (int i = 0; i < presets.Length; i++)
            {
                int qty = presets[i];
                if (qty <= 0 || qty > maxBuyable || !seen.Add(qty)) continue;

                int cost = qty * FCSettings.silverPerResource;
                int capturedQty = qty;
                options.Add(new FloatMenuOption(
                    "SC_BuyOption".Translate(capturedQty.ToString(), def.LabelCap, cost.ToString()),
                    delegate
                    {
                        if (PaymentUtil.PaySilver(cost, "SC_BuyReason".Translate(def.LabelCap)))
                        {
                            stockpile.Credit(def, capturedQty);
                            onPurchased?.Invoke();
                        }
                    }));
            }

            Find.WindowStack.Add(new FloatMenu(options));
        }
    }
}
