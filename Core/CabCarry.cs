using Il2Cpp;
using Il2CppTLD.SteamTrain;
using UnityEngine;

namespace SteamRailway
{
    /// While a menu is open in the cab (feeding the firebox) or an item is being moved, the game stops carrying the
    /// player with the train and the view slides towards the back of a moving train. In those moments the player is
    /// kept at the same place relative to the cab.
    internal static class CabCarry
    {
        static Vector3 s_LocalPosition;
        static bool s_HasPosition;

        public static void Update(SteamTrain train)
        {
            var player = GameManager.GetPlayerObject();
            var playerManager = GameManager.GetPlayerManagerComponent();
            if (train == null || player == null || playerManager == null || !train.IsInsideTrain) { s_HasPosition = false; return; }

            Transform trainTransform = train.transform;
            if (!MenuOrPlacementActive(playerManager))
            {
                s_LocalPosition = trainTransform.InverseTransformPoint(player.transform.position);
                s_HasPosition = true;
                return;
            }
            if (!s_HasPosition) return;

            Vector3 target = trainTransform.TransformPoint(s_LocalPosition);
            Vector3 offset = target - player.transform.position;
            if (offset.sqrMagnitude < 0.0001f || offset.sqrMagnitude > 25f) return;
            var controller = player.GetComponent<CharacterController>();
            if (controller != null && controller.enabled) controller.Move(offset);
            else player.transform.position = target;
        }

        static bool MenuOrPlacementActive(PlayerManager playerManager)
        {
            if (playerManager.GetControlMode() != PlayerControlMode.InSteamTrain) return true;
            var feedPanel = InterfaceManager.GetPanel<Panel_FeedFire>();
            return feedPanel != null && feedPanel.IsEnabled();
        }
    }
}
