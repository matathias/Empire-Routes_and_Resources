using System;
using System.Collections.Generic;

namespace FactionColonies.SupplyChain
{
    /// <summary>
    /// IStockpile backed by a pair of dictionaries (amounts + caps).
    /// Used by both Simple mode (faction-level) and Complex mode (per-settlement).
    /// </summary>
    public class DictionaryStockpile : IStockpile
    {
        private readonly Dictionary<ResourceTypeDef, double> stockpile;
        private readonly Dictionary<ResourceTypeDef, double> caps;

        public DictionaryStockpile(Dictionary<ResourceTypeDef, double> stockpile, Dictionary<ResourceTypeDef, double> caps)
        {
            this.stockpile = stockpile;
            this.caps = caps;
        }

        public double GetAmount(ResourceTypeDef resource)
        {
            return stockpile.TryGetValue(resource, out double val) ? val : 0.0;
        }

        public double GetCap(ResourceTypeDef resource)
        {
            return caps.TryGetValue(resource, out double val) ? val : 0.0;
        }

        public bool TryDraw(ResourceTypeDef resource, double amount, out double drawn)
        {
            drawn = 0.0;
            if (amount <= 0)
                return false;

            if (!stockpile.TryGetValue(resource, out double current) || current <= 0)
                return false;

            drawn = Math.Min(amount, current);
            stockpile[resource] = current - drawn;
            return true;
        }

        public double Credit(ResourceTypeDef resource, double amount)
        {
            if (amount <= 0)
                return 0.0;

            if (!stockpile.TryGetValue(resource, out double current))
                current = 0.0;

            if (!caps.TryGetValue(resource, out double cap))
                cap = 0.0;

            double space = Math.Max(0, cap - current);
            double credited = Math.Min(amount, space);
            stockpile[resource] = current + credited;

            return amount - credited; // excess
        }
    }
}
