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
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            return go;
        }

        /// Placeholder geometry until the real locomotive model exists.
        public static GameObject Cube(Transform parent, string name, Vector3 localPos, Vector3 scale, Material mat, bool keepCollider)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = scale;
            if (mat != null) go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            if (!keepCollider) Object.DestroyImmediate(go.GetComponent<BoxCollider>());
            return go;
        }

        public static Il2Cpp.LocalizedString Loc(string id)
        {
            var l = new Il2Cpp.LocalizedString();
            l.m_LocalizationID = id;
            return l;
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

        public static float TerrainHeight(Vector3 p, float fallback)
        {
            var terrains = Terrain.activeTerrains;
            for (int i = 0; i < terrains.Length; i++)
            {
                var t = terrains[i];
                var tp = t.transform.position;
                var size = t.terrainData.size;
                if (p.x >= tp.x && p.x <= tp.x + size.x && p.z >= tp.z && p.z <= tp.z + size.z)
                    return t.SampleHeight(p) + tp.y;
            }
            return fallback;
        }

        public static Vector3 GroundBelow(Vector3 p, float maxDrop = 20f)
        {
            if (Physics.Raycast(p + Vector3.up * 5f, Vector3.down, out RaycastHit hit, maxDrop + 5f))
                return hit.point + Vector3.up * 0.1f;
            return p;
        }
    }
}
