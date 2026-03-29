using System.Collections.Generic;
using System.Linq;
using UnityEngine;
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
    public class BuildingNeedExtension : DefModExtension, IBuildingDetailSection
    {
        public List<BuildingResourceInput> inputs;
        public List<NeedPenalty> penalties;
        public List<BuildingCapBonus> capBonuses;

        private bool hasInputs => inputs?.Count > 0;
        private bool hasCaps => capBonuses?.Count > 0;
        private bool hasBoth => hasInputs && hasCaps;

        public string SectionLabel
        {
            get
            {
                if (hasBoth) return "SC_BuildingSupplyChain".Translate();
                if (hasCaps) return "SC_BuildingStockpileBonuses".Translate();
                return "SC_BuildingNeeds".Translate();
            }
        }

        public float GetSectionHeight(BuildingFCDef def, float width)
        {
            int rows = (inputs?.Count ?? 0) + (capBonuses?.Count ?? 0);
            if (rows == 0) return 0f;
            float h = rows * 22f;
            if (hasBoth) h += 18f * 2; // two sub-headers
            return h;
        }

        public void DrawSection(BuildingFCDef def, Rect contentRect)
        {
            float curY = contentRect.y;

            if (hasInputs)
            {
                if (hasBoth)
                {
                    Text.Font = GameFont.Tiny;
                    Text.Anchor = TextAnchor.MiddleLeft;
                    GUI.color = new Color(1f, 1f, 1f, 0.6f);
                    Widgets.Label(new Rect(contentRect.x, curY, contentRect.width, 18f), "SC_BuildingNeeds".Translate());
                    GUI.color = Color.white;
                    curY += 18f;
                }

                Text.Font = GameFont.Small;
                Text.Anchor = TextAnchor.MiddleLeft;
                foreach (BuildingResourceInput input in inputs)
                {
                    Widgets.DrawBoxSolid(new Rect(contentRect.x, curY, 3f, 22f), input.resource.color);
                    float x = contentRect.x + 3f + 4f;
                    GUI.DrawTexture(new Rect(x, curY + 1f, 20f, 20f), input.resource.Icon);
                    Rect labelRect = new Rect(x + 24f, curY, contentRect.width - 31f, 22f);
                    Widgets.Label(labelRect, "SC_BuildingNeedInput".Translate(input.amount.ToString("0.##"), input.resource.LabelCap));
                    curY += 22f;
                }
            }

            if (hasCaps)
            {
                if (hasBoth)
                {
                    Text.Font = GameFont.Tiny;
                    Text.Anchor = TextAnchor.MiddleLeft;
                    GUI.color = new Color(1f, 1f, 1f, 0.6f);
                    Widgets.Label(new Rect(contentRect.x, curY, contentRect.width, 18f), "SC_BuildingStockpileBonuses".Translate());
                    GUI.color = Color.white;
                    curY += 18f;
                }

                Text.Font = GameFont.Small;
                Text.Anchor = TextAnchor.MiddleLeft;
                foreach (BuildingCapBonus bonus in capBonuses)
                {
                    Widgets.DrawBoxSolid(new Rect(contentRect.x, curY, 3f, 22f), bonus.resource.color);
                    float x = contentRect.x + 3f + 4f;
                    GUI.DrawTexture(new Rect(x, curY + 1f, 20f, 20f), bonus.resource.Icon);
                    Rect labelRect = new Rect(x + 24f, curY, contentRect.width - 31f, 22f);
                    GUI.color = Color.green;
                    Widgets.Label(labelRect, "SC_BuildingCapBonus".Translate(bonus.amount.ToString("0.##"), bonus.resource.LabelCap));
                    GUI.color = Color.white;
                    curY += 22f;
                }
            }
        }

        public string GetCardDescription(BuildingFCDef def)
        {
            List<string> parts = new List<string>();
            if (hasInputs)
            {
                string needStr = string.Join(", ", inputs.Select(i => i.amount.ToString("0.##") + " " + i.resource.LabelCap));
                parts.Add("SC_BuildingNeedCardNeeds".Translate(needStr));
            }
            if (hasCaps)
            {
                string capStr = string.Join(", ", capBonuses.Select(b => "+" + b.amount.ToString("0.##") + " " + b.resource.LabelCap));
                parts.Add("SC_BuildingNeedCardCap".Translate(capStr));
            }
            return parts.Count > 0 ? string.Join("\n", parts) : null;
        }

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
