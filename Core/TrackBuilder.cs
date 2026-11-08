using System.Collections.Generic;
using Il2CppFluffyUnderware.Curvy;
using UnityEngine;

namespace SteamRailway
{
    internal sealed class BuiltTrack
    {
        public string Id;
        public GameObject Root;
        public CurvySpline Spline;   // the path the train follows (rail-top centre)
        public CurvePath Path;       // the same curve, densely sampled (rails, snow)
        public Transform Rails;
        public readonly List<Vector2> Skip = new List<Vector2>();   // path metres (from, to) where an original piece stays (bridges)
        public readonly List<Vector2> Laid = new List<Vector2>();   // path metres (from, to) where new rail was laid
    }

    /// Turns a line into a Curvy spline for the train (the spline is the track, the rails are
    /// generated along it), then lays the game's own rail piece along it.
    internal static class TrackBuilder
    {
        const float TrackHeightAboveTerrain = 0.3f;

        /// The spline and the sampled path. Rails are laid later (LayRails), once the junctions are known.
        /// keep: stretches (pairs of points on the line) where an original piece with a structure stays.
        public static BuiltTrack Build(string id, IList<Vector3> points, IList<KeyValuePair<Vector3, Vector3>> keep)
        {
            if (points == null || points.Count < 2) return null;

            var root = new GameObject("SteamRailway_Track_" + id);
            var spline = CreateSpline(root.transform, points);
            var path = CurvePath.CatmullRom(points);
            var rails = new GameObject("Rails").transform;
            rails.SetParent(root.transform, false);

            var built = new BuiltTrack { Id = id, Root = root, Spline = spline, Path = path, Rails = rails };
            if (keep != null)
                foreach (var k in keep)
                {
                    float a = path.NearestDistance(k.Key, out _), b = path.NearestDistance(k.Value, out _);
                    if (b < a) (a, b) = (b, a);
                    built.Skip.Add(new Vector2(a, b));
                }
            built.Skip.Sort((x, y) => x.x.CompareTo(y.x));
            return built;
        }

        /// Curvy's own Add() builds every control point through its editor-style path (~23 ms each, seconds for a
        /// line). Plain child objects with a segment component, read in one sync, take milliseconds.
        static CurvySpline CreateSpline(Transform parent, IList<Vector3> points)
        {
            var go = new GameObject("Spline");
            go.transform.SetParent(parent, false);
            var spline = go.AddComponent<CurvySpline>();
            go.SetActive(false);
            for (int i = 0; i < points.Count; i++)
            {
                var cp = new GameObject("CP" + i.ToString("D4"));
                cp.transform.SetParent(go.transform, false);
                cp.transform.position = points[i];
                cp.AddComponent<CurvySplineSegment>();
            }
            go.SetActive(true);
            spline.Interpolation = CurvyInterpolation.CatmullRom;
            // up = world up (control points are unrotated); Dynamic twists over a long 3D line and rolls the train
            spline.Orientation = CurvyOrientation.Static;
            spline.SyncSplineFromHierarchy();
            spline.Refresh();
            return spline;
        }

        /// Lays the rail along the whole line except the kept original pieces. Turnouts change what is laid near
        /// a junction (see Switches): the branch gets rails without sleepers under the points, the main line long
        /// sleepers that carry both tracks.
        public static void LayRails(BuiltTrack t)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var plain = new List<Vector2>();
            float from = 0f;
            foreach (var k in t.Skip)
            {
                if (k.x > from + 0.4f) plain.Add(new Vector2(from, k.x));
                from = Mathf.Max(from, k.y);
            }
            if (t.Path.Length > from + 0.4f) plain.Add(new Vector2(from, t.Path.Length));

            // one go along the whole line (the sleepers keep one rhythm through the points); the turnouts change
            // what is laid where through functions of the position
            var combined = Switches.Combined(t);
            foreach (var r in plain)
            {
                RailPieces.Lay(t.Rails, t.Path, r.x, r.y, combined);
                t.Laid.Add(r);
            }
            // the branch's rails at the points (one of them the moving blade)
            var blades = Switches.Blades(t);
            foreach (var bl in blades)
                foreach (var o in bl.Options) RailPieces.Lay(t.Rails, t.Path, bl.From, bl.To, o);
            SteamRailwayMod.Debug($"Track '{t.Id}': spline {t.Spline.Length:F0} m, path {t.Path.Length:F0} m, {t.Laid.Count} stretch(es) of new rail, {blades.Count} set(s) of points, {sw.ElapsedMilliseconds} ms.");
        }

        static void LayPlain(BuiltTrack t, float a, float b)
        {
            RailPieces.Lay(t.Rails, t.Path, a, b, null);
            t.Laid.Add(new Vector2(a, b));
        }

        /// Snaps a point to the terrain surface (used by the route editor).
        public static Vector3 SnapToTerrain(Vector3 p)
        {
            p.y = H.TerrainHeight(p, p.y) + TrackHeightAboveTerrain;
            return p;
        }
    }
}
