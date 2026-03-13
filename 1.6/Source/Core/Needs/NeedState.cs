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
