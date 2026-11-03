using HarmonyLib;

namespace FactionColonies.SupplyChain
{
    [HarmonyPatch(typeof(WorldSettlementFC))]
    [HarmonyPatch("IncreaseWorkers")]
    public static class Patch_IncreaseWorkers
    {
        public static void Postfix(WorldSettlementFC __instance)
        {
            SupplyChainCache.Comp?.DirtyFlowCache();
            WorldObjectComp_SettlementNeeds needsComp = SupplyChainCache.GetNeedsComp(__instance);
            if (needsComp != null)
                needsComp.RebuildNeedStates();
        }
    }
}
