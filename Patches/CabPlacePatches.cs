using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using Il2CppTLD.SteamTrain;
using UnityEngine;

namespace SteamRailway.Patches
{
    /// The game forbids dropping and placing items while the player is inside a steam train (SteamTrain.ActiveSteamTrain
    /// is set). In our locomotive items may be put down in the cab (CabItems keeps them with the train), so while one of
    /// these checks or actions runs the active train is hidden from the game and put back right after.
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
            foreach (var kv in names)
                foreach (var m in kv.Key.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                    if (m.DeclaringType == kv.Key && !m.IsGenericMethod
                        && kv.Value.Any(n => n.EndsWith("*") ? m.Name.StartsWith(n.TrimEnd('*')) : m.Name == n))
                        yield return m;
        }

        static int s_Depth;
        static SteamTrain s_Hidden;

        private static void Prefix(out bool __state)
        {
            __state = false;
            var t = SteamTrain.ActiveSteamTrain;
            if (s_Depth == 0 && (t == null || !RailwayManager.IsOurTrain(t))) return;
            if (s_Depth == 0) { s_Hidden = t; SteamTrain.ActiveSteamTrain = null; }
            s_Depth++;
            __state = true;
        }

        private static void Finalizer(bool __state)
        {
            if (!__state) return;
            if (--s_Depth == 0)
            {
                if (s_Hidden != null) SteamTrain.ActiveSteamTrain = s_Hidden;
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

    /// Placing needs the player's normal control mode; in the train it is InSteamTrain. While a placement starts in
    /// our cab the mode is switched to Normal, and back to InSteamTrain once the placement is over (CabPlace.Update).
    [HarmonyPatch]
    internal static class CabPlace_ControlMode
    {
        static IEnumerable<MethodBase> TargetMethods() =>
            typeof(PlayerManager).GetMethods().Where(m => m.DeclaringType == typeof(PlayerManager) && m.Name == "StartPlaceMesh");

        static bool s_Restore;

        private static void Prefix(out bool __state)
        {
            __state = false;
            var pm = GameManager.GetPlayerManagerComponent();
            var train = CabPlace_HideTrain.Current;
            if (pm == null || train == null || !RailwayManager.IsOurTrain(train)) return;
            if (pm.GetControlMode() != PlayerControlMode.InSteamTrain) return;
            pm.SetControlMode(PlayerControlMode.Normal);
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
            var pm = GameManager.GetPlayerManagerComponent();
            if (pm == null) { s_Restore = false; return; }
            if (pm.IsInMeshPlacementMode()) return;
            s_Restore = false;
            var train = SteamTrain.ActiveSteamTrain;
            if (train != null && RailwayManager.IsOurTrain(train) && pm.GetControlMode() == PlayerControlMode.Normal)
                pm.SetControlMode(PlayerControlMode.InSteamTrain);
        }
    }
}
