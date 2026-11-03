using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace FactionColonies.SupplyChain
{
    public class SupplyRoute : IExposable
    {
        public WorldSettlementFC source;
        public WorldSettlementFC destination;
        public ResourceTypeDef resource;
        public double amountPerPeriod;

        // How often (in days) this route dispatches a delivery, and when its next dispatch is due.
        // nextDispatchTick = -1 is a sentinel meaning "not yet scheduled" — a new route dispatches
        // immediately on the first daily tick after creation, then every frequencyDays thereafter.
        public int frequencyDays = SupplyChainSettings.defaultRouteFrequencyDays;
        public int nextDispatchTick = -1;

        // Cached (not saved). Two independent dirty tiers:
        //  - pathDirty: cachedTravelTicks + cachedPathTiles need the expensive A* world pathfind. Only the
        //    endpoints' tiles and the road network affect these, so they change rarely (route created,
        //    loaded, roads change, pods research). Recomputed off the main thread by SupplyRouteWarmer.
        //  - efficiencyDirty: cachedEfficiency needs recompute from the cached travel time plus settlement
        //    stats / modifier hooks. Cheap, main-thread only (touches faction stat aggregation).
        private int cachedTravelTicks;
        private double cachedEfficiency;
        private List<PlanetTile> cachedPathTiles;
        private bool pathDirty = true;
        private bool efficiencyDirty = true;

        public int CachedTravelTicks => cachedTravelTicks;
        public double CachedEfficiency => cachedEfficiency;

        /// <summary>True once the travel time / path has been computed (UI shows a placeholder until then).</summary>
        public bool PathReady => !pathDirty;

        // Ordered overland tile path (source -> destination) for this route, or null when travel is
        // straight-line (pods/shuttle/cross-layer). Captured for free from the travel-time recache below.
        public List<PlanetTile> CachedPathTiles => cachedPathTiles;

        public SupplyRoute()
        {
        }

        public SupplyRoute(WorldSettlementFC source, WorldSettlementFC destination,
            ResourceTypeDef resource, double amountPerPeriod)
        {
            this.source = source;
            this.destination = destination;
            this.resource = resource;
            this.amountPerPeriod = amountPerPeriod;

            MarkPathDirty();
        }

        /// <summary>
        /// Sets the delivery frequency (clamped to the configured bounds) and reschedules the next
        /// dispatch relative to now, so a shortened frequency takes effect promptly.
        /// </summary>
        public void SetFrequencyDays(int days)
        {
            int clamped = Mathf.Clamp(days, SupplyChainSettings.minRouteFrequencyDays,
                SupplyChainSettings.maxRouteFrequencyDays);
            if (clamped == frequencyDays) return;
            frequencyDays = clamped;
            nextDispatchTick = Find.TickManager.TicksGame + frequencyDays * GenDate.TicksPerDay;
        }

        /// <summary>
        /// Returns true if both source and destination settlements still exist and are valid.
        /// </summary>
        public bool IsValid()
        {
            return source != null && destination != null && resource != null
                && !source.Destroyed && !destination.Destroyed;
        }

        /// <summary>Mark the travel time / path (and therefore efficiency) as needing recompute.</summary>
        public void MarkPathDirty()
        {
            pathDirty = true;
            efficiencyDirty = true;
        }

        /// <summary>Mark only the (cheap) efficiency as needing recompute — used when stats change but the
        /// road path does not (e.g. founding/removing a settlement).</summary>
        public void MarkEfficiencyDirty()
        {
            efficiencyDirty = true;
        }

        /// <summary>
        /// Main-thread: apply a travel time + path computed elsewhere (the background warmer, or a
        /// synchronous recompute). Clears the path-dirty flag and forces an efficiency recompute.
        /// </summary>
        public void ApplyPathResult(int travelTicks, List<PlanetTile> path)
        {
            cachedTravelTicks = travelTicks;
            cachedPathTiles = path;
            pathDirty = false;
            efficiencyDirty = true;
        }

        /// <summary>
        /// Main-thread synchronous path recompute (runs the A* world pathfind). Prefer the background
        /// <see cref="SupplyRouteWarmer"/>; this is the fallback for synchronous mode and on-demand dispatch.
        /// </summary>
        public void RecachePathSync()
        {
            if (!pathDirty) return;

            if (!IsValid())
            {
                cachedTravelTicks = 0;
                cachedPathTiles = null;
                cachedEfficiency = 0.0;
                pathDirty = false;
                efficiencyDirty = false;
                return;
            }

            int ticks = TravelUtil.ReturnTicksToArrive(source.Tile, destination.Tile, out List<PlanetTile> path);
            ApplyPathResult(ticks, path);
        }

        /// <summary>
        /// Main-thread: recompute the cheap, stat-dependent efficiency from the cached travel time. No
        /// pathfind. Skipped while the path is still dirty (a valid travel time isn't available yet).
        /// </summary>
        public void RecacheEfficiencyIfDirty()
        {
            if (!efficiencyDirty || pathDirty) return;
            efficiencyDirty = false;

            if (!IsValid())
            {
                cachedEfficiency = 0.0;
                return;
            }

            double travelDays = cachedTravelTicks / (double)GenDate.TicksPerDay;
            double baseEfficiency = FormulaUtil.RouteEfficiency(travelDays);

            // Apply route efficiency bonus stat from source settlement
            FCStatDef routeEffStat = SCStatDefOf.SC_RouteEfficiencyBonus;
            if (routeEffStat != null)
            {
                double bonus = FindFC.FactionComp.GetStatValue(routeEffStat, source);
                baseEfficiency += bonus;
            }

            // Apply modifier hooks
            foreach (ISupplyRouteModifier mod in SupplyRouteModifierRegistry.Modifiers)
            {
                try
                {
                    baseEfficiency = mod.ModifyRouteEfficiency(this, baseEfficiency);
                }
                catch (Exception e)
                {
                    LogSC.Error($"ISupplyRouteModifier {mod.GetType().Name} threw: {e}");
                }
            }

            cachedEfficiency = Math.Max(0.0, Math.Min(1.0, baseEfficiency));
        }

        /// <summary>Full synchronous warm (path + efficiency). Used by dispatch when a due route isn't ready.</summary>
        public void RecacheIfDirty()
        {
            RecachePathSync();
            RecacheEfficiencyIfDirty();
        }

        /// <summary>
        /// Dispatch a delivery: draw up to <see cref="amountPerPeriod"/> from the source stockpile
        /// (shipping whatever is available if short) and return a self-contained
        /// <see cref="PendingDelivery"/> that arrives after this route's travel time, with efficiency
        /// applied on arrival. Returns null if the route is invalid or nothing could be drawn.
        /// </summary>
        public PendingDelivery TryDispatch(IStockpile sourceStockpile)
        {
            if (!IsValid() || amountPerPeriod <= 0 || cachedEfficiency <= 0)
                return null;

            if (!sourceStockpile.TryDraw(resource, amountPerPeriod, out double drawn) || drawn <= 0)
                return null;

            int now = Find.TickManager.TicksGame;
            return new PendingDelivery
            {
                source = source,
                destination = destination,
                resource = resource,
                amount = drawn,                // amountPerPeriod is a target; ship whatever we could draw
                efficiency = cachedEfficiency, // snapshot; the route may change before arrival
                dispatchTick = now,
                arrivalTick = now + cachedTravelTicks,
                // Snapshot the road path only when delivery caravans are enabled — that keeps the abstract
                // path byte-identical to the pre-caravan behaviour (no saved path, no world object). Null for
                // straight-line travel (pods/shuttle) too, which never spawns a caravan.
                pathTiles = (SupplyChainSettings.useDeliveryCaravans && cachedPathTiles != null)
                    ? new List<PlanetTile>(cachedPathTiles)
                    : null
            };
        }

        public void ExposeData()
        {
            Scribe_References.Look(ref source, "source");
            Scribe_References.Look(ref destination, "destination");
            Scribe_Defs.Look(ref resource, "resource");
            Scribe_Values.Look(ref amountPerPeriod, "amountPerPeriod", 0.0);
            Scribe_Values.Look(ref frequencyDays, "frequencyDays", SupplyChainSettings.defaultRouteFrequencyDays);
            Scribe_Values.Look(ref nextDispatchTick, "nextDispatchTick", -1);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                MarkPathDirty();
            }
        }
    }
}
