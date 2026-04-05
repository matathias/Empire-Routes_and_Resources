using HarmonyLib;

namespace FactionColonies.SupplyChain
{
    [HarmonyPatch(typeof(CreateColonyWindowFc))]
    [HarmonyPatch("PreOpen")]
    public static class Patch_CreateColony_PreOpen
    {
        public static void Postfix()
        {
            FCWindow_FoundingSource.TryOpen();
        }
    }

    [HarmonyPatch(typeof(CreateColonyWindowFc))]
    [HarmonyPatch("PreClose")]
    public static class Patch_CreateColony_PreClose
    {
        public static void Postfix()
        {
            FCWindow_FoundingSource.TryClose();
        }
    }
}
