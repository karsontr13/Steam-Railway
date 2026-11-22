using System.Collections.Generic;
using Il2CppFluffyUnderware.Curvy;
using UnityEngine;

namespace SteamRailway
{
    internal sealed class BuiltTrack
    {
        public string Identifier;
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
        const float MinimumPlainStretchInMeters = 0.4f;
        const float TrackHeightAboveTerrain = 0.3f;

        /// The spline and the sampled path. Rails are laid later (LayRails), once the junctions are known.
        /// keep: stretches (pairs of points on the line) where an original piece with a structure stays.
        public static BuiltTrack Build(string identifier, IList<Vector3> points, IList<KeyValuePair<Vector3, Vector3>> keep)
        {
            if (points == null || points.Count < 2) return null;

            var root = new GameObject("SteamRailway_Track_" + identifier);
            var spline = CreateSpline(root.transform, points);
            var path = CurvePath.CatmullRom(points);
            var rails = new GameObject("Rails").transform;
            rails.SetParent(root.transform, false);

            var built = new BuiltTrack { Identifier = identifier, Root = root, Spline = spline, Path = path, Rails = rails };
            if (keep != null)
                foreach (var pair in keep)
                {
                    float firstValue = path.NearestDistance(pair.Key, out _), secondValue = path.NearestDistance(pair.Value, out _);
                    if (secondValue < firstValue) (firstValue, secondValue) = (secondValue, firstValue);
                    built.Skip.Add(new Vector2(firstValue, secondValue));
                }
            built.Skip.Sort((x, y) => x.x.CompareTo(y.x));
            return built;
        }

        /// Curvy's own Add() builds every control point through its editor-style path (~23 ms each, seconds for a
        /// line). Plain child objects with a segment component, read in one sync, take milliseconds.
        static CurvySpline CreateSpline(Transform parent, IList<Vector3> points)
        {
            var targetObject = new GameObject("Spline");
            targetObject.transform.SetParent(parent, false);
            var spline = targetObject.AddComponent<CurvySpline>();
            targetObject.SetActive(false);
            for (int index = 0; index < points.Count; index++)
            {
                var copyObject = new GameObject("CP" + index.ToString("D4"));
                copyObject.transform.SetParent(targetObject.transform, false);
                copyObject.transform.position = points[index];
                copyObject.AddComponent<CurvySplineSegment>();
            }
            targetObject.SetActive(true);
            spline.Interpolation = CurvyInterpolation.CatmullRom;
            // up = world up (control points are unrotated); Dynamic twists over a long 3D line and rolls the train
            spline.Orientation = CurvyOrientation.Static;
            spline.SyncSplineFromHierarchy();
            spline.Refresh();
            return spline;
        }

        /// Lays the rail along the whole line except the kept original pieces; turnouts change what is laid near a junction
        /// (see Switches): rails without sleepers under the points, long sleepers on the main line.
        public static void LayRails(BuiltTrack track)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var plain = new List<Vector2>();
            float from = 0f;
            // hard mode: no rail across a broken bridge that has not been repaired
            var skipped = new List<Vector2>(track.Skip);
            skipped.AddRange(Obstacles.UnrepairedBridgeRanges(track.Path));
            skipped.Sort((first, second) => first.x.CompareTo(second.x));
            foreach (var skippedRange in skipped)
            {
                if (skippedRange.x > from + MinimumPlainStretchInMeters) plain.Add(new Vector2(from, skippedRange.x));
                from = Mathf.Max(from, skippedRange.y);
            }
            if (track.Path.Length > from + MinimumPlainStretchInMeters) plain.Add(new Vector2(from, track.Path.Length));

            // one go along the whole line (the sleepers keep one rhythm through the points); the turnouts change
            // what is laid where through functions of the position
            var combined = Switches.Combined(track);
            foreach (var plainRange in plain)
            {
                RailPieces.Lay(track.Rails, track.Path, plainRange.x, plainRange.y, combined);
                track.Laid.Add(plainRange);
            }
            // the branch's rails at the points (one of them the moving blade)
            var blades = Switches.Blades(track);
            foreach (var turnoutRange in blades)
                foreach (var options in turnoutRange.Options) RailPieces.Lay(track.Rails, track.Path, turnoutRange.StartPathMeters, turnoutRange.EndPathMeters, options);
            SteamRailwayMod.Debug($"Track '{track.Identifier}': spline {track.Spline.Length:F0} m, path {track.Path.Length:F0} m, {track.Laid.Count} stretch(es) of new rail, {blades.Count} set(s) of points, {stopwatch.ElapsedMilliseconds} ms.");
        }

        static void LayPlain(BuiltTrack track, float firstValue, float secondValue)
        {
            RailPieces.Lay(track.Rails, track.Path, firstValue, secondValue, null);
            track.Laid.Add(new Vector2(firstValue, secondValue));
        }

        /// Snaps a point to the terrain surface (used by the route editor).
        public static Vector3 SnapToTerrain(Vector3 point)
        {
            point.y = Il2CppHelpers.TerrainHeight(point, point.y) + TrackHeightAboveTerrain;
            return point;
        }
    }
}
