using System.Collections.Generic;
using Verse;

namespace FactionColonies.SupplyChain
{
    public enum NeedCategory : byte
    {
        Base,
        Building,
        Comp
    }

    /// <summary>
    /// Tracks the satisfaction of a single need for one settlement.
    /// Persisted on WorldObjectComp_SupplyChain.
    /// </summary>
    public class NeedState : IExposable
    {
        public string needId;
        public SettlementNeedDef needDef;
        public ResourceTypeDef resource;
        public double demanded;
        public double fulfilled;
        public string label;
        public NeedCategory category;

        /// <summary>
        /// Stat penalties applied when this need is unsatisfied.
        /// Populated for all categories: from SettlementNeedDef for base needs,
        /// from BuildingNeedExtension for building needs, from INeedProvider for comp needs.
        /// Not serialized — recomputed each rebuild/resolution.
        /// </summary>
        public List<NeedPenalty> penalties;

        /// <summary>
        /// Surplus ratio and bonus data. surplusRatio is set by NeedResolver post-draw:
        /// stockpile remaining / demanded. surplusBonuses and maxSurplusRatio are copied
        /// from the SettlementNeedDef. Not serialized — recomputed each resolution,
        /// preserved between cycles by RebuildNeedStates.
        /// </summary>
        public double surplusRatio;
        public List<NeedSurplusBonus> surplusBonuses;
        public double maxSurplusRatio;

        public float Satisfaction => demanded > 0 ? (float)(fulfilled / demanded) : 1f;

        public NeedState()
        {
        }

        public NeedState(string needId, ResourceTypeDef resource, double demanded, double fulfilled,
            string label, NeedCategory category, List<NeedPenalty> penalties = null,
            List<NeedSurplusBonus> surplusBonuses = null, double maxSurplusRatio = 2.0,
            SettlementNeedDef needDef = null)
        {
            this.needId = needId;
            this.needDef = needDef;
            this.resource = resource;
            this.demanded = demanded;
            this.fulfilled = fulfilled;
            this.label = needDef?.label ?? label ?? needId;
            this.category = category;
            this.penalties = penalties;
            this.surplusBonuses = surplusBonuses;
            this.maxSurplusRatio = maxSurplusRatio;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref needId, "needId");
            Scribe_Defs.Look(ref needDef, "needDef");
            Scribe_Defs.Look(ref resource, "resource");
            Scribe_Values.Look(ref demanded, "demanded");
            Scribe_Values.Look(ref fulfilled, "fulfilled");
            Scribe_Values.Look(ref label, "label");
            Scribe_Values.Look(ref category, "category");
        }
    }
}
