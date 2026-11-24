using UnityEngine;

namespace SteamRailway
{
    /// After walking through a rail transition, the player waits wherever the region loaded them until they are put at the
    /// line's end; left to run they would fall and land with a sprain. They are held still only for that placement.
    internal static class ArrivalGuard
    {
        /// The hold never outlasts this, whatever happens to the placement.
        const float MaximumHoldInSeconds = 15f;

        static bool s_Holding;
        static float s_HeldAt;

        public static void Begin()
        {
            s_Holding = true;
            s_HeldAt = Time.realtimeSinceStartup;
        }

        public static void End() => s_Holding = false;

        /// The player is being placed at a walking transition's end (the controller must not move them).
        public static bool Holding()
        {
            if (!s_Holding) return false;
            if (Time.realtimeSinceStartup - s_HeldAt > MaximumHoldInSeconds) s_Holding = false;
            return s_Holding;
        }
    }
}
