using System.Collections.Generic;
using Verse;

namespace FactionColonies.SupplyChain
{
    public class BuildingResourceInput
    {
        public ResourceTypeDef resource;
        public double amount;
    }

    /// <summary>
    /// DefModExtension on BuildingFCDef. Declares resource inputs the building
    /// consumes each tax period. Unmet inputs apply penalties from the penalties list.
    /// If penalties is null, a default small happiness penalty applies.
    /// </summary>
    public class BuildingNeedExtension : DefModExtension
    {
        public List<BuildingResourceInput> inputs;
        public List<NeedPenalty> penalties;

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string e in base.ConfigErrors())
                yield return e;
            if (inputs == null || inputs.Count == 0)
                yield return "BuildingNeedExtension has no inputs";
            else
            {
                for (int i = 0; i < inputs.Count; i++)
                {
                    if (inputs[i].resource == null)
                        yield return "BuildingNeedExtension input " + i + " has null resource";
                }
            }
        }
    }
}
