using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using Il2CppTLD.SteamTrain;

namespace SteamRailway.Patches
{
    /// Our trains have no real Addressables prefab, so the game must never save them itself:
    /// on load it would try to instantiate them from that (non-existent) asset.
    [HarmonyPatch(typeof(TrainCar), nameof(TrainCar.SerializeScene))]
    internal static class TrainCar_SerializeScene
    {
        private static void Postfix(Il2CppSystem.Collections.Generic.List<SavedTrainCarState> __result)
        {
            if (__result == null) return;
            for (int index = __result.Count - 1; index >= 0; index--)
            {
                var savedState = __result[index];
                if (savedState != null && RailwayManager.IsOurGuid(savedState.m_Guid)) __result.RemoveAt(index);
            }
        }
    }

    /// Instead RailwayManager writes them into the save slot through ModData, just before the slot is written.
    /// Both overloads are patched: the game calls WriteSlotToDisk(SlotData, Timestamp) when it autosaves or quits.
    [HarmonyPatch]
    internal static class SaveGameSlots_WriteSlotToDisk
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            return typeof(SaveGameSlots).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method => method.Name == nameof(SaveGameSlots.WriteSlotToDisk));
        }

        private static void Prefix()
        {
            try { RailwayManager.WriteSave(); }
            catch (System.Exception exception) { SteamRailwayMod.Log.Error($"Saving railway state failed: {exception}"); }
        }
    }

    /// Capture train state before leaving a region (the periodic capture may be up to two seconds old).
    [HarmonyPatch(typeof(LoadScene), nameof(LoadScene.PerformSceneLoad))]
    internal static class LoadScene_PerformSceneLoad
    {
        private static void Prefix() => RailwayManager.OnPerformSceneLoad();
    }

    /// A new game: the railway starts from nothing (one brand new locomotive).
    [HarmonyPatch(typeof(SaveGameSystem), nameof(SaveGameSystem.GenerateNewSaveName))]
    internal static class SaveGameSystem_GenerateNewSaveName
    {
        private static void Postfix() => RailwayManager.OnNewGame();
    }
}

namespace SteamRailway.Patches
{
    /// Our trains cross region transitions through RailwayManager (the game's own train transition needs
    /// data from the stock train that ours do not have).
    [HarmonyPatch(typeof(LoadScene), nameof(LoadScene.HandleTrainCarImpact))]
    internal static class LoadScene_HandleTrainCarImpact
    {
        private static bool Prefix(LoadScene __instance, TrainCar trainCar)
        {
            if (!RailwayManager.IsOurTrain(trainCar)) return true;
            try { RailwayManager.TrainHitTransition(trainCar, __instance); }
            catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"Train at a transition: {exception.Message}"); }
            return false;
        }
    }

    /// Riding our train into a transition takes the train along (instead of the game moving only the player).
    [HarmonyPatch(typeof(LoadScene), nameof(LoadScene.OnTriggerEnter))]
    internal static class LoadScene_OnTriggerEnter
    {
        private static bool Prefix(LoadScene __instance, UnityEngine.Collider other) =>
            RailwayManager.AllowTransitionTrigger(__instance, other);
    }

    /// Items lying in the cab are saved with the train, not with the region (no copy stays where the train stood).
    [HarmonyPatch(typeof(GearManager), nameof(GearManager.Serialize))]
    internal static class GearManager_Serialize
    {
        private static void Prefix(out System.Collections.Generic.List<GearItem> __state) => __state = CabItems.HideFromRegionSave();
        private static void Postfix(System.Collections.Generic.List<GearItem> __state) => CabItems.Unhide(__state);
    }
}
