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
            foreach (var d in defs) paths.Add(CurvePath.CatmullRom(d.Points));
            for (int bi = 0; bi < defs.Count; bi++)
            {
                var pts = defs[bi].Points;
                for (int end = 0; end < 2; end++)
                {
                    var p = end == 1 ? pts[pts.Count - 1] : pts[0];
                    if (!switchPoints.Exists(q => (q - p).sqrMagnitude < 2.25f)) continue;
                    // the main line under this end
                    int ai = -1; float best = 2f;
                    for (int k = 0; k < defs.Count; k++)
                    {
                        if (k == bi) continue;
                        float s = paths[k].NearestDistance(p, out float d);
                        if (d < best && s > 3f && s < paths[k].Length - 3f) { best = d; ai = k; }
                    }
                    if (ai < 0) continue;
                    var a = paths[ai];
                    float run = 0f;
                    int changed = 0;
                    for (int k = 0; k < pts.Count; k++)
                    {
                        int i = end == 1 ? pts.Count - 1 - k : k;
                        if (k > 0) run += Vector3.Distance(pts[i], pts[end == 1 ? i + 1 : i - 1]);
                        if (run > LevelLength + LevelBlend) break;
                        a.Sample(a.NearestDistance(pts[i], out _), out var pa, out _);
                        float w = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((run - LevelLength) / LevelBlend));
                        var q = pts[i];
                        q.y = Mathf.Lerp(pa.y, q.y, w);
                        pts[i] = q;
                        changed++;
                    }
                    SteamRailwayMod.Debug($"Branch {defs[bi].Id} levelled with {defs[ai].Id} at the points ({changed} points).");
                }
            }
        }

        /// Lines with an end lying on another line (branches leaving it).
        public static List<string> Branches(IList<TrackDef> defs)
        {
            var res = new List<string>();
            var paths = new List<CurvePath>();
            foreach (var d in defs) paths.Add(CurvePath.CatmullRom(d.Points));
            for (int bi = 0; bi < defs.Count; bi++)
            {
                if (defs[bi].HasCustom) continue;      // a drawn line joins where it was drawn to
                bool branch = false;
                for (int end = 0; end < 2 && !branch; end++)
                    for (int ai = 0; ai < defs.Count && !branch; ai++)
                        if (ai != bi && !res.Contains(defs[ai].Id) && Joins(paths[bi], end == 1, paths[ai], out _, out _)) branch = true;
                if (branch) res.Add(defs[bi].Id);
            }
            return res;
        }

        static readonly List<TrackSwitch> s_All = new List<TrackSwitch>();
        public static IReadOnlyList<TrackSwitch> All => s_All;

        public static void Clear()
        {
            foreach (var s in s_All) if (s.Stand != null) Object.Destroy(s.Stand);
            s_All.Clear();
        }

        static float ToSpline(BuiltTrack t, float pathDist) => pathDist * t.Spline.Length / Mathf.Max(1f, t.Path.Length);

        static Vector3 Tangent(CurvePath p, float d)
        {
            p.Sample(d, out _, out var tan);
            tan.y = 0f;
            return tan.normalized;
        }

        /// Does end `atEnd` of line b lie on line a (not at a's ends), heading the same way?
        static bool Joins(CurvePath b, bool atEnd, CurvePath a, out float sa, out float dot)
        {
            dot = 0f;
            var p = atEnd ? b[b.Count - 1] : b[0];
            sa = a.NearestDistance(p, out float d);
            if (d > MaxJoinDistance) return false;
            if (sa < 5f || sa > a.Length - 5f) return false;          // a plain end-to-end meeting
            a.Sample(sa, out var pa, out _);
            if (Mathf.Abs(pa.y - p.y) > 1.5f) return false;
            var inward = atEnd ? -Tangent(b, b.Length - 2f) : Tangent(b, 2f);
            dot = Vector3.Dot(inward, Tangent(a, sa));
            return Mathf.Abs(dot) >= Mathf.Cos(MaxJoinAngle * Mathf.Deg2Rad);
        }

        /// Before the splines are built: a branch's end is moved exactly onto the main line's centre (blended over
        /// its last metres), so its rails start in the main line's rails like the blades of real points.
        public static void SnapBranches(IList<TrackDef> defs)
        {
            var paths = new List<CurvePath>();
            foreach (var d in defs) paths.Add(CurvePath.CatmullRom(d.Points));
            for (int bi = 0; bi < defs.Count; bi++)
                for (int end = 0; end < 2; end++)
                    for (int ai = 0; ai < defs.Count; ai++)
                    {
                        if (ai == bi || !Joins(paths[bi], end == 1, paths[ai], out float sa, out _)) continue;
                        var pts = defs[bi].Points;
                        var p = end == 1 ? pts[pts.Count - 1] : pts[0];
                        paths[ai].Sample(sa, out var pa, out _);
                        var off = pa - p;
                        float run = 0f;
                        for (int k = 0; k < pts.Count; k++)
                        {
                            int i = end == 1 ? pts.Count - 1 - k : k;
                            if (k > 0) run += Vector3.Distance(pts[i], pts[end == 1 ? i + 1 : i - 1]);
                            if (run >= SnapLength) break;
                            pts[i] += off * (1f - Mathf.SmoothStep(0f, 1f, run / SnapLength));
                        }
                        paths[bi] = CurvePath.CatmullRom(pts);
                        SteamRailwayMod.Debug($"Branch {defs[bi].Id} snapped {off.magnitude:F2} m onto {defs[ai].Id}.");
                        break;
                    }
        }

        /// Finds every junction (an end of one line lying on another line, the two heading the same way) and works
        /// out its turnout. Call before the rails are laid.
        public static void Find(IList<BuiltTrack> tracks, Dictionary<string, bool> saved, System.Func<Vector3, bool> allowAt = null)
        {
            Clear();
            foreach (var b in tracks)
                for (int end = 0; end < 2; end++)
                {
                    bool atEnd = end == 1;
                    foreach (var a in tracks)
                    {
                        if (a == b || !Joins(b.Path, atEnd, a.Path, out float sa, out float dot)) continue;
                        if (allowAt != null && !allowAt(atEnd ? b.Path[b.Path.Count - 1] : b.Path[0])) continue;
                        float sSpline = ToSpline(a, sa);
                        string id = $"{a.Id}@{Mathf.RoundToInt(sSpline)}";
                        if (s_All.Exists(x => x.Id == id)) continue;
                        var sw = new TrackSwitch
                        {
                            Id = id, A = a, B = b, S = sSpline, SaPath = sa, DirA = dot > 0f ? 1 : -1, BAtEnd = atEnd,
                            Diverging = saved != null && saved.TryGetValue(id, out bool dv) && dv,
                            BJoin = atEnd ? b.Path.Length : 0f, InB = atEnd ? -1 : 1,
                        };
                        s_All.Add(sw);
                        try { Turnout(sw); }
                        catch (System.Exception e) { SteamRailwayMod.Log.Warning($"Turnout {id}: {e.Message}"); }
                        try { BuildStand(sw); }
                        catch (System.Exception e) { SteamRailwayMod.Log.Warning($"Switch stand {id}: {e.Message}"); }
                        SteamRailwayMod.Debug($"Switch {id}: branch {b.Id} ({(atEnd ? "end" : "start")}), points face {(sw.DirA > 0 ? "+" : "-")}, turnout {sw.ZoneB:F0} m.");
                    }
                }
        }

        /// Piece-space x axis of the rail laid along a path at d (the same frame RailPieces bends with).
        static Vector3 SideAxis(CurvePath p, float d)
        {
            p.Sample(d, out _, out var t);
            return Vector3.Cross(Vector3.up, -t).normalized;
        }

        static void Turnout(TrackSwitch sw)
        {
            var a = sw.A.Path; var b = sw.B.Path;
            float maxU = Mathf.Min(MaxZone, b.Length - 1f);
            float lastLat = 0f;
            for (float u = 0f; u <= maxU; u += 0.5f)
            {
                b.Sample(sw.BJoin + sw.InB * u, out var pb, out _);
                float sA = a.NearestDistance(pb, out _);
                a.Sample(sA, out var pa, out _);
                float lat = Vector3.Dot(pb - pa, SideAxis(a, sA));
                if (Mathf.Abs(lat) > ZoneLateral) break;
                sw.ExtA.Add(new Vector2(sA, lat));
                // angle between the two tracks here (A run towards the frog, B run away from the points)
                var ta = Tangent(a, sA) * sw.DirA;
                var tb = Tangent(b, sw.BJoin + sw.InB * u) * sw.InB;
                sw.YawA.Add(new Vector2(sA, Vector3.Angle(ta, tb) * Mathf.Deg2Rad));
                sw.ZoneB = u;
                lastLat = lat;
            }
            if (sw.ZoneB < 4f) { sw.ZoneB = 0f; sw.ExtA.Clear(); return; }
            sw.ExtA.Sort((x, y) => x.x.CompareTo(y.x));
            // each long sleeper turns by half the angle between the tracks (square to the line between them)
            float sideSign = Mathf.Sign(lastLat);
            for (int i = 0; i < sw.YawA.Count; i++)
                sw.YawA[i] = new Vector2(sw.YawA[i].x, -sw.DirA * sideSign * Mathf.Tan(sw.YawA[i].y * 0.5f));
            sw.YawA.Sort((x, y) => x.x.CompareTo(y.x));
            // where the two tracks part, both have their own sleepers: they must not run into each other
            for (float u = sw.ZoneB; u <= Mathf.Min(sw.ZoneB + 40f, b.Length - 0.5f); u += 0.5f)
            {
                float sB = sw.BJoin + sw.InB * u;
                b.Sample(sB, out var pb, out _);
                float sA = a.NearestDistance(pb, out _);
                a.Sample(sA, out var pa, out _);
                float lat = Vector3.Dot(pb - pa, SideAxis(a, sA));
                if (Mathf.Abs(lat) > CutUntil) break;
                sw.CutA.Add(new Vector2(sA, lat));
                sw.CutB.Add(new Vector2(sB, Vector3.Dot(pa - pb, SideAxis(b, sB))));
            }
            sw.CutA.Sort((x, y) => x.x.CompareTo(y.x));
            sw.CutB.Sort((x, y) => x.x.CompareTo(y.x));
            sw.SideB = SideAxis(a, sw.SaPath) * Mathf.Sign(lastLat);
            float uSide = Mathf.Min(sw.ZoneB, 3f);
            sw.BranchRail = Vector3.Dot(SideAxis(b, sw.BJoin + sw.InB * uSide) * 1f, sw.SideB) * 1f >= 0f ? 1 : -1;
            sw.BladeLen = Mathf.Min(BladeLength, sw.ZoneB);

            // the blade turns about its heel; set for the main line its toe stands off towards the branch side
            b.Sample(sw.BJoin, out var toe, out _);
            b.Sample(sw.BJoin + sw.InB * sw.BladeLen, out var heel, out _);
            var pivot = new GameObject("SwitchBlade").transform;
            pivot.SetParent(sw.B.Rails, false);
            pivot.position = heel;
            sw.Blade = pivot;
            var v = toe - heel; v.y = 0f;
            float sign = Mathf.Sign(Vector3.Dot(Vector3.Cross(Vector3.up, v), sw.SideB));
            sw.BladeOpenDeg = sign * Mathf.Rad2Deg * BladeOpen / Mathf.Max(1f, v.magnitude);
        }

        const float CutUntil = 4.3f;     // m between the centres: from here on the sleepers no longer touch
        const float MinReach = 1.12f;    // a sleeper always reaches past its rail's foot

        /// Signed reach of the sleepers on the other track's side at path metres s (0: whole sleepers).
        static float CutAt(List<Vector2> e, float s)
        {
            if (e.Count < 2 || s < e[0].x || s > e[e.Count - 1].x) return 0f;
            int lo = 0, hi = e.Count - 1;
            while (hi - lo > 1) { int m = (lo + hi) >> 1; if (e[m].x <= s) lo = m; else hi = m; }
            float t = e[hi].x - e[lo].x > 1e-4f ? (s - e[lo].x) / (e[hi].x - e[lo].x) : 0f;
            float lat = Mathf.Lerp(e[lo].y, e[hi].y, t);
            float reach = Mathf.Max(MinReach, Mathf.Abs(lat) * 0.5f - 0.05f);
            return Mathf.Sign(lat) * reach;
        }

        static float YawAt(TrackSwitch sw, float s)
        {
            var e = sw.YawA;
            if (e.Count < 2 || s < e[0].x || s > e[e.Count - 1].x) return 0f;
            int lo = 0, hi = e.Count - 1;
            while (hi - lo > 1) { int m = (lo + hi) >> 1; if (e[m].x <= s) lo = m; else hi = m; }
            float t = e[hi].x - e[lo].x > 1e-4f ? (s - e[lo].x) / (e[hi].x - e[lo].x) : 0f;
            return Mathf.Lerp(e[lo].y, e[hi].y, t);
        }

        /// Signed lengthening of A's sleepers at path metres s on A (0 outside the turnout).
        static float ExtAt(TrackSwitch sw, float s)
        {
            var e = sw.ExtA;
            if (e.Count < 2 || s < e[0].x || s > e[e.Count - 1].x) return 0f;
            int lo = 0, hi = e.Count - 1;
            while (hi - lo > 1) { int m = (lo + hi) >> 1; if (e[m].x <= s) lo = m; else hi = m; }
            float t = e[hi].x - e[lo].x > 1e-4f ? (s - e[lo].x) / (e[hi].x - e[lo].x) : 0f;
            return Mathf.Lerp(e[lo].y, e[hi].y, t);
        }

        /// How a line's turnouts change its rail, as functions of path metres, so the whole line is laid in one go
        /// and its sleepers keep one even rhythm through the points. null: the line has no turnout.
        public static LayOptions Combined(BuiltTrack t)
        {
            var asA = s_All.FindAll(sw => sw.A == t && sw.ZoneB > 0f);
            var asB = s_All.FindAll(sw => sw.B == t && sw.ZoneB > 0f);
            if (asA.Count == 0 && asB.Count == 0) return null;
            bool InZone(TrackSwitch sw, float d) { float z = sw.BJoin + sw.InB * sw.ZoneB; return d >= Mathf.Min(sw.BJoin, z) && d <= Mathf.Max(sw.BJoin, z); }
            bool InBlade(TrackSwitch sw, float d) { float k = sw.BJoin + sw.InB * sw.BladeLen; return d >= Mathf.Min(sw.BJoin, k) && d <= Mathf.Max(sw.BJoin, k); }
            return new LayOptions
            {
                SleeperExt = d => { foreach (var sw in asA) { float e = ExtAt(sw, d); if (e != 0f) return e; } return 0f; },
                SleeperYaw = d => { foreach (var sw in asA) { float y = YawAt(sw, d); if (y != 0f) return y; } return 0f; },
                SleeperCut = d =>
                {
                    foreach (var sw in asA) { float c = CutAt(sw.CutA, d); if (c != 0f) return c; }
                    foreach (var sw in asB) { float c = CutAt(sw.CutB, d); if (c != 0f) return c; }
                    return 0f;
                },
                // under the points the branch has no sleepers of its own (the main line's long ones carry it)
                SleeperKeep = asB.Count == 0 ? null : (System.Func<float, bool>)(d => { foreach (var sw in asB) if (InZone(sw, d)) return false; return true; }),
                // the branch's rails at the points are laid apart (one of them is the moving blade)
                RailKeep = asB.Count == 0 ? null : (System.Func<float, bool>)(d => { foreach (var sw in asB) if (InBlade(sw, d)) return false; return true; }),
            };
        }

        /// The branch rails at the points: the fixed one and the moving blade (rails only).
        public static List<TurnoutRange> Blades(BuiltTrack t)
        {
            var res = new List<TurnoutRange>();
            foreach (var sw in s_All)
            {
                if (sw.B != t || sw.ZoneB <= 0f) continue;
                float j = sw.BJoin, k = sw.BJoin + sw.InB * sw.BladeLen;
                res.Add(new TurnoutRange { From = Mathf.Min(j, k), To = Mathf.Max(j, k), Options = new[]
                {
                    new LayOptions { Sleepers = false, RailSide = sw.BranchRail, YOffset = -0.004f },
                    new LayOptions { Sleepers = false, RailSide = -sw.BranchRail, YOffset = -0.003f, Parent = sw.Blade },
                } });
            }
            return res;
        }

        /// The stretches of a line its turnouts lay differently, sorted, not overlapping.
        public static List<TurnoutRange> TurnoutRanges(BuiltTrack t)
        {
            var res = new List<TurnoutRange>();
            foreach (var sw in s_All)
            {
                if (sw.ZoneB <= 0f) continue;
                if (sw.A == t)
                {
                    var s = sw;
                    res.Add(new TurnoutRange { From = sw.ExtA[0].x, To = sw.ExtA[sw.ExtA.Count - 1].x,
                        Options = new[] { new LayOptions { SleeperExt = d => ExtAt(s, d), SleeperYaw = d => YawAt(s, d) } } });
                    if (sw.CutA.Count >= 2)
                        res.Add(new TurnoutRange { From = sw.CutA[0].x, To = sw.CutA[sw.CutA.Count - 1].x,
                            Options = new[] { new LayOptions { SleeperCut = d => CutAt(s.CutA, d) } } });
                }
                if (sw.B == t)
                {
                    float j = sw.BJoin, k = sw.BJoin + sw.InB * sw.BladeLen, z = sw.BJoin + sw.InB * sw.ZoneB;
                    // under the points: the branch-side rail stays put, the other rail is the moving blade
                    res.Add(new TurnoutRange { From = Mathf.Min(j, k), To = Mathf.Max(j, k), Options = new[]
                    {
                        new LayOptions { Sleepers = false, RailSide = sw.BranchRail, YOffset = -0.004f },
                        new LayOptions { Sleepers = false, RailSide = -sw.BranchRail, YOffset = -0.003f, Parent = sw.Blade },
                    } });
                    // on to the frog: rails on A's long sleepers
                    res.Add(new TurnoutRange { From = Mathf.Min(k, z), To = Mathf.Max(k, z),
                        Options = new[] { new LayOptions { Sleepers = false, YOffset = -0.004f } } });
                    var sb = sw;
                    if (sw.CutB.Count >= 2)
                        res.Add(new TurnoutRange { From = sw.CutB[0].x, To = sw.CutB[sw.CutB.Count - 1].x,
                            Options = new[] { new LayOptions { SleeperCut = d => CutAt(sb.CutB, d) } } });
                }
            }
            res.Sort((x, y) => x.From.CompareTo(y.From));
            // two turnouts on one stretch: the first wins
            for (int i = 1; i < res.Count; i++)
                if (res[i].From < res[i - 1].To) { res[i].From = res[i - 1].To; if (res[i].To <= res[i].From) res.RemoveAt(i--); }
            return res;
        }

        /// After the rails are laid: the blades take their positions.
        public static void RailsLaid()
        {
            foreach (var sw in s_All) { sw.RailsLaid = true; UpdateLook(sw, true); }
        }

        // ------------------------------------------------------------------ the stand

        static void BuildStand(TrackSwitch sw)
        {
            var a = sw.A.Path;
            // beside the toe of the points, on the side away from the branch
            float sStand = Mathf.Clamp(sw.SaPath - sw.DirA * 3f, 0f, a.Length);
            a.Sample(sStand, out var p, out _);
            var ta = Tangent(a, sStand);
            var side = Vector3.Cross(Vector3.up, ta).normalized;               // right of +A
            Vector3 away;
            if (sw.SideB.sqrMagnitude > 0.5f) away = -sw.SideB;
            else
            {
                var bp = sw.BAtEnd ? sw.B.Path[sw.B.Path.Count - 1] : sw.B.Path[0];
                sw.B.Path.Sample(sw.BJoin + sw.InB * 15f, out var bIn, out _);
                away = side * -Mathf.Sign(Vector3.Dot(bIn - bp, side));
            }
            sw.RailY = p.y;
            var pos = p + away * 2.7f;
            pos.y = H.GroundBelow(pos + Vector3.up * 3f, 10f).y - 0.1f;

            var root = new GameObject(StandPrefix + sw.Id);
            root.transform.position = pos;
            root.transform.rotation = Quaternion.LookRotation(ta, Vector3.up) * (Vector3.Dot(away, side) > 0 ? Quaternion.identity : Quaternion.Euler(0f, 180f, 0f));
            sw.Stand = root;

            var model = LocomotiveModel.SpawnSwitch(root.transform);
            if (model != null)
            {
                sw.Lever = LocomotiveModel.FindDeep(model.transform, "Switch_Lever");
                sw.Target = LocomotiveModel.FindDeep(model.transform, "Switch_Target");
            }
            else SteamRailwayMod.Log.Warning($"Switch {sw.Id}: the stand model is missing from the bundle.");
            // which way the lever leans for the branch: towards the branch's side, measured on the model itself
            var branchSide = -away;
            if (sw.Lever != null)
            {
                var keep = sw.Lever.localRotation;
                sw.Lever.localRotation = Quaternion.Euler(0f, 0f, LeverThrow);
                var up = sw.Lever.up; up.y = 0f;
                sw.LeverDiv = Vector3.Dot(up, branchSide) > 0f ? LeverThrow : -LeverThrow;
                sw.Lever.localRotation = keep;
            }
            var run = ta * sw.DirA;
            sw.BranchLeft = Vector3.Dot(branchSide, Vector3.Cross(Vector3.up, run)) < 0f;

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
            UpdateLook(sw, true);
        }

        /// After the snow along the lines was lowered or filled: every stand is put back on the ground.
        public static void GroundStands()
        {
            foreach (var sw in s_All)
            {
                if (sw.Stand == null) continue;
                var p = sw.Stand.transform.position;
                // the highest solid surface not above the rails (terrain or a ground mesh; not branches or wires)
                float top = sw.RailY + 0.5f, y = float.MinValue;
                foreach (var h in Physics.RaycastAll(new Vector3(p.x, top + 30f, p.z), Vector3.down, 80f, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (h.point.y > top || h.point.y <= y) continue;
                    bool ours = false;
                    for (var x = h.collider.transform; x != null; x = x.parent)
                        if (x.name.StartsWith("SR_") || x.name.StartsWith("SteamRailway") || x.name.StartsWith(TrainBuilder.NamePrefix)) { ours = true; break; }
                    if (!ours) y = h.point.y;
                }
                if (y == float.MinValue) y = sw.RailY - RailPieces.RailTop;
                sw.Stand.transform.position = new Vector3(p.x, y - 0.1f, p.z);
            }
        }

        public static string HoverText(TrackSwitch sw) =>
            (sw.Diverging
                ? (sw.BranchLeft ? L.T("Makas: SOLA (sapak hat)", "Switch: LEFT (branch)") : L.T("Makas: SAĞA (sapak hat)", "Switch: RIGHT (branch)"))
                : (sw.BranchLeft ? L.T("Makas: SAĞA / düz (ana hat)", "Switch: RIGHT / straight (main line)") : L.T("Makas: SOLA / düz (ana hat)", "Switch: LEFT / straight (main line)")))
            + L.T("  (basılı tut: çevir)", "  (hold: throw)");

        static Quaternion BladeRot(TrackSwitch sw) => Quaternion.Euler(0f, sw.Diverging ? 0f : sw.BladeOpenDeg, 0f);

        static void UpdateLook(TrackSwitch sw, bool immediate)
        {
            if (sw.Stand != null)
            {
                var bi = sw.Stand.GetComponent<BaseInteraction>();
                if (bi != null) bi.HoverText = HoverText(sw);
                var th = sw.Stand.GetComponent<TimedHoldInteraction>();
                if (th != null)
                {
                    th.HoldText = L.T("Makas çevriliyor...", "Throwing the switch...");
                    if (th.HoldTime <= 0f) th.HoldTime = 1.2f;
                }
            }
            if (immediate)
            {
                if (sw.Lever != null) sw.Lever.localRotation = Quaternion.Euler(0f, 0f, sw.Diverging ? sw.LeverDiv : -sw.LeverDiv);
                if (sw.Target != null) sw.Target.localRotation = Quaternion.Euler(0f, sw.Diverging ? 90f : 0f, 0f);
                if (sw.Blade != null && sw.RailsLaid) sw.Blade.localRotation = BladeRot(sw);
            }
            else MelonLoader.MelonCoroutines.Start(Animate(sw));
        }

        static System.Collections.IEnumerator Animate(TrackSwitch sw)
        {
            float t = 0f;
            Quaternion l0 = sw.Lever != null ? sw.Lever.localRotation : Quaternion.identity;
            Quaternion g0 = sw.Target != null ? sw.Target.localRotation : Quaternion.identity;
            Quaternion b0 = sw.Blade != null ? sw.Blade.localRotation : Quaternion.identity;
            var l1 = Quaternion.Euler(0f, 0f, sw.Diverging ? sw.LeverDiv : -sw.LeverDiv);
            var g1 = Quaternion.Euler(0f, sw.Diverging ? 90f : 0f, 0f);
            var b1 = BladeRot(sw);
            while (t < 1f)
            {
                t += Time.deltaTime / 0.6f;
                float k = Mathf.SmoothStep(0f, 1f, t);
                if (sw.Lever != null) sw.Lever.localRotation = Quaternion.Slerp(l0, l1, k);
                if (sw.Target != null) sw.Target.localRotation = Quaternion.Slerp(g0, g1, Mathf.Clamp01(k * 1.4f - 0.2f));
                if (sw.Blade != null && sw.RailsLaid) sw.Blade.localRotation = Quaternion.Slerp(b0, b1, Mathf.Clamp01(k * 1.25f - 0.25f));
                yield return null;
            }
        }

        /// The player threw the lever of a stand (see Patches/SwitchPatches).
        public static bool Throw(GameObject stand)
        {
            var sw = s_All.Find(x => x.Stand == stand);
            if (sw == null) return false;
            sw.Diverging = !sw.Diverging;
            RailwayManager.SaveSwitch(sw.Id, sw.Diverging);
            UpdateLook(sw, false);
            TrainSounds.Play(TrainSounds.SwitchThrow, stand);
            CabControls.Message(HoverText(sw).Replace(L.T("  (basılı tut: çevir)", "  (hold: throw)"), ""));
            return true;
        }

        // ------------------------------------------------------------------ trains

        /// Moves a train onto the other line when it runs over a junction. prev/pos: spline metres on its line.
        public static BuiltTrack Pass(LiveTrain t, BuiltTrack line, float prev, float pos)
        {
            if (Mathf.Abs(pos - prev) < 1e-4f) return null;
            int moving = pos > prev ? 1 : -1;
            float v = t.Sim.Velocity;
            int run = v >= 0f ? 1 : -1;
            foreach (var sw in s_All)
            {
                if (sw.A == line && sw.Diverging && moving == sw.DirA && (prev - sw.S) * (pos - sw.S) <= 0f)
                {
                    // facing points set for the branch: onto B, into it
                    float over = Mathf.Abs(pos - sw.S);
                    float lenB = sw.B.Spline.Length;
                    int inB = sw.BAtEnd ? -1 : 1;
                    Move(t, sw.B, sw.BAtEnd ? lenB - over : over, inB * run);
                    return sw.B;
                }
                if (sw.B == line)
                {
                    float lenB = sw.B.Spline.Length;
                    bool leaving = sw.BAtEnd ? (moving > 0 && pos >= lenB - 0.3f) : (moving < 0 && pos <= 0.3f);
                    if (!leaving) continue;
                    // trailing out of the branch onto the main line (the points are pushed over)
                    int alongA = -sw.DirA;
                    if (!sw.Diverging) { sw.Diverging = true; RailwayManager.SaveSwitch(sw.Id, true); UpdateLook(sw, false); }
                    Move(t, sw.A, sw.S + alongA * 0.3f, alongA * run);
                    return sw.A;
                }
            }
            return null;
        }

        /// Puts the train on another line at spline metres `pos`, standing with `facing`.
        static void Move(LiveTrain t, BuiltTrack to, float pos, int facing)
        {
            var sc = t.Train.m_SplineController;
            sc.Spline = to.Spline;
            sc.Position = Mathf.Clamp(pos, 0.05f, to.Spline.Length - 0.05f);
            t.Sim.S.Facing = facing >= 0 ? 1 : -1;
            t.Sim.EnforceFacing();
            t.RouteId = to.Id;
            SteamRailwayMod.Debug($"Train {t.Guid} switched onto {to.Id} at {sc.Position:F1} m.");
        }

        /// The stand belonging to an interaction object (null if it is not one of ours).
        public static bool IsStand(GameObject go) => go != null && go.name.StartsWith(StandPrefix);
    }
}
