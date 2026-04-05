using System.Collections.Generic;
using Verse;

namespace FactionColonies.SupplyChain
{
    /// <summary>
    /// DefModExtension on <see cref="WorldSettlementDef"/> defining resource costs for founding.
    /// Settlement types without this extension have no resource founding cost.
    /// Resource amounts are base values, scaled by distance multiplier at founding time.
    /// </summary>
    public class FoundingCostExtension : DefModExtension
    {
        public List<ResourceCostEntry> resourceCosts;
    }

    public class ResourceCostEntry
    {
        public ResourceTypeDef resource;
        public double amount;
    }
}
