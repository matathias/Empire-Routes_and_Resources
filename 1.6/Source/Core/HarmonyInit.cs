using System.Reflection;
using HarmonyLib;
using Verse;

namespace FactionColonies.SupplyChain
{
    [StaticConstructorOnStartup]
    public static class HarmonyInit
    {
        static HarmonyInit()
        {
            new Harmony("Empire.SupplyChain").PatchAll(Assembly.GetExecutingAssembly());
        }
    }

    [HarmonyPatch(typeof(Game), "Dispose")]
    static class Patch_GameDispose
    {
        static void Postfix()
        {
            SupplyChainCache.InvalidateCache();
            SupplyRouteModifierRegistry.ClearAll();
        }
    }
}
