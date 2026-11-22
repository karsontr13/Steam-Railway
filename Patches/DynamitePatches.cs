using HarmonyLib;
using Il2Cpp;

namespace SteamRailway.Patches
{
    /// The detonator box is set down with the game's own placement mode. The click that would set it down is refused
    /// (with a message) when the box is not 12-30 m from the charges.
    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.AttemptToPlaceMesh))]
    internal static class DetonatorBox_AttemptToPlace
    {
        private static bool Prefix(PlayerManager __instance) => Dynamite.AllowPlacement(__instance.m_ObjectToPlace);
    }
}
