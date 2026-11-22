using HarmonyLib;
using Il2CppTLD.Interactions;

namespace SteamRailway.Patches
{
    /// Our switch stands use the game's plain hold interaction; when the hold completes the switch is thrown.
    /// The blasting-charge zones over a rockfall are the same kind of hold: completing one places the charges.
    [HarmonyPatch(typeof(TimedHoldInteraction), nameof(TimedHoldInteraction.PerformHold))]
    internal static class SwitchStand_PerformHold
    {
        private static bool Prefix(TimedHoldInteraction __instance, ref bool __result)
        {
            if (__instance != null && Wagons.IsCouplingZone(__instance.gameObject))
            {
                __result = Wagons.ToggleCoupling();
                return false;
            }
            if (__instance != null && BridgeSite.IsZone(__instance.gameObject))
            {
                __result = BridgeSite.Perform();
                return false;
            }
            if (__instance != null && Dynamite.IsZone(__instance.gameObject))
            {
                __result = Dynamite.Place(__instance.gameObject);
                return false;
            }
            if (__instance == null || !Switches.IsStand(__instance.gameObject)) return true;
            __result = Switches.Throw(__instance.gameObject);
            return false;
        }
    }
}
