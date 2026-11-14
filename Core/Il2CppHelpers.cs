using Il2CppAK.Wwise;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppTLD.IntBackedUnit;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace SteamRailway
{
    /// Small helpers for building Il2Cpp objects from code.
    internal static class H
    {
        public static Vector3 V(float x, float y, float z) => new Vector3(x, y, z);

        public static GameObject Child(Transform parent, string name, Vector3 localPos)
        {
            var targetObject = new GameObject(name);
            targetObject.transform.SetParent(parent, false);
            targetObject.transform.localPosition = localPos;
            return targetObject;
        }

        /// Placeholder geometry until the real locomotive model exists.
        public static GameObject Cube(Transform parent, string name, Vector3 localPos, Vector3 scale, Material material, bool keepCollider)
        {
            var targetObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            targetObject.name = name;
            targetObject.transform.SetParent(parent, false);
            targetObject.transform.localPosition = localPos;
            targetObject.transform.localScale = scale;
            if (material != null) targetObject.GetComponent<MeshRenderer>().sharedMaterial = material;
            if (!keepCollider) Object.DestroyImmediate(targetObject.GetComponent<BoxCollider>());
            return targetObject;
        }

        public static Il2Cpp.LocalizedString Loc(string identifier)
        {
            var localized = new Il2Cpp.LocalizedString();
            localized.m_LocalizationID = identifier;
            return localized;
        }

        // Empty Wwise handles: the train code calls into these unconditionally and throws on null.
        public static RTPC Rtpc() => new RTPC();
        public static State St() => new State();
        public static Il2CppAK.Wwise.Event Ev() => new Il2CppAK.Wwise.Event();

        public static ItemLiquidVolume Vol(long units)
        {
            var v = new ItemLiquidVolume();
            v.m_Units = units;
            return v;
        }

        public static Il2CppReferenceArray<T> Arr<T>(params T[] items) where T : Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase
            => new Il2CppReferenceArray<T>(items);

        /// Loads a base-game asset through its Addressables path (e.g. "Assets/Prefabs/...prefab").
        public static T LoadAsset<T>(string address) where T : Object
        {
            try
            {
                return Addressables.LoadAssetAsync<T>(address).WaitForCompletion();
            }
            catch (System.Exception e)
            {
                SteamRailwayMod.Log.Error($"Could not load '{address}': {e.Message}");
                return null;
            }
        }

        public static float TerrainHeight(Vector3 point, float fallback)
        {
            var terrains = Terrain.activeTerrains;
            for (int index = 0; index < terrains.Length; index++)
            {
                var terrain = terrains[index];
                var targetPoint = terrain.transform.position;
                var size = terrain.terrainData.size;
                if (point.x >= targetPoint.x && point.x <= targetPoint.x + size.x && point.z >= targetPoint.z && point.z <= targetPoint.z + size.z)
                    return terrain.SampleHeight(point) + targetPoint.y;
            }
            return fallback;
        }

        public static Vector3 GroundBelow(Vector3 samplePoint, float maxDrop = 20f)
        {
            if (Physics.Raycast(samplePoint + Vector3.up * 5f, Vector3.down, out RaycastHit hit, maxDrop + 5f))
                return hit.point + Vector3.up * 0.1f;
            return samplePoint;
        }
    }
}
