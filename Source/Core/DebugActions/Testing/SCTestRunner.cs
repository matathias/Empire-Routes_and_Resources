using LudeonTK;
using System.Reflection;

namespace FactionColonies.SupplyChain;

/// <summary>
/// Routes & Resources test runner: the debug-menu entry points for this mod's tests. Discovery, running and
/// reporting live in the base mod's <see cref="EmpireTestRunner"/>; this class only names the
/// suite (its assembly, the <see cref="LogSC"/> logger and its destructive-run warnings).
/// </summary>
public static class SCTestRunner
{
    private const string DebugCategory = "Empire Refactored: Routes & Resources";

    private static readonly EmpireTestSuite Suite = new(
        Assembly.GetExecutingAssembly(),
        "Routes & Resources",
        "DESTRUCTIVE Routes & Resources tests mutate live game state: they create transient "
            + "settlements, run tax-resolution cycles, and overwrite stockpile contents and "
            + "settlement need-states. They are NOT cleaned up afterward.",
        "Destructive tests left residue that is NOT auto-reverted: "
            + "transient settlements created, stockpile contents drawn/credited, and settlement "
            + "need-states overwritten. Reload your pre-test save to restore the prior state.",
        LogSC.Message, LogSC.Error, LogSC.MessageForce);

    /*-*-*- Standard (non-destructive) tier -*-*-*/

    [DebugAction(DebugCategory, "Run All Tests", allowedGameStates = AllowedGameStates.Invalid)]
    public static void RunAll() => EmpireTestRunner.RunTests(Suite, null, destructive: false);

    [DebugAction(DebugCategory, "Run Tests by Category", allowedGameStates = AllowedGameStates.Invalid)]
    public static void RunByCategory() => EmpireTestRunner.ShowCategoryMenu(Suite, destructive: false);

    /*-*-*- Destructive tier (save-first, mutates live state) -*-*-*/

    [DebugAction(DebugCategory, "Run Destructive Tests", allowedGameStates = AllowedGameStates.Invalid)]
    public static void RunAllDestructive() =>
        EmpireTestRunner.ConfirmDestructive(Suite, () => EmpireTestRunner.RunTests(Suite, null, destructive: true));

    [DebugAction(DebugCategory, "Run Destructive Tests by Category", allowedGameStates = AllowedGameStates.Invalid)]
    public static void RunDestructiveByCategory() =>
        EmpireTestRunner.ConfirmDestructive(Suite, () => EmpireTestRunner.ShowCategoryMenu(Suite, destructive: true));
}
