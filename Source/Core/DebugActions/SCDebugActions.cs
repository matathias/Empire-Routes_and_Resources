using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using LudeonTK;
using UnityEngine;
using Verse;
using RimWorld;
using RimWorld.Planet;
using FactionColonies.util;

namespace FactionColonies.SupplyChain
{
    public static class SCDebugActions
    {
        /// <summary>
        /// Iterates all stockpiles (one in Simple mode, per-settlement in Complex mode).
        /// Calls action with (stockpile, label) for each.
        /// </summary>
        private static void ForEachStockpile(Action<IStockpile, string> action)
        {
            WorldComponent_SupplyChain comp = SupplyChainCache.Comp;
            if (comp == null) return;

            if (comp.Mode == SupplyChainMode.Simple)
            {
                IStockpile stockpile = comp.Stockpile;
                if (stockpile != null)
                    action(stockpile, "Faction");
            }
            else
            {
                FactionFC faction = FindFC.FactionComp;
                if (faction == null) return;
                foreach (WorldSettlementFC settlement in faction.settlements)
                {
                    WorldObjectComp_SupplyChain sc = SupplyChainCache.GetSettlementComp(settlement);
                    if (sc == null) continue;
                    IStockpile stockpile = sc.GetStockpile();
                    if (stockpile != null)
                        action(stockpile, settlement.Name);
                }
            }
        }

        [DebugAction("Empire Refactored: Routes & Resources", "Fill all stockpiles", allowedGameStates = AllowedGameStates.Playing)]
        private static void FillAllStockpiles()
        {
            ForEachStockpile((stockpile, label) =>
            {
                foreach (ResourceTypeDef rtd in SupplyChainCache.AllResourceTypeDefs)
                {
                    double cap = stockpile.GetCap(rtd);
                    double current = stockpile.GetAmount(rtd);
                    if (cap > current)
                        stockpile.Credit(rtd, cap - current);
                }
            });
            Log.Message("[Empire-SupplyChain] Debug: All stockpiles filled to cap.");
        }

        [DebugAction("Empire Refactored: Routes & Resources", "Empty all stockpiles", allowedGameStates = AllowedGameStates.Playing)]
        private static void EmptyAllStockpiles()
        {
            ForEachStockpile((stockpile, label) =>
            {
                foreach (ResourceTypeDef rtd in SupplyChainCache.AllResourceTypeDefs)
                {
                    double current = stockpile.GetAmount(rtd);
                    if (current > 0)
                    {
                        double drawn;
                        stockpile.TryDraw(rtd, current, out drawn);
                    }
                }
            });
            Log.Message("[Empire-SupplyChain] Debug: All stockpiles emptied.");
        }

        [DebugAction("Empire Refactored: Routes & Resources", "Force resolve needs", allowedGameStates = AllowedGameStates.Playing)]
        private static void ForceResolveNeeds()
        {
            WorldComponent_SupplyChain comp = SupplyChainCache.Comp;
            if (comp == null) return;

            FactionFC faction = FindFC.FactionComp;
            if (faction == null) return;

            if (comp.Mode == SupplyChainMode.Simple)
            {
                IStockpile stockpile = comp.Stockpile;
                if (stockpile == null) return;
                NeedResolver.ResolveSettlementNeedsFair(faction, stockpile);
            }
            else
            {
                foreach (WorldSettlementFC settlement in faction.settlements)
                {
                    WorldObjectComp_SupplyChain sc = SupplyChainCache.GetSettlementComp(settlement);
                    WorldObjectComp_SettlementNeeds needsComp = SupplyChainCache.GetNeedsComp(settlement);
                    if (sc == null || needsComp == null) continue;
                    IStockpile stockpile = sc.GetStockpile();
                    if (stockpile == null) continue;
                    NeedResolver.ResolveSettlementNeeds(settlement, stockpile, needsComp);
                }
            }
            Log.Message("[Empire-SupplyChain] Debug: Needs resolved.");
        }

        [DebugAction("Empire Refactored: Routes & Resources", "Force dispatch all due routes", allowedGameStates = AllowedGameStates.Playing)]
        private static void ForceDispatchAllRoutes()
        {
            WorldComponent_SupplyChain comp = SupplyChainCache.Comp;
            if (comp == null || comp.Mode != SupplyChainMode.Complex) return;

            comp.DebugForceDispatchAllRoutes();
            Log.Message("[Empire-SupplyChain] Debug: Dispatched all routes now; "
                + comp.PendingDeliveries.Count + " deliveries in transit.");
        }

        [DebugAction("Empire Refactored: Routes & Resources", "Force arrive all deliveries", allowedGameStates = AllowedGameStates.Playing)]
        private static void ForceArriveAllDeliveries()
        {
            WorldComponent_SupplyChain comp = SupplyChainCache.Comp;
            if (comp == null || comp.Mode != SupplyChainMode.Complex) return;

            int count = comp.PendingDeliveries.Count;
            comp.DebugForceArriveAllDeliveries();
            Log.Message("[Empire-SupplyChain] Debug: Forced arrival of " + count + " deliveries.");
        }

        [DebugAction("Empire Refactored: Routes & Resources", "Force execute sell orders", allowedGameStates = AllowedGameStates.Playing)]
        private static void ForceExecuteSellOrders()
        {
            WorldComponent_SupplyChain comp = SupplyChainCache.Comp;
            if (comp == null) return;

            FactionFC faction = FindFC.FactionComp;
            if (faction == null) return;

            comp.PreTaxResolution(faction);
            Log.Message("[Empire-SupplyChain] Debug: PreTaxResolution executed (includes sell orders).");
        }

        [DebugAction("Empire Refactored: Routes & Resources", "Print stockpile state", allowedGameStates = AllowedGameStates.Playing)]
        private static void PrintStockpileState()
        {
            WorldComponent_SupplyChain comp = SupplyChainCache.Comp;
            if (comp == null) return;

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[Empire-SupplyChain] === Stockpile State (" + comp.Mode + " mode) ===");

            ForEachStockpile((stockpile, label) =>
            {
                sb.AppendLine("  " + label + ":");
                foreach (ResourceTypeDef rtd in SupplyChainCache.AllResourceTypeDefs)
                {
                    double amount = stockpile.GetAmount(rtd);
                    double cap = stockpile.GetCap(rtd);
                    if (cap > 0 || amount > 0)
                        sb.AppendLine("    " + rtd.label + ": " + amount.ToString("F1") + " / " + cap.ToString("F1"));
                }
            });

            if (comp.Mode == SupplyChainMode.Complex)
            {
                sb.AppendLine("  Routes: " + comp.SupplyRoutes.Count);
                foreach (SupplyRoute route in comp.SupplyRoutes)
                {
                    if (!route.IsValid()) continue;
                    route.RecacheIfDirty();
                    sb.AppendLine("    " + route.source.Name + " -> " + route.destination.Name
                        + " (" + route.resource.label + " x" + route.amountPerPeriod.ToString("F1")
                        + ", every " + route.frequencyDays + "d, eff=" + route.CachedEfficiency.ToString("F2") + ")");
                }

                sb.AppendLine("  In-transit deliveries: " + comp.PendingDeliveries.Count);
                foreach (PendingDelivery d in comp.PendingDeliveries)
                {
                    string src = d.source != null ? d.source.Name : "?";
                    string dst = d.destination != null ? d.destination.Name : "?";
                    string res = d.resource != null ? d.resource.label : "?";
                    sb.AppendLine("    " + src + " -> " + dst + " (" + res + " x" + d.amount.ToString("F1")
                        + ", eff=" + d.efficiency.ToString("F2") + ", ETA " + d.TicksRemaining.ToStringTicksToPeriod() + ")");
                }
            }

            Log.Message(sb.ToString());
        }

        [DebugAction("Empire Refactored: Routes & Resources", "Force full tax cycle", allowedGameStates = AllowedGameStates.Playing)]
        private static void ForceFullTaxCycle()
        {
            WorldComponent_SupplyChain comp = SupplyChainCache.Comp;
            if (comp == null) return;

            FactionFC faction = FindFC.FactionComp;
            if (faction == null) return;

            comp.PreTaxResolution(faction);
            comp.PostTaxResolution(faction);
            Log.Message("[Empire-SupplyChain] Debug: Full tax cycle executed (Pre + Post + cleanup).");
        }

        /// <summary>
        /// Stress-test scenario for the delivery-caravan system: spawns 10 settlements at random valid
        /// tiles, upgrades each 1-9 times, puts all its workers on one random (non-pool) resource,
        /// seeds its stockpile, and links a daily outgoing route to every other settlement carrying
        /// 1 unit. Switches to Complex mode first (routes only run there). Once travel times overlap
        /// this puts up to 90 delivery caravans pathfinding across the map at once. Re-runnable.
        /// </summary>
        [DebugAction("Empire Refactored: Routes & Resources", "Stress test: 10 settlements + daily route mesh", allowedGameStates = AllowedGameStates.Playing)]
        private static void StressTestRouteMesh()
        {
            const int settlementCount = 10;
            const int maxTileAttempts = 2000;

            WorldComponent_SupplyChain comp = SupplyChainCache.Comp;
            FactionFC faction = FindFC.FactionComp;
            if (comp == null || faction == null)
            {
                Log.Warning("[Empire-SupplyChain] Debug: stress test aborted (no supply-chain component or faction).");
                return;
            }

            try
            {
                // Routes only dispatch in Complex mode.
                if (comp.Mode != SupplyChainMode.Complex)
                {
                    comp.SwitchMode(SupplyChainMode.Complex);
                    SupplyChainSettings.mode = SupplyChainMode.Complex;
                }

                // 1. Spawn settlements at valid random surface tiles.
                WorldSettlementDef def = WorldSettlementDefOf.WorldSettlementDef_Surface;
                List<WorldSettlementFC> created = new List<WorldSettlementFC>();
                int attempts = 0;
                while (created.Count < settlementCount && attempts < maxTileAttempts)
                {
                    attempts++;
                    PlanetTile tile = TileFinder.RandomSettlementTileFor(Find.WorldGrid.Surface, FindFC.EmpireFaction);
                    if (!tile.Valid) continue;
                    if (!WorldTileChecker.IsValidTileForNewSettlement(tile, def)) continue;
                    if (faction.CheckSettlementCaravansList(tile)) continue;

                    WorldSettlementFC s = ColonyUtil.CreatePlayerColonySettlement(tile, def);
                    if (s != null) created.Add(s);
                }

                // 2. Configure each: random upgrades, one worked resource, all workers on it, seeded stock.
                Dictionary<WorldSettlementFC, ResourceTypeDef> workedResource = new Dictionary<WorldSettlementFC, ResourceTypeDef>();
                foreach (WorldSettlementFC s in created)
                {
                    s.UpgradeSettlement(Rand.RangeInclusive(1, 9));

                    // Non-pool so the scenario also exercises the silver flows (sell orders,
                    // overflow auto-sell), which pool resources never enter.
                    ResourceFC chosen = s.Resources.Where(r => r.def != null && !r.def.isPoolResource).RandomElementWithFallback(null);
                    if (chosen == null) continue;
                    workedResource[s] = chosen.def;

                    // All workers onto the chosen resource: shed everything, then load to the ultra cap.
                    int cap = (int)s.workersUltraMax;
                    foreach (ResourceFC r in s.Resources) s.IncreaseWorkers(r, -(cap + 1));
                    s.IncreaseWorkers(chosen, cap);

                    // Pre-seed the source stockpile to cap so routes dispatch immediately.
                    IStockpile sp = SupplyChainCache.GetSettlementComp(s)?.EnsureLocalStockpile();
                    if (sp != null)
                    {
                        double room = sp.GetCap(chosen.def) - sp.GetAmount(chosen.def);
                        if (room > 0) sp.Credit(chosen.def, room);
                    }
                }

                // 3. Wire an outgoing daily route from each settlement to every other one.
                int freq = Mathf.Clamp(1, SupplyChainSettings.minRouteFrequencyDays, SupplyChainSettings.maxRouteFrequencyDays);
                int routes = 0;
                foreach (WorldSettlementFC src in created)
                {
                    ResourceTypeDef res;
                    if (!workedResource.TryGetValue(src, out res)) continue;
                    foreach (WorldSettlementFC dst in created)
                    {
                        if (src == dst) continue;
                        SupplyRoute route = new SupplyRoute(src, dst, res, 1.0);
                        route.frequencyDays = freq;   // leaves nextDispatchTick = -1 -> dispatches next daily tick
                        comp.LinkRoute(route);
                        routes++;
                    }
                }

                Log.Message("[Empire-SupplyChain] Debug: stress test created " + created.Count
                    + " settlements and " + routes + " routes (Complex mode, " + freq + "-day frequency).");
            }
            catch (Exception e)
            {
                Log.Error("[Empire-SupplyChain] Debug: stress test threw: " + e);
            }
        }

        [DebugAction("Empire Refactored: Routes & Resources", "Force dispatch all routes x10", allowedGameStates = AllowedGameStates.Playing)]
        private static void ForceDispatchAllRoutesTenTimes()
        {
            WorldComponent_SupplyChain comp = SupplyChainCache.Comp;
            if (comp == null || comp.Mode != SupplyChainMode.Complex) return;

            for (int i = 0; i < 10; i++)
                comp.DebugForceDispatchAllRoutes();

            Log.Message("[Empire-SupplyChain] Debug: force-dispatched all routes 10x; "
                + comp.PendingDeliveries.Count + " deliveries in transit.");
        }

        [DebugAction("Empire Refactored: Routes & Resources", "Clear all supply caravans", allowedGameStates = AllowedGameStates.Playing)]
        private static void ClearAllSupplyCaravans()
        {
            WorldComponent_SupplyChain comp = SupplyChainCache.Comp;
            if (comp == null || comp.Mode != SupplyChainMode.Complex) return;

            int count = comp.PendingDeliveries.Count;
            comp.DebugForceArriveAllDeliveries();  // credits goods + removes caravans
            Log.Message("[Empire-SupplyChain] Debug: cleared " + count + " supply caravans (force-arrived).");
        }
    }
}
