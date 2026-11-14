using System.Collections.Generic;
using UnityEngine;

namespace SteamRailway
{
    /// Changes to built lines made with the track editor: a stretch cut out, or a stretch given a new shape. They are
    /// written into the line files the region is built from: the region's own lines (Lines\<region>.json, then
    /// locked so the line finder never replaces it) and the drawn lines (Custom\<region>.json).
    internal static class LineEdits
    {
        const float OnLine = 1.2f;     // m: a line point this near the built track belongs to the edited stretch

        sealed class Src
        {
            public List<float[]> Pts;
            public LineData Auto;
            public CustomLineData Custom;
        }

        static List<Src> Sources(string scene, out LineFile auto, out CustomLineFile custom)
        {
            var results = new List<Src>();
            auto = OriginalRails.ReadLineFile(scene);
            if (auto?.Lines != null) foreach (var lineData in auto.Lines) if (lineData.Points != null) results.Add(new Src { Pts = lineData.Points, Auto = lineData });
            custom = CustomLines.For(scene);
            foreach (var customLine in custom.Lines) if (customLine.Points != null) results.Add(new Src { Pts = customLine.Points, Custom = customLine });
            return results;
        }

        static void Save(string scene, LineFile auto, bool autoChanged, bool customChanged)
        {
            if (autoChanged && auto != null) OriginalRails.WriteLineFile(scene, auto);
            if (customChanged) CustomLines.Save();
        }

        /// Both files as they are now (for undo).
        public static (string auto, string custom) Snapshot(string scene)
        {
            CustomLines.For(scene);
            return (OriginalRails.LineFileText(scene), CustomLines.Snapshot());
        }

        public static void Restore(string scene, (string auto, string custom) snap)
        {
            OriginalRails.RestoreLineFileText(scene, snap.auto);
            CustomLines.For(scene);
            CustomLines.Restore(snap.custom);
        }

        /// Is a line point on the built track between path metres s0 and s1 (its path metres in s)?
        static bool In(BuiltTrack track, Vector3 point, float startArc, float endArc, out float arcLength)
        {
            arcLength = track.Path.NearestDistance(point, out float distance);
            return distance <= OnLine && arcLength >= startArc && arcLength <= endArc;
        }

        /// Removes the stretch [s0, s1] of a built track; the lines it was made of are split there.
        /// Returns how many line points were removed.
        public static int Cut(string scene, BuiltTrack track, float startArc, float endArc)
        {
            var srcs = Sources(scene, out var auto, out var custom);
            int removed = 0;
            bool autoChanged = false, customChanged = false;
            var newAuto = new List<LineData>();
            var newCustom = new List<CustomLineData>();
            foreach (var src in srcs)
            {
                var runs = new List<List<float[]>>();
                var run = new List<float[]>();
                bool hit = false;
                foreach (var q in src.Pts)
                {
                    if (In(track, CustomLines.V(q), startArc, endArc, out _))
                    {
                        hit = true; removed++;
                        if (run.Count > 0) { runs.Add(run); run = new List<float[]>(); }
                        continue;
                    }
                    run.Add(q);
                }
                if (run.Count > 0) runs.Add(run);
                if (!hit) continue;
                runs.RemoveAll(results => results.Count < 2);
                if (src.Auto != null)
                {
                    autoChanged = true;
                    if (runs.Count == 0) { auto.Lines.Remove(src.Auto); continue; }
                    // the first part keeps its place in the file (the other lines keep their names)
                    var keep = src.Auto.Keep;
                    src.Auto.Points = runs[0];
                    src.Auto.Keep = KeepIn(keep, runs[0]);
                    for (int index = 1; index < runs.Count; index++)
                        newAuto.Add(new LineData { Points = runs[index], Keep = KeepIn(keep, runs[index]), Pieces = src.Auto.Pieces });
                }
                else
                {
                    customChanged = true;
                    if (runs.Count == 0) { custom.Lines.Remove(src.Custom); continue; }
                    var sw = src.Custom.Switches ?? new List<float[]>();
                    src.Custom.Points = runs[0];
                    src.Custom.Switches = SwitchesIn(sw, runs[0]);
                    for (int index = 1; index < runs.Count; index++)
                        newCustom.Add(new CustomLineData { Id = null, Points = runs[index], Fill = src.Custom.Fill, Switches = SwitchesIn(sw, runs[index]) });
                }
            }
            if (auto != null) auto.Lines.AddRange(newAuto);
            foreach (var customLine in newCustom) { customLine.Id = CustomLines.NextId(scene); custom.Lines.Add(customLine); }
            Save(scene, auto, autoChanged, customChanged);
            return removed;
        }

        /// The kept stretches (bridges) that lie on this part of a split line.
        static List<float[]> KeepIn(List<float[]> keep, List<float[]> points)
        {
            var results = new List<float[]>();
            if (keep == null) return results;
            foreach (var k in keep)
            {
                if (k == null || k.Length < 6) continue;
                if (Near(points, new Vector3(k[0], k[1], k[2])) && Near(points, new Vector3(k[3], k[4], k[5]))) results.Add(k);
            }
            return results;
        }

        static bool Near(List<float[]> points, Vector3 point)
        {
            foreach (var q in points) { float deltaX = q[0] - point.x, deltaZ = q[2] - point.z; if (deltaX * deltaX + deltaZ * deltaZ < 9f) return true; }
            return false;
        }

        static List<float[]> SwitchesIn(List<float[]> sw, List<float[]> points)
        {
            var results = new List<float[]>();
            if (points.Count == 0) return results;
            var start = CustomLines.V(points[0]); var end = CustomLines.V(points[points.Count - 1]);
            foreach (var p in sw)
            {
                var vector = CustomLines.V(p);
                if ((vector - start).sqrMagnitude < 1f || (vector - end).sqrMagnitude < 1f) results.Add(p);
            }
            return results;
        }

        /// Gives the stretch [s0, s1] of a built track a new shape: every line point on it moves to where `shape`
        /// puts its path metres. Points on a kept original piece (a bridge) stay. Returns how many points moved.
        public static int Reshape(string scene, BuiltTrack track, float startArc, float endArc, System.Func<float, Vector3> shape)
        {
            var srcs = Sources(scene, out var auto, out var custom);
            int moved = 0;
            bool autoChanged = false, customChanged = false;
            foreach (var src in srcs)
            {
                for (int index = 0; index < src.Pts.Count; index++)
                {
                    var point = CustomLines.V(src.Pts[index]);
                    if (!In(track, point, startArc, endArc, out float arcLength)) continue;
                    if (src.Auto != null && OnKept(src.Auto, point)) continue;
                    var np = shape(arcLength);
                    if ((np - point).sqrMagnitude < 1e-6f) continue;
                    src.Pts[index] = CustomLines.F(np);
                    moved++;
                    if (src.Auto != null) autoChanged = true; else customChanged = true;
                }
            }
            Save(scene, auto, autoChanged, customChanged);
            return moved;
        }

        static bool OnKept(LineData lineData, Vector3 point)
        {
            if (lineData.Keep == null) return false;
            foreach (var k in lineData.Keep)
            {
                if (k == null || k.Length < 6) continue;
                var start = new Vector3(k[0], k[1], k[2]); var end = new Vector3(k[3], k[4], k[5]);
                var segment = end - start;
                float fraction = Mathf.Clamp01(Vector3.Dot(point - start, segment) / Mathf.Max(segment.sqrMagnitude, 1e-4f));
                if ((start + segment * fraction - point).magnitude < 3f) return true;
            }
            return false;
        }
    }
}
