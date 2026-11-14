using System.Collections.Generic;
using Il2CppTLD.Interactions;
using UnityEngine;

namespace SteamRailway
{
    /// A junction: the main line (A) and the branch (B) are separate
    /// splines meeting at the points; the lever decides which one the train takes there. Moving along A in
    /// direction DirA the train reaches the points; set to Diverging it runs onto B, otherwise straight on along A.
    internal sealed class TrackSwitch
    {
        public string Id;
        public BuiltTrack A, B;
        public float S;              // spline metres on A where B joins (the toe of the points)
        public float SaPath;         // the same on A's sampled path
        public int DirA;             // +1 / -1: the direction along A that faces the points
        public bool BAtEnd;          // B joins with its end (else with its start)
        public bool Diverging;
        public GameObject Stand;
        public Transform Lever, Target;

        // turnout geometry
        public float BJoin;          // path metres on B at the toe (0 or its length)
        public int InB;              // +1 / -1: along B away from the points
        public float ZoneB;          // metres of B under the points (on the long sleepers of A)
        public float BladeLen;
        public readonly List<Vector2> ExtA = new List<Vector2>();   // (path metres on A, signed offset of B's centre)
        public readonly List<Vector2> YawA = new List<Vector2>();   // (metres on A, sleeper turn: along shift per metre out)
        public readonly List<Vector2> CutA = new List<Vector2>();   // after the long sleepers: (metres on A, signed offset of B)
        public readonly List<Vector2> CutB = new List<Vector2>();   // the same seen from B: (metres on B, signed offset of A)
        public Vector3 SideB;        // horizontal unit vector from A towards the branch
        public int BranchRail;       // B's rail on the branch side, in B's piece space (-1 / +1)
        public Transform Blade;      // the moving switch blade (B's other rail at the toe)
        public float BladeOpenDeg;   // blade rotation when set for the main line
        public bool RailsLaid;
        public float LeverDiv = -38f;   // lever angle set for the branch (it leans towards the side the train will take)
        public bool BranchLeft;
        public float RailY;             // rail top height beside the stand (the ground lies just below it)         // the branch goes off to the left, seen by a train running into the points
    }

    /// One stretch of a line that a turnout lays differently.
    internal sealed class TurnoutRange
    {
        public float From, To;
        public LayOptions[] Options;
    }

    /// Junctions between the region's lines, the switch stands beside them, and moving trains across them.
    internal static class Switches
    {
        const float MaxJoinDistance = 2.0f;     // m between the branch end and the main line
        const float MaxJoinAngle = 30f;         // degrees between the two lines at the join
        const float SnapLength = 25f;           // m of the branch bent onto the main line's centre
        const float ZoneLateral = 2.4f;         // B's centre this far from A: separate sleepers again
        const float MaxZone = 70f;
        const float BladeLength = 8f;
        const float BladeOpen = 0.12f;          // m the blade toe stands off the stock rail
        const float LeverThrow = 38f;           // degrees the lever swings over
        public const string StandPrefix = "SR_Switch_";

        /// Junctions are not made on their own any more: the switches will be placed by hand at chosen points.
        public static readonly bool Automatic = false;

        const float LevelLength = 15f;     // m of a branch kept at the main line's height (under the long sleepers)
        const float LevelBlend = 15f;      // m over which it then goes over to its own grade

        /// Before the splines are built: a branch leaving a line at a switch keeps the main line's height through the
        /// points (the long sleepers carry both tracks), then goes over to its own grade.
        public static void LevelBranches(IList<TrackDef> defs, List<Vector3> switchPoints)
        {
            var paths = new List<CurvePath>();
            foreach (var trackDef in defs) paths.Add(CurvePath.CatmullRom(trackDef.Points));
            for (int bi = 0; bi < defs.Count; bi++)
            {
                var points = defs[bi].Points;
                for (int end = 0; end < 2; end++)
                {
                    var point = end == 1 ? points[points.Count - 1] : points[0];
                    if (!switchPoints.Exists(otherPoint => (otherPoint - point).sqrMagnitude < 2.25f)) continue;
                    // the main line under this end
                    int ai = -1; float best = 2f;
                    for (int deepIndex = 0; deepIndex < defs.Count; deepIndex++)
                    {
                        if (deepIndex == bi) continue;
                        float arcLength = paths[deepIndex].NearestDistance(point, out float distance);
                        if (distance < best && arcLength > 3f && arcLength < paths[deepIndex].Length - 3f) { best = distance; ai = deepIndex; }
                    }
                    if (ai < 0) continue;
                    var firstCurve = paths[ai];
                    float run = 0f;
                    int changed = 0;
                    for (int deepIndex = 0; deepIndex < points.Count; deepIndex++)
                    {
                        int index = end == 1 ? points.Count - 1 - deepIndex : deepIndex;
                        if (deepIndex > 0) run += Vector3.Distance(points[index], points[end == 1 ? index + 1 : index - 1]);
                        if (run > LevelLength + LevelBlend) break;
                        firstCurve.Sample(firstCurve.NearestDistance(points[index], out _), out var pa, out _);
                        float weight = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((run - LevelLength) / LevelBlend));
                        var otherPoint = points[index];
                        otherPoint.y = Mathf.Lerp(pa.y, otherPoint.y, weight);
                        points[index] = otherPoint;
                        changed++;
                    }
                    SteamRailwayMod.Debug($"Branch {defs[bi].Id} levelled with {defs[ai].Id} at the points ({changed} points).");
                }
            }
        }

        /// Lines with an end lying on another line (branches leaving it).
        public static List<string> Branches(IList<TrackDef> defs)
        {
            var results = new List<string>();
            var paths = new List<CurvePath>();
            foreach (var trackDef in defs) paths.Add(CurvePath.CatmullRom(trackDef.Points));
            for (int bi = 0; bi < defs.Count; bi++)
            {
                if (defs[bi].HasCustom) continue;      // a drawn line joins where it was drawn to
                bool branch = false;
                for (int end = 0; end < 2 && !branch; end++)
                    for (int ai = 0; ai < defs.Count && !branch; ai++)
                        if (ai != bi && !results.Contains(defs[ai].Id) && Joins(paths[bi], end == 1, paths[ai], out _, out _)) branch = true;
                if (branch) results.Add(defs[bi].Id);
            }
            return results;
        }

        static readonly List<TrackSwitch> s_All = new List<TrackSwitch>();
        public static IReadOnlyList<TrackSwitch> All => s_All;

        public static void Clear()
        {
            foreach (var trackSwitch in s_All) if (trackSwitch.Stand != null) Object.Destroy(trackSwitch.Stand);
            s_All.Clear();
        }

        static float ToSpline(BuiltTrack track, float pathDist) => pathDist * track.Spline.Length / Mathf.Max(1f, track.Path.Length);

        static Vector3 Tangent(CurvePath curve, float distance)
        {
            curve.Sample(distance, out _, out var tangent);
            tangent.y = 0f;
            return tangent.normalized;
        }

        /// Does end `atEnd` of line b lie on line a (not at a's ends), heading the same way?
        static bool Joins(CurvePath secondCurve, bool atEnd, CurvePath firstCurve, out float firstArc, out float dotProduct)
        {
            dotProduct = 0f;
            var point = atEnd ? secondCurve[secondCurve.Count - 1] : secondCurve[0];
            firstArc = firstCurve.NearestDistance(point, out float distance);
            if (distance > MaxJoinDistance) return false;
            if (firstArc < 5f || firstArc > firstCurve.Length - 5f) return false;          // a plain end-to-end meeting
            firstCurve.Sample(firstArc, out var pa, out _);
            if (Mathf.Abs(pa.y - point.y) > 1.5f) return false;
            var inward = atEnd ? -Tangent(secondCurve, secondCurve.Length - 2f) : Tangent(secondCurve, 2f);
            dotProduct = Vector3.Dot(inward, Tangent(firstCurve, firstArc));
            return Mathf.Abs(dotProduct) >= Mathf.Cos(MaxJoinAngle * Mathf.Deg2Rad);
        }

        /// Before the splines are built: a branch's end is moved exactly onto the main line's centre (blended over
        /// its last metres), so its rails start in the main line's rails like the blades of real points.
        public static void SnapBranches(IList<TrackDef> defs)
        {
            var paths = new List<CurvePath>();
            foreach (var trackDef in defs) paths.Add(CurvePath.CatmullRom(trackDef.Points));
            for (int bi = 0; bi < defs.Count; bi++)
                for (int end = 0; end < 2; end++)
                    for (int ai = 0; ai < defs.Count; ai++)
                    {
                        if (ai == bi || !Joins(paths[bi], end == 1, paths[ai], out float firstArc, out _)) continue;
                        var points = defs[bi].Points;
                        var point = end == 1 ? points[points.Count - 1] : points[0];
                        paths[ai].Sample(firstArc, out var pa, out _);
                        var off = pa - point;
                        float run = 0f;
                        for (int deepIndex = 0; deepIndex < points.Count; deepIndex++)
                        {
                            int index = end == 1 ? points.Count - 1 - deepIndex : deepIndex;
                            if (deepIndex > 0) run += Vector3.Distance(points[index], points[end == 1 ? index + 1 : index - 1]);
                            if (run >= SnapLength) break;
                            points[index] += off * (1f - Mathf.SmoothStep(0f, 1f, run / SnapLength));
                        }
                        paths[bi] = CurvePath.CatmullRom(points);
                        SteamRailwayMod.Debug($"Branch {defs[bi].Id} snapped {off.magnitude:F2} m onto {defs[ai].Id}.");
                        break;
                    }
        }

        /// Finds every junction (an end of one line lying on another line, the two heading the same way) and works
        /// out its turnout. Call before the rails are laid.
        public static void Find(IList<BuiltTrack> tracks, Dictionary<string, bool> saved, System.Func<Vector3, bool> allowAt = null)
        {
            Clear();
            foreach (var secondTrack in tracks)
                for (int end = 0; end < 2; end++)
                {
                    bool atEnd = end == 1;
                    foreach (var firstTrack in tracks)
                    {
                        if (firstTrack == secondTrack || !Joins(secondTrack.Path, atEnd, firstTrack.Path, out float firstArc, out float dotProduct)) continue;
                        if (allowAt != null && !allowAt(atEnd ? secondTrack.Path[secondTrack.Path.Count - 1] : secondTrack.Path[0])) continue;
                        float sSpline = ToSpline(firstTrack, firstArc);
                        string identifier = $"{firstTrack.Id}@{Mathf.RoundToInt(sSpline)}";
                        if (s_All.Exists(x => x.Id == identifier)) continue;
                        var trackSwitch = new TrackSwitch
                        {
                            Id = identifier, A = firstTrack, B = secondTrack, S = sSpline, SaPath = firstArc, DirA = dotProduct > 0f ? 1 : -1, BAtEnd = atEnd,
                            Diverging = saved != null && saved.TryGetValue(identifier, out bool dv) && dv,
                            BJoin = atEnd ? secondTrack.Path.Length : 0f, InB = atEnd ? -1 : 1,
                        };
                        s_All.Add(trackSwitch);
                        try { Turnout(trackSwitch); }
                        catch (System.Exception e) { SteamRailwayMod.Log.Warning($"Turnout {identifier}: {e.Message}"); }
                        try { BuildStand(trackSwitch); }
                        catch (System.Exception e) { SteamRailwayMod.Log.Warning($"Switch stand {identifier}: {e.Message}"); }
                        SteamRailwayMod.Debug($"Switch {identifier}: branch {secondTrack.Id} ({(atEnd ? "end" : "start")}), points face {(trackSwitch.DirA > 0 ? "+" : "-")}, turnout {trackSwitch.ZoneB:F0} m.");
                    }
                }
        }

        /// Piece-space x axis of the rail laid along a path at d (the same frame RailPieces bends with).
        static Vector3 SideAxis(CurvePath curve, float distance)
        {
            curve.Sample(distance, out _, out var t);
            return Vector3.Cross(Vector3.up, -t).normalized;
        }

        static void Turnout(TrackSwitch trackSwitch)
        {
            var firstCurve = trackSwitch.A.Path; var secondCurve = trackSwitch.B.Path;
            float maxU = Mathf.Min(MaxZone, secondCurve.Length - 1f);
            float lastLat = 0f;
            for (float u = 0f; u <= maxU; u += 0.5f)
            {
                secondCurve.Sample(trackSwitch.BJoin + trackSwitch.InB * u, out var pb, out _);
                float firstArc = firstCurve.NearestDistance(pb, out _);
                firstCurve.Sample(firstArc, out var pa, out _);
                float lateral = Vector3.Dot(pb - pa, SideAxis(firstCurve, firstArc));
                if (Mathf.Abs(lateral) > ZoneLateral) break;
                trackSwitch.ExtA.Add(new Vector2(firstArc, lateral));
                // angle between the two tracks here (A run towards the frog, B run away from the points)
                var ta = Tangent(firstCurve, firstArc) * trackSwitch.DirA;
                var tb = Tangent(secondCurve, trackSwitch.BJoin + trackSwitch.InB * u) * trackSwitch.InB;
                trackSwitch.YawA.Add(new Vector2(firstArc, Vector3.Angle(ta, tb) * Mathf.Deg2Rad));
                trackSwitch.ZoneB = u;
                lastLat = lateral;
            }
            if (trackSwitch.ZoneB < 4f) { trackSwitch.ZoneB = 0f; trackSwitch.ExtA.Clear(); return; }
            trackSwitch.ExtA.Sort((x, y) => x.x.CompareTo(y.x));
            // each long sleeper turns by half the angle between the tracks (square to the line between them)
            float sideSign = Mathf.Sign(lastLat);
            for (int index = 0; index < trackSwitch.YawA.Count; index++)
                trackSwitch.YawA[index] = new Vector2(trackSwitch.YawA[index].x, -trackSwitch.DirA * sideSign * Mathf.Tan(trackSwitch.YawA[index].y * 0.5f));
            trackSwitch.YawA.Sort((x, y) => x.x.CompareTo(y.x));
            // where the two tracks part, both have their own sleepers: they must not run into each other
            for (float u = trackSwitch.ZoneB; u <= Mathf.Min(trackSwitch.ZoneB + 40f, secondCurve.Length - 0.5f); u += 0.5f)
            {
                float secondArc = trackSwitch.BJoin + trackSwitch.InB * u;
                secondCurve.Sample(secondArc, out var pb, out _);
                float firstArc = firstCurve.NearestDistance(pb, out _);
                firstCurve.Sample(firstArc, out var pa, out _);
                float lateral = Vector3.Dot(pb - pa, SideAxis(firstCurve, firstArc));
                if (Mathf.Abs(lateral) > CutUntil) break;
                trackSwitch.CutA.Add(new Vector2(firstArc, lateral));
                trackSwitch.CutB.Add(new Vector2(secondArc, Vector3.Dot(pa - pb, SideAxis(secondCurve, secondArc))));
            }
            trackSwitch.CutA.Sort((x, y) => x.x.CompareTo(y.x));
            trackSwitch.CutB.Sort((x, y) => x.x.CompareTo(y.x));
            trackSwitch.SideB = SideAxis(firstCurve, trackSwitch.SaPath) * Mathf.Sign(lastLat);
            float uSide = Mathf.Min(trackSwitch.ZoneB, 3f);
            trackSwitch.BranchRail = Vector3.Dot(SideAxis(secondCurve, trackSwitch.BJoin + trackSwitch.InB * uSide) * 1f, trackSwitch.SideB) * 1f >= 0f ? 1 : -1;
            trackSwitch.BladeLen = Mathf.Min(BladeLength, trackSwitch.ZoneB);

            // the blade turns about its heel; set for the main line its toe stands off towards the branch side
            secondCurve.Sample(trackSwitch.BJoin, out var toe, out _);
            secondCurve.Sample(trackSwitch.BJoin + trackSwitch.InB * trackSwitch.BladeLen, out var heel, out _);
            var pivot = new GameObject("SwitchBlade").transform;
            pivot.SetParent(trackSwitch.B.Rails, false);
            pivot.position = heel;
            trackSwitch.Blade = pivot;
            var vector = toe - heel; vector.y = 0f;
            float sign = Mathf.Sign(Vector3.Dot(Vector3.Cross(Vector3.up, vector), trackSwitch.SideB));
            trackSwitch.BladeOpenDeg = sign * Mathf.Rad2Deg * BladeOpen / Mathf.Max(1f, vector.magnitude);
        }

        const float CutUntil = 4.3f;     // m between the centres: from here on the sleepers no longer touch
        const float MinReach = 1.12f;    // a sleeper always reaches past its rail's foot

        /// Signed reach of the sleepers on the other track's side at path metres s (0: whole sleepers).
        static float CutAt(List<Vector2> edges, float arcLength)
        {
            if (edges.Count < 2 || arcLength < edges[0].x || arcLength > edges[edges.Count - 1].x) return 0f;
            int lower = 0, upper = edges.Count - 1;
            while (upper - lower > 1) { int m = (lower + upper) >> 1; if (edges[m].x <= arcLength) lower = m; else upper = m; }
            float fraction = edges[upper].x - edges[lower].x > 1e-4f ? (arcLength - edges[lower].x) / (edges[upper].x - edges[lower].x) : 0f;
            float lateral = Mathf.Lerp(edges[lower].y, edges[upper].y, fraction);
            float reach = Mathf.Max(MinReach, Mathf.Abs(lateral) * 0.5f - 0.05f);
            return Mathf.Sign(lateral) * reach;
        }

        static float YawAt(TrackSwitch trackSwitch, float arcLength)
        {
            var edges = trackSwitch.YawA;
            if (edges.Count < 2 || arcLength < edges[0].x || arcLength > edges[edges.Count - 1].x) return 0f;
            int lower = 0, upper = edges.Count - 1;
            while (upper - lower > 1) { int m = (lower + upper) >> 1; if (edges[m].x <= arcLength) lower = m; else upper = m; }
            float fraction = edges[upper].x - edges[lower].x > 1e-4f ? (arcLength - edges[lower].x) / (edges[upper].x - edges[lower].x) : 0f;
            return Mathf.Lerp(edges[lower].y, edges[upper].y, fraction);
        }

        /// Signed lengthening of A's sleepers at path metres s on A (0 outside the turnout).
        static float ExtAt(TrackSwitch trackSwitch, float arcLength)
        {
            var edges = trackSwitch.ExtA;
            if (edges.Count < 2 || arcLength < edges[0].x || arcLength > edges[edges.Count - 1].x) return 0f;
            int lower = 0, upper = edges.Count - 1;
            while (upper - lower > 1) { int m = (lower + upper) >> 1; if (edges[m].x <= arcLength) lower = m; else upper = m; }
            float fraction = edges[upper].x - edges[lower].x > 1e-4f ? (arcLength - edges[lower].x) / (edges[upper].x - edges[lower].x) : 0f;
            return Mathf.Lerp(edges[lower].y, edges[upper].y, fraction);
        }

        /// How a line's turnouts change its rail, as functions of path metres, so the whole line is laid in one go
        /// and its sleepers keep one even rhythm through the points. null: the line has no turnout.
        public static LayOptions Combined(BuiltTrack track)
        {
            var switchesOfFirst = s_All.FindAll(trackSwitch => trackSwitch.A == track && trackSwitch.ZoneB > 0f);
            var switchesOfSecond = s_All.FindAll(trackSwitch => trackSwitch.B == track && trackSwitch.ZoneB > 0f);
            if (switchesOfFirst.Count == 0 && switchesOfSecond.Count == 0) return null;
            bool InZone(TrackSwitch trackSwitch, float distance) { float z = trackSwitch.BJoin + trackSwitch.InB * trackSwitch.ZoneB; return distance >= Mathf.Min(trackSwitch.BJoin, z) && distance <= Mathf.Max(trackSwitch.BJoin, z); }
            bool InBlade(TrackSwitch trackSwitch, float distance) { float scale = trackSwitch.BJoin + trackSwitch.InB * trackSwitch.BladeLen; return distance >= Mathf.Min(trackSwitch.BJoin, scale) && distance <= Mathf.Max(trackSwitch.BJoin, scale); }
            return new LayOptions
            {
                SleeperExt = distance => { foreach (var trackSwitch in switchesOfFirst) { float error = ExtAt(trackSwitch, distance); if (error != 0f) return error; } return 0f; },
                SleeperYaw = distance => { foreach (var trackSwitch in switchesOfFirst) { float y = YawAt(trackSwitch, distance); if (y != 0f) return y; } return 0f; },
                SleeperCut = distance =>
                {
                    foreach (var trackSwitch in switchesOfFirst) { float cosine = CutAt(trackSwitch.CutA, distance); if (cosine != 0f) return cosine; }
                    foreach (var trackSwitch in switchesOfSecond) { float cosine = CutAt(trackSwitch.CutB, distance); if (cosine != 0f) return cosine; }
                    return 0f;
                },
                // under the points the branch has no sleepers of its own (the main line's long ones carry it)
                SleeperKeep = switchesOfSecond.Count == 0 ? null : (System.Func<float, bool>)(distance => { foreach (var trackSwitch in switchesOfSecond) if (InZone(trackSwitch, distance)) return false; return true; }),
                // the branch's rails at the points are laid apart (one of them is the moving blade)
                RailKeep = switchesOfSecond.Count == 0 ? null : (System.Func<float, bool>)(distance => { foreach (var trackSwitch in switchesOfSecond) if (InBlade(trackSwitch, distance)) return false; return true; }),
            };
        }

        /// The branch rails at the points: the fixed one and the moving blade (rails only).
        public static List<TurnoutRange> Blades(BuiltTrack track)
        {
            var results = new List<TurnoutRange>();
            foreach (var trackSwitch in s_All)
            {
                if (trackSwitch.B != track || trackSwitch.ZoneB <= 0f) continue;
                float j = trackSwitch.BJoin, scale = trackSwitch.BJoin + trackSwitch.InB * trackSwitch.BladeLen;
                results.Add(new TurnoutRange { From = Mathf.Min(j, scale), To = Mathf.Max(j, scale), Options = new[]
                {
                    new LayOptions { Sleepers = false, RailSide = trackSwitch.BranchRail, YOffset = -0.004f },
                    new LayOptions { Sleepers = false, RailSide = -trackSwitch.BranchRail, YOffset = -0.003f, Parent = trackSwitch.Blade },
                } });
            }
            return results;
        }

        /// The stretches of a line its turnouts lay differently, sorted, not overlapping.
        public static List<TurnoutRange> TurnoutRanges(BuiltTrack track)
        {
            var results = new List<TurnoutRange>();
            foreach (var trackSwitch in s_All)
            {
                if (trackSwitch.ZoneB <= 0f) continue;
                if (trackSwitch.A == track)
                {
                    var s = trackSwitch;
                    results.Add(new TurnoutRange { From = trackSwitch.ExtA[0].x, To = trackSwitch.ExtA[trackSwitch.ExtA.Count - 1].x,
                        Options = new[] { new LayOptions { SleeperExt = distance => ExtAt(s, distance), SleeperYaw = distance => YawAt(s, distance) } } });
                    if (trackSwitch.CutA.Count >= 2)
                        results.Add(new TurnoutRange { From = trackSwitch.CutA[0].x, To = trackSwitch.CutA[trackSwitch.CutA.Count - 1].x,
                            Options = new[] { new LayOptions { SleeperCut = distance => CutAt(s.CutA, distance) } } });
                }
                if (trackSwitch.B == track)
                {
                    float j = trackSwitch.BJoin, scale = trackSwitch.BJoin + trackSwitch.InB * trackSwitch.BladeLen, z = trackSwitch.BJoin + trackSwitch.InB * trackSwitch.ZoneB;
                    // under the points: the branch-side rail stays put, the other rail is the moving blade
                    results.Add(new TurnoutRange { From = Mathf.Min(j, scale), To = Mathf.Max(j, scale), Options = new[]
                    {
                        new LayOptions { Sleepers = false, RailSide = trackSwitch.BranchRail, YOffset = -0.004f },
                        new LayOptions { Sleepers = false, RailSide = -trackSwitch.BranchRail, YOffset = -0.003f, Parent = trackSwitch.Blade },
                    } });
                    // on to the frog: rails on A's long sleepers
                    results.Add(new TurnoutRange { From = Mathf.Min(scale, z), To = Mathf.Max(scale, z),
                        Options = new[] { new LayOptions { Sleepers = false, YOffset = -0.004f } } });
                    var branchSwitch = trackSwitch;
                    if (trackSwitch.CutB.Count >= 2)
                        results.Add(new TurnoutRange { From = trackSwitch.CutB[0].x, To = trackSwitch.CutB[trackSwitch.CutB.Count - 1].x,
                            Options = new[] { new LayOptions { SleeperCut = distance => CutAt(branchSwitch.CutB, distance) } } });
                }
            }
            results.Sort((turnoutRange, otherTurnoutRange) => turnoutRange.From.CompareTo(otherTurnoutRange.From));
            // two turnouts on one stretch: the first wins
            for (int index = 1; index < results.Count; index++)
                if (results[index].From < results[index - 1].To) { results[index].From = results[index - 1].To; if (results[index].To <= results[index].From) results.RemoveAt(index--); }
            return results;
        }

        /// After the rails are laid: the blades take their positions.
        public static void RailsLaid()
        {
            foreach (var trackSwitch in s_All) { trackSwitch.RailsLaid = true; UpdateLook(trackSwitch, true); }
        }

        // ------------------------------------------------------------------ the stand

        static void BuildStand(TrackSwitch trackSwitch)
        {
            var firstCurve = trackSwitch.A.Path;
            // beside the toe of the points, on the side away from the branch
            float sStand = Mathf.Clamp(trackSwitch.SaPath - trackSwitch.DirA * 3f, 0f, firstCurve.Length);
            firstCurve.Sample(sStand, out var point, out _);
            var ta = Tangent(firstCurve, sStand);
            var side = Vector3.Cross(Vector3.up, ta).normalized;               // right of +A
            Vector3 away;
            if (trackSwitch.SideB.sqrMagnitude > 0.5f) away = -trackSwitch.SideB;
            else
            {
                var bp = trackSwitch.BAtEnd ? trackSwitch.B.Path[trackSwitch.B.Path.Count - 1] : trackSwitch.B.Path[0];
                trackSwitch.B.Path.Sample(trackSwitch.BJoin + trackSwitch.InB * 15f, out var bIn, out _);
                away = side * -Mathf.Sign(Vector3.Dot(bIn - bp, side));
            }
            trackSwitch.RailY = point.y;
            var pos = point + away * 2.7f;
            pos.y = H.GroundBelow(pos + Vector3.up * 3f, 10f).y - 0.1f;

            var root = new GameObject(StandPrefix + trackSwitch.Id);
            root.transform.position = pos;
            root.transform.rotation = Quaternion.LookRotation(ta, Vector3.up) * (Vector3.Dot(away, side) > 0 ? Quaternion.identity : Quaternion.Euler(0f, 180f, 0f));
            trackSwitch.Stand = root;

            var model = LocomotiveModel.SpawnSwitch(root.transform);
            if (model != null)
            {
                trackSwitch.Lever = LocomotiveModel.FindDeep(model.transform, "Switch_Lever");
                trackSwitch.Target = LocomotiveModel.FindDeep(model.transform, "Switch_Target");
            }
            else SteamRailwayMod.Log.Warning($"Switch {trackSwitch.Id}: the stand model is missing from the bundle.");
            // which way the lever leans for the branch: towards the branch's side, measured on the model itself
            var branchSide = -away;
            if (trackSwitch.Lever != null)
            {
                var keep = trackSwitch.Lever.localRotation;
                trackSwitch.Lever.localRotation = Quaternion.Euler(0f, 0f, LeverThrow);
                var up = trackSwitch.Lever.up; up.y = 0f;
                trackSwitch.LeverDiv = Vector3.Dot(up, branchSide) > 0f ? LeverThrow : -LeverThrow;
                trackSwitch.Lever.localRotation = keep;
            }
            var run = ta * trackSwitch.DirA;
            trackSwitch.BranchLeft = Vector3.Dot(branchSide, Vector3.Cross(Vector3.up, run)) < 0f;

            // the clickable part: hold to throw
            root.layer = LayerMask.NameToLayer("InteractiveProp");
            var box = root.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, 0.75f, 0f);
            box.size = new Vector3(1.2f, 1.5f, 0.9f);
            var hold = root.AddComponent<TimedHoldInteraction>();
            // everything the game's hover and hold code reads (a missing one throws in the HUD)
            hold.m_EventEntries = new Il2CppSystem.Collections.Generic.List<InteractionEventEntry>();
            hold.m_DefaultHoverText = H.Loc("GAMEPLAY_Use");
            hold.m_DefaultHoldText = H.Loc("GAMEPLAY_Use");
            hold.m_DefaultHoldTime = 1.2f;
            hold.HoldTime = 1.2f;
            hold.HoldText = L.T("Makas çevriliyor...", "Throwing the switch...");
            hold.CanInteract = true;
            UpdateLook(trackSwitch, true);
        }

        /// After the snow along the lines was lowered or filled: every stand is put back on the ground.
        public static void GroundStands()
        {
            foreach (var trackSwitch in s_All)
            {
                if (trackSwitch.Stand == null) continue;
                var samplePoint = trackSwitch.Stand.transform.position;
                // the highest solid surface not above the rails (terrain or a ground mesh; not branches or wires)
                float top = trackSwitch.RailY + 0.5f, y = float.MinValue;
                foreach (var hit in Physics.RaycastAll(new Vector3(samplePoint.x, top + 30f, samplePoint.z), Vector3.down, 80f, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (hit.point.y > top || hit.point.y <= y) continue;
                    bool ours = false;
                    for (var node = hit.collider.transform; node != null; node = node.parent)
                        if (node.name.StartsWith("SR_") || node.name.StartsWith("SteamRailway") || node.name.StartsWith(TrainBuilder.NamePrefix)) { ours = true; break; }
                    if (!ours) y = hit.point.y;
                }
                if (y == float.MinValue) y = trackSwitch.RailY - RailPieces.RailTop;
                trackSwitch.Stand.transform.position = new Vector3(samplePoint.x, y - 0.1f, samplePoint.z);
            }
        }

        public static string HoverText(TrackSwitch trackSwitch) =>
            (trackSwitch.Diverging
                ? (trackSwitch.BranchLeft ? L.T("Makas: SOLA (sapak hat)", "Switch: LEFT (branch)") : L.T("Makas: SAĞA (sapak hat)", "Switch: RIGHT (branch)"))
                : (trackSwitch.BranchLeft ? L.T("Makas: SAĞA / düz (ana hat)", "Switch: RIGHT / straight (main line)") : L.T("Makas: SOLA / düz (ana hat)", "Switch: LEFT / straight (main line)")))
            + L.T("  (basılı tut: çevir)", "  (hold: throw)");

        static Quaternion BladeRot(TrackSwitch trackSwitch) => Quaternion.Euler(0f, trackSwitch.Diverging ? 0f : trackSwitch.BladeOpenDeg, 0f);

        static void UpdateLook(TrackSwitch trackSwitch, bool immediate)
        {
            if (trackSwitch.Stand != null)
            {
                var interaction = trackSwitch.Stand.GetComponent<BaseInteraction>();
                if (interaction != null) interaction.HoverText = HoverText(trackSwitch);
                var holdInteraction = trackSwitch.Stand.GetComponent<TimedHoldInteraction>();
                if (holdInteraction != null)
                {
                    holdInteraction.HoldText = L.T("Makas çevriliyor...", "Throwing the switch...");
                    if (holdInteraction.HoldTime <= 0f) holdInteraction.HoldTime = 1.2f;
                }
            }
            if (immediate)
            {
                if (trackSwitch.Lever != null) trackSwitch.Lever.localRotation = Quaternion.Euler(0f, 0f, trackSwitch.Diverging ? trackSwitch.LeverDiv : -trackSwitch.LeverDiv);
                if (trackSwitch.Target != null) trackSwitch.Target.localRotation = Quaternion.Euler(0f, trackSwitch.Diverging ? 90f : 0f, 0f);
                if (trackSwitch.Blade != null && trackSwitch.RailsLaid) trackSwitch.Blade.localRotation = BladeRot(trackSwitch);
            }
            else MelonLoader.MelonCoroutines.Start(Animate(trackSwitch));
        }

        static System.Collections.IEnumerator Animate(TrackSwitch trackSwitch)
        {
            float fraction = 0f;
            Quaternion l0 = trackSwitch.Lever != null ? trackSwitch.Lever.localRotation : Quaternion.identity;
            Quaternion g0 = trackSwitch.Target != null ? trackSwitch.Target.localRotation : Quaternion.identity;
            Quaternion b0 = trackSwitch.Blade != null ? trackSwitch.Blade.localRotation : Quaternion.identity;
            var l1 = Quaternion.Euler(0f, 0f, trackSwitch.Diverging ? trackSwitch.LeverDiv : -trackSwitch.LeverDiv);
            var g1 = Quaternion.Euler(0f, trackSwitch.Diverging ? 90f : 0f, 0f);
            var b1 = BladeRot(trackSwitch);
            while (fraction < 1f)
            {
                fraction += Time.deltaTime / 0.6f;
                float scale = Mathf.SmoothStep(0f, 1f, fraction);
                if (trackSwitch.Lever != null) trackSwitch.Lever.localRotation = Quaternion.Slerp(l0, l1, scale);
                if (trackSwitch.Target != null) trackSwitch.Target.localRotation = Quaternion.Slerp(g0, g1, Mathf.Clamp01(scale * 1.4f - 0.2f));
                if (trackSwitch.Blade != null && trackSwitch.RailsLaid) trackSwitch.Blade.localRotation = Quaternion.Slerp(b0, b1, Mathf.Clamp01(scale * 1.25f - 0.25f));
                yield return null;
            }
        }

        /// The player threw the lever of a stand (see Patches/SwitchPatches).
        public static bool Throw(GameObject stand)
        {
            var trackSwitch = s_All.Find(x => x.Stand == stand);
            if (trackSwitch == null) return false;
            trackSwitch.Diverging = !trackSwitch.Diverging;
            RailwayManager.SaveSwitch(trackSwitch.Id, trackSwitch.Diverging);
            UpdateLook(trackSwitch, false);
            TrainSounds.Play(TrainSounds.SwitchThrow, stand);
            CabControls.Message(HoverText(trackSwitch).Replace(L.T("  (basılı tut: çevir)", "  (hold: throw)"), ""));
            return true;
        }

        // ------------------------------------------------------------------ trains

        /// Moves a train onto the other line when it runs over a junction. prev/pos: spline metres on its line.
        public static BuiltTrack Pass(LiveTrain liveTrain, BuiltTrack line, float prev, float position)
        {
            if (Mathf.Abs(position - prev) < 1e-4f) return null;
            int moving = position > prev ? 1 : -1;
            float value = liveTrain.Sim.Velocity;
            int run = value >= 0f ? 1 : -1;
            foreach (var trackSwitch in s_All)
            {
                if (trackSwitch.A == line && trackSwitch.Diverging && moving == trackSwitch.DirA && (prev - trackSwitch.S) * (position - trackSwitch.S) <= 0f)
                {
                    // facing points set for the branch: onto B, into it
                    float over = Mathf.Abs(position - trackSwitch.S);
                    float lenB = trackSwitch.B.Spline.Length;
                    int inB = trackSwitch.BAtEnd ? -1 : 1;
                    Move(liveTrain, trackSwitch.B, trackSwitch.BAtEnd ? lenB - over : over, inB * run);
                    return trackSwitch.B;
                }
                if (trackSwitch.B == line)
                {
                    float lenB = trackSwitch.B.Spline.Length;
                    bool leaving = trackSwitch.BAtEnd ? (moving > 0 && position >= lenB - 0.3f) : (moving < 0 && position <= 0.3f);
                    if (!leaving) continue;
                    // trailing out of the branch onto the main line (the points are pushed over)
                    int alongA = -trackSwitch.DirA;
                    if (!trackSwitch.Diverging) { trackSwitch.Diverging = true; RailwayManager.SaveSwitch(trackSwitch.Id, true); UpdateLook(trackSwitch, false); }
                    Move(liveTrain, trackSwitch.A, trackSwitch.S + alongA * 0.3f, alongA * run);
                    return trackSwitch.A;
                }
            }
            return null;
        }

        /// Puts the train on another line at spline metres `pos`, standing with `facing`.
        static void Move(LiveTrain liveTrain, BuiltTrack destinationTrack, float position, int facing)
        {
            var splineController = liveTrain.Train.m_SplineController;
            splineController.Spline = destinationTrack.Spline;
            splineController.Position = Mathf.Clamp(position, 0.05f, destinationTrack.Spline.Length - 0.05f);
            liveTrain.Sim.S.Facing = facing >= 0 ? 1 : -1;
            liveTrain.Sim.EnforceFacing();
            liveTrain.RouteId = destinationTrack.Id;
            SteamRailwayMod.Debug($"Train {liveTrain.Guid} switched onto {destinationTrack.Id} at {splineController.Position:F1} m.");
        }

        /// The stand belonging to an interaction object (null if it is not one of ours).
        public static bool IsStand(GameObject targetObject) => targetObject != null && targetObject.name.StartsWith(StandPrefix);
    }
}
