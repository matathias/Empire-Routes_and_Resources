using System;
using System.Collections.Generic;

namespace FactionColonies.SupplyChain
{
    /// <summary>
    /// IStockpilePool implementation for Simple Mode.
    /// Wraps the faction-level dictionaries owned by WorldComponent_SupplyChain.
    /// </summary>
    public class FactionStockpilePool : IStockpilePool
    {
        private readonly Dictionary<ResourceTypeDef, double> stockpile;
        private readonly Dictionary<ResourceTypeDef, double> caps;

        public FactionStockpilePool(Dictionary<ResourceTypeDef, double> stockpile, Dictionary<ResourceTypeDef, double> caps)
        {
            this.stockpile = stockpile;
            this.caps = caps;
        }

        public double GetAmount(ResourceTypeDef resource)
        {
            double val;
            return stockpile.TryGetValue(resource, out val) ? val : 0.0;
        }

        public double GetCap(ResourceTypeDef resource)
        {
            double val;
            return caps.TryGetValue(resource, out val) ? val : 0.0;
        }

        public bool TryDraw(ResourceTypeDef resource, double amount, out double drawn)
        {
            drawn = 0.0;
            if (amount <= 0)
                return false;

            double current;
            if (!stockpile.TryGetValue(resource, out current) || current <= 0)
                return false;

            drawn = Math.Min(amount, current);
            stockpile[resource] = current - drawn;
            return true;
        }

        public double Credit(ResourceTypeDef resource, double amount)
        {
            if (amount <= 0)
                return 0.0;

            double current;
            if (!stockpile.TryGetValue(resource, out current))
                current = 0.0;

            double cap;
            if (!caps.TryGetValue(resource, out cap))
                cap = 0.0;

            double space = Math.Max(0, cap - current);
            double credited = Math.Min(amount, space);
            stockpile[resource] = current + credited;

            return amount - credited; // excess
        }
    }
}
