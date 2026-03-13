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
            Color origColor = GUI.color;
            if (net > 0.01)
            {
                GUI.color = Color.green;
                Widgets.DrawHighlight(rect);
            }
            else if (net < -0.01)
            {
                GUI.color = Color.red;
                Widgets.DrawHighlight(rect);
            }
            GUI.color = origColor;
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
    }
}
