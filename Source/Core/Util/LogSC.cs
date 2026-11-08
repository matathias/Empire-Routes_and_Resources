namespace FactionColonies.SupplyChain;

/// <summary>Routes & Resources logging, prefixed <c>[Empire-SupplyChain]</c>; debug messages follow the mod's debug setting.</summary>
public static class LogSC
{
    public readonly struct DebugGate : ISubmodLogGate
    {
        public static string Slug => "[Empire-SupplyChain]";
        public static bool Enabled => SupplyChainSettings.PrintDebug;
    }

    public static void Message(string message) => SubmodLog<DebugGate>.Message(message);
    public static void Message(ref DebugLogHandler<DebugGate> message) => SubmodLog<DebugGate>.Message(ref message);
    public static void MessageForce(string message) => SubmodLog<DebugGate>.MessageForce(message);
    public static void Warning(string message) => SubmodLog<DebugGate>.Warning(message);
    public static void Error(string message) => SubmodLog<DebugGate>.Error(message);
}
