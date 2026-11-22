using System.Collections.Generic;
using UnityEngine;

namespace SteamRailway
{
    /// Cuts and reshapes built lines for the track editor, writing the changes into the line files the region is built from
    /// (Lines\<region>.json and Custom\<region>.json).
    internal static class LineEdits
    {
        const int KeptStretchComponentCount = 6;
        const int KeptEndXIndex = 3;
        const int KeptEndYIndex = 4;
        const int KeptEndZIndex = 5;
        const float NearPointSquaredDistanceInSquareMeters = 9f;
        const float MinimumMoveSquaredDistanceInSquareMeters = 1e-6f;
        const float MinimumSegmentSquaredLengthInSquareMeters = 1e-4f;
        const float KeptStretchReachInMeters = 3f;
        const float OnLine = 1.2f;     // m: a line point this near the built track belongs to the edited stretch

        sealed class LineSource
        {
            public List<float[]> Points;
            public LineData Auto;
            public CustomLineData Custom;
        }

        static List<LineSource> Sources(string scene, out LineFile auto, out CustomLineFile custom)
        {
            var results = new List<LineSource>();
            auto = OriginalRails.ReadLineFile(scene);
            if (auto?.Lines != null) foreach (var lineData in auto.Lines) if (lineData.Points != null) results.Add(new LineSource { Points = lineData.Points, Auto = lineData });
            custom = CustomLines.For(scene);
            foreach (var customLine in custom.Lines) if (customLine.Points != null) results.Add(new LineSource { Points = customLine.Points, Custom = customLine });
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
        static bool IsOnStretch(BuiltTrack track, Vector3 point, float startArc, float endArc, out float arcLength)
        {
            arcLength = track.Path.NearestDistance(point, out float distance);
            return distance <= OnLine && arcLength >= startArc && arcLength <= endArc;
        }

        /// Removes the stretch [s0, s1] of a built track; the lines it was made of are split there.
        /// Returns how many line points were removed.
        public static int Cut(string scene, BuiltTrack track, float startArc, float endArc)
        {
            var lineSources = Sources(scene, out var auto, out var custom);
            int removed = 0;
            bool autoChanged = false, customChanged = false;
            var newAuto = new List<LineData>();
            var newCustom = new List<CustomLineData>();
            foreach (var lineSource in lineSources)
            {
                var runs = new List<List<float[]>>();
                var run = new List<float[]>();
                bool touchesStretch = false;
                foreach (var pointComponents in lineSource.Points)
                {
                    if (IsOnStretch(track, CustomLines.ToVector3(pointComponents), startArc, endArc, out _))
                    {
                        touchesStretch = true; removed++;
                        if (run.Count > 0) { runs.Add(run); run = new List<float[]>(); }
                        continue;
                    }
                    run.Add(pointComponents);
                }
                if (run.Count > 0) runs.Add(run);
                if (!touchesStretch) continue;
                runs.RemoveAll(results => results.Count < 2);
                if (lineSource.Auto != null)
                {
                    autoChanged = true;
                    if (runs.Count == 0) { auto.Lines.Remove(lineSource.Auto); continue; }
                    // the first part keeps its place in the file (the other lines keep their names)
                    var keep = lineSource.Auto.Keep;
                    lineSource.Auto.Points = runs[0];
                    lineSource.Auto.Keep = KeepIn(keep, runs[0]);
                    for (int index = 1; index < runs.Count; index++)
                        newAuto.Add(new LineData { Points = runs[index], Keep = KeepIn(keep, runs[index]), Pieces = lineSource.Auto.Pieces });
                }
                else
                {
                    customChanged = true;
                    if (runs.Count == 0) { custom.Lines.Remove(lineSource.Custom); continue; }
                    var switchPoints = lineSource.Custom.Switches ?? new List<float[]>();
                    lineSource.Custom.Points = runs[0];
                    lineSource.Custom.Switches = SwitchesIn(switchPoints, runs[0]);
                    for (int index = 1; index < runs.Count; index++)
                        newCustom.Add(new CustomLineData { Identifier = null, Points = runs[index], Fill = lineSource.Custom.Fill, Switches = SwitchesIn(switchPoints, runs[index]) });
                }
            }
            if (auto != null) auto.Lines.AddRange(newAuto);
            foreach (var customLine in newCustom) { customLine.Identifier = CustomLines.NextId(scene); custom.Lines.Add(customLine); }
            Save(scene, auto, autoChanged, customChanged);
            return removed;
        }

        /// The kept stretches (bridges) that lie on this part of a split line.
        static List<float[]> KeepIn(List<float[]> keep, List<float[]> points)
        {
            var results = new List<float[]>();
            if (keep == null) return results;
            foreach (var keptStretch in keep)
            {
                if (keptStretch == null || keptStretch.Length < KeptStretchComponentCount) continue;
                if (Near(points, new Vector3(keptStretch[0], keptStretch[1], keptStretch[2])) && Near(points, new Vector3(keptStretch[KeptEndXIndex], keptStretch[KeptEndYIndex], keptStretch[KeptEndZIndex]))) results.Add(keptStretch);
            }
            return results;
        }

        static bool Near(List<float[]> points, Vector3 point)
        {
            foreach (var pointComponents in points) { float deltaX = pointComponents[0] - point.x, deltaZ = pointComponents[2] - point.z; if (deltaX * deltaX + deltaZ * deltaZ < NearPointSquaredDistanceInSquareMeters) return true; }
            return false;
        }

        static List<float[]> SwitchesIn(List<float[]> switchPoints, List<float[]> points)
        {
            var results = new List<float[]>();
            if (points.Count == 0) return results;
            var start = CustomLines.ToVector3(points[0]); var end = CustomLines.ToVector3(points[points.Count - 1]);
            foreach (var switchComponents in switchPoints)
            {
                var vector = CustomLines.ToVector3(switchComponents);
                if ((vector - start).sqrMagnitude < 1f || (vector - end).sqrMagnitude < 1f) results.Add(switchComponents);
            }
            return results;
        }

        /// Gives the stretch [s0, s1] of a built track a new shape: every line point on it moves to where `shape`
        /// puts its path metres. Points on a kept original piece (a bridge) stay. Returns how many points moved.
        public static int Reshape(string scene, BuiltTrack track, float startArc, float endArc, System.Func<float, Vector3> shape)
        {
            var lineSources = Sources(scene, out var auto, out var custom);
            int moved = 0;
            bool autoChanged = false, customChanged = false;
            foreach (var lineSource in lineSources)
            {
                for (int index = 0; index < lineSource.Points.Count; index++)
                {
                    var point = CustomLines.ToVector3(lineSource.Points[index]);
                    if (!IsOnStretch(track, point, startArc, endArc, out float arcLength)) continue;
                    if (lineSource.Auto != null && OnKept(lineSource.Auto, point)) continue;
                    var newPoint = shape(arcLength);
                    if ((newPoint - point).sqrMagnitude < MinimumMoveSquaredDistanceInSquareMeters) continue;
                    lineSource.Points[index] = CustomLines.ToFloatArray(newPoint);
                    moved++;
                    if (lineSource.Auto != null) autoChanged = true; else customChanged = true;
                }
            }
            Save(scene, auto, autoChanged, customChanged);
            return moved;
        }

        static bool OnKept(LineData lineData, Vector3 point)
        {
            if (lineData.Keep == null) return false;
            foreach (var keptStretch in lineData.Keep)
            {
                if (keptStretch == null || keptStretch.Length < KeptStretchComponentCount) continue;
                var start = new Vector3(keptStretch[0], keptStretch[1], keptStretch[2]); var end = new Vector3(keptStretch[KeptEndXIndex], keptStretch[KeptEndYIndex], keptStretch[KeptEndZIndex]);
                var segment = end - start;
                float fraction = Mathf.Clamp01(Vector3.Dot(point - start, segment) / Mathf.Max(segment.sqrMagnitude, MinimumSegmentSquaredLengthInSquareMeters));
                if ((start + segment * fraction - point).magnitude < KeptStretchReachInMeters) return true;
            }
            return false;
        }
    }
}
