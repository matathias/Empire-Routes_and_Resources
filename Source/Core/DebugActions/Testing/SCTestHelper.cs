using System.Collections.Generic;

namespace FactionColonies.SupplyChain
{
    /// <summary>
    /// Shared helpers for the Routes & Resources tests: a snapshot/restore for the mutable
    /// <see cref="SupplyChainSettings"/> statics (so non-destructive formula tests can pin a known
    /// value and still leave settings as they found them), plus small fixture builders for the pure
    /// stockpile / resource tests.
    /// </summary>
    public static class SCTestHelper
    {
        /// <summary>Captured values of the settings statics that the tests may overwrite.</summary>
        public struct SettingsSnapshot
        {
            public float overflowPenaltyRate;
            public float routeDecayPerDay;
            public float resourceCostMultiplier;
            public float distanceNormalizingDays;
            public bool useMaxWorkersForNeeds;
        }

        public static SettingsSnapshot SnapshotSettings()
        {
            SettingsSnapshot s;
            s.overflowPenaltyRate = SupplyChainSettings.overflowPenaltyRate;
            s.routeDecayPerDay = SupplyChainSettings.routeDecayPerDay;
            s.resourceCostMultiplier = SupplyChainSettings.resourceCostMultiplier;
            s.distanceNormalizingDays = SupplyChainSettings.distanceNormalizingDays;
            s.useMaxWorkersForNeeds = SupplyChainSettings.useMaxWorkersForNeeds;
            return s;
        }

        public static void RestoreSettings(SettingsSnapshot s)
        {
            SupplyChainSettings.overflowPenaltyRate = s.overflowPenaltyRate;
            SupplyChainSettings.routeDecayPerDay = s.routeDecayPerDay;
            SupplyChainSettings.resourceCostMultiplier = s.resourceCostMultiplier;
            SupplyChainSettings.distanceNormalizingDays = s.distanceNormalizingDays;
            SupplyChainSettings.useMaxWorkersForNeeds = s.useMaxWorkersForNeeds;
        }

        /// <summary>
        /// A throwaway <see cref="ResourceTypeDef"/> usable as a stockpile/dictionary key in pure
        /// tests. Reference identity is all the tests rely on, so it never needs to be registered
        /// in the DefDatabase.
        /// </summary>
        public static ResourceTypeDef MakeResourceType(string defName)
        {
            return new ResourceTypeDef { defName = defName, label = defName };
        }

        /// <summary>Builds a <see cref="DictionaryStockpile"/> with a single resource seeded.</summary>
        public static DictionaryStockpile MakeStockpile(ResourceTypeDef resource, double amount, double cap)
        {
            var amounts = new Dictionary<ResourceTypeDef, double>();
            var caps = new Dictionary<ResourceTypeDef, double>();
            amounts[resource] = amount;
            caps[resource] = cap;
            return new DictionaryStockpile(amounts, caps);
        }

        /// <summary>Builds an empty <see cref="DictionaryStockpile"/> backed by fresh dictionaries.</summary>
        public static DictionaryStockpile MakeEmptyStockpile()
        {
            return new DictionaryStockpile(
                new Dictionary<ResourceTypeDef, double>(),
                new Dictionary<ResourceTypeDef, double>());
        }
    }
}
