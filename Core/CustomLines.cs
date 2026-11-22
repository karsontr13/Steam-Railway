using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using UnityEngine;

namespace SteamRailway
{
    /// A line drawn with the track editor.
    internal sealed class CustomLineData
    {
        [JsonPropertyName("Id")] public string Identifier { get; set; }
        public List<float[]> Points { get; set; } = new List<float[]>();    // rail-top centre, every ~2 m
        public bool Fill { get; set; } = true;                               // snow embankment under it where the ground is low
        public List<float[]> Switches { get; set; } = new List<float[]>();  // ends that join another line with a switch
    }

    internal sealed class CustomLineFile
    {
        public int Version { get; set; } = 1;
        public List<CustomLineData> Lines { get; set; } = new List<CustomLineData>();
    }

    /// The lines drawn with the track editor, kept in Mods\SteamRailway\Custom\<region>.json.
    /// Added to the region's lines when it is built: touching ends merge into one line, an end on a line's middle gets a switch.
    internal static class CustomLines
    {
        const int Vector3ComponentCount = 3;
        const int CoordinateDecimalPlaces = 3;
        const int MaximumMergePassCount = 100;
        const float JoinDistanceInMeters = 0.6f;
        const float OppositeDirectionDotLimit = 0.7f;
        static readonly JsonSerializerOptions s_Json = new JsonSerializerOptions { WriteIndented = true };
        static string s_Scene;
        static CustomLineFile s_File = new CustomLineFile();

        static string FilePath(string scene) => Path.Combine(RouteStore.FolderPath, "Custom", scene + ".json");

        public static Vector3 ToVector3(float[] components) => components != null && components.Length >= Vector3ComponentCount ? new Vector3(components[0], components[1], components[2]) : Vector3.zero;
        public static float[] ToFloatArray(Vector3 vector) => new[] { (float)System.Math.Round(vector.x, CoordinateDecimalPlaces), (float)System.Math.Round(vector.y, CoordinateDecimalPlaces), (float)System.Math.Round(vector.z, CoordinateDecimalPlaces) };

        public static CustomLineFile For(string scene)
        {
            if (scene == s_Scene) return s_File;
            s_Scene = scene;
            s_File = new CustomLineFile();
            var path = FilePath(scene);
            if (File.Exists(path))
            {
                try { s_File = JsonSerializer.Deserialize<CustomLineFile>(File.ReadAllText(path), s_Json) ?? new CustomLineFile(); }
                catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"{path} could not be read: {exception.Message}"); }
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
            catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"Writing the drawn lines failed: {exception.Message}"); }
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
                if (!customLineFile.Lines.Exists(customLine => customLine.Identifier == identifier)) return identifier;
            }
        }

        public static void Add(string scene, CustomLineData customLine) { For(scene).Lines.Add(customLine); Save(); }

        public static bool Remove(string scene, string identifier)
        {
            int count = For(scene).Lines.RemoveAll(customLine => customLine.Identifier == identifier);
            if (count > 0) Save();
            return count > 0;
        }

        /// The drawn line nearest to p (horizontally, within maxDist), or null.
        public static CustomLineData Nearest(string scene, Vector3 point, float maxDist)
        {
            CustomLineData best = null; float bestDistance = maxDist * maxDist;
            foreach (var customLine in For(scene).Lines)
                foreach (var pointComponents in customLine.Points)
                {
                    float deltaX = pointComponents[0] - point.x, deltaZ = pointComponents[2] - point.z, distance = deltaX * deltaX + deltaZ * deltaZ;
                    if (distance < bestDistance) { bestDistance = distance; best = customLine; }
                }
            return best;
        }

        // ------------------------------------------------------------------ building

        /// Adds the region's drawn lines to its lines.
        public static void AddDefs(string scene, List<TrackDef> trackDefinitions)
        {
            foreach (var customLine in For(scene).Lines)
            {
                if (customLine.Points == null || customLine.Points.Count < 2) continue;
                var points = new List<Vector3>();
                foreach (var pointComponents in customLine.Points) points.Add(ToVector3(pointComponents));
                trackDefinitions.Add(new TrackDef { Identifier = customLine.Identifier, Points = points, HasCustom = true });
            }
        }

        public static List<Vector3> SwitchPoints(string scene)
        {
            var results = new List<Vector3>();
            foreach (var customLine in For(scene).Lines) if (customLine.Switches != null) foreach (var pointComponents in customLine.Switches) results.Add(ToVector3(pointComponents));
            return results;
        }

        public static List<CurvePath> FillPaths(string scene)
        {
            var results = new List<CurvePath>();
            foreach (var customLine in For(scene).Lines)
            {
                if (!customLine.Fill || customLine.Points == null || customLine.Points.Count < 2) continue;
                var points = new List<Vector3>();
                foreach (var pointComponents in customLine.Points) points.Add(ToVector3(pointComponents));
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
        public static void Merge(List<TrackDef> trackDefinitions)
        {
            for (int guard = 0; guard < MaximumMergePassCount; guard++)
            {
                bool merged = false;
                for (int index = 0; index < trackDefinitions.Count && !merged; index++)
                for (int innerIndex = 0; innerIndex < trackDefinitions.Count && !merged; innerIndex++)
                {
                    if (index == innerIndex || (!trackDefinitions[index].HasCustom && !trackDefinitions[innerIndex].HasCustom)) continue;
                    if (trackDefinitions[index].Points.Count < 2 || trackDefinitions[innerIndex].Points.Count < 2) continue;
                    for (int firstEndIndex = 0; firstEndIndex < 2 && !merged; firstEndIndex++)
                    for (int secondEndIndex = 0; secondEndIndex < 2 && !merged; secondEndIndex++)
                    {
                        if ((End(trackDefinitions[index], firstEndIndex) - End(trackDefinitions[innerIndex], secondEndIndex)).sqrMagnitude > JoinDistanceInMeters * JoinDistanceInMeters) continue;
                        if (Vector3.Dot(Out(trackDefinitions[index], firstEndIndex), Out(trackDefinitions[innerIndex], secondEndIndex)) > -OppositeDirectionDotLimit) continue;
                        var trackDef = Join(trackDefinitions[index], firstEndIndex, trackDefinitions[innerIndex], secondEndIndex);
                        SteamRailwayMod.Debug($"Lines {trackDefinitions[index].Identifier} and {trackDefinitions[innerIndex].Identifier} joined end to end as {trackDef.Identifier}.");
                        int upper = Mathf.Max(index, innerIndex), lower = Mathf.Min(index, innerIndex);
                        trackDefinitions.RemoveAt(upper);
                        trackDefinitions[lower] = trackDef;
                        merged = true;
                    }
                }
                if (!merged) return;
            }
        }

        static TrackDef Join(TrackDef firstTrackDef, int firstEndIndex, TrackDef secondTrackDef, int secondEndIndex)
        {
            var firstPoints = new List<Vector3>(firstTrackDef.Points);
            if (firstEndIndex == 0) firstPoints.Reverse();                 // a's joining end last
            var secondPoints = new List<Vector3>(secondTrackDef.Points);
            if (secondEndIndex == 1) secondPoints.Reverse();                 // b's joining end first
            for (int deepIndex = 1; deepIndex < secondPoints.Count; deepIndex++) firstPoints.Add(secondPoints[deepIndex]);
            var keep = new List<KeyValuePair<Vector3, Vector3>>();
            if (firstTrackDef.Keep != null) keep.AddRange(firstTrackDef.Keep);
            if (secondTrackDef.Keep != null) keep.AddRange(secondTrackDef.Keep);
            // the region's own line keeps its name (trains and switches refer to it)
            string identifier = firstTrackDef.HasCustom && !secondTrackDef.HasCustom ? secondTrackDef.Identifier : firstTrackDef.Identifier;
            var trackDef = new TrackDef { Identifier = identifier, Points = firstPoints, Keep = keep.Count > 0 ? keep : null, HasCustom = true };
            trackDef.Originals.AddRange(firstTrackDef.Originals);
            trackDef.Originals.AddRange(secondTrackDef.Originals);
            return trackDef;
        }
    }
}
