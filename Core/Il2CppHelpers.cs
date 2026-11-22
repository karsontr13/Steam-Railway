using Il2CppAK.Wwise;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppTLD.IntBackedUnit;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace SteamRailway
{
    /// Small helpers for building Il2Cpp objects from code.
    internal static class Il2CppHelpers
    {
        const float DefaultMaxDropInMeters = 20f;
        const float RayMarginInMeters = 5f;
        const float GroundLiftInMeters = 0.1f;
        public static Vector3 CreateVector(float x, float y, float z) => new Vector3(x, y, z);

        public static GameObject Child(Transform parent, string name, Vector3 localPosition)
        {
            var targetObject = new GameObject(name);
            targetObject.transform.SetParent(parent, false);
            targetObject.transform.localPosition = localPosition;
            return targetObject;
        }

        /// Placeholder geometry until the real locomotive model exists.
        public static GameObject Cube(Transform parent, string name, Vector3 localPosition, Vector3 scale, Material material, bool keepCollider)
        {
            var targetObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            targetObject.name = name;
            targetObject.transform.SetParent(parent, false);
            targetObject.transform.localPosition = localPosition;
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
        public static State CreateWwiseState() => new State();
        public static Il2CppAK.Wwise.Event CreateWwiseEvent() => new Il2CppAK.Wwise.Event();

        public static ItemLiquidVolume Vol(long units)
        {
            var volume = new ItemLiquidVolume();
            volume.m_Units = units;
            return volume;
        }

        public static Il2CppReferenceArray<ElementType> Arr<ElementType>(params ElementType[] items) where ElementType : Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase
            => new Il2CppReferenceArray<ElementType>(items);

        /// Loads a base-game asset through its Addressables path (e.g. "Assets/Prefabs/...prefab").
        public static AssetType LoadAsset<AssetType>(string address) where AssetType : Object
        {
            try
            {
                return Addressables.LoadAssetAsync<AssetType>(address).WaitForCompletion();
            }
            catch (System.Exception exception)
            {
                SteamRailwayMod.Log.Error($"Could not load '{address}': {exception.Message}");
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

        public static Vector3 GroundBelow(Vector3 samplePoint, float maxDrop = DefaultMaxDropInMeters)
        {
            if (Physics.Raycast(samplePoint + Vector3.up * RayMarginInMeters, Vector3.down, out RaycastHit raycastHit, maxDrop + RayMarginInMeters))
                return raycastHit.point + Vector3.up * GroundLiftInMeters;
            return samplePoint;
        }
    }
}
