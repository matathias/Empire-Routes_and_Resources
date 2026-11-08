using UnityEngine;
using static FactionColonies.SupplyChain.WorldComponent_SupplyChain;

namespace FactionColonies.SupplyChain;

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

        // --- Daily section: production, needs, and tithe injection all resolve per day ---
        double net = flow.DailyNet;
        if (net > 0.01)
            tip += "\n" + (string)"SC_BarNetPositive".Translate(net.ToString("F1"));
        else if (net < -0.01)
            tip += "\n" + (string)"SC_BarNetNegative".Translate((-net).ToString("F1"));
        else
            tip += "\n" + (string)"SC_BarNetEven".Translate();

        if (flow.production > 0)
            tip += "\n" + (string)"SC_BarFlowProduction".Translate(flow.production.ToString("F1"));
        if (flow.baseNeeds > 0)
            tip += "\n" + (string)"SC_BarFlowBaseNeeds".Translate(flow.baseNeeds.ToString("F1"));
        if (flow.buildingNeeds > 0)
            tip += "\n" + (string)"SC_BarFlowBuildingNeeds".Translate(flow.buildingNeeds.ToString("F1"));
        if (flow.compNeedLines is not null)
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
        if (flow.titheInjection > 0)
            tip += "\n" + (string)"SC_BarFlowTitheInjection".Translate(flow.titheInjection.ToString("F1"));

        // Routes now dispatch on each route's own frequency; routeIn/routeOut are daily averages.
        if (flow.routeIn > 0)
            tip += "\n" + (string)"SC_BarFlowRouteIn".Translate(flow.routeIn.ToString("F1"));
        if (flow.routeOut > 0)
            tip += "\n" + (string)"SC_BarFlowRouteOut".Translate(flow.routeOut.ToString("F1"));

        // --- Per-tax-cycle section: sell orders resolve once per tax cycle ---
        if (flow.sellOrders > 0)
        {
            tip += "\n" + (string)"SC_BarFlowCycleHeader".Translate();
            tip += "\n" + (string)"SC_BarFlowSellOrders".Translate(flow.sellOrders.ToString("F1"));
        }

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

        List<FloatMenuOption> options = [];
        HashSet<int> seen = [];
        int[] presets = [1, 5, 10, maxBuyable];

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
                    // The game keeps running while the menu is open, so the pile may have filled
                    // since it was built: buy only what still fits, and charge only for that.
                    int fits = (int)Math.Min(capturedQty, Math.Max(0, stockpile.GetCap(def) - stockpile.GetAmount(def)));
                    if (fits <= 0)
                    {
                        Messages.Message("SC_CannotBuyFull".Translate(def.LabelCap), MessageTypeDefOf.RejectInput, false);
                        return;
                    }
                    if (PaymentUtil.PaySilver(fits * FCSettings.silverPerResource, "SC_BuyReason".Translate(def.LabelCap)))
                    {
                        stockpile.Credit(def, fits);
                        onPurchased?.Invoke();
                    }
                }));
        }

        Find.WindowStack.Add(new FloatMenu(options));
    }

    /* Shared rows for the faction tab and the settlement tab */

    /// <summary>
    /// A button showing <paramref name="current"/>'s label, or <paramref name="emptyLabel"/> when
    /// nothing is picked; clicking it opens a menu of <paramref name="choices"/> that calls
    /// <paramref name="onPick"/>.
    /// </summary>
    internal static void ResourcePickerButton(Rect rect, ResourceTypeDef current, string emptyLabel,
        IEnumerable<ResourceTypeDef> choices, Action<ResourceTypeDef> onPick)
    {
        string label = current is not null ? current.label.CapitalizeFirst() : emptyLabel;
        if (!Widgets.ButtonText(rect, label)) return;

        List<FloatMenuOption> options = [];
        foreach (ResourceTypeDef def in choices)
        {
            ResourceTypeDef captured = def;
            options.Add(new FloatMenuOption(def.label.CapitalizeFirst(), () => onPick(captured)));
        }
        Find.WindowStack.Add(new FloatMenu(options));
    }

    /// <summary>
    /// A button showing <paramref name="current"/>'s name, or <paramref name="emptyLabel"/> when
    /// nothing is picked; clicking it opens a menu of <paramref name="choices"/> (less
    /// <paramref name="exclude"/>) that calls <paramref name="onPick"/>. Each option is the settlement's
    /// name followed by <paramref name="suffix"/>, when one is given.
    /// </summary>
    internal static void SettlementPickerButton(Rect rect, WorldSettlementFC current, string emptyLabel,
        IEnumerable<WorldSettlementFC> choices, Func<WorldSettlementFC, string> suffix,
        Action<WorldSettlementFC> onPick, WorldSettlementFC exclude = null)
    {
        string label = current is not null ? current.Name : emptyLabel;
        if (!Widgets.ButtonText(rect, label)) return;

        List<FloatMenuOption> options = [];
        if (choices is not null)
        {
            foreach (WorldSettlementFC s in choices)
            {
                if (exclude is not null && s == exclude) continue;
                WorldSettlementFC captured = s;
                string optionLabel = suffix is null ? s.Name : $"{s.Name}{suffix(s)}";
                options.Add(new FloatMenuOption(optionLabel, () => onPick(captured)));
            }
        }
        Find.WindowStack.Add(new FloatMenu(options));
    }

    /// <summary>Resources a sell order can name: every non-pool resource.</summary>
    internal static IEnumerable<ResourceTypeDef> SellableResources => FactionCache.NonPoolResourceTypeDefs;

    /// <summary>
    /// The "Add: [resource] [amount] [Add]" row at <paramref name="x"/>. Clicking Add with a resource
    /// and a positive amount calls <paramref name="onAdd"/>, then clears the resource and amount.
    /// Advances <paramref name="curY"/> past the row.
    /// </summary>
    internal static void DrawAddResourceRow(float x, ref float curY, ResourceTypeDef resource,
        Action<ResourceTypeDef> setResource, IEnumerable<ResourceTypeDef> choices,
        ref float amount, ref string amountBuffer, Action<ResourceTypeDef, float> onAdd)
    {
        Text.Anchor = TextAnchor.MiddleLeft;
        Widgets.Label(new Rect(x, curY, 40f, 26f), "SC_AddColon".Translate());

        ResourcePickerButton(new Rect(x + 44f, curY, 130f, 24f), resource, "SC_PickResource".Translate(), choices, setResource);

        Widgets.TextFieldNumeric(new Rect(x + 180f, curY, 80f, 24f), ref amount, ref amountBuffer, 0f, 9999f);

        if (Widgets.ButtonText(new Rect(x + 268f, curY, 60f, 24f), "SC_Add".Translate())
            && resource is not null && amount > 0)
        {
            onAdd(resource, amount);
            setResource(null);
            amount = 0;
            amountBuffer = "";
        }

        Text.Anchor = TextAnchor.UpperLeft;
        curY += 28f;
    }

    /// <summary>Width of <see cref="DrawAddResourceRow"/>, for centering it.</summary>
    internal const float AddResourceRowWidth = 40f + 4f + 130f + 6f + 80f + 8f + 60f;

    /// <summary>A route-form menu suffix with <paramref name="settlement"/>'s daily need for
    /// <paramref name="resource"/>, or "" when no active need uses it.</summary>
    internal static string RouteNeedSuffix(WorldSettlementFC settlement, ResourceTypeDef resource)
    {
        foreach (SettlementNeedDef needDef in SupplyChainCache.AllNeedDefs)
        {
            if (!needDef.IsActiveForSettlement(settlement) || !needDef.UsesResource(resource)) continue;
            double demand = needDef.CalculateDemand(settlement)
                * needDef.GetResourceFraction(FindFC.TechLevel, resource);
            return "SC_RouteFormNeedSuffix".Translate(demand.ToString("F1"));
        }
        return "";
    }

    /// <summary>A route-form menu suffix with <paramref name="settlement"/>'s daily production of
    /// <paramref name="resource"/>, or "" when it has none.</summary>
    internal static string RouteProductionSuffix(WorldSettlementFC settlement, ResourceTypeDef resource)
    {
        ResourceFC res = settlement.GetResource(resource);
        return res is null ? "" : (string)"SC_RouteFormProdSuffix".Translate(res.rawTotalProduction.ToString("F1"));
    }

    /// <summary>
    /// The tail of an add-route form starting at <paramref name="x"/>: amount, frequency (days between
    /// deliveries) and the Add button. Returns true when Add is clicked.
    /// </summary>
    internal static bool DrawRouteFormTail(float x, float y, ref float amount, ref string amountBuffer,
        ref int frequency, ref string frequencyBuffer)
    {
        Widgets.TextFieldNumeric(new Rect(x, y, 70f, 24f), ref amount, ref amountBuffer, 0f, 9999f);
        x += 74f;

        Rect freqRect = new(x, y, 44f, 24f);
        Widgets.TextFieldNumeric(freqRect, ref frequency, ref frequencyBuffer,
            SupplyChainSettings.minRouteFrequencyDays, SupplyChainSettings.maxRouteFrequencyDays);
        TooltipHandler.TipRegion(freqRect, "SC_FrequencyTooltip".Translate());
        x += 48f;

        return Widgets.ButtonText(new Rect(x, y, 50f, 24f), "SC_Add".Translate());
    }

    /// <summary>Width of <see cref="DrawRouteFormTail"/>.</summary>
    internal const float RouteFormTailWidth = 74f + 48f + 50f;

    /// <summary>Creates and links a route, clamping <paramref name="frequency"/> to the allowed range.</summary>
    internal static void LinkNewRoute(WorldComponent_SupplyChain wc, WorldSettlementFC source,
        WorldSettlementFC destination, ResourceTypeDef resource, float amount, int frequency)
    {
        SupplyRoute route = new(source, destination, resource, amount);
        route.frequencyDays = Mathf.Clamp(frequency,
            SupplyChainSettings.minRouteFrequencyDays, SupplyChainSettings.maxRouteFrequencyDays);
        wc.LinkRoute(route);
        wc.DirtyFlowCache();
    }

    /// <summary>
    /// The route list's filter bar at (<paramref name="x"/>, <paramref name="y"/>): "All", then one
    /// button per resource in <paramref name="resources"/>. Clicking one sets
    /// <paramref name="filter"/>; a filter whose resource is no longer listed is cleared.
    /// </summary>
    internal static void DrawResourceFilterBar(float x, float y, float height, HashSet<ResourceTypeDef> resources,
        ref ResourceTypeDef filter)
    {
        if (UIUtil.ButtonFlat(new Rect(x, y, 40f, height), (string)"SC_All".Translate(), highlighted: filter is null))
            filter = null;
        x += 44f;

        foreach (ResourceTypeDef def in resources)
        {
            string btnLabel = def.label.CapitalizeFirst();
            float btnW = (def.Icon is not null ? 20f : 0f) + Text.CalcSize(btnLabel).x + 10f;
            if (UIUtil.ButtonFlatIcon(new Rect(x, y, btnW, height), btnLabel,
                def.Icon, labelColor: def.color, highlighted: filter == def))
                filter = def;
            x += btnW + 4f;
        }

        if (filter is not null && !resources.Contains(filter))
            filter = null;
    }

    internal const float RouteAccentW = 4f;

    /// <summary>
    /// The left edge of route row <paramref name="index"/> in <paramref name="shown"/>: the zebra
    /// highlight, the resource and efficiency accent strips, and the reorder arrows. Returns true when
    /// an arrow moved the route; the caller then stops drawing, since the route list changed under it.
    /// <paramref name="contentX"/> is where the row's own columns start.
    /// </summary>
    internal static bool DrawRouteRowLead(float y, float width, float rowH, List<SupplyRoute> shown, int index,
        WorldComponent_SupplyChain wc, out Color effAccent, out bool pathReady, out float contentX)
    {
        SupplyRoute route = shown[index];

        // Cheap efficiency refresh only — the expensive travel-time/path pathfind is warmed off
        // the UI thread by SupplyRouteWarmer; show a placeholder until it's ready.
        route.RecacheEfficiencyIfDirty();
        pathReady = route.PathReady;

        if (index % 2 == 0) Widgets.DrawHighlight(new Rect(0f, y, width, rowH));

        float eff = (float)route.CachedEfficiency;
        effAccent = pathReady ? AccentUtil.GetStatColor(eff * 100f, false) : Color.gray;
        Widgets.DrawBoxSolid(new Rect(0f, y, RouteAccentW, rowH), route.resource?.color ?? Color.gray);
        Widgets.DrawBoxSolid(new Rect(RouteAccentW + 2f, y, RouteAccentW, rowH), effAccent);

        // Reorder arrows: dispatch precedence is list order
        float reorderX = RouteAccentW * 2 + 6f;
        float arrowH = rowH / 2f;
        contentX = reorderX + 14f;
        if (index > 0 && Widgets.ButtonImage(new Rect(reorderX, y, 14f, arrowH), TexButton.ReorderUp))
        {
            wc.MoveRouteBefore(route, shown[index - 1]);
            return true;
        }
        if (index < shown.Count - 1 && Widgets.ButtonImage(new Rect(reorderX, y + arrowH, 14f, arrowH), TexButton.ReorderDown))
        {
            wc.MoveRouteAfter(route, shown[index + 1]);
            return true;
        }
        TooltipHandler.TipRegion(new Rect(reorderX, y, 14f, rowH), "SC_RouteReorderTooltip".Translate());
        return false;
    }
}
