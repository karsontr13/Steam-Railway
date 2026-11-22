using System.Collections.Generic;
using Il2CppTLD.Interactions;
using UnityEngine;

namespace SteamRailway
{
    /// A junction: the main line and the branch are separate splines meeting at the points; the lever decides which one
    /// the train takes (Diverging runs onto the branch, otherwise straight on along the main line).
    internal sealed class TrackSwitch
    {
        const float DefaultLeverAngleInDegrees = 38f;
        public string Identifier;
        public BuiltTrack MainTrack, BranchTrack;
        public float MainSplineMeters;              // spline metres on A where B joins (the toe of the points)
        public float MainPathMeters;         // the same on A's sampled path
        public int MainFacingDirection;             // +1 / -1: the direction along A that faces the points
        public bool BranchJoinsAtEnd;          // B joins with its end (else with its start)
        public bool Diverging;
        public GameObject Stand;
        public Transform Lever, Target;

        // turnout geometry
        public float BranchJoinPathMeters;          // path metres on B at the toe (0 or its length)
        public int BranchAwayDirection;              // +1 / -1: along B away from the points
        public float BranchZoneLengthInMeters;          // metres of B under the points (on the long sleepers of A)
        public float BladeLengthInMeters;
        public readonly List<Vector2> MainSleeperExtents = new List<Vector2>();   // (path metres on A, signed offset of B's centre)
        public readonly List<Vector2> MainSleeperYaws = new List<Vector2>();   // (metres on A, sleeper turn: along shift per metre out)
        public readonly List<Vector2> MainSleeperCuts = new List<Vector2>();   // after the long sleepers: (metres on A, signed offset of B)
        public readonly List<Vector2> BranchSleeperCuts = new List<Vector2>();   // the same seen from B: (metres on B, signed offset of A)
        public Vector3 BranchSideDirection;        // horizontal unit vector from A towards the branch
        public int BranchRail;       // B's rail on the branch side, in B's piece space (-1 / +1)
        public Transform Blade;      // the moving switch blade (B's other rail at the toe)
        public float BladeOpenAngleInDegrees;   // blade rotation when set for the main line
        public bool RailsLaid;
        public float LeverDiv = -DefaultLeverAngleInDegrees;   // lever angle set for the branch (it leans towards the side the train will take)
        public bool BranchLeft;
        public float RailY;             // rail top height beside the stand (the ground lies just below it)         // the branch goes off to the left, seen by a train running into the points
    }

    /// One stretch of a line that a turnout lays differently.
    internal sealed class TurnoutRange
    {
        public float StartPathMeters, EndPathMeters;
        public LayOptions[] Options;
    }

    /// Junctions between the region's lines, the switch stands beside them, and moving trains across them.
    internal static class Switches
    {
        const float NearSwitchSquaredDistanceInSquareMeters = 2.25f;
        const float MinimumJoinDistanceFromEndInMeters = 3f;
        const float MinimumJunctionDistanceFromEndInMeters = 5f;
        const float MaximumJunctionHeightDifferenceInMeters = 1.5f;
        const float MinimumZoneLengthInMeters = 4f;
        const float MaximumZoneExtensionInMeters = 40f;
        const float SideSampleDistanceInMeters = 3f;
        const float MinimumEdgeSpanInMeters = 1e-4f;
        const float ReachMarginInMeters = 0.05f;
        const float FixedRailHeightOffsetInMeters = 0.004f;
        const float BladeRailHeightOffsetInMeters = 0.003f;
        const float StandBackDistanceInMeters = 3f;
        const float BranchProbeDistanceInMeters = 15f;
        const float StandSideOffsetInMeters = 2.7f;
        const float StandGroundProbeLiftInMeters = 3f;
        const float StandGroundProbeDropInMeters = 10f;
        const float StandSinkInMeters = 0.1f;
        const float OppositeSideYawInDegrees = 180f;
        const float StandColliderCenterHeightInMeters = 0.75f;
        const float StandColliderWidthInMeters = 1.2f;
        const float StandColliderHeightInMeters = 1.5f;
        const float StandColliderDepthInMeters = 0.9f;
        const float LeverHoldTimeInSeconds = 1.2f;
        const float StandProbeLiftInMeters = 30f;
        const float StandProbeDropInMeters = 80f;
        const float TargetDivergingAngleInDegrees = 90f;
        const float LeverMoveDurationInSeconds = 0.6f;
        const float TargetTurnSlope = 1.4f;
        const float TargetTurnOffset = 0.2f;
        const float BladeTurnSlope = 1.25f;
        const float BladeTurnOffset = 0.25f;
        const float MinimumMoveInMeters = 1e-4f;
        const float BranchExitMarginInMeters = 0.3f;
        const float SplineEdgeMarginInMeters = 0.05f;
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
        public static void LevelBranches(IList<TrackDef> trackDefinitions, List<Vector3> switchPoints)
        {
            var paths = new List<CurvePath>();
            foreach (var trackDef in trackDefinitions) paths.Add(CurvePath.CatmullRom(trackDef.Points));
            for (int branchIndex = 0; branchIndex < trackDefinitions.Count; branchIndex++)
            {
                var points = trackDefinitions[branchIndex].Points;
                for (int end = 0; end < 2; end++)
                {
                    var point = end == 1 ? points[points.Count - 1] : points[0];
                    if (!switchPoints.Exists(otherPoint => (otherPoint - point).sqrMagnitude < NearSwitchSquaredDistanceInSquareMeters)) continue;
                    // the main line under this end
                    int mainIndex = -1; float best = 2f;
                    for (int deepIndex = 0; deepIndex < trackDefinitions.Count; deepIndex++)
                    {
                        if (deepIndex == branchIndex) continue;
                        float arcLength = paths[deepIndex].NearestDistance(point, out float distance);
                        if (distance < best && arcLength > MinimumJoinDistanceFromEndInMeters && arcLength < paths[deepIndex].Length - MinimumJoinDistanceFromEndInMeters) { best = distance; mainIndex = deepIndex; }
                    }
                    if (mainIndex < 0) continue;
                    var firstCurve = paths[mainIndex];
                    float run = 0f;
                    int changed = 0;
                    for (int deepIndex = 0; deepIndex < points.Count; deepIndex++)
                    {
                        int index = end == 1 ? points.Count - 1 - deepIndex : deepIndex;
                        if (deepIndex > 0) run += Vector3.Distance(points[index], points[end == 1 ? index + 1 : index - 1]);
                        if (run > LevelLength + LevelBlend) break;
                        firstCurve.Sample(firstCurve.NearestDistance(points[index], out _), out var mainPoint, out _);
                        float weight = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((run - LevelLength) / LevelBlend));
                        var otherPoint = points[index];
                        otherPoint.y = Mathf.Lerp(mainPoint.y, otherPoint.y, weight);
                        points[index] = otherPoint;
                        changed++;
                    }
                    SteamRailwayMod.Debug($"Branch {trackDefinitions[branchIndex].Identifier} levelled with {trackDefinitions[mainIndex].Identifier} at the points ({changed} points).");
                }
            }
        }

        /// Lines with an end lying on another line (branches leaving it).
        public static List<string> Branches(IList<TrackDef> trackDefinitions)
        {
            var results = new List<string>();
            var paths = new List<CurvePath>();
            foreach (var trackDef in trackDefinitions) paths.Add(CurvePath.CatmullRom(trackDef.Points));
            for (int branchIndex = 0; branchIndex < trackDefinitions.Count; branchIndex++)
            {
                if (trackDefinitions[branchIndex].HasCustom) continue;      // a drawn line joins where it was drawn to
                bool branch = false;
                for (int end = 0; end < 2 && !branch; end++)
                    for (int mainIndex = 0; mainIndex < trackDefinitions.Count && !branch; mainIndex++)
                        if (mainIndex != branchIndex && !results.Contains(trackDefinitions[mainIndex].Identifier) && Joins(paths[branchIndex], end == 1, paths[mainIndex], out _, out _)) branch = true;
                if (branch) results.Add(trackDefinitions[branchIndex].Identifier);
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
            if (firstArc < MinimumJunctionDistanceFromEndInMeters || firstArc > firstCurve.Length - MinimumJunctionDistanceFromEndInMeters) return false;          // a plain end-to-end meeting
            firstCurve.Sample(firstArc, out var mainPoint, out _);
            if (Mathf.Abs(mainPoint.y - point.y) > MaximumJunctionHeightDifferenceInMeters) return false;
            var inward = atEnd ? -Tangent(secondCurve, secondCurve.Length - 2f) : Tangent(secondCurve, 2f);
            dotProduct = Vector3.Dot(inward, Tangent(firstCurve, firstArc));
            return Mathf.Abs(dotProduct) >= Mathf.Cos(MaxJoinAngle * Mathf.Deg2Rad);
        }

        /// Before the splines are built: a branch's end is moved exactly onto the main line's centre (blended over
        /// its last metres), so its rails start in the main line's rails like the blades of real points.
        public static void SnapBranches(IList<TrackDef> trackDefinitions)
        {
            var paths = new List<CurvePath>();
            foreach (var trackDef in trackDefinitions) paths.Add(CurvePath.CatmullRom(trackDef.Points));
            for (int branchIndex = 0; branchIndex < trackDefinitions.Count; branchIndex++)
                for (int end = 0; end < 2; end++)
                    for (int mainIndex = 0; mainIndex < trackDefinitions.Count; mainIndex++)
                    {
                        if (mainIndex == branchIndex || !Joins(paths[branchIndex], end == 1, paths[mainIndex], out float firstArc, out _)) continue;
                        var points = trackDefinitions[branchIndex].Points;
                        var point = end == 1 ? points[points.Count - 1] : points[0];
                        paths[mainIndex].Sample(firstArc, out var mainPoint, out _);
                        var off = mainPoint - point;
                        float run = 0f;
                        for (int deepIndex = 0; deepIndex < points.Count; deepIndex++)
                        {
                            int index = end == 1 ? points.Count - 1 - deepIndex : deepIndex;
                            if (deepIndex > 0) run += Vector3.Distance(points[index], points[end == 1 ? index + 1 : index - 1]);
                            if (run >= SnapLength) break;
                            points[index] += off * (1f - Mathf.SmoothStep(0f, 1f, run / SnapLength));
                        }
                        paths[branchIndex] = CurvePath.CatmullRom(points);
                        SteamRailwayMod.Debug($"Branch {trackDefinitions[branchIndex].Identifier} snapped {off.magnitude:F2} m onto {trackDefinitions[mainIndex].Identifier}.");
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
                        float mainSplineMeters = ToSpline(firstTrack, firstArc);
                        string identifier = $"{firstTrack.Identifier}@{Mathf.RoundToInt(mainSplineMeters)}";
                        if (s_All.Exists(x => x.Identifier == identifier)) continue;
                        var trackSwitch = new TrackSwitch
                        {
                            Identifier = identifier, MainTrack = firstTrack, BranchTrack = secondTrack, MainSplineMeters = mainSplineMeters, MainPathMeters = firstArc, MainFacingDirection = dotProduct > 0f ? 1 : -1, BranchJoinsAtEnd = atEnd,
                            Diverging = saved != null && saved.TryGetValue(identifier, out bool savedDiverging) && savedDiverging,
                            BranchJoinPathMeters = atEnd ? secondTrack.Path.Length : 0f, BranchAwayDirection = atEnd ? -1 : 1,
                        };
                        s_All.Add(trackSwitch);
                        try { Turnout(trackSwitch); }
                        catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"Turnout {identifier}: {exception.Message}"); }
                        try { BuildStand(trackSwitch); }
                        catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"Switch stand {identifier}: {exception.Message}"); }
                        SteamRailwayMod.Debug($"Switch {identifier}: branch {secondTrack.Identifier} ({(atEnd ? "end" : "start")}), points face {(trackSwitch.MainFacingDirection > 0 ? "+" : "-")}, turnout {trackSwitch.BranchZoneLengthInMeters:F0} m.");
                    }
                }
        }

        /// Piece-space x axis of the rail laid along a path at d (the same frame RailPieces bends with).
        static Vector3 SideAxis(CurvePath curve, float distance)
        {
            curve.Sample(distance, out _, out var tangent);
            return Vector3.Cross(Vector3.up, -tangent).normalized;
        }

        static void Turnout(TrackSwitch trackSwitch)
        {
            var firstCurve = trackSwitch.MainTrack.Path; var secondCurve = trackSwitch.BranchTrack.Path;
            float maxBranchDistance = Mathf.Min(MaxZone, secondCurve.Length - 1f);
            float lastLat = 0f;
            for (float branchDistance = 0f; branchDistance <= maxBranchDistance; branchDistance += 0.5f)
            {
                secondCurve.Sample(trackSwitch.BranchJoinPathMeters + trackSwitch.BranchAwayDirection * branchDistance, out var branchPoint, out _);
                float firstArc = firstCurve.NearestDistance(branchPoint, out _);
                firstCurve.Sample(firstArc, out var mainPoint, out _);
                float lateral = Vector3.Dot(branchPoint - mainPoint, SideAxis(firstCurve, firstArc));
                if (Mathf.Abs(lateral) > ZoneLateral) break;
                trackSwitch.MainSleeperExtents.Add(new Vector2(firstArc, lateral));
                // angle between the two tracks here (A run towards the frog, B run away from the points)
                var mainTangent = Tangent(firstCurve, firstArc) * trackSwitch.MainFacingDirection;
                var branchTangent = Tangent(secondCurve, trackSwitch.BranchJoinPathMeters + trackSwitch.BranchAwayDirection * branchDistance) * trackSwitch.BranchAwayDirection;
                trackSwitch.MainSleeperYaws.Add(new Vector2(firstArc, Vector3.Angle(mainTangent, branchTangent) * Mathf.Deg2Rad));
                trackSwitch.BranchZoneLengthInMeters = branchDistance;
                lastLat = lateral;
            }
            if (trackSwitch.BranchZoneLengthInMeters < MinimumZoneLengthInMeters) { trackSwitch.BranchZoneLengthInMeters = 0f; trackSwitch.MainSleeperExtents.Clear(); return; }
            trackSwitch.MainSleeperExtents.Sort((x, y) => x.x.CompareTo(y.x));
            // each long sleeper turns by half the angle between the tracks (square to the line between them)
            float sideSign = Mathf.Sign(lastLat);
            for (int index = 0; index < trackSwitch.MainSleeperYaws.Count; index++)
                trackSwitch.MainSleeperYaws[index] = new Vector2(trackSwitch.MainSleeperYaws[index].x, -trackSwitch.MainFacingDirection * sideSign * Mathf.Tan(trackSwitch.MainSleeperYaws[index].y * 0.5f));
            trackSwitch.MainSleeperYaws.Sort((x, y) => x.x.CompareTo(y.x));
            // where the two tracks part, both have their own sleepers: they must not run into each other
            for (float branchDistance = trackSwitch.BranchZoneLengthInMeters; branchDistance <= Mathf.Min(trackSwitch.BranchZoneLengthInMeters + MaximumZoneExtensionInMeters, secondCurve.Length - 0.5f); branchDistance += 0.5f)
            {
                float secondArc = trackSwitch.BranchJoinPathMeters + trackSwitch.BranchAwayDirection * branchDistance;
                secondCurve.Sample(secondArc, out var branchPoint, out _);
                float firstArc = firstCurve.NearestDistance(branchPoint, out _);
                firstCurve.Sample(firstArc, out var mainPoint, out _);
                float lateral = Vector3.Dot(branchPoint - mainPoint, SideAxis(firstCurve, firstArc));
                if (Mathf.Abs(lateral) > CutUntil) break;
                trackSwitch.MainSleeperCuts.Add(new Vector2(firstArc, lateral));
                trackSwitch.BranchSleeperCuts.Add(new Vector2(secondArc, Vector3.Dot(mainPoint - branchPoint, SideAxis(secondCurve, secondArc))));
            }
            trackSwitch.MainSleeperCuts.Sort((x, y) => x.x.CompareTo(y.x));
            trackSwitch.BranchSleeperCuts.Sort((x, y) => x.x.CompareTo(y.x));
            trackSwitch.BranchSideDirection = SideAxis(firstCurve, trackSwitch.MainPathMeters) * Mathf.Sign(lastLat);
            float sideDistance = Mathf.Min(trackSwitch.BranchZoneLengthInMeters, SideSampleDistanceInMeters);
            trackSwitch.BranchRail = Vector3.Dot(SideAxis(secondCurve, trackSwitch.BranchJoinPathMeters + trackSwitch.BranchAwayDirection * sideDistance) * 1f, trackSwitch.BranchSideDirection) * 1f >= 0f ? 1 : -1;
            trackSwitch.BladeLengthInMeters = Mathf.Min(BladeLength, trackSwitch.BranchZoneLengthInMeters);

            // the blade turns about its heel; set for the main line its toe stands off towards the branch side
            secondCurve.Sample(trackSwitch.BranchJoinPathMeters, out var toe, out _);
            secondCurve.Sample(trackSwitch.BranchJoinPathMeters + trackSwitch.BranchAwayDirection * trackSwitch.BladeLengthInMeters, out var heel, out _);
            var pivot = new GameObject("SwitchBlade").transform;
            pivot.SetParent(trackSwitch.BranchTrack.Rails, false);
            pivot.position = heel;
            trackSwitch.Blade = pivot;
            var vector = toe - heel; vector.y = 0f;
            float sign = Mathf.Sign(Vector3.Dot(Vector3.Cross(Vector3.up, vector), trackSwitch.BranchSideDirection));
            trackSwitch.BladeOpenAngleInDegrees = sign * Mathf.Rad2Deg * BladeOpen / Mathf.Max(1f, vector.magnitude);
        }

        const float CutUntil = 4.3f;     // m between the centres: from here on the sleepers no longer touch
        const float MinReach = 1.12f;    // a sleeper always reaches past its rail's foot

        /// Signed reach of the sleepers on the other track's side at path metres s (0: whole sleepers).
        static float CutAt(List<Vector2> edges, float arcLength)
        {
            if (edges.Count < 2 || arcLength < edges[0].x || arcLength > edges[edges.Count - 1].x) return 0f;
            int lower = 0, upper = edges.Count - 1;
            while (upper - lower > 1) { int middleIndex = (lower + upper) >> 1; if (edges[middleIndex].x <= arcLength) lower = middleIndex; else upper = middleIndex; }
            float fraction = edges[upper].x - edges[lower].x > MinimumEdgeSpanInMeters ? (arcLength - edges[lower].x) / (edges[upper].x - edges[lower].x) : 0f;
            float lateral = Mathf.Lerp(edges[lower].y, edges[upper].y, fraction);
            float reach = Mathf.Max(MinReach, Mathf.Abs(lateral) * 0.5f - ReachMarginInMeters);
            return Mathf.Sign(lateral) * reach;
        }

        static float YawAt(TrackSwitch trackSwitch, float arcLength)
        {
            var edges = trackSwitch.MainSleeperYaws;
            if (edges.Count < 2 || arcLength < edges[0].x || arcLength > edges[edges.Count - 1].x) return 0f;
            int lower = 0, upper = edges.Count - 1;
            while (upper - lower > 1) { int middleIndex = (lower + upper) >> 1; if (edges[middleIndex].x <= arcLength) lower = middleIndex; else upper = middleIndex; }
            float fraction = edges[upper].x - edges[lower].x > MinimumEdgeSpanInMeters ? (arcLength - edges[lower].x) / (edges[upper].x - edges[lower].x) : 0f;
            return Mathf.Lerp(edges[lower].y, edges[upper].y, fraction);
        }

        /// Signed lengthening of A's sleepers at path metres s on A (0 outside the turnout).
        static float ExtAt(TrackSwitch trackSwitch, float arcLength)
        {
            var edges = trackSwitch.MainSleeperExtents;
            if (edges.Count < 2 || arcLength < edges[0].x || arcLength > edges[edges.Count - 1].x) return 0f;
            int lower = 0, upper = edges.Count - 1;
            while (upper - lower > 1) { int middleIndex = (lower + upper) >> 1; if (edges[middleIndex].x <= arcLength) lower = middleIndex; else upper = middleIndex; }
            float fraction = edges[upper].x - edges[lower].x > MinimumEdgeSpanInMeters ? (arcLength - edges[lower].x) / (edges[upper].x - edges[lower].x) : 0f;
            return Mathf.Lerp(edges[lower].y, edges[upper].y, fraction);
        }

        /// How a line's turnouts change its rail, as functions of path metres, so the whole line is laid in one go
        /// and its sleepers keep one even rhythm through the points. null: the line has no turnout.
        public static LayOptions Combined(BuiltTrack track)
        {
            var switchesOfFirst = s_All.FindAll(trackSwitch => trackSwitch.MainTrack == track && trackSwitch.BranchZoneLengthInMeters > 0f);
            var switchesOfSecond = s_All.FindAll(trackSwitch => trackSwitch.BranchTrack == track && trackSwitch.BranchZoneLengthInMeters > 0f);
            if (switchesOfFirst.Count == 0 && switchesOfSecond.Count == 0) return null;
            bool InZone(TrackSwitch trackSwitch, float distance) { float z = trackSwitch.BranchJoinPathMeters + trackSwitch.BranchAwayDirection * trackSwitch.BranchZoneLengthInMeters; return distance >= Mathf.Min(trackSwitch.BranchJoinPathMeters, z) && distance <= Mathf.Max(trackSwitch.BranchJoinPathMeters, z); }
            bool InBlade(TrackSwitch trackSwitch, float distance) { float scale = trackSwitch.BranchJoinPathMeters + trackSwitch.BranchAwayDirection * trackSwitch.BladeLengthInMeters; return distance >= Mathf.Min(trackSwitch.BranchJoinPathMeters, scale) && distance <= Mathf.Max(trackSwitch.BranchJoinPathMeters, scale); }
            return new LayOptions
            {
                SleeperExtension = distance => { foreach (var trackSwitch in switchesOfFirst) { float error = ExtAt(trackSwitch, distance); if (error != 0f) return error; } return 0f; },
                SleeperYaw = distance => { foreach (var trackSwitch in switchesOfFirst) { float y = YawAt(trackSwitch, distance); if (y != 0f) return y; } return 0f; },
                SleeperCut = distance =>
                {
                    foreach (var trackSwitch in switchesOfFirst) { float cosine = CutAt(trackSwitch.MainSleeperCuts, distance); if (cosine != 0f) return cosine; }
                    foreach (var trackSwitch in switchesOfSecond) { float cosine = CutAt(trackSwitch.BranchSleeperCuts, distance); if (cosine != 0f) return cosine; }
                    return 0f;
                },
                // under the points the branch has no sleepers of its own (the main line's long ones carry it)
                SleeperKeep = switchesOfSecond.Count == 0 ? null : (System.Func<float, bool>)(distance => { foreach (var trackSwitch in switchesOfSecond) if (InZone(trackSwitch, distance)) return false; return true; }),
                // the branch's rails at the points are laid apart (one of them is the moving blade)
                RailKeep = switchesOfSecond.Count == 0 ? null : (System.Func<float, bool>)(distance => { foreach (var trackSwitch in switchesOfSecond) if (InBlade(trackSwitch, distance)) return false; return true; }),
            };
        }

        /// The branch rails at the points: the stationary one and the moving blade (rails only).
        public static List<TurnoutRange> Blades(BuiltTrack track)
        {
            var results = new List<TurnoutRange>();
            foreach (var trackSwitch in s_All)
            {
                if (trackSwitch.BranchTrack != track || trackSwitch.BranchZoneLengthInMeters <= 0f) continue;
                float branchJoin = trackSwitch.BranchJoinPathMeters, scale = trackSwitch.BranchJoinPathMeters + trackSwitch.BranchAwayDirection * trackSwitch.BladeLengthInMeters;
                results.Add(new TurnoutRange { StartPathMeters = Mathf.Min(branchJoin, scale), EndPathMeters = Mathf.Max(branchJoin, scale), Options = new[]
                {
                    new LayOptions { Sleepers = false, RailSide = trackSwitch.BranchRail, YOffset = -FixedRailHeightOffsetInMeters },
                    new LayOptions { Sleepers = false, RailSide = -trackSwitch.BranchRail, YOffset = -BladeRailHeightOffsetInMeters, Parent = trackSwitch.Blade },
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
                if (trackSwitch.BranchZoneLengthInMeters <= 0f) continue;
                if (trackSwitch.MainTrack == track)
                {
                    var capturedSwitch = trackSwitch;
                    results.Add(new TurnoutRange { StartPathMeters = trackSwitch.MainSleeperExtents[0].x, EndPathMeters = trackSwitch.MainSleeperExtents[trackSwitch.MainSleeperExtents.Count - 1].x,
                        Options = new[] { new LayOptions { SleeperExtension = distance => ExtAt(capturedSwitch, distance), SleeperYaw = distance => YawAt(capturedSwitch, distance) } } });
                    if (trackSwitch.MainSleeperCuts.Count >= 2)
                        results.Add(new TurnoutRange { StartPathMeters = trackSwitch.MainSleeperCuts[0].x, EndPathMeters = trackSwitch.MainSleeperCuts[trackSwitch.MainSleeperCuts.Count - 1].x,
                            Options = new[] { new LayOptions { SleeperCut = distance => CutAt(capturedSwitch.MainSleeperCuts, distance) } } });
                }
                if (trackSwitch.BranchTrack == track)
                {
                    float branchJoin = trackSwitch.BranchJoinPathMeters, scale = trackSwitch.BranchJoinPathMeters + trackSwitch.BranchAwayDirection * trackSwitch.BladeLengthInMeters, z = trackSwitch.BranchJoinPathMeters + trackSwitch.BranchAwayDirection * trackSwitch.BranchZoneLengthInMeters;
                    // under the points: the branch-side rail stays put, the other rail is the moving blade
                    results.Add(new TurnoutRange { StartPathMeters = Mathf.Min(branchJoin, scale), EndPathMeters = Mathf.Max(branchJoin, scale), Options = new[]
                    {
                        new LayOptions { Sleepers = false, RailSide = trackSwitch.BranchRail, YOffset = -FixedRailHeightOffsetInMeters },
                        new LayOptions { Sleepers = false, RailSide = -trackSwitch.BranchRail, YOffset = -BladeRailHeightOffsetInMeters, Parent = trackSwitch.Blade },
                    } });
                    // on to the frog: rails on A's long sleepers
                    results.Add(new TurnoutRange { StartPathMeters = Mathf.Min(scale, z), EndPathMeters = Mathf.Max(scale, z),
                        Options = new[] { new LayOptions { Sleepers = false, YOffset = -FixedRailHeightOffsetInMeters } } });
                    var branchSwitch = trackSwitch;
                    if (trackSwitch.BranchSleeperCuts.Count >= 2)
                        results.Add(new TurnoutRange { StartPathMeters = trackSwitch.BranchSleeperCuts[0].x, EndPathMeters = trackSwitch.BranchSleeperCuts[trackSwitch.BranchSleeperCuts.Count - 1].x,
                            Options = new[] { new LayOptions { SleeperCut = distance => CutAt(branchSwitch.BranchSleeperCuts, distance) } } });
                }
            }
            results.Sort((turnoutRange, otherTurnoutRange) => turnoutRange.StartPathMeters.CompareTo(otherTurnoutRange.StartPathMeters));
            // two turnouts on one stretch: the first wins
            for (int index = 1; index < results.Count; index++)
                if (results[index].StartPathMeters < results[index - 1].EndPathMeters) { results[index].StartPathMeters = results[index - 1].EndPathMeters; if (results[index].EndPathMeters <= results[index].StartPathMeters) results.RemoveAt(index--); }
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
            var firstCurve = trackSwitch.MainTrack.Path;
            // beside the toe of the points, on the side away from the branch
            float standSplineMeters = Mathf.Clamp(trackSwitch.MainPathMeters - trackSwitch.MainFacingDirection * StandBackDistanceInMeters, 0f, firstCurve.Length);
            firstCurve.Sample(standSplineMeters, out var point, out _);
            var mainTangent = Tangent(firstCurve, standSplineMeters);
            var side = Vector3.Cross(Vector3.up, mainTangent).normalized;               // right of +A
            Vector3 away;
            if (trackSwitch.BranchSideDirection.sqrMagnitude > 0.5f) away = -trackSwitch.BranchSideDirection;
            else
            {
                var branchEndPoint = trackSwitch.BranchJoinsAtEnd ? trackSwitch.BranchTrack.Path[trackSwitch.BranchTrack.Path.Count - 1] : trackSwitch.BranchTrack.Path[0];
                trackSwitch.BranchTrack.Path.Sample(trackSwitch.BranchJoinPathMeters + trackSwitch.BranchAwayDirection * BranchProbeDistanceInMeters, out var branchInnerPoint, out _);
                away = side * -Mathf.Sign(Vector3.Dot(branchInnerPoint - branchEndPoint, side));
            }
            trackSwitch.RailY = point.y;
            var standPosition = point + away * StandSideOffsetInMeters;
            standPosition.y = Il2CppHelpers.GroundBelow(standPosition + Vector3.up * StandGroundProbeLiftInMeters, StandGroundProbeDropInMeters).y - StandSinkInMeters;

            var root = new GameObject(StandPrefix + trackSwitch.Identifier);
            root.transform.position = standPosition;
            root.transform.rotation = Quaternion.LookRotation(mainTangent, Vector3.up) * (Vector3.Dot(away, side) > 0 ? Quaternion.identity : Quaternion.Euler(0f, OppositeSideYawInDegrees, 0f));
            trackSwitch.Stand = root;

            var model = LocomotiveModel.SpawnSwitch(root.transform);
            if (model != null)
            {
                trackSwitch.Lever = LocomotiveModel.FindDeep(model.transform, "Switch_Lever");
                trackSwitch.Target = LocomotiveModel.FindDeep(model.transform, "Switch_Target");
            }
            else SteamRailwayMod.Log.Warning($"Switch {trackSwitch.Identifier}: the stand model is missing from the bundle.");
            // which way the lever leans for the branch: towards the branch's side, measured on the model itself
            var branchSide = -away;
            if (trackSwitch.Lever != null)
            {
                var keep = trackSwitch.Lever.localRotation;
                trackSwitch.Lever.localRotation = Quaternion.Euler(0f, 0f, LeverThrow);
                var leverUp = trackSwitch.Lever.up; leverUp.y = 0f;
                trackSwitch.LeverDiv = Vector3.Dot(leverUp, branchSide) > 0f ? LeverThrow : -LeverThrow;
                trackSwitch.Lever.localRotation = keep;
            }
            var run = mainTangent * trackSwitch.MainFacingDirection;
            trackSwitch.BranchLeft = Vector3.Dot(branchSide, Vector3.Cross(Vector3.up, run)) < 0f;

            // the clickable part: hold to throw
            root.layer = LayerMask.NameToLayer("InteractiveProp");
            var box = root.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, StandColliderCenterHeightInMeters, 0f);
            box.size = new Vector3(StandColliderWidthInMeters, StandColliderHeightInMeters, StandColliderDepthInMeters);
            var hold = root.AddComponent<TimedHoldInteraction>();
            // everything the game's hover and hold code reads (a missing one throws in the HUD)
            hold.m_EventEntries = new Il2CppSystem.Collections.Generic.List<InteractionEventEntry>();
            hold.m_DefaultHoverText = Il2CppHelpers.Loc("GAMEPLAY_Use");
            hold.m_DefaultHoldText = Il2CppHelpers.Loc("GAMEPLAY_Use");
            hold.m_DefaultHoldTime = LeverHoldTimeInSeconds;
            hold.HoldTime = LeverHoldTimeInSeconds;
            hold.HoldText = Localizer.Translate("Makas çevriliyor...", "Throwing the switch...");
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
                foreach (var raycastHit in Physics.RaycastAll(new Vector3(samplePoint.x, top + StandProbeLiftInMeters, samplePoint.z), Vector3.down, StandProbeDropInMeters, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (raycastHit.point.y > top || raycastHit.point.y <= y) continue;
                    bool ours = false;
                    for (var node = raycastHit.collider.transform; node != null; node = node.parent)
                        if (node.name.StartsWith("SR_") || node.name.StartsWith("SteamRailway") || node.name.StartsWith(TrainBuilder.NamePrefix)) { ours = true; break; }
                    if (!ours) y = raycastHit.point.y;
                }
                if (y == float.MinValue) y = trackSwitch.RailY - RailPieces.RailTop;
                trackSwitch.Stand.transform.position = new Vector3(samplePoint.x, y - StandSinkInMeters, samplePoint.z);
            }
        }

        public static string HoverText(TrackSwitch trackSwitch) =>
            (trackSwitch.Diverging
                ? (trackSwitch.BranchLeft ? Localizer.Translate("Makas: SOLA (sapak hat)", "Switch: LEFT (branch)") : Localizer.Translate("Makas: SAĞA (sapak hat)", "Switch: RIGHT (branch)"))
                : (trackSwitch.BranchLeft ? Localizer.Translate("Makas: SAĞA / düz (ana hat)", "Switch: RIGHT / straight (main line)") : Localizer.Translate("Makas: SOLA / düz (ana hat)", "Switch: LEFT / straight (main line)")))
            + Localizer.Translate("  (basılı tut: çevir)", "  (hold: throw)");

        static Quaternion BladeRotation(TrackSwitch trackSwitch) => Quaternion.Euler(0f, trackSwitch.Diverging ? 0f : trackSwitch.BladeOpenAngleInDegrees, 0f);

        static void UpdateLook(TrackSwitch trackSwitch, bool immediate)
        {
            if (trackSwitch.Stand != null)
            {
                var interaction = trackSwitch.Stand.GetComponent<BaseInteraction>();
                if (interaction != null) interaction.HoverText = HoverText(trackSwitch);
                var holdInteraction = trackSwitch.Stand.GetComponent<TimedHoldInteraction>();
                if (holdInteraction != null)
                {
                    holdInteraction.HoldText = Localizer.Translate("Makas çevriliyor...", "Throwing the switch...");
                    if (holdInteraction.HoldTime <= 0f) holdInteraction.HoldTime = LeverHoldTimeInSeconds;
                }
            }
            if (immediate)
            {
                if (trackSwitch.Lever != null) trackSwitch.Lever.localRotation = Quaternion.Euler(0f, 0f, trackSwitch.Diverging ? trackSwitch.LeverDiv : -trackSwitch.LeverDiv);
                if (trackSwitch.Target != null) trackSwitch.Target.localRotation = Quaternion.Euler(0f, trackSwitch.Diverging ? TargetDivergingAngleInDegrees : 0f, 0f);
                if (trackSwitch.Blade != null && trackSwitch.RailsLaid) trackSwitch.Blade.localRotation = BladeRotation(trackSwitch);
            }
            else MelonLoader.MelonCoroutines.Start(Animate(trackSwitch));
        }

        static System.Collections.IEnumerator Animate(TrackSwitch trackSwitch)
        {
            float fraction = 0f;
            Quaternion leverStartRotation = trackSwitch.Lever != null ? trackSwitch.Lever.localRotation : Quaternion.identity;
            Quaternion targetStartRotation = trackSwitch.Target != null ? trackSwitch.Target.localRotation : Quaternion.identity;
            Quaternion bladeStartRotation = trackSwitch.Blade != null ? trackSwitch.Blade.localRotation : Quaternion.identity;
            var leverEndRotation = Quaternion.Euler(0f, 0f, trackSwitch.Diverging ? trackSwitch.LeverDiv : -trackSwitch.LeverDiv);
            var targetEndRotation = Quaternion.Euler(0f, trackSwitch.Diverging ? TargetDivergingAngleInDegrees : 0f, 0f);
            var bladeEndRotation = BladeRotation(trackSwitch);
            while (fraction < 1f)
            {
                fraction += Time.deltaTime / LeverMoveDurationInSeconds;
                float scale = Mathf.SmoothStep(0f, 1f, fraction);
                if (trackSwitch.Lever != null) trackSwitch.Lever.localRotation = Quaternion.Slerp(leverStartRotation, leverEndRotation, scale);
                if (trackSwitch.Target != null) trackSwitch.Target.localRotation = Quaternion.Slerp(targetStartRotation, targetEndRotation, Mathf.Clamp01(scale * TargetTurnSlope - TargetTurnOffset));
                if (trackSwitch.Blade != null && trackSwitch.RailsLaid) trackSwitch.Blade.localRotation = Quaternion.Slerp(bladeStartRotation, bladeEndRotation, Mathf.Clamp01(scale * BladeTurnSlope - BladeTurnOffset));
                yield return null;
            }
        }

        /// The player threw the lever of a stand (see Patches/SwitchPatches).
        public static bool Throw(GameObject stand)
        {
            var trackSwitch = s_All.Find(x => x.Stand == stand);
            if (trackSwitch == null) return false;
            trackSwitch.Diverging = !trackSwitch.Diverging;
            RailwayManager.SaveSwitch(trackSwitch.Identifier, trackSwitch.Diverging);
            UpdateLook(trackSwitch, false);
            TrainSounds.Play(TrainSounds.SwitchThrow, stand);
            CabControls.Message(HoverText(trackSwitch).Replace(Localizer.Translate("  (basılı tut: çevir)", "  (hold: throw)"), ""));
            return true;
        }

        // ------------------------------------------------------------------ trains

        /// Moves a train onto the other line when it runs over a junction. prev/pos: spline metres on its line.
        public static BuiltTrack Pass(LiveTrain liveTrain, BuiltTrack line, float previousPosition, float position)
        {
            if (!Cross(line, previousPosition, position, out var destination, out float destinationPosition, out int alongDestination)) return null;
            // the engine keeps its run (forward or reverse): its facing on the new line follows the way it moves there
            int run = liveTrain.Simulation.Velocity >= 0f ? 1 : -1;
            Move(liveTrain, destination, destinationPosition, alongDestination * run);
            return destination;
        }

        /// A vehicle moving from `previousPosition` to `position` (spline metres on `line`) over the points: where it goes on
        /// (false when it stays); trailing out of a branch pushes the points over to the branch.
        public static bool Cross(BuiltTrack line, float previousPosition, float position, out BuiltTrack destination, out float destinationPosition, out int alongDestination)
        {
            destination = null; destinationPosition = 0f; alongDestination = 0;
            if (Mathf.Abs(position - previousPosition) < MinimumMoveInMeters) return false;
            int moving = position > previousPosition ? 1 : -1;
            foreach (var trackSwitch in s_All)
            {
                if (trackSwitch.MainTrack == line && trackSwitch.Diverging && moving == trackSwitch.MainFacingDirection && (previousPosition - trackSwitch.MainSplineMeters) * (position - trackSwitch.MainSplineMeters) <= 0f)
                {
                    // facing points set for the branch: onto B, into it
                    float over = Mathf.Abs(position - trackSwitch.MainSplineMeters);
                    float branchLength = trackSwitch.BranchTrack.Spline.Length;
                    destination = trackSwitch.BranchTrack;
                    destinationPosition = trackSwitch.BranchJoinsAtEnd ? branchLength - over : over;
                    alongDestination = trackSwitch.BranchJoinsAtEnd ? -1 : 1;
                    return true;
                }
                if (trackSwitch.BranchTrack == line)
                {
                    float branchLength = trackSwitch.BranchTrack.Spline.Length;
                    bool leaving = trackSwitch.BranchJoinsAtEnd ? (moving > 0 && position >= branchLength - BranchExitMarginInMeters) : (moving < 0 && position <= BranchExitMarginInMeters);
                    if (!leaving) continue;
                    // trailing out of the branch onto the main line (the points are pushed over)
                    int alongMain = -trackSwitch.MainFacingDirection;
                    if (!trackSwitch.Diverging) { trackSwitch.Diverging = true; RailwayManager.SaveSwitch(trackSwitch.Identifier, true); UpdateLook(trackSwitch, false); }
                    destination = trackSwitch.MainTrack;
                    destinationPosition = trackSwitch.MainSplineMeters + alongMain * BranchExitMarginInMeters;
                    alongDestination = alongMain;
                    return true;
                }
            }
            return false;
        }

        /// Puts the train on another line at spline metres `pos`, standing with `facing`.
        static void Move(LiveTrain liveTrain, BuiltTrack destinationTrack, float position, int facing)
        {
            var splineController = liveTrain.Train.m_SplineController;
            splineController.Spline = destinationTrack.Spline;
            splineController.Position = Mathf.Clamp(position, SplineEdgeMarginInMeters, destinationTrack.Spline.Length - SplineEdgeMarginInMeters);
            liveTrain.Simulation.State.Facing = facing >= 0 ? 1 : -1;
            liveTrain.Simulation.EnforceFacing();
            liveTrain.RouteId = destinationTrack.Identifier;
            SteamRailwayMod.Debug($"Train {liveTrain.Guid} switched onto {destinationTrack.Identifier} at {splineController.Position:F1} m.");
        }

        /// The stand belonging to an interaction object (null if it is not one of ours).
        public static bool IsStand(GameObject targetObject) => targetObject != null && targetObject.name.StartsWith(StandPrefix);
    }
}
