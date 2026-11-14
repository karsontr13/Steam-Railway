using Il2Cpp;
using UnityEngine;

namespace SteamRailway
{
    /// Open water the steam pump can draw from: holes the player cut in the ice themselves (the game's own
    /// "place ice fishing hole" action) that are not frozen over, and flowing water volumes.
    /// The pre-made holes inside fishing huts are deliberately not used.
    internal static class WaterSources
    {
        public const float HoseLength = 40f;
        const float MaxFrozen = 0.75f;
        const float RefreshSeconds = 10f;

        static IceFishingHole[] s_Holes = new IceFishingHole[0];
        static FlowingWater[] s_Flowing = new FlowingWater[0];
        static float s_NextRefresh;

        public static float NearestDistance(Vector3 pos)
        {
            if (Time.time >= s_NextRefresh)
            {
                s_NextRefresh = Time.time + RefreshSeconds;
                s_Holes = Object.FindObjectsOfType<IceFishingHole>();
                s_Flowing = Object.FindObjectsOfType<FlowingWater>();
            }

            float best = float.PositiveInfinity;
            foreach (var fishingHole in s_Holes)
            {
                if (fishingHole == null || !fishingHole.m_IsUserPlaced || fishingHole.NormalizedFrozen > MaxFrozen) continue;
                best = Mathf.Min(best, Vector3.Distance(pos, fishingHole.transform.position));
            }
            foreach (var flowingWater in s_Flowing)
            {
                if (flowingWater == null) continue;
                var collider = flowingWater.GetComponent<Collider>();
                var point = collider != null ? collider.ClosestPoint(pos) : flowingWater.transform.position;
                best = Mathf.Min(best, Vector3.Distance(pos, point));
            }
            return best;
        }
    }
}
