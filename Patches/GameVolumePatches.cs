using HarmonyLib;
using Il2Cpp;

namespace SteamRailway.Patches
{
    /// The audio options are applied at start-up and whenever a volume slider moves; the values are kept for the mod's
    /// own sounds (see GameVolume).
    [HarmonyPatch(typeof(Panel_OptionsMenu), nameof(Panel_OptionsMenu.ApplyVolumeSettings))]
    internal static class GameVolume_ApplyVolumeSettings
    {
        private static void Postfix(Panel_OptionsMenu __instance) => GameVolume.Read(__instance.State);
    }

    [HarmonyPatch(typeof(Panel_OptionsMenu), nameof(Panel_OptionsMenu.ApplyMasterVolume))]
    internal static class GameVolume_ApplyMasterVolume
    {
        private static void Postfix(Panel_OptionsMenu __instance) => GameVolume.Read(__instance.State);
    }

    [HarmonyPatch(typeof(Panel_OptionsMenu), nameof(Panel_OptionsMenu.ApplySoundVolume))]
    internal static class GameVolume_ApplySoundVolume
    {
        private static void Postfix(Panel_OptionsMenu __instance) => GameVolume.Read(__instance.State);
    }
}
