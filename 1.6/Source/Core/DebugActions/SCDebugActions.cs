using System;
using System.Text;
using LudeonTK;
using Verse;

namespace FactionColonies.SupplyChain
{
    public static class SCDebugActions
    {
        /// <summary>
        /// Iterates all stockpile pools (one in Simple mode, per-settlement in Complex mode).
        /// Calls action with (pool, label) for each.
        /// </summary>
        private static void ForEachPool(Action<IStockpilePool, string> action)
        {
            WorldComponent_SupplyChain comp = SupplyChainCache.Comp;
            if (comp == null) return;

            if (comp.Mode == SupplyChainMode.Simple)
            {
                IStockpilePool pool = comp.Pool;
                if (pool != null)
                    action(pool, "Faction");
            }
            else
            {
                FactionFC faction = FactionCache.FactionComp;
                if (faction == null) return;
                foreach (WorldSettlementFC settlement in faction.settlements)
                {
                    WorldObjectComp_SupplyChain sc = SupplyChainCache.GetSettlementComp(settlement);
                    if (sc == null) continue;
                    IStockpilePool pool = sc.GetPool();
                    if (pool != null)
                        action(pool, settlement.Name);
                }
            }
        }

        [DebugAction("Empire - Supply Chain", "Fill all stockpiles", allowedGameStates = AllowedGameStates.Playing)]
        private static void FillAllStockpiles()
        {
            ForEachPool((pool, label) =>
            {
                foreach (ResourceTypeDef rtd in DefDatabase<ResourceTypeDef>.AllDefs)
                {
                    double cap = pool.GetCap(rtd);
                    double current = pool.GetAmount(rtd);
                    if (cap > current)
                        pool.Credit(rtd, cap - current);
                }
            });
            Log.Message("[Empire-SupplyChain] Debug: All stockpiles filled to cap.");
        }

        [DebugAction("Empire - Supply Chain", "Empty all stockpiles", allowedGameStates = AllowedGameStates.Playing)]
        private static void EmptyAllStockpiles()
        {
            ForEachPool((pool, label) =>
            {
                foreach (ResourceTypeDef rtd in DefDatabase<ResourceTypeDef>.AllDefs)
                {
                    double current = pool.GetAmount(rtd);
                    if (current > 0)
                    {
                        double drawn;
                        pool.TryDraw(rtd, current, out drawn);
                    }
                }
            });
            Log.Message("[Empire-SupplyChain] Debug: All stockpiles emptied.");
        }

        [DebugAction("Empire - Supply Chain", "Force resolve needs", allowedGameStates = AllowedGameStates.Playing)]
        private static void ForceResolveNeeds()
        {
            WorldComponent_SupplyChain comp = SupplyChainCache.Comp;
            if (comp == null) return;

            FactionFC faction = FactionCache.FactionComp;
            if (faction == null) return;

            if (comp.Mode == SupplyChainMode.Simple)
            {
                IStockpilePool pool = comp.Pool;
                if (pool == null) return;
                NeedResolver.ResolveSettlementNeedsFair(faction, pool);
            }
            else
            {
                foreach (WorldSettlementFC settlement in faction.settlements)
                {
                    WorldObjectComp_SupplyChain sc = SupplyChainCache.GetSettlementComp(settlement);
                    if (sc == null) continue;
                    IStockpilePool pool = sc.GetPool();
                    if (pool == null) continue;
                    NeedResolver.ResolveSettlementNeeds(settlement, pool, sc);
                }
            }
            Log.Message("[Empire-SupplyChain] Debug: Needs resolved.");
        }

        [DebugAction("Empire - Supply Chain", "Force execute routes", allowedGameStates = AllowedGameStates.Playing)]
        private static void ForceExecuteRoutes()
        {
            WorldComponent_SupplyChain comp = SupplyChainCache.Comp;
            if (comp == null || comp.Mode != SupplyChainMode.Complex) return;

            int count = 0;
            foreach (SupplyRoute route in comp.SupplyRoutes)
            {
                if (!route.IsValid()) continue;
                route.RecacheIfDirty();

                WorldObjectComp_SupplyChain srcComp = SupplyChainCache.GetSettlementComp(route.source);
                WorldObjectComp_SupplyChain destComp = SupplyChainCache.GetSettlementComp(route.destination);
                if (srcComp == null || destComp == null) continue;

                IStockpilePool srcPool = srcComp.GetPool();
                IStockpilePool destPool = destComp.GetPool();
                if (srcPool == null || destPool == null) continue;

                double transferred = route.Execute(srcPool, destPool);
                Log.Message("[Empire-SupplyChain] Debug: Route " + route.source.Name + " -> " + route.destination.Name
                    + " (" + route.resource.label + "): transferred " + transferred.ToString("F1"));
                count++;
            }
            Log.Message("[Empire-SupplyChain] Debug: Executed " + count + " routes.");
        }

        [DebugAction("Empire - Supply Chain", "Force execute sell orders", allowedGameStates = AllowedGameStates.Playing)]
        private static void ForceExecuteSellOrders()
        {
            WorldComponent_SupplyChain comp = SupplyChainCache.Comp;
            if (comp == null) return;

            FactionFC faction = FactionCache.FactionComp;
            if (faction == null) return;

            comp.PreTaxResolution(faction);
            Log.Message("[Empire-SupplyChain] Debug: PreTaxResolution executed (includes sell orders).");
        }

        [DebugAction("Empire - Supply Chain", "Force post-tax cleanup", allowedGameStates = AllowedGameStates.Playing)]
        private static void ForcePostTaxCleanup()
        {
            WorldComponent_SupplyChain comp = SupplyChainCache.Comp;
            if (comp == null) return;

            FactionFC faction = FactionCache.FactionComp;
            if (faction == null) return;

            comp.PostTaxResolution(faction);
            Log.Message("[Empire-SupplyChain] Debug: PostTaxResolution executed (tithe injection cleanup).");
        }

        [DebugAction("Empire - Supply Chain", "Print stockpile state", allowedGameStates = AllowedGameStates.Playing)]
        private static void PrintStockpileState()
        {
            WorldComponent_SupplyChain comp = SupplyChainCache.Comp;
            if (comp == null) return;

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[Empire-SupplyChain] === Stockpile State (" + comp.Mode + " mode) ===");

            ForEachPool((pool, label) =>
            {
                sb.AppendLine("  " + label + ":");
                foreach (ResourceTypeDef rtd in DefDatabase<ResourceTypeDef>.AllDefs)
                {
                    double amount = pool.GetAmount(rtd);
                    double cap = pool.GetCap(rtd);
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
                        + ", eff=" + route.CachedEfficiency.ToString("F2") + ")");
                }
            }

            Log.Message(sb.ToString());
        }

        [DebugAction("Empire - Supply Chain", "Force full tax cycle", allowedGameStates = AllowedGameStates.Playing)]
        private static void ForceFullTaxCycle()
        {
            WorldComponent_SupplyChain comp = SupplyChainCache.Comp;
            if (comp == null) return;

            FactionFC faction = FactionCache.FactionComp;
            if (faction == null) return;

            comp.PreTaxResolution(faction);
            comp.PostTaxResolution(faction);
            Log.Message("[Empire-SupplyChain] Debug: Full tax cycle executed (Pre + Post + cleanup).");
        }
    }
}
