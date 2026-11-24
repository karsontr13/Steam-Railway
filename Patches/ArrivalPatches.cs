using HarmonyLib;
using Il2Cpp;

namespace SteamRailway.Patches
{
    /// While the player is placed at a walking transition's end, the controller does not run: no fall to land from.
    [HarmonyPatch(typeof(vp_FPSController), nameof(vp_FPSController.Update))]
    internal static class ArrivalHold_ControllerUpdate
    {
        private static bool Prefix(vp_FPSController __instance)
        {
            if (!ArrivalGuard.Holding()) return true;
            __instance.m_FallSpeed = 0f;
            __instance.m_LastFallSpeed = 0f;
            __instance.m_HighestFallSpeed = 0f;
            __instance.m_FallDistance = 0f;
            __instance.m_FallImpact = 0f;
            return false;
        }
    }
}
