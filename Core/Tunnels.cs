using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using UnityEngine;

namespace SteamRailway
{
    internal sealed class TunnelData
    {
        public string Source { get; set; }       // the region's tunnel object copied (by name)
        public float[] SourcePos { get; set; }    // where that object stands (several may share a name)
        public float[] Pos { get; set; }
        public float[] Rot { get; set; }
    }

    internal sealed class TunnelFile
    {
        public int Version { get; set; } = 1;
        public List<TunnelData> Tunnels { get; set; } = new List<TunnelData>();
    }

    /// Tunnel mouths placed with the track editor: copies of a train tunnel the region already has (only the
    /// loaded region's own models are used), kept in Mods\SteamRailway\Tunnels\<region>.json. The copy's own rails
    /// are hidden (the line's rails run through it) and a dark screen just inside hides whatever the ground
    /// leaves in the opening.
    internal static class Tunnels
    {
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
                catch (System.Exception e) { SteamRailwayMod.Log.Warning($"{path} could not be read: {e.Message}"); }
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
            catch (System.Exception e) { SteamRailwayMod.Log.Warning($"Writing the tunnels failed: {e.Message}"); }
        }

        /// The region's tunnels (again after a rebuild).
        public static void ApplyScene(string scene)
        {
            foreach (var g in s_Live) if (g != null) Object.Destroy(g);
            s_Live.Clear();
            var f = For(scene);
            int made = 0;
            foreach (var d in f.Tunnels)
            {
                try { if (Spawn(d) != null) made++; }
                catch (System.Exception e) { SteamRailwayMod.Log.Warning($"Tunnel {d.Source}: {e.Message}"); }
            }
            if (f.Tunnels.Count > 0) SteamRailwayMod.Log.Msg($"{scene}: {made} of {f.Tunnels.Count} tunnel(s) placed.");
        }

        /// A train tunnel the region has (one with a train light absorber first).
        static Transform PickSource()
        {
            Transform best = null; int bestScore = 0;
            foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (t.gameObject.scene.name == null || t.name.StartsWith("SR_")) continue;
                if (!t.name.StartsWith("STR_Tunnel") || !t.name.Contains("_Prefab")) continue;
                int score = 1;
                foreach (var c in t.GetComponentsInChildren<Transform>(true))
                    if (c.name.Contains("TrainTunnel") || c.name.StartsWith("OBJ_RailTrack")) { score = 2; break; }
                if (score > bestScore) { best = t; bestScore = score; }
            }
            return best;
        }

        static Transform FindSource(TunnelData d)
        {
            Transform best = null; float bd = float.MaxValue;
            var want = CustomLines.V(d.SourcePos);
            foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (t.gameObject.scene.name == null || t.name != d.Source) continue;
                float dd = d.SourcePos != null ? (t.position - want).sqrMagnitude : 0f;
                if (dd < bd) { bd = dd; best = t; }
            }
            return best;
        }

        static Material s_Dark;

        static GameObject Spawn(TunnelData d)
        {
            var src = FindSource(d);
            if (src == null) { SteamRailwayMod.Log.Warning($"Tunnel model {d.Source} is not in this region."); return null; }
            var go = Object.Instantiate(src.gameObject);
            go.name = "SR_Tunnel_" + d.Source;
            go.transform.SetPositionAndRotation(CustomLines.V(d.Pos), d.Rot != null && d.Rot.Length >= 4 ? new Quaternion(d.Rot[0], d.Rot[1], d.Rot[2], d.Rot[3]) : Quaternion.identity);
            // the line's own rails run through it; no region transitions copied along
            foreach (var c in go.GetComponentsInChildren<Transform>(true))
                if (c.name.StartsWith("OBJ_RailTrack")) c.gameObject.SetActive(false);
            foreach (var ls in go.GetComponentsInChildren<Il2Cpp.LoadScene>(true)) ls.gameObject.SetActive(false);
            // dark just inside the opening (the ground or rocks may fill the inside)
            if (s_Dark == null) foreach (var m in Resources.FindObjectsOfTypeAll<Material>()) if (m.name == "GLB_Black_A01") { s_Dark = m; break; }
            var plug = GameObject.CreatePrimitive(PrimitiveType.Cube);
            plug.name = "SR_TunnelDark";
            var col = plug.GetComponent<Collider>();
            if (col != null) Object.Destroy(col);
            plug.transform.SetParent(go.transform, false);
            plug.transform.localPosition = new Vector3(0f, 4f, 1.5f);
            plug.transform.localScale = new Vector3(9f, 9f, 0.3f);
            var mr = plug.GetComponent<MeshRenderer>();
            if (s_Dark != null) mr.sharedMaterial = s_Dark;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            s_Live.Add(go);
            return go;
        }

        /// Puts a tunnel mouth on a line at path metres s; the tunnel runs on along the line away from `viewer`.
        public static string PlaceAt(string scene, BuiltTrack t, float s, Vector3 viewer)
        {
            var src = PickSource();
            if (src == null) return L.T("Bu bölgede kopyalanacak bir tünel modeli yok.", "This region has no tunnel model to copy.");
            t.Path.Sample(s, out var p, out var tan);
            tan.y = 0f; tan.Normalize();
            var inward = Vector3.Dot(tan, p - viewer) >= 0f ? tan : -tan;
            var rot = Quaternion.LookRotation(-inward, Vector3.up);
            var right = rot * Vector3.right;
            // the model's rails: centre at x -0.025, rail top 0.402 above its origin; the mouth MouthDepth in front
            t.Path.Sample(Mathf.Clamp(s + Vector3.Dot(inward, tan) * MouthDepth, 0f, t.Path.Length), out var inner, out _);
            var origin = inner - (right * RailPieces.CentreX + Vector3.up * RailPieces.RailTop);
            var d = new TunnelData { Source = src.name, SourcePos = CustomLines.F(src.position), Pos = CustomLines.F(origin),
                                     Rot = new[] { rot.x, rot.y, rot.z, rot.w } };
            For(scene).Tunnels.Add(d);
            Save();
            Spawn(d);
            return L.T($"Tünel yerleştirildi ({src.name}).", $"Tunnel placed ({src.name}).");
        }

        /// Removes the placed tunnel nearest to p (within r metres).
        public static bool RemoveNear(string scene, Vector3 p, float r)
        {
            var f = For(scene);
            TunnelData best = null; float bd = r * r;
            foreach (var d in f.Tunnels)
            {
                var q = CustomLines.V(d.Pos);
                float dx = q.x - p.x, dz = q.z - p.z, dd = dx * dx + dz * dz;
                if (dd < bd) { bd = dd; best = d; }
            }
            if (best == null) return false;
            f.Tunnels.Remove(best);
            Save();
            ApplyScene(scene);
            return true;
        }
    }
}
