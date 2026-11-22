using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using UnityEngine;

namespace SteamRailway
{
    internal sealed class TunnelData
    {
        public string Source { get; set; }       // the region's tunnel object copied (by name)
        [JsonPropertyName("SourcePos")] public float[] SourcePosition { get; set; }    // where that object stands (several may share a name)
        [JsonPropertyName("Pos")] public float[] Position { get; set; }
        [JsonPropertyName("Rot")] public float[] Rotation { get; set; }
    }

    internal sealed class TunnelFile
    {
        public int Version { get; set; } = 1;
        public List<TunnelData> Tunnels { get; set; } = new List<TunnelData>();
    }

    /// Tunnel mouths placed with the track editor: copies of a tunnel the region has, kept in Mods\SteamRailway\Tunnels\<region>.json.
    /// The copy's own rails are hidden and a dark screen just inside covers the opening.
    internal static class Tunnels
    {
        const int QuaternionComponentCount = 4;
        const int QuaternionLastComponentIndex = 3;
        const float PlugCenterHeightInMeters = 4f;
        const float PlugCenterDepthInMeters = 1.5f;
        const float PlugSizeInMeters = 9f;
        const float PlugThicknessInMeters = 0.3f;
        const float MouthDepth = 8f;      // m from the tunnel object's origin to the front of its mouth
        static readonly JsonSerializerOptions s_Json = new JsonSerializerOptions { WriteIndented = true };
        static readonly List<GameObject> s_Live = new List<GameObject>();
        static string s_Scene;
        static TunnelFile s_File = new TunnelFile();

        static string FilePath(string scene) => Path.Combine(RouteStore.FolderPath, "Tunnels", scene + ".json");

        static TunnelFile For(string scene)
        {
            if (scene == s_Scene) return s_File;
            s_Scene = scene;
            s_File = new TunnelFile();
            var path = FilePath(scene);
            if (File.Exists(path))
            {
                try { s_File = JsonSerializer.Deserialize<TunnelFile>(File.ReadAllText(path), s_Json) ?? new TunnelFile(); }
                catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"{path} could not be read: {exception.Message}"); }
            }
            if (s_File.Tunnels == null) s_File.Tunnels = new List<TunnelData>();
            return s_File;
        }

        static void Save()
        {
            if (s_Scene == null) return;
            try
            {
                var path = FilePath(s_Scene);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonSerializer.Serialize(s_File, s_Json));
            }
            catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"Writing the tunnels failed: {exception.Message}"); }
        }

        /// The region's tunnels (again after a rebuild).
        public static void ApplyScene(string scene)
        {
            foreach (var targetObject in s_Live) if (targetObject != null) Object.Destroy(targetObject);
            s_Live.Clear();
            var tunnelFile = For(scene);
            int made = 0;
            foreach (var tunnel in tunnelFile.Tunnels)
            {
                if (Obstacles.IsHeldBack(tunnel.Position)) continue;
                try { if (Spawn(tunnel) != null) made++; }
                catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"Tunnel {tunnel.Source}: {exception.Message}"); }
            }
            if (tunnelFile.Tunnels.Count > 0) SteamRailwayMod.Log.Msg($"{scene}: {made} of {tunnelFile.Tunnels.Count} tunnel(s) placed.");
        }

        /// A train tunnel the region has (one with a train light absorber first).
        static Transform PickSource()
        {
            Transform best = null; int bestScore = 0;
            foreach (var transform in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (transform.gameObject.scene.name == null || transform.name.StartsWith("SR_")) continue;
                if (!transform.name.StartsWith("STR_Tunnel") || !transform.name.Contains("_Prefab")) continue;
                int score = 1;
                foreach (var child in transform.GetComponentsInChildren<Transform>(true))
                    if (child.name.Contains("TrainTunnel") || child.name.StartsWith("OBJ_RailTrack")) { score = 2; break; }
                if (score > bestScore) { best = transform; bestScore = score; }
            }
            return best;
        }

        static Transform FindSource(TunnelData tunnel)
        {
            Transform best = null; float bestDistance = float.MaxValue;
            var want = CustomLines.ToVector3(tunnel.SourcePosition);
            foreach (var transform in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (transform.gameObject.scene.name == null || transform.name != tunnel.Source) continue;
                float deltaDistance = tunnel.SourcePosition != null ? (transform.position - want).sqrMagnitude : 0f;
                if (deltaDistance < bestDistance) { bestDistance = deltaDistance; best = transform; }
            }
            return best;
        }

        static Material s_Dark;

        static GameObject Spawn(TunnelData tunnel)
        {
            var source = FindSource(tunnel);
            if (source == null) { SteamRailwayMod.Log.Warning($"Tunnel model {tunnel.Source} is not in this region."); return null; }
            var targetObject = Object.Instantiate(source.gameObject);
            targetObject.name = "SR_Tunnel_" + tunnel.Source;
            targetObject.transform.SetPositionAndRotation(CustomLines.ToVector3(tunnel.Position), tunnel.Rotation != null && tunnel.Rotation.Length >= QuaternionComponentCount ? new Quaternion(tunnel.Rotation[0], tunnel.Rotation[1], tunnel.Rotation[2], tunnel.Rotation[QuaternionLastComponentIndex]) : Quaternion.identity);
            // the line's own rails run through it; no region transitions copied along
            foreach (var child in targetObject.GetComponentsInChildren<Transform>(true))
                if (child.name.StartsWith("OBJ_RailTrack")) child.gameObject.SetActive(false);
            foreach (var loadScene in targetObject.GetComponentsInChildren<Il2Cpp.LoadScene>(true)) loadScene.gameObject.SetActive(false);
            // dark just inside the opening (the ground or rocks may fill the inside)
            if (s_Dark == null) foreach (var material in Resources.FindObjectsOfTypeAll<Material>()) if (material.name == "GLB_Black_A01") { s_Dark = material; break; }
            var plug = GameObject.CreatePrimitive(PrimitiveType.Cube);
            plug.name = "SR_TunnelDark";
            var collider = plug.GetComponent<Collider>();
            if (collider != null) Object.Destroy(collider);
            plug.transform.SetParent(targetObject.transform, false);
            plug.transform.localPosition = new Vector3(0f, PlugCenterHeightInMeters, PlugCenterDepthInMeters);
            plug.transform.localScale = new Vector3(PlugSizeInMeters, PlugSizeInMeters, PlugThicknessInMeters);
            var meshRenderer = plug.GetComponent<MeshRenderer>();
            if (s_Dark != null) meshRenderer.sharedMaterial = s_Dark;
            meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            s_Live.Add(targetObject);
            return targetObject;
        }

        /// Puts a tunnel mouth on a line at path metres s; the tunnel runs on along the line away from `viewer`.
        public static string PlaceAt(string scene, BuiltTrack track, float arcLength, Vector3 viewer)
        {
            var source = PickSource();
            if (source == null) return Localizer.Translate("Bu bölgede kopyalanacak bir tünel modeli yok.", "This region has no tunnel model to copy.");
            track.Path.Sample(arcLength, out var point, out var tangent);
            tangent.y = 0f; tangent.Normalize();
            var inward = Vector3.Dot(tangent, point - viewer) >= 0f ? tangent : -tangent;
            var rotation = Quaternion.LookRotation(-inward, Vector3.up);
            var right = rotation * Vector3.right;
            // the model's rails: centre at x -0.025, rail top 0.402 above its origin; the mouth MouthDepth in front
            track.Path.Sample(Mathf.Clamp(arcLength + Vector3.Dot(inward, tangent) * MouthDepth, 0f, track.Path.Length), out var inner, out _);
            var origin = inner - (right * RailPieces.CentreX + Vector3.up * RailPieces.RailTop);
            var tunnel = new TunnelData { Source = source.name, SourcePosition = CustomLines.ToFloatArray(source.position), Position = CustomLines.ToFloatArray(origin),
                                     Rotation = new[] { rotation.x, rotation.y, rotation.z, rotation.w } };
            For(scene).Tunnels.Add(tunnel);
            Save();
            Spawn(tunnel);
            return Localizer.Translate($"Tünel yerleştirildi ({source.name}).", $"Tunnel placed ({source.name}).");
        }

        /// Removes the placed tunnel nearest to p (within r metres).
        public static bool RemoveNear(string scene, Vector3 point, float radius)
        {
            var tunnelFile = For(scene);
            TunnelData best = null; float bestDistance = radius * radius;
            foreach (var tunnel in tunnelFile.Tunnels)
            {
                var otherPoint = CustomLines.ToVector3(tunnel.Position);
                float deltaX = otherPoint.x - point.x, deltaZ = otherPoint.z - point.z, deltaDistance = deltaX * deltaX + deltaZ * deltaZ;
                if (deltaDistance < bestDistance) { bestDistance = deltaDistance; best = tunnel; }
            }
            if (best == null) return false;
            tunnelFile.Tunnels.Remove(best);
            Save();
            ApplyScene(scene);
            return true;
        }
    }
}
