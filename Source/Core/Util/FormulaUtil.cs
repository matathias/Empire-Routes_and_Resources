using System;

namespace FactionColonies.SupplyChain
{
    public static class FormulaUtil
    {
        public static double ResourceCost(double amount, double mult)
        {
            return amount * mult * SupplyChainSettings.resourceCostMultiplier;
        }

        public static float OverflowSilver(double amount)
        {
            return (float)(amount * FCSettings.silverPerResource * SupplyChainSettings.overflowPenaltyRate);
        }

        public static double RouteEfficiency(double travelDays)
        {
            return 1.0 / (1.0 + travelDays * SupplyChainSettings.routeDecayPerDay);
        }

        public static double TaxEfficiency(double averageSatisfaction)
        {
            return 1.0 + 0.20 * averageSatisfaction;
        }

        public static double SellRateMultiplier(int connectedPartners, int hubScore)
        {
            return 1.0 + 0.10 * Math.Min(connectedPartners, 5) + 0.10 * Math.Min(hubScore, 3);
        }

        public static double HappinessNetworkBonus(int connectedPartners)
        {
            return 0.5 * Math.Min(connectedPartners, 5);
        }

        public static double ProsperityNetworkBonus(int hubScore)
        {
            return 1.0 * Math.Min(hubScore, 3);
        }
    }
}
