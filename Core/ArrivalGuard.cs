using Il2Cpp;
using UnityEngine;

namespace SteamRailway
{
    /// While a region loads, the player stands at the world's origin until the game puts them at the arrival point; left
    /// to run there they fall freely and land on arrival with a sprain. They are held still until they arrive.
    internal static class ArrivalGuard
    {
        /// The player stands further from the origin than this once the game has put them at the arrival point.
        const float OriginRadiusInMeters = 5f;
        /// A region whose arrival point lies at the origin: the hold ends this long after the load anyway.
        const float MaximumHoldInSeconds = 60f;

        static bool s_Waiting;
        static float s_LoadedAt;

        public static void SceneLoaded()
        {
            s_Waiting = true;
            s_LoadedAt = Time.realtimeSinceStartup;
        }

        /// The player still waits at the origin for the arrival (the controller must not move them).
        public static bool HoldingAtOrigin(Transform player)
        {
            if (!s_Waiting) return false;
            if (player.position.sqrMagnitude > OriginRadiusInMeters * OriginRadiusInMeters || Time.realtimeSinceStartup - s_LoadedAt > MaximumHoldInSeconds)
            {
                s_Waiting = false;
                return false;
            }
            return true;
        }
    }
}
