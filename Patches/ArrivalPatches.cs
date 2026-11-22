using HarmonyLib;
using Il2Cpp;

namespace SteamRailway.Patches
{
    /// While a region loads, the player controller does not run at the origin: no gravity, so no fall to land from.
    [HarmonyPatch(typeof(vp_FPSController), nameof(vp_FPSController.Update))]
    internal static class ArrivalHold_ControllerUpdate
    {
        private static bool Prefix(vp_FPSController __instance)
        {
            if (!ArrivalGuard.HoldingAtOrigin(__instance.transform)) return true;
            __instance.m_FallSpeed = 0f;
            __instance.m_LastFallSpeed = 0f;
            __instance.m_HighestFallSpeed = 0f;
            __instance.m_FallDistance = 0f;
            __instance.m_FallImpact = 0f;
            return false;
        }
    }
}
