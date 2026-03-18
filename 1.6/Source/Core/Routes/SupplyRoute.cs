using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace FactionColonies.SupplyChain
{
    public class SupplyRoute : IExposable
    {
        public WorldSettlementFC source;
        public WorldSettlementFC destination;
        public ResourceTypeDef resource;
        public double amountPerPeriod;
        public int priority;

        // Cached (not saved)
        private int cachedTravelTicks;
        private double cachedEfficiency;
        private bool dirty = true;

        public int CachedTravelTicks { get { return cachedTravelTicks; } }
        public double CachedEfficiency { get { return cachedEfficiency; } }

        public SupplyRoute()
        {
        }

        public SupplyRoute(WorldSettlementFC source, WorldSettlementFC destination,
            ResourceTypeDef resource, double amountPerPeriod, int priority = 0)
        {
            this.source = source;
            this.destination = destination;
            this.resource = resource;
            this.amountPerPeriod = amountPerPeriod;
            this.priority = priority;
            this.dirty = true;
        }

        /// <summary>
        /// Returns true if both source and destination settlements still exist and are valid.
        /// </summary>
        public bool IsValid()
        {
            return source != null && destination != null && resource != null
                && !source.Destroyed && !destination.Destroyed;
        }

        public void Invalidate()
        {
            dirty = true;
        }

        public void RecacheIfDirty()
        {
            if (!dirty) return;
            dirty = false;

            if (!IsValid())
            {
                cachedTravelTicks = 0;
                cachedEfficiency = 0.0;
                return;
            }

            cachedTravelTicks = TravelUtil.ReturnTicksToArrive(source.Tile, destination.Tile);
            double travelDays = cachedTravelTicks / (double)GenDate.TicksPerDay;
            double baseEfficiency = 1.0 / (1.0 + travelDays * SupplyChainSettings.routeDecayPerDay);

            // Apply route efficiency bonus stat from source settlement
            FCStatDef routeEffStat = DefDatabase<FCStatDef>.GetNamedSilentFail("SC_RouteEfficiencyBonus");
            if (routeEffStat != null)
            {
                double bonus = FactionCache.FactionComp.GetStatValue(routeEffStat, source);
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
                    LogUtil.Error("ISupplyRouteModifier " + mod.GetType().Name + " threw: " + e);
                }
            }

            cachedEfficiency = Math.Max(0.0, Math.Min(1.0, baseEfficiency));
        }

        /// <summary>
        /// Execute this route: draw from source stockpile, credit dest stockpile with efficiency loss.
        /// Returns the actual amount credited to destination.
        /// </summary>
        public double Execute(IStockpile sourceStockpile, IStockpile destStockpile)
        {
            if (!IsValid() || amountPerPeriod <= 0 || cachedEfficiency <= 0)
                return 0.0;

            double drawn;
            if (!sourceStockpile.TryDraw(resource, amountPerPeriod, out drawn) || drawn <= 0)
                return 0.0;

            double transferred = drawn * cachedEfficiency;
            double excess = destStockpile.Credit(resource, transferred);

            // If destination is full, the excess is lost (efficiency loss + overflow)
            if (excess > 0)
            {
                LogUtil.Message("Route " + source.Name + " -> " + destination.Name + ": "
                    + excess.ToString("F1") + " " + resource.label + " lost to destination overflow.");
            }

            return transferred - excess;
        }

        public void ExposeData()
        {
            Scribe_References.Look(ref source, "source");
            Scribe_References.Look(ref destination, "destination");
            Scribe_Defs.Look(ref resource, "resource");
            Scribe_Values.Look(ref amountPerPeriod, "amountPerPeriod", 0.0);
            Scribe_Values.Look(ref priority, "priority", 0);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                dirty = true;
            }
        }
    }
}
