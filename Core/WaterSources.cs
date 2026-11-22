using Il2Cpp;
using UnityEngine;

namespace SteamRailway
{
    /// Open water the steam pump can draw from: ice holes the player cut that are not frozen over, and flowing water.
    /// The pre-made holes inside fishing huts are not used.
    internal static class WaterSources
    {
        public const float HoseLength = 40f;
        const float MaxFrozen = 0.75f;
        const float RefreshSeconds = 10f;

        static IceFishingHole[] s_Holes = new IceFishingHole[0];
        static FlowingWater[] s_Flowing = new FlowingWater[0];
        static Collider[] s_FlowingColliders = new Collider[0];
        static float s_NextRefresh;

        public static float NearestDistance(Vector3 worldPosition)
        {
            if (Time.time >= s_NextRefresh)
            {
                s_NextRefresh = Time.time + RefreshSeconds;
                s_Holes = Object.FindObjectsOfType<IceFishingHole>();
                s_Flowing = Object.FindObjectsOfType<FlowingWater>();
                s_FlowingColliders = new Collider[s_Flowing.Length];
                for (int index = 0; index < s_Flowing.Length; index++)
                    s_FlowingColliders[index] = s_Flowing[index] != null ? s_Flowing[index].GetComponent<Collider>() : null;
            }

            float best = float.PositiveInfinity;
            foreach (var fishingHole in s_Holes)
            {
                if (fishingHole == null || !fishingHole.m_IsUserPlaced || fishingHole.NormalizedFrozen > MaxFrozen) continue;
                best = Mathf.Min(best, Vector3.Distance(worldPosition, fishingHole.transform.position));
            }
            for (int index = 0; index < s_Flowing.Length; index++)
            {
                var flowingWater = s_Flowing[index];
                if (flowingWater == null) continue;
                var collider = s_FlowingColliders[index];
                var point = collider != null ? collider.ClosestPoint(worldPosition) : flowingWater.transform.position;
                best = Mathf.Min(best, Vector3.Distance(worldPosition, point));
            }
            return best;
        }
    }
}
