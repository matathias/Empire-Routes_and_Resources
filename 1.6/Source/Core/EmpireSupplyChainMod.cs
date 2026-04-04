using FactionColonies;
using HarmonyLib;
using System.Reflection;
using UnityEngine;
using Verse;

namespace FactionColonies.SupplyChain
{
    public class SupplyChainSettings : ModSettings
    {
        private static bool printDebug = false;
        public static bool PrintDebug => printDebug;

        public static SupplyChainMode mode = SupplyChainMode.Simple;
        public static float overflowPenaltyRate = 0.5f;
        public static double baseCapPerSettlement = 50.0;
        public static double routeDecayPerDay = 0.1;
        public static double localCapBase = 50.0;
        public static bool animateRouteArrows = false;
        public static bool useMaxWorkersForNeeds = false;

        private static string capBuffer = null;
        private static string routeDecayBuffer = null;
        private static string localCapBuffer = null;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref mode, "mode", SupplyChainMode.Simple);
            Scribe_Values.Look(ref printDebug, "printDebug", false);
            Scribe_Values.Look(ref overflowPenaltyRate, "overflowPenaltyRate", 0.5f);
            Scribe_Values.Look(ref baseCapPerSettlement, "baseCapPerSettlement", 50.0);
            Scribe_Values.Look(ref routeDecayPerDay, "routeDecayPerDay", 0.1);
            Scribe_Values.Look(ref localCapBase, "localCapBase", 50.0);
            Scribe_Values.Look(ref animateRouteArrows, "animateRouteArrows", false);
            Scribe_Values.Look(ref useMaxWorkersForNeeds, "useMaxWorkersForNeeds", false);
        }

        public void DoWindowContents(Rect inRect)
        {
            Listing_Standard ls = new Listing_Standard();
            ls.Begin(inRect);

            // Mode toggle
            string modeLabel = mode == SupplyChainMode.Simple ? "Simple" : "Complex";
            ls.Label("SC_SettingsMode".Translate(modeLabel));

            string buttonLabel = mode == SupplyChainMode.Simple
                ? "SC_SettingsSwitchComplex".Translate()
                : "SC_SettingsSwitchSimple".Translate();
            if (ls.ButtonText(buttonLabel))
            {
                SupplyChainMode newMode = mode == SupplyChainMode.Simple
                    ? SupplyChainMode.Complex : SupplyChainMode.Simple;
                mode = newMode;

                // If a world is loaded, apply the switch immediately
                if (Find.World != null)
                {
                    WorldComponent_SupplyChain wc = SupplyChainCache.Comp;
                    if (wc != null)
                        wc.SwitchMode(newMode);
                }
            }
            ls.Gap(12f);

            ls.CheckboxLabeled("SC_SettingsDebugLog".Translate(), ref printDebug);
            ls.Gap(12f);

            ls.CheckboxLabeled("SC_SettingsAnimateArrows".Translate(), ref animateRouteArrows);
            ls.Gap(12f);

            ls.CheckboxLabeled("SC_SettingsUseMaxWorkers".Translate(), ref useMaxWorkersForNeeds);
            ls.Gap(12f);

            ls.Label("SC_SettingsOverflowRate".Translate(
                overflowPenaltyRate.ToString("P0"),
                (FCSettings.silverPerResource * overflowPenaltyRate).ToString("F0")));
            overflowPenaltyRate = ls.Slider(overflowPenaltyRate, 0.1f, 1.0f);
            ls.Gap(12f);

            ls.Label("SC_SettingsBaseCap".Translate(baseCapPerSettlement.ToString("F0")));
            if (capBuffer == null)
                capBuffer = baseCapPerSettlement.ToString("F0");
            ls.TextFieldNumeric(ref baseCapPerSettlement, ref capBuffer, 10f, 500f);
            ls.Gap(12f);

            ls.Label("SC_SettingsRouteDecay".Translate(
                routeDecayPerDay.ToString("F2"),
                (1.0 / (1.0 + 5.0 * routeDecayPerDay) * 100).ToString("F0")));
            if (routeDecayBuffer == null)
                routeDecayBuffer = routeDecayPerDay.ToString("F2");
            ls.TextFieldNumeric(ref routeDecayPerDay, ref routeDecayBuffer, 0.01f, 1f);
            ls.Gap(12f);

            ls.Label("SC_SettingsLocalCap".Translate(localCapBase.ToString("F0")));
            if (localCapBuffer == null)
                localCapBuffer = localCapBase.ToString("F0");
            ls.TextFieldNumeric(ref localCapBase, ref localCapBuffer, 10f, 500f);

            ls.Gap(12f);
            if (ls.ButtonText("SC_OpenPatchNotes".Translate()))
                Find.WindowStack.Add(new PatchNotesDisplayWindow("matathias.empire.supplychain", "SC_PatchTitle".Translate()));

            ls.End();
        }
    }

    [StaticConstructorOnStartup]
    public static class SupplyChainStartup
    {
        static SupplyChainStartup()
        {
            new Harmony("com.Matathias.Empire.SupplyChain").PatchAll(Assembly.GetExecutingAssembly());
            EmpireCacheUtil.RegisterCacheInvalidator("SupplyChain", () =>
            {
                SupplyChainCache.InvalidateCache();
                SupplyRouteModifierRegistry.ClearAll();
            });
        }
    }

    public class EmpireSupplyChainMod : Mod
    {
        public SupplyChainSettings settings;

        public EmpireSupplyChainMod(ModContentPack content) : base(content)
        {
            settings = GetSettings<SupplyChainSettings>();
            
            string modVersion = content?.ModMetaData?.ModVersion;
            if (modVersion.NullOrEmpty())
            {
                LogSC.MessageForce("Did not load a mod version");
            }
            else
            {
                LogSC.MessageForce($"v{modVersion}");
            }
        }

        public override string SettingsCategory() => "SC_SettingsCategory".Translate();

        public override void DoSettingsWindowContents(Rect inRect) => settings.DoWindowContents(inRect);
    }
}
