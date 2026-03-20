using System.Collections.Generic;
using Verse;

namespace FactionColonies.SupplyChain
{
    /// <summary>
    /// Tracks the satisfaction of a single need for one settlement.
    /// Persisted on WorldObjectComp_SupplyChain.
    /// </summary>
    public class NeedState : IExposable
    {
        public string needId;
        public ResourceTypeDef resource;
        public double demanded;
        public double fulfilled;

        /// <summary>
        /// Penalties for comp-provided needs (via INeedProvider). Null for def-based needs
        /// (which get penalties from the SettlementNeedDef itself).
        /// Not serialized — recomputed each tax resolution.
        /// </summary>
        public List<NeedPenalty> penalties;

        /// <summary>
        /// Display label for comp-provided needs. Null for def-based needs
        /// (which use SettlementNeedDef.label). Not serialized.
        /// </summary>
        public string label;

        public float Satisfaction
        {
            get { return demanded > 0 ? (float)(fulfilled / demanded) : 1f; }
        }

        public NeedState()
        {
        }

        public NeedState(string needId, ResourceTypeDef resource, double demanded, double fulfilled)
        {
            this.needId = needId;
            this.resource = resource;
            this.demanded = demanded;
            this.fulfilled = fulfilled;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref needId, "needId");
            Scribe_Defs.Look(ref resource, "resource");
            Scribe_Values.Look(ref demanded, "demanded");
            Scribe_Values.Look(ref fulfilled, "fulfilled");
        }
    }
}
