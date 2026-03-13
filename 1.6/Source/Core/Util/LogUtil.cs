using Verse;

namespace FactionColonies.SupplyChain
{
    public static class LogUtil
    {
        public const string Slug = "[Empire-SupplyChain]";

        public static void Message(string message)
        {
            if (SupplyChainSettings.PrintDebug)
                Log.Message(Slug + " " + message);
        }

        public static void Warning(string message)
        {
            Log.Warning(Slug + " " + message);
        }

        public static void Error(string message)
        {
            Log.Error(Slug + " " + message);
        }
    }
}
