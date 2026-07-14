using FactionColonies;
using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Verse;

namespace FactionColonies.SupplyChain
{
    public class SupplyChainSettings : ModSettings
    {
        private static bool printDebug = false;
        public static bool PrintDebug => printDebug;

        private const float DEFAULT_DISTANCE_NORMALIZING_DAYS = 3f;

        public static SupplyChainMode mode = SupplyChainMode.Simple;
        public static float overflowPenaltyRate = 0.5f;
        public static int baseCapPerSettlement = 50;
        public static float routeDecayPerDay = 0.1f;
        public static int localCapBase = 50;
        public static bool animateRouteArrows = false;
        public static bool useDeliveryCaravans = false;
        public static bool useThreadedRouteComputation = true;
        public static bool useMaxWorkersForNeeds = false;
        public static int freeSettlementThreshold = 3;
        public static float distanceNormalizingDays = DEFAULT_DISTANCE_NORMALIZING_DAYS;
        public static int baseSilverSurcharge = (int)FCSettings.silverToCreateSettlement; //1000, as of 2026-04-05
        public static float resourceCostMultiplier = 1.0f;

        // Delivery frequency bounds are fixed; only the default (applied to newly created routes) is configurable.
        public const int minRouteFrequencyDays = 1;
        public const int maxRouteFrequencyDays = 60;
        public static int defaultRouteFrequencyDays = 5;

        private static string capBuffer = null;
        private static string routeDecayBuffer = null;
        private static string localCapBuffer = null;
        private static string thresholdBuffer = null;
        private static string distNormBuffer = null;
        private static string surchargeBuffer = null;
        private static string resourceCostMultBuffer = null;
        private static string routeFreqBuffer = null;

        // Per-resource starting stockpile amounts, keyed by ResourceTypeDef.defName. String keys
        // (not LookMode.Def): ModSettings load before the DefDatabase is populated, so Def keys
        // would fail to resolve. Resources absent from the map start at 0.
        private static Dictionary<string, int> startingResources = DefaultStartingResources();

        private static Dictionary<string, int> DefaultStartingResources() =>
            new Dictionary<string, int> { { "RTD_Food", 5 }, { "RTD_Logging", 2 }, { "RTD_Mining", 2 } };

        public static int GetStartingAmount(string defName) =>
            startingResources.TryGetValue(defName, out int v) ? v : 0;

        // Tabbed settings window state
        private static int settingsTab = 0;
        private static List<TabRecord> settingsTabs = new List<TabRecord>();
        private static Vector2 scrollGeneral, scrollFounding, scrollComplex;
        private static float heightGeneral, heightFounding, heightComplex;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref mode, "mode", SupplyChainMode.Simple);
            Scribe_Values.Look(ref printDebug, "printDebug", false);
            Scribe_Values.Look(ref overflowPenaltyRate, "overflowPenaltyRate", 0.5f);
            Scribe_Values.Look(ref baseCapPerSettlement, "baseCapPerSettlement", 50);
            Scribe_Values.Look(ref routeDecayPerDay, "routeDecayPerDay", 0.1f);
            Scribe_Values.Look(ref localCapBase, "localCapBase", 50);
            Scribe_Values.Look(ref animateRouteArrows, "animateRouteArrows", false);
            Scribe_Values.Look(ref useDeliveryCaravans, "useDeliveryCaravans", false);
            Scribe_Values.Look(ref useThreadedRouteComputation, "useThreadedRouteComputation", true);
            Scribe_Values.Look(ref useMaxWorkersForNeeds, "useMaxWorkersForNeeds", false);
            Scribe_Values.Look(ref freeSettlementThreshold, "freeSettlementThreshold", 3);
            Scribe_Values.Look(ref distanceNormalizingDays, "distanceNormalizingDays", DEFAULT_DISTANCE_NORMALIZING_DAYS);
            Scribe_Values.Look(ref baseSilverSurcharge, "baseSilverSurcharge", (int)FCSettings.silverToCreateSettlement);
            Scribe_Values.Look(ref resourceCostMultiplier, "resourceCostMultiplier", 1.0f);
            Scribe_Values.Look(ref defaultRouteFrequencyDays, "defaultRouteFrequencyDays", 5);

            Scribe_Collections.Look(ref startingResources, "startingResources", LookMode.Value, LookMode.Value);
            // Re-seed defaults only when the node is entirely absent (a config predating this
            // setting), so an explicit saved 0 is respected rather than reset to the default.
            if (Scribe.mode == LoadSaveMode.LoadingVars && startingResources == null)
                startingResources = DefaultStartingResources();
        }

        public void DoWindowContents(Rect inRect)
        {
            settingsTabs.Clear();
            settingsTabs.Add(new TabRecord("SC_SettingsTabGeneral".Translate(), delegate { settingsTab = 0; }, settingsTab == 0));
            settingsTabs.Add(new TabRecord("SC_SettingsTabFounding".Translate(), delegate { settingsTab = 1; }, settingsTab == 1));
            settingsTabs.Add(new TabRecord("SC_SettingsTabComplex".Translate(), delegate { settingsTab = 2; }, settingsTab == 2));

            Rect contentRect = new Rect(inRect.x, inRect.y + 40f, inRect.width, inRect.height - 40f);
            Widgets.DrawMenuSection(contentRect);
            TabDrawer.DrawTabs(contentRect, settingsTabs);

            Rect innerRect = contentRect.ContractedBy(5f);
            switch (settingsTab)
            {
                case 0: DoGeneralTab(innerRect); break;
                case 1: DoFoundingTab(innerRect); break;
                case 2: DoComplexTab(innerRect); break;
            }
        }

        /* General: mode toggle and settings that apply in both Simple and Complex mode. */
        private void DoGeneralTab(Rect rect)
        {
            Rect viewRect = ScrollUtil.BeginScrollView(rect, ref scrollGeneral, heightGeneral);
            Rect listRect = new Rect(viewRect.x, viewRect.y, viewRect.width, float.MaxValue);
            Listing_Standard ls = new Listing_Standard();
            ls.Begin(listRect);
            Listing_StandardExtensions.ResetRowStripe();

            ls.Label("SC_ModVersion".Translate(EmpireSupplyChainMod.GetModVersion()));
            ls.Gap(10f);

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

            ls.CheckboxLabeled("SC_SettingsUseMaxWorkers".Translate(), ref useMaxWorkersForNeeds);
            ls.Gap(12f);

            overflowPenaltyRate = ls.SliderTextField("SC_SettingsOverflowRate", "SC_SettingsOverflowRate".Translate(),
                overflowPenaltyRate, 0.1f, 1.0f, decimals: 2,
                tooltip: "SC_SettingsOverflowRateTip".Translate(FormulaUtil.OverflowSilver(1).ToString("0.##")));
            ls.Gap(12f);

            ls.Label("SC_SettingsBaseCap".Translate(baseCapPerSettlement.ToString("F0")));
            if (capBuffer == null)
                capBuffer = baseCapPerSettlement.ToString("F0");
            ls.TextFieldNumeric(ref baseCapPerSettlement, ref capBuffer, 10f, 500f);
            ls.Gap(12f);

            if (ls.ButtonText("SC_OpenPatchNotes".Translate()))
                Find.WindowStack.Add(new PatchNotesDisplayWindow("matathias.empire.supplychain", "SC_PatchTitle".Translate()));

            heightGeneral = ls.CurHeight + 12f;
            ls.End();
            ScrollUtil.EndScrollView();
        }

        /* Founding: settlement founding costs and per-resource starting stockpile amounts. */
        private void DoFoundingTab(Rect rect)
        {
            Rect viewRect = ScrollUtil.BeginScrollView(rect, ref scrollFounding, heightFounding);
            Rect listRect = new Rect(viewRect.x, viewRect.y, viewRect.width, float.MaxValue);
            Listing_Standard ls = new Listing_Standard();
            ls.Begin(listRect);
            Listing_StandardExtensions.ResetRowStripe();

            ls.Label("SC_SettingsFoundingThreshold".Translate(freeSettlementThreshold.ToString()));
            if (thresholdBuffer == null)
                thresholdBuffer = freeSettlementThreshold.ToString();
            ls.TextFieldNumeric(ref freeSettlementThreshold, ref thresholdBuffer, 0, 1000);
            ls.Gap(6f);

            ls.Label("SC_SettingsDistanceNorm".Translate(distanceNormalizingDays.ToString("F1")));
            if (distNormBuffer == null)
                distNormBuffer = distanceNormalizingDays.ToString("F1");
            ls.TextFieldNumeric(ref distanceNormalizingDays, ref distNormBuffer, 0.001f, 100f);
            ls.Gap(6f);

            ls.Label("SC_SettingsBaseSurcharge".Translate(baseSilverSurcharge.ToString()));
            if (surchargeBuffer == null)
                surchargeBuffer = baseSilverSurcharge.ToString();
            ls.TextFieldNumeric(ref baseSilverSurcharge, ref surchargeBuffer, 0, 100000);
            ls.Gap(6f);

            ls.Label("SC_SettingsResourceMultiplier".Translate(resourceCostMultiplier.ToString("F2")));
            if (resourceCostMultBuffer is null)
                resourceCostMultBuffer = resourceCostMultiplier.ToString("F2");
            ls.TextFieldNumeric(ref resourceCostMultiplier, ref resourceCostMultBuffer, 0.1f, 10f);
            ls.Gap(12f);

            // Per-resource starting stockpile amounts (one slider per stockpile-able resource).
            ls.GapLine(12f);
            ls.Label("SC_SettingsStartingResourcesHeader".Translate());
            ls.Gap(6f);
            foreach (ResourceTypeDef def in SupplyChainCache.AllResourceTypeDefs
                         .Where(d => !d.isPoolResource).OrderBy(d => d.uiPriority))
            {
                int cur = GetStartingAmount(def.defName);
                int nv = ls.SliderTextField("SC_StartRes_" + def.defName, def.LabelCap, cur, 0, 100);
                if (nv != cur)
                    startingResources[def.defName] = nv;
            }

            heightFounding = ls.CurHeight + 12f;
            ls.End();
            ScrollUtil.EndScrollView();
        }

        /* Complex Mode: settings that only take effect in Complex mode (routes, local caps). */
        private void DoComplexTab(Rect rect)
        {
            Rect viewRect = ScrollUtil.BeginScrollView(rect, ref scrollComplex, heightComplex);
            Rect listRect = new Rect(viewRect.x, viewRect.y, viewRect.width, float.MaxValue);
            Listing_Standard ls = new Listing_Standard();
            ls.Begin(listRect);
            Listing_StandardExtensions.ResetRowStripe();

            ls.CheckboxLabeled("SC_SettingsAnimateArrows".Translate(), ref animateRouteArrows);
            ls.Gap(12f);

            ls.CheckboxLabeled("SC_SettingsUseDeliveryCaravans".Translate(), ref useDeliveryCaravans,
                "SC_SettingsUseDeliveryCaravansTip".Translate());
            ls.Gap(12f);

            ls.CheckboxLabeled("SC_SettingsThreadedRoutes".Translate(), ref useThreadedRouteComputation,
                "SC_SettingsThreadedRoutesTip".Translate());
            ls.Gap(12f);

            ls.Label("SC_SettingsRouteDecay".Translate(
                routeDecayPerDay.ToString("F2"),
                (FormulaUtil.RouteEfficiency(5.0) * 100).ToString("F0")));
            if (routeDecayBuffer == null)
                routeDecayBuffer = routeDecayPerDay.ToString("F2");
            ls.TextFieldNumeric(ref routeDecayPerDay, ref routeDecayBuffer, 0.01f, 1f);
            ls.Gap(12f);

            ls.Label("SC_SettingsLocalCap".Translate(localCapBase.ToString("F0")));
            if (localCapBuffer == null)
                localCapBuffer = localCapBase.ToString("F0");
            ls.TextFieldNumeric(ref localCapBase, ref localCapBuffer, 10f, 500f);
            ls.Gap(12f);

            ls.Label("SC_SettingsDefaultRouteFreq".Translate(defaultRouteFrequencyDays.ToString()));
            if (routeFreqBuffer == null)
                routeFreqBuffer = defaultRouteFrequencyDays.ToString();
            ls.TextFieldNumeric(ref defaultRouteFrequencyDays, ref routeFreqBuffer, minRouteFrequencyDays, maxRouteFrequencyDays);

            heightComplex = ls.CurHeight + 12f;
            ls.End();
            ScrollUtil.EndScrollView();
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

        public static string GetModVersion()
        {
            var mod = LoadedModManager.GetMod<EmpireSupplyChainMod>();
            string version = mod?.Content?.ModMetaData?.ModVersion;
            return version.NullOrEmpty() ? "Unknown" : version;
        }

        public override string SettingsCategory() => "SC_SettingsCategory".Translate();

        public override void DoSettingsWindowContents(Rect inRect) => settings.DoWindowContents(inRect);
    }
}
