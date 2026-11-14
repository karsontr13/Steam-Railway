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
        public static float[] F(Vector3 vector) => new[] { (float)System.Math.Round(vector.x, 3), (float)System.Math.Round(vector.y, 3), (float)System.Math.Round(vector.z, 3) };

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
            var customLineFile = For(scene);
            for (int index = 1; ; index++)
            {
                string identifier = $"{scene}_drawn{index}";
                if (!customLineFile.Lines.Exists(customLine => customLine.Id == identifier)) return identifier;
            }
        }

        public static void Add(string scene, CustomLineData customLine) { For(scene).Lines.Add(customLine); Save(); }

        public static bool Remove(string scene, string identifier)
        {
            int count = For(scene).Lines.RemoveAll(customLine => customLine.Id == identifier);
            if (count > 0) Save();
            return count > 0;
        }

        /// The drawn line nearest to p (horizontally, within maxDist), or null.
        public static CustomLineData Nearest(string scene, Vector3 point, float maxDist)
        {
            CustomLineData best = null; float bestDistance = maxDist * maxDist;
            foreach (var customLine in For(scene).Lines)
                foreach (var q in customLine.Points)
                {
                    float deltaX = q[0] - point.x, deltaZ = q[2] - point.z, distance = deltaX * deltaX + deltaZ * deltaZ;
                    if (distance < bestDistance) { bestDistance = distance; best = customLine; }
                }
            return best;
        }

        // ------------------------------------------------------------------ building

        /// Adds the region's drawn lines to its lines.
        public static void AddDefs(string scene, List<TrackDef> defs)
        {
            foreach (var customLine in For(scene).Lines)
            {
                if (customLine.Points == null || customLine.Points.Count < 2) continue;
                var points = new List<Vector3>();
                foreach (var p in customLine.Points) points.Add(V(p));
                defs.Add(new TrackDef { Id = customLine.Id, Points = points, HasCustom = true });
            }
        }

        public static List<Vector3> SwitchPoints(string scene)
        {
            var results = new List<Vector3>();
            foreach (var customLine in For(scene).Lines) if (customLine.Switches != null) foreach (var p in customLine.Switches) results.Add(V(p));
            return results;
        }

        public static List<CurvePath> FillPaths(string scene)
        {
            var results = new List<CurvePath>();
            foreach (var customLine in For(scene).Lines)
            {
                if (!customLine.Fill || customLine.Points == null || customLine.Points.Count < 2) continue;
                var points = new List<Vector3>();
                foreach (var p in customLine.Points) points.Add(V(p));
                results.Add(CurvePath.CatmullRom(points));
            }
            return results;
        }

        static Vector3 End(TrackDef trackDef, int endIndex) => endIndex == 0 ? trackDef.Points[0] : trackDef.Points[trackDef.Points.Count - 1];

        static Vector3 Out(TrackDef trackDef, int endIndex)
        {
            var points = trackDef.Points;
            var vector = endIndex == 0 ? points[0] - points[1] : points[points.Count - 1] - points[points.Count - 2];
            vector.y = 0f;
            return vector.normalized;
        }

        /// Lines meeting end to end, heading on (at least one of them drawn), become one line.
        public static void Merge(List<TrackDef> defs)
        {
            for (int guard = 0; guard < 100; guard++)
            {
                bool merged = false;
                for (int index = 0; index < defs.Count && !merged; index++)
                for (int innerIndex = 0; innerIndex < defs.Count && !merged; innerIndex++)
                {
                    if (index == innerIndex || (!defs[index].HasCustom && !defs[innerIndex].HasCustom)) continue;
                    if (defs[index].Points.Count < 2 || defs[innerIndex].Points.Count < 2) continue;
                    for (int ea = 0; ea < 2 && !merged; ea++)
                    for (int eb = 0; eb < 2 && !merged; eb++)
                    {
                        if ((End(defs[index], ea) - End(defs[innerIndex], eb)).sqrMagnitude > 0.6f * 0.6f) continue;
                        if (Vector3.Dot(Out(defs[index], ea), Out(defs[innerIndex], eb)) > -0.7f) continue;
                        var trackDef = Join(defs[index], ea, defs[innerIndex], eb);
                        SteamRailwayMod.Debug($"Lines {defs[index].Id} and {defs[innerIndex].Id} joined end to end as {trackDef.Id}.");
                        int upper = Mathf.Max(index, innerIndex), lower = Mathf.Min(index, innerIndex);
                        defs.RemoveAt(upper);
                        defs[lower] = trackDef;
                        merged = true;
                    }
                }
                if (!merged) return;
            }
        }

        static TrackDef Join(TrackDef firstTrackDef, int ea, TrackDef secondTrackDef, int eb)
        {
            var firstPoints = new List<Vector3>(firstTrackDef.Points);
            if (ea == 0) firstPoints.Reverse();                 // a's joining end last
            var secondPoints = new List<Vector3>(secondTrackDef.Points);
            if (eb == 1) secondPoints.Reverse();                 // b's joining end first
            for (int deepIndex = 1; deepIndex < secondPoints.Count; deepIndex++) firstPoints.Add(secondPoints[deepIndex]);
            var keep = new List<KeyValuePair<Vector3, Vector3>>();
            if (firstTrackDef.Keep != null) keep.AddRange(firstTrackDef.Keep);
            if (secondTrackDef.Keep != null) keep.AddRange(secondTrackDef.Keep);
            // the region's own line keeps its name (trains and switches refer to it)
            string identifier = firstTrackDef.HasCustom && !secondTrackDef.HasCustom ? secondTrackDef.Id : firstTrackDef.Id;
            var trackDef = new TrackDef { Id = identifier, Points = firstPoints, Keep = keep.Count > 0 ? keep : null, HasCustom = true };
            trackDef.Originals.AddRange(firstTrackDef.Originals);
            trackDef.Originals.AddRange(secondTrackDef.Originals);
            return trackDef;
        }
    }
}
