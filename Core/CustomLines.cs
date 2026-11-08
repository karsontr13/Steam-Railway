using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using UnityEngine;

namespace SteamRailway
{
    /// A line drawn with the track editor.
    internal sealed class CustomLineData
    {
        public string Id { get; set; }
        public List<float[]> Points { get; set; } = new List<float[]>();    // rail-top centre, every ~2 m
        public bool Fill { get; set; } = true;                               // snow embankment under it where the ground is low
        public List<float[]> Switches { get; set; } = new List<float[]>();  // ends that join another line with a switch
    }

    internal sealed class CustomLineFile
    {
        public int Version { get; set; } = 1;
        public List<CustomLineData> Lines { get; set; } = new List<CustomLineData>();
    }

    /// The lines drawn with the track editor, in Mods\SteamRailway\Custom\<region>.json (part of the mod, the same
    /// in every game). When the region is built they are added to its lines: a drawn line whose end meets the end
    /// of another line becomes one line with it; an end on the middle of a line gets a switch there.
    internal static class CustomLines
    {
        static readonly JsonSerializerOptions s_Json = new JsonSerializerOptions { WriteIndented = true };
        static string s_Scene;
        static CustomLineFile s_File = new CustomLineFile();

        static string FilePath(string scene) => Path.Combine(RouteStore.FolderPath, "Custom", scene + ".json");

        public static Vector3 V(float[] a) => a != null && a.Length >= 3 ? new Vector3(a[0], a[1], a[2]) : Vector3.zero;
        public static float[] F(Vector3 v) => new[] { (float)System.Math.Round(v.x, 3), (float)System.Math.Round(v.y, 3), (float)System.Math.Round(v.z, 3) };

        public static CustomLineFile For(string scene)
        {
            if (scene == s_Scene) return s_File;
            s_Scene = scene;
            s_File = new CustomLineFile();
            var path = FilePath(scene);
            if (File.Exists(path))
            {
                try { s_File = JsonSerializer.Deserialize<CustomLineFile>(File.ReadAllText(path), s_Json) ?? new CustomLineFile(); }
                catch (System.Exception e) { SteamRailwayMod.Log.Warning($"{path} could not be read: {e.Message}"); }
            }
            if (s_File.Lines == null) s_File.Lines = new List<CustomLineData>();
            return s_File;
        }

        public static void Save()
        {
            if (s_Scene == null) return;
            try
            {
                var path = FilePath(s_Scene);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonSerializer.Serialize(s_File, s_Json));
            }
            catch (System.Exception e) { SteamRailwayMod.Log.Warning($"Writing the drawn lines failed: {e.Message}"); }
        }

        public static string Snapshot() => JsonSerializer.Serialize(s_File, s_Json);

        public static void Restore(string json)
        {
            try { s_File = JsonSerializer.Deserialize<CustomLineFile>(json, s_Json) ?? new CustomLineFile(); }
            catch { s_File = new CustomLineFile(); }
            Save();
        }

        public static string NextId(string scene)
        {
            var f = For(scene);
            for (int i = 1; ; i++)
            {
                string id = $"{scene}_drawn{i}";
                if (!f.Lines.Exists(x => x.Id == id)) return id;
            }
        }

        public static void Add(string scene, CustomLineData d) { For(scene).Lines.Add(d); Save(); }

        public static bool Remove(string scene, string id)
        {
            int n = For(scene).Lines.RemoveAll(x => x.Id == id);
            if (n > 0) Save();
            return n > 0;
        }

        /// The drawn line nearest to p (horizontally, within maxDist), or null.
        public static CustomLineData Nearest(string scene, Vector3 p, float maxDist)
        {
            CustomLineData best = null; float bd = maxDist * maxDist;
            foreach (var l in For(scene).Lines)
                foreach (var q in l.Points)
                {
                    float dx = q[0] - p.x, dz = q[2] - p.z, d = dx * dx + dz * dz;
                    if (d < bd) { bd = d; best = l; }
                }
            return best;
        }

        // ------------------------------------------------------------------ building

        /// Adds the region's drawn lines to its lines.
        public static void AddDefs(string scene, List<TrackDef> defs)
        {
            foreach (var l in For(scene).Lines)
            {
                if (l.Points == null || l.Points.Count < 2) continue;
                var pts = new List<Vector3>();
                foreach (var p in l.Points) pts.Add(V(p));
                defs.Add(new TrackDef { Id = l.Id, Points = pts, HasCustom = true });
            }
        }

        public static List<Vector3> SwitchPoints(string scene)
        {
            var res = new List<Vector3>();
            foreach (var l in For(scene).Lines) if (l.Switches != null) foreach (var p in l.Switches) res.Add(V(p));
            return res;
        }

        public static List<CurvePath> FillPaths(string scene)
        {
            var res = new List<CurvePath>();
            foreach (var l in For(scene).Lines)
            {
                if (!l.Fill || l.Points == null || l.Points.Count < 2) continue;
                var pts = new List<Vector3>();
                foreach (var p in l.Points) pts.Add(V(p));
                res.Add(CurvePath.CatmullRom(pts));
            }
            return res;
        }

        static Vector3 End(TrackDef d, int e) => e == 0 ? d.Points[0] : d.Points[d.Points.Count - 1];

        static Vector3 Out(TrackDef d, int e)
        {
            var p = d.Points;
            var v = e == 0 ? p[0] - p[1] : p[p.Count - 1] - p[p.Count - 2];
            v.y = 0f;
            return v.normalized;
        }

        /// Lines meeting end to end, heading on (at least one of them drawn), become one line.
        public static void Merge(List<TrackDef> defs)
        {
            for (int guard = 0; guard < 100; guard++)
            {
                bool merged = false;
                for (int i = 0; i < defs.Count && !merged; i++)
                for (int j = 0; j < defs.Count && !merged; j++)
                {
                    if (i == j || (!defs[i].HasCustom && !defs[j].HasCustom)) continue;
                    if (defs[i].Points.Count < 2 || defs[j].Points.Count < 2) continue;
                    for (int ea = 0; ea < 2 && !merged; ea++)
                    for (int eb = 0; eb < 2 && !merged; eb++)
                    {
                        if ((End(defs[i], ea) - End(defs[j], eb)).sqrMagnitude > 0.6f * 0.6f) continue;
                        if (Vector3.Dot(Out(defs[i], ea), Out(defs[j], eb)) > -0.7f) continue;
                        var m = Join(defs[i], ea, defs[j], eb);
                        SteamRailwayMod.Debug($"Lines {defs[i].Id} and {defs[j].Id} joined end to end as {m.Id}.");
                        int hi = Mathf.Max(i, j), lo = Mathf.Min(i, j);
                        defs.RemoveAt(hi);
                        defs[lo] = m;
                        merged = true;
                    }
                }
                if (!merged) return;
            }
        }

        static TrackDef Join(TrackDef a, int ea, TrackDef b, int eb)
        {
            var pa = new List<Vector3>(a.Points);
            if (ea == 0) pa.Reverse();                 // a's joining end last
            var pb = new List<Vector3>(b.Points);
            if (eb == 1) pb.Reverse();                 // b's joining end first
            for (int k = 1; k < pb.Count; k++) pa.Add(pb[k]);
            var keep = new List<KeyValuePair<Vector3, Vector3>>();
            if (a.Keep != null) keep.AddRange(a.Keep);
            if (b.Keep != null) keep.AddRange(b.Keep);
            // the region's own line keeps its name (trains and switches refer to it)
            string id = a.HasCustom && !b.HasCustom ? b.Id : a.Id;
            var m = new TrackDef { Id = id, Points = pa, Keep = keep.Count > 0 ? keep : null, HasCustom = true };
            m.Originals.AddRange(a.Originals);
            m.Originals.AddRange(b.Originals);
            return m;
        }
    }
}
