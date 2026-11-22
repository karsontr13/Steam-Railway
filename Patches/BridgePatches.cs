using HarmonyLib;
using Il2Cpp;
using Il2CppTLD.Gear;

namespace SteamRailway.Patches
{
    /// The bridge repair stages are crafting blueprints that only the construction board's menu lists, one at a time.
    [HarmonyPatch(typeof(Panel_Crafting), nameof(Panel_Crafting.ItemPassesFilter))]
    internal static class BridgeStage_ItemPassesFilter
    {
        private static void Postfix(BlueprintData bpi, ref bool __result)
        {
            if (BridgeSite.MenuOpen) __result = BridgeSite.IsCurrentStage(bpi);
            else if (BridgeSite.StageOfBlueprint(bpi) >= 0) __result = false;
        }
    }

    /// The hour selector offers no more hours than the stage still needs.
    [HarmonyPatch(typeof(CraftingRequirementQuantitySelect), nameof(CraftingRequirementQuantitySelect.Enable))]
    internal static class BridgeStage_QuantityLimit
    {
        private static void Postfix(CraftingRequirementQuantitySelect __instance, BlueprintData bp)
        {
            if (__instance == null || !BridgeSite.IsCurrentStage(bp)) return;
            int remaining = BridgeSite.RemainingHours();
            if (remaining > 0 && __instance.m_Maximum > remaining) __instance.m_Maximum = remaining;
            if (__instance.m_Current > __instance.m_Maximum) __instance.m_Current = __instance.m_Maximum;
        }
    }
}

namespace SteamRailway.Patches
{
    /// The working hours of a stage begin: the work sound plays until the game's progress bar ends.
    [HarmonyLib.HarmonyPatch(typeof(Il2Cpp.Panel_Crafting), nameof(Il2Cpp.Panel_Crafting.CraftingStart))]
    internal static class BridgeStage_WorkSound
    {
        private static void Postfix(Il2Cpp.Panel_Crafting __instance)
        {
            if (__instance != null && BridgeSite.IsCurrentStage(__instance.SelectedBPI)) BridgeSite.StartWorkSound();
        }
    }
}

namespace SteamRailway.Patches
{
    /// One stage "item" is one hour of work whatever the tool: the tool's speed-up does not shorten the bridge work.
    [HarmonyLib.HarmonyPatch(typeof(Il2Cpp.Panel_Crafting), nameof(Il2Cpp.Panel_Crafting.GetModifiedCraftingDuration))]
    internal static class BridgeStage_Duration
    {
        private static void Postfix(Il2Cpp.Panel_Crafting __instance, int baseMinutes, ref int __result)
        {
            if (__instance != null && BridgeSite.IsCurrentStage(__instance.SelectedBPI)) __result = baseMinutes;
        }
    }
}
