using HarmonyLib;
using Il2CppTLD.Interactions;

namespace SteamRailway.Patches
{
    /// Our switch stands use the game's plain hold interaction; when the hold completes the switch is thrown.
    [HarmonyPatch(typeof(TimedHoldInteraction), nameof(TimedHoldInteraction.PerformHold))]
    internal static class SwitchStand_PerformHold
    {
        private static bool Prefix(TimedHoldInteraction __instance, ref bool __result)
        {
            if (__instance == null || !Switches.IsStand(__instance.gameObject)) return true;
            __result = Switches.Throw(__instance.gameObject);
            return false;
        }
    }
}
