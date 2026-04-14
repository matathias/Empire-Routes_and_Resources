namespace FactionColonies.SupplyChain
{
    public static class FormulaUtil
    {
        public static double ResourceCost(double amount, double mult)
        {
            return amount * mult * SupplyChainSettings.resourceCostMultiplier;
        }
    }
}