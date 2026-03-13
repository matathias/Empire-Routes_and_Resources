using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
using static FactionColonies.SupplyChain.WorldComponent_SupplyChain;

namespace FactionColonies.SupplyChain
{
    internal static class UIUtilSC
    {
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
            if (flow.routeOut > 0)
                tip += "\n" + (string)"SC_BarFlowRouteOut".Translate(flow.routeOut.ToString("F1"));
            if (flow.sellOrders > 0)
                tip += "\n" + (string)"SC_BarFlowSellOrders".Translate(flow.sellOrders.ToString("F1"));

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
    }
}
