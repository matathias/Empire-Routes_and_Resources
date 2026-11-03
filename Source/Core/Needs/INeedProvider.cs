using System.Collections.Generic;

namespace FactionColonies.SupplyChain
{
    /// <summary>
    /// A single resource need contributed by an INeedProvider.
    /// </summary>
    public struct NeedEntry
    {
        public string needId;
        public string label;
        public ResourceTypeDef resource;
        public double amount;
        public List<NeedPenalty> penalties;
        public List<NeedSurplusBonus> surplusBonuses;
        public double maxSurplusRatio;
    }

    /// <summary>
    /// Lightweight resolution result passed back to INeedProvider after need resolution.
    /// </summary>
    public struct NeedResolution
    {
        public string needId;
        public double demanded;
        public double fulfilled;

        public float Satisfaction => demanded > 0 ? (float)(fulfilled / demanded) : 1f;
    }

    /// <summary>
    /// WorldObjectComp interface for contributing dynamic resource needs to the supply chain's
    /// need resolution system. Needs contributed here are resolved alongside SettlementNeedDef
    /// and BuildingNeedExtension needs.
    /// <para>Implement on a WorldObjectComp attached to WorldSettlementFC. The NeedResolver
    /// discovers implementors via standard comp scan during tax resolution.</para>
    /// </summary>
    public interface INeedProvider
    {
        /// <summary>
        /// Populates the list with resource needs this comp contributes for this settlement.
        /// Called once per tax period during need resolution.
        /// </summary>
        void CollectNeeds(WorldSettlementFC settlement, List<NeedEntry> needs);

        /// <summary>
        /// Called after need resolution with the satisfaction results for needs this provider contributed.
        /// </summary>
        void OnNeedsResolved(List<NeedResolution> resolvedNeeds);
    }
}
