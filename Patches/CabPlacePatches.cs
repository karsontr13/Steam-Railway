using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using Il2CppTLD.SteamTrain;
using UnityEngine;

namespace SteamRailway.Patches
{
    /// The game forbids dropping and placing items while SteamTrain.ActiveSteamTrain is set; during these checks
    /// the active train is hidden from the game and put back right after.
    [HarmonyPatch]
    internal static class CabPlace_HideTrain
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            var names = new Dictionary<System.Type, string[]>
            {
                { typeof(ItemDescriptionPage), new[] { "CanDrop" } },
                { typeof(Panel_Inventory), new[] { "OnDrop", "OnPlace" } },
                { typeof(Panel_ActionsRadial), new[] { "CanPlaceFromRadial", "ShowPlaceItemRadial", "_ShowPlaceItemRadial_b__*" } },
                { typeof(InputManager), new[] { "ExecuteAltFire" } },                 // right click with an item in hand (lantern)
                { typeof(PlayerManager), new[] { "Drop", "CheckIfCanDropGearItem", "ItemInHandsPlaceable", "CanPlaceCurrentPlaceable",
                                                 "CanPlaceMeshImmediately", "StartPlaceMesh", "InitPlaceMeshMode", "UpdatePlaceMesh",
                                                 "AttemptToPlaceMesh", "AttemptToPlaceObject", "PlaceMeshInWorld",
                                                 "InteractiveObjectsProcessAltFire" } },   // right click on an item to move it
            };
            foreach (var pair in names)
                foreach (var method in pair.Key.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                    if (method.DeclaringType == pair.Key && !method.IsGenericMethod
                        && pair.Value.Any(name => name.EndsWith("*") ? method.Name.StartsWith(name.TrimEnd('*')) : method.Name == name))
                        yield return method;
        }

        static int s_Depth;
        static SteamTrain s_Hidden;

        private static void Prefix(out bool __state)
        {
            __state = false;
            var train = SteamTrain.ActiveSteamTrain;
            if (s_Depth == 0 && (train == null || !RailwayManager.IsOurTrain(train))) return;
            if (s_Depth == 0) { s_Hidden = train; SteamTrain.ActiveSteamTrain = null; }
            s_Depth++;
            __state = true;
        }

        private static void Finalizer(bool __state)
        {
            if (!__state) return;
            if (--s_Depth == 0)
            {
                if (s_Hidden != null)
                {
                    SteamTrain.ActiveSteamTrain = s_Hidden;
                    try { CabItems.ScanNow(s_Hidden.transform); } catch { }
                }
                s_Hidden = null;
            }
        }

        /// The train the player is in, also while it is hidden by the patch above.
        public static SteamTrain Current => s_Hidden != null ? s_Hidden : SteamTrain.ActiveSteamTrain;
    }

    [HarmonyPatch(typeof(Panel_ActionsRadial), nameof(Panel_ActionsRadial.ShowCannotPlaceItemInTrainMessage))]
    internal static class CabPlace_NoMessage
    {
        private static bool Prefix() => !RailwayManager.IsOurTrain(CabPlace_HideTrain.Current);
    }

    /// An item dropped in the cab lands on the cab floor at the player's feet (the game would let it fall through
    /// the moving train to the ground).
    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Drop))]
    internal static class CabPlace_DropOnFloor
    {
        private static void Postfix(GameObject go)
        {
            var train = CabPlace_HideTrain.Current;
            if (go == null || train == null || !RailwayManager.IsOurTrain(train) || train.m_CameraDummy == null) return;
            CabItems.PutOnFloor(train.transform, go, train.m_CameraDummy.position);
        }
    }

    /// Brings the cab's colliders up to date before the placement ray, so at speed the item lands in the cab
    /// instead of behind the train.
    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.UpdatePlaceMesh))]
    internal static class CabPlace_SyncColliders
    {
        private static void Prefix()
        {
            var train = CabPlace_HideTrain.Current;
            if (train != null && RailwayManager.IsOurTrain(train)) Physics.SyncTransforms();
        }

        private static void Postfix(PlayerManager __instance)
        {
            var train = CabPlace_HideTrain.Current;
            if (train == null || !RailwayManager.IsOurTrain(train)) return;
            CabItems.RememberPlacement(train.transform, __instance.m_ObjectToPlace);
        }
    }

    /// Placing needs the player's normal control mode; in the train it is InSteamTrain. While a placement starts in
    /// our cab the mode is switched to Normal, and back to InSteamTrain once the placement is over (CabPlace.Update).
    [HarmonyPatch]
    internal static class CabPlace_ControlMode
    {
        static IEnumerable<MethodBase> TargetMethods() =>
            typeof(PlayerManager).GetMethods().Where(method => method.DeclaringType == typeof(PlayerManager) && method.Name == "StartPlaceMesh");

        static bool s_Restore;

        private static void Prefix(out bool __state)
        {
            __state = false;
            var playerManager = GameManager.GetPlayerManagerComponent();
            var train = CabPlace_HideTrain.Current;
            if (playerManager == null || train == null || !RailwayManager.IsOurTrain(train)) return;
            if (playerManager.GetControlMode() != PlayerControlMode.InSteamTrain) return;
            playerManager.SetControlMode(PlayerControlMode.Normal);
            __state = true;
        }

        private static void Postfix(bool __state, bool __result)
        {
            if (!__state) return;
            if (__result) s_Restore = true;
            else GameManager.GetPlayerManagerComponent()?.SetControlMode(PlayerControlMode.InSteamTrain);
        }

        /// Called every frame: puts the player back into the train's control mode when the placement is over.
        public static void Update()
        {
            if (!s_Restore) return;
            var playerManager = GameManager.GetPlayerManagerComponent();
            if (playerManager == null) { s_Restore = false; return; }
            if (playerManager.IsInMeshPlacementMode()) return;
            s_Restore = false;
            var train = SteamTrain.ActiveSteamTrain;
            if (train != null && RailwayManager.IsOurTrain(train) && playerManager.GetControlMode() == PlayerControlMode.Normal)
                playerManager.SetControlMode(PlayerControlMode.InSteamTrain);
        }
    }
}
