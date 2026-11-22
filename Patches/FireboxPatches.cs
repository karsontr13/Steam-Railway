using HarmonyLib;
using Il2Cpp;
using Il2CppTLD.SteamTrain;
using UnityEngine;

namespace SteamRailway.Patches
{
    /// The firebox is the game's stove (hidden behind the fire hole); only its hover text is ours.
    [HarmonyPatch(typeof(FireplaceInteraction), nameof(FireplaceInteraction.GetHoverText))]
    internal static class WoodStove_GetHoverText
    {
        private static void Postfix(FireplaceInteraction __instance, ref string __result)
        {
            var train = __instance.GetComponentInParent<SteamTrain>();
            var simulation = train != null ? RailwayManager.GetSimulation(train) : null;
            if (simulation != null) __result = FireboxControl.HoverText(simulation);
        }
    }

    /// A burst boiler or a blown fusible plug (water in the firebox) keeps the fire out.
    [HarmonyPatch(typeof(FireplaceInteraction), nameof(FireplaceInteraction.PerformInteraction))]
    internal static class WoodStove_PerformInteraction
    {
        private static bool Prefix(FireplaceInteraction __instance, ref bool __result)
        {
            var train = __instance.GetComponentInParent<SteamTrain>();
            var simulation = train != null ? RailwayManager.GetSimulation(train) : null;
            if (simulation == null || simulation.FireBurning) return true;
            string error = null;
            if (simulation.State.Exploded) error = Localizer.Translate("Kazan patlamış: önce onarılmalı.", "The boiler has burst: repair it first.");
            else if (simulation.State.PlugBlown && SteamSimulation.Mode == Realism.Medium) error = Localizer.Translate("Eriyen tapa onarılmadan ocak yakılamaz.", "The fire can't be lit until the fusible plug is replaced.");
            if (error == null) return true;
            CabControls.Message(error);
            __result = false;
            return false;
        }
    }
}

namespace SteamRailway.Patches
{
    /// Once the fire is lit, the firebox is only fed with coal (lighting it the first time works with anything).
    [HarmonyPatch(typeof(Panel_FeedFire), nameof(Panel_FeedFire.FilterItemFuelSource))]
    internal static class FeedFire_CoalOnly
    {
        private static void Postfix(Panel_FeedFire __instance, GameObject go, ref bool __result)
        {
            if (!__result || go == null) return;
            var fireplace = __instance.m_FireplaceInteraction;
            var train = fireplace != null ? fireplace.GetComponentInParent<SteamTrain>() : null;
            if (train == null || !RailwayManager.IsOurTrain(train)) return;
            var gearItem = go.GetComponent<GearItem>();
            if (gearItem != null && gearItem.name.StartsWith("GEAR_Coal")) return;
            __result = false;
        }
    }
}

namespace SteamRailway.Patches
{
    /// The fuel list is built with direct calls that a filter cannot intercept, so the feeding itself is
    /// refused: with anything but coal selected the click only tells the player why.
    internal static class CoalRule
    {
        public static bool Refuses(Panel_FeedFire panel)
        {
            var fireplace = panel != null ? panel.m_FireplaceInteraction : null;
            var train = fireplace != null ? fireplace.GetComponentInParent<SteamTrain>() : null;
            if (train == null || !RailwayManager.IsOurTrain(train)) return false;
            var selectedItem = panel.GetSelectedFuelSource();
            if (selectedItem != null && selectedItem.name.StartsWith("GEAR_Coal")) return false;
            CabControls.Message(Localizer.Translate("Ocağa yalnızca kömür atılır.", "Only coal goes into the firebox."));
            return true;
        }
    }

    [HarmonyPatch(typeof(Panel_FeedFire), nameof(Panel_FeedFire.OnFeedFire))]
    internal static class FeedFire_OnFeedFire
    {
        private static bool Prefix(Panel_FeedFire __instance) => !CoalRule.Refuses(__instance);
    }

    [HarmonyPatch(typeof(Panel_FeedFire), nameof(Panel_FeedFire.OnDoAction))]
    internal static class FeedFire_OnDoAction
    {
        private static bool Prefix(Panel_FeedFire __instance) => !CoalRule.Refuses(__instance);
    }
}
