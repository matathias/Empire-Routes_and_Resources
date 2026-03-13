using System.Collections.Generic;
using Verse;

namespace FactionColonies.SupplyChain
{
    public class BuildingResourceInput
    {
        public ResourceTypeDef resource;
        public double amount;
    }

    public class BuildingCapBonus
    {
        public ResourceTypeDef resource;
        public double amount;
    }

    /// <summary>
    /// DefModExtension on BuildingFCDef. Declares resource inputs the building
    /// consumes each tax period, and/or stockpile cap bonuses the building provides.
    /// Unmet inputs apply penalties from the penalties list.
    /// If penalties is null, a default small happiness penalty applies.
    /// </summary>
    public class BuildingNeedExtension : DefModExtension
    {
        public List<BuildingResourceInput> inputs;
        public List<NeedPenalty> penalties;
        public List<BuildingCapBonus> capBonuses;

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string e in base.ConfigErrors())
                yield return e;
            if ((inputs == null || inputs.Count == 0) && (capBonuses == null || capBonuses.Count == 0))
                yield return "BuildingNeedExtension has no inputs and no capBonuses";
            if (inputs != null)
            {
                for (int i = 0; i < inputs.Count; i++)
                {
                    if (inputs[i].resource == null)
                        yield return "BuildingNeedExtension input " + i + " has null resource";
                }
            }
            if (capBonuses != null)
            {
                for (int i = 0; i < capBonuses.Count; i++)
                {
                    if (capBonuses[i].resource == null)
                        yield return "BuildingNeedExtension capBonus " + i + " has null resource";
                }
            }
        }
    }
}
