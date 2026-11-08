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
            var res = new List<Src>();
            auto = OriginalRails.ReadLineFile(scene);
            if (auto?.Lines != null) foreach (var l in auto.Lines) if (l.Points != null) res.Add(new Src { Pts = l.Points, Auto = l });
            custom = CustomLines.For(scene);
            foreach (var l in custom.Lines) if (l.Points != null) res.Add(new Src { Pts = l.Points, Custom = l });
            return res;
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
        static bool In(BuiltTrack t, Vector3 p, float s0, float s1, out float s)
        {
            s = t.Path.NearestDistance(p, out float d);
            return d <= OnLine && s >= s0 && s <= s1;
        }

        /// Removes the stretch [s0, s1] of a built track; the lines it was made of are split there.
        /// Returns how many line points were removed.
        public static int Cut(string scene, BuiltTrack t, float s0, float s1)
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
                    if (In(t, CustomLines.V(q), s0, s1, out _))
                    {
                        hit = true; removed++;
                        if (run.Count > 0) { runs.Add(run); run = new List<float[]>(); }
                        continue;
                    }
                    run.Add(q);
                }
                if (run.Count > 0) runs.Add(run);
                if (!hit) continue;
                runs.RemoveAll(r => r.Count < 2);
                if (src.Auto != null)
                {
                    autoChanged = true;
                    if (runs.Count == 0) { auto.Lines.Remove(src.Auto); continue; }
                    // the first part keeps its place in the file (the other lines keep their names)
                    var keep = src.Auto.Keep;
                    src.Auto.Points = runs[0];
                    src.Auto.Keep = KeepIn(keep, runs[0]);
                    for (int i = 1; i < runs.Count; i++)
                        newAuto.Add(new LineData { Points = runs[i], Keep = KeepIn(keep, runs[i]), Pieces = src.Auto.Pieces });
                }
                else
                {
                    customChanged = true;
                    if (runs.Count == 0) { custom.Lines.Remove(src.Custom); continue; }
                    var sw = src.Custom.Switches ?? new List<float[]>();
                    src.Custom.Points = runs[0];
                    src.Custom.Switches = SwitchesIn(sw, runs[0]);
                    for (int i = 1; i < runs.Count; i++)
                        newCustom.Add(new CustomLineData { Id = null, Points = runs[i], Fill = src.Custom.Fill, Switches = SwitchesIn(sw, runs[i]) });
                }
            }
            if (auto != null) auto.Lines.AddRange(newAuto);
            foreach (var c in newCustom) { c.Id = CustomLines.NextId(scene); custom.Lines.Add(c); }
            Save(scene, auto, autoChanged, customChanged);
            return removed;
        }

        /// The kept stretches (bridges) that lie on this part of a split line.
        static List<float[]> KeepIn(List<float[]> keep, List<float[]> pts)
        {
            var res = new List<float[]>();
            if (keep == null) return res;
            foreach (var k in keep)
            {
                if (k == null || k.Length < 6) continue;
                if (Near(pts, new Vector3(k[0], k[1], k[2])) && Near(pts, new Vector3(k[3], k[4], k[5]))) res.Add(k);
            }
            return res;
        }

        static bool Near(List<float[]> pts, Vector3 p)
        {
            foreach (var q in pts) { float dx = q[0] - p.x, dz = q[2] - p.z; if (dx * dx + dz * dz < 9f) return true; }
            return false;
        }

        static List<float[]> SwitchesIn(List<float[]> sw, List<float[]> pts)
        {
            var res = new List<float[]>();
            if (pts.Count == 0) return res;
            var a = CustomLines.V(pts[0]); var b = CustomLines.V(pts[pts.Count - 1]);
            foreach (var p in sw)
            {
                var v = CustomLines.V(p);
                if ((v - a).sqrMagnitude < 1f || (v - b).sqrMagnitude < 1f) res.Add(p);
            }
            return res;
        }

        /// Gives the stretch [s0, s1] of a built track a new shape: every line point on it moves to where `shape`
        /// puts its path metres. Points on a kept original piece (a bridge) stay. Returns how many points moved.
        public static int Reshape(string scene, BuiltTrack t, float s0, float s1, System.Func<float, Vector3> shape)
        {
            var srcs = Sources(scene, out var auto, out var custom);
            int moved = 0;
            bool autoChanged = false, customChanged = false;
            foreach (var src in srcs)
            {
                for (int i = 0; i < src.Pts.Count; i++)
                {
                    var p = CustomLines.V(src.Pts[i]);
                    if (!In(t, p, s0, s1, out float s)) continue;
                    if (src.Auto != null && OnKept(src.Auto, p)) continue;
                    var np = shape(s);
                    if ((np - p).sqrMagnitude < 1e-6f) continue;
                    src.Pts[i] = CustomLines.F(np);
                    moved++;
                    if (src.Auto != null) autoChanged = true; else customChanged = true;
                }
            }
            Save(scene, auto, autoChanged, customChanged);
            return moved;
        }

        static bool OnKept(LineData l, Vector3 p)
        {
            if (l.Keep == null) return false;
            foreach (var k in l.Keep)
            {
                if (k == null || k.Length < 6) continue;
                var a = new Vector3(k[0], k[1], k[2]); var b = new Vector3(k[3], k[4], k[5]);
                var ab = b - a;
                float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-4f));
                if ((a + ab * t - p).magnitude < 3f) return true;
            }
            return false;
        }
    }
}
