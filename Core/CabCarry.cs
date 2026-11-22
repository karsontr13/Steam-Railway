using Il2Cpp;
using Il2CppTLD.SteamTrain;
using UnityEngine;

namespace SteamRailway
{
    /// While a cab menu is open or an item is being moved, the game stops carrying the player with the train.
    /// In those moments the player is kept at the same place relative to the cab.
    internal static class CabCarry
    {
        const float MinimumCorrectionSquaredDistance = 0.0001f;
        const float MaximumCorrectionSquaredDistance = 25f;

        static Vector3 s_LocalPosition;
        static bool s_HasPosition;
        static GameObject s_ControllerOwner;
        static CharacterController s_Controller;

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
            if (offset.sqrMagnitude < MinimumCorrectionSquaredDistance || offset.sqrMagnitude > MaximumCorrectionSquaredDistance) return;
            if (s_ControllerOwner != player)
            {
                s_ControllerOwner = player;
                s_Controller = player.GetComponent<CharacterController>();
            }
            if (s_Controller != null && s_Controller.enabled) s_Controller.Move(offset);
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
