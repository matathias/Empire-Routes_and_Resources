using UnityEngine;
using Verse;

namespace FactionColonies.SupplyChain
{
    public class SupplyChainSettings : ModSettings
    {
        private static bool printDebug = false;
        public static bool PrintDebug => printDebug;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref printDebug, "printDebug", false);
        }

        public void DoWindowContents(Rect inRect)
        {
            Listing_Standard ls = new Listing_Standard();
            ls.Begin(inRect);
            ls.CheckboxLabeled("Enable debug logging", ref printDebug);
            ls.End();
        }
    }

    public class EmpireSupplyChainMod : Mod
    {
        public SupplyChainSettings settings;

        public EmpireSupplyChainMod(ModContentPack content) : base(content)
        {
            settings = GetSettings<SupplyChainSettings>();
        }

        public override string SettingsCategory() => "Empire - Supply Chain";

        public override void DoSettingsWindowContents(Rect inRect) => settings.DoWindowContents(inRect);
    }
}
