using HarmonyLib;

namespace FactionColonies.SupplyChain
{
    [HarmonyPatch(typeof(CreateColonyWindowFc))]
    [HarmonyPatch("PostOpen")]
    public static class Patch_CreateColony_PostOpen
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
