using System.Collections.Generic;
using UnityEngine;

namespace SteamRailway
{
    /// One railway line found in a region: control points along the (smoothed) rail-top centre, and the stretches
    /// where an original piece that carries a structure (a trestle bridge) stays; everywhere else the line gets
    /// continuous new rail and the original pieces are hidden.
    internal sealed class RailLine
    {
        public readonly List<Vector3> Points = new List<Vector3>();
        public readonly List<KeyValuePair<Vector3, Vector3>> Keep = new List<KeyValuePair<Vector3, Vector3>>();
        public readonly List<Transform> Replaced = new List<Transform>();   // original pieces under the new rail
        public readonly List<PieceRef> Used = new List<PieceRef>();          // every original piece of the line (saved)
        public float Length;
    }

    internal sealed class PieceRef
    {
        public string Name { get; set; }
        public float[] Pos { get; set; }
        public bool Keep { get; set; }
    }

    /// A region's lines as written to Mods\SteamRailway\Lines\<region>.json: made once from the original pieces,
    /// then read back. Set Locked to keep hand edits when the mod's
    /// line finder changes.
    internal sealed class LineFile
    {
        public int Version { get; set; }
        public bool Locked { get; set; }
        public List<LineData> Lines { get; set; } = new List<LineData>();
    }

    internal sealed class LineData
    {
        public List<float[]> Points { get; set; } = new List<float[]>();
        public List<float[]> Keep { get; set; } = new List<float[]>();
        public List<PieceRef> Pieces { get; set; } = new List<PieceRef>();
    }

    /// Reads the game's own track pieces in the loaded region and joins them into lines. Every piece type
    /// (straights, bends, trestle bridge pieces ...) gets its centreline from its own mesh: the rail heads are the
    /// highest vertices, and they lie either on two parallel lines or on two concentric arcs.
    internal static class OriginalRails
    {
        const float MaxGap = 80f;          // farthest two pieces may be apart and still belong to one line
        const float MaxOverlap = 30f;      // the artists overlap neighbouring pieces by up to ~15 m
        const float PointSpacing = 8f;
        const float MinLineLength = 30f;     // one original piece is enough (a short siding behind a switch)
        const float BendPenalty = 25f;     // at a switch the straight road wins over the bend into the siding

        /// Centreline of one piece type in its own space, start to end, at rail-head height.
        sealed class Shape
        {
            public Vector3[] Pts;
            public bool Bend;
            public bool Keep;              // carries a structure (bridge) besides the rails: never replaced
        }

        sealed class Piece
        {
            public int Id;                 // the piece's transform
            public Transform T;
            public Vector3[] Pts;          // world centreline, A = Pts[0], B = Pts[^1]
            public bool Bend;
            public bool Keep;
            public readonly List<Transform> Dups = new List<Transform>();   // the same piece placed again
            public Vector3 A => Pts[0];
            public Vector3 B => Pts[Pts.Length - 1];
            public Vector3 OutA => Flat(Pts[0] - Pts[1]);
            public Vector3 OutB => Flat(Pts[Pts.Length - 1] - Pts[Pts.Length - 2]);
        }

        static readonly Dictionary<int, Shape> s_Shapes = new Dictionary<int, Shape>();   // by mesh instance (names repeat)
        static readonly HashSet<int> s_UsedPieces = new HashSet<int>();                    // piece transforms on a line

        static Vector3 Flat(Vector3 v) { v.y = 0f; return v.normalized; }

        /// Track pieces the train can run on (broken rails, buffer stops and groups are scenery).
        static bool IsTrackPiece(string n) =>
            n.StartsWith("OBJ_RailTrack") && n.Contains("Prefab") &&
            !n.Contains("Broken") && !n.Contains("Stop") && !n.Contains("Group");

        public static List<RailLine> Find()
        {
            var pieces = new List<Piece>();
            foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (t.gameObject.scene.name == null || !t.gameObject.activeInHierarchy || !IsTrackPiece(t.name)) continue;
                var shape = ShapeOf(t, out var space);
                if (shape == null) continue;
                var w = new Vector3[shape.Pts.Length];
                for (int i = 0; i < w.Length; i++) w[i] = space.TransformPoint(shape.Pts[i]);
                var piece = new Piece { Pts = w, Bend = shape.Bend, Keep = shape.Keep, Id = t.GetInstanceID(), T = t };
                // the same piece placed twice (it happens in the regions) would split the line
                var twin = pieces.Find(o => Same(o, piece));
                if (twin != null) { twin.Dups.Add(t); continue; }
                pieces.Add(piece);
            }
            var lines = new List<RailLine>();
            s_UsedPieces.Clear();
            if (pieces.Count == 0) return lines;

            // ---- link piece ends (end index = piece * 2 + side, side 0 = A, 1 = B)
            int ends = pieces.Count * 2;
            var link = new int[ends];
            for (int i = 0; i < ends; i++) link[i] = -1;
            var cand = new List<(float score, int e, int f)>();
            for (int e = 0; e < ends; e++)
            for (int f = e + 1; f < ends; f++)
            {
                if (e / 2 == f / 2) continue;
                if (Match(pieces, e, f, out float s1) && Match(pieces, f, e, out float s2)) cand.Add((s1 + s2, e, f));
            }
            cand.Sort((x, y) => x.score.CompareTo(y.score));
            foreach (var c in cand)
                if (link[c.e] < 0 && link[c.f] < 0) { link[c.e] = c.f; link[c.f] = c.e; }

            // ---- walk the chains
            var used = new bool[pieces.Count];
            for (int pass = 0; pass < 2; pass++)
            for (int start = 0; start < pieces.Count; start++)
            {
                if (used[start]) continue;
                int entrySide;
                if (link[start * 2] < 0) entrySide = 0;
                else if (link[start * 2 + 1] < 0) entrySide = 1;
                else if (pass == 1) entrySide = 0;      // closed loop
                else continue;
                var chain = new List<(Piece p, bool reversed)>();
                int cur = start, side = entrySide;
                while (cur >= 0 && !used[cur])
                {
                    used[cur] = true;
                    chain.Add((pieces[cur], side == 1));
                    int exitEnd = cur * 2 + (1 - side);
                    int next = link[exitEnd];
                    if (next < 0) break;
                    cur = next / 2;
                    side = next % 2;
                }
                var line = Build(chain);
                if (line != null && line.Length >= MinLineLength)
                {
                    lines.Add(line);
                    foreach (var c in chain)
                    {
                        s_UsedPieces.Add(c.p.Id);
                        line.Used.Add(Ref(c.p.T, c.p.Keep));
                        if (!c.p.Keep) line.Replaced.Add(c.p.T);
                        foreach (var d in c.p.Dups)
                        {
                            s_UsedPieces.Add(d.GetInstanceID());
                            line.Used.Add(Ref(d, c.p.Keep));
                            if (!c.p.Keep) line.Replaced.Add(d);
                        }
                    }
                }
            }
            lines.Sort((x, y) => y.Length.CompareTo(x.Length));
            SteamRailwayMod.Debug($"Original rails: {pieces.Count} pieces -> {lines.Count} line(s).");
            return lines;
        }

        // ------------------------------------------------------------------ piece shapes

        /// The piece's centreline and the transform it is expressed in.
        static Shape ShapeOf(Transform piece, out Transform space)
        {
            space = piece;
            // Straight pieces whose visuals the game draws itself (the prefab only carries a collider).
            var mf = FindMeshFilter(piece);
            if (mf == null)
            {
                if (!piece.name.Contains("Strait") && !piece.name.Contains("Straight")) return null;
                return StraightA();
            }
            space = mf.transform;
            var mesh = mf.sharedMesh;
            if (s_Shapes.TryGetValue(mesh.GetInstanceID(), out var cached)) return cached;
            Shape shape = null;
            try { shape = Fit(mesh); }
            catch (System.Exception e) { SteamRailwayMod.Log.Warning($"Rail piece {mesh.name}: {e.Message}"); }
            // geometry well below the rails = a bridge or trestle built into the piece
            if (shape != null) shape.Keep = mesh.bounds.min.y < -1.0f;
            s_Shapes[mesh.GetInstanceID()] = shape;
            if (shape != null)
                SteamRailwayMod.Debug($"Rail piece {mesh.name}: {(shape.Bend ? "bend" : "straight")}, {shape.Pts.Length} points, {Length(shape.Pts):F1} m.");
            return shape;
        }

        static Shape s_StraightA;
        static Shape StraightA()
        {
            if (s_StraightA != null) return s_StraightA;
            var a = new Vector3(RailPieces.CentreX, RailPieces.RailTop, RailPieces.SrcZ0);
            var b = new Vector3(RailPieces.CentreX, RailPieces.RailTop, RailPieces.SrcZ1);
            return s_StraightA = new Shape { Pts = new[] { a, b }, Bend = false };
        }

        /// The most detailed readable mesh of the piece (LOD0).
        static MeshFilter FindMeshFilter(Transform piece)
        {
            MeshFilter best = null;
            foreach (var mf in piece.GetComponentsInChildren<MeshFilter>(true))
            {
                var m = mf.sharedMesh;
                if (m == null || !m.isReadable) continue;
                if (best == null || m.vertexCount > best.sharedMesh.vertexCount) best = mf;
            }
            return best;
        }

        /// Rail heads = the highest vertices. Two parallel lines -> straight; two concentric arcs -> bend.
        static Shape Fit(Mesh mesh)
        {
            var verts = mesh.vertices;
            float ymax = float.MinValue;
            for (int i = 0; i < verts.Length; i++) ymax = Mathf.Max(ymax, verts[i].y);
            var top = new List<Vector2>();
            for (int i = 0; i < verts.Length; i++)
                if (verts[i].y > ymax - 0.03f) top.Add(new Vector2(verts[i].x, verts[i].z));
            if (top.Count < 8) return null;
            float y = ymax;

            // straight? principal axis of the rail-head points
            Vector2 mean = Vector2.zero;
            foreach (var p in top) mean += p;
            mean /= top.Count;
            float sxx = 0, sxz = 0, szz = 0;
            foreach (var p in top) { var d = p - mean; sxx += d.x * d.x; sxz += d.x * d.y; szz += d.y * d.y; }
            float ang = 0.5f * Mathf.Atan2(2f * sxz, sxx - szz);
            var axis = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));
            var side = new Vector2(-axis.y, axis.x);
            var lat = new List<float>();
            float amin = float.MaxValue, amax = float.MinValue;
            foreach (var p in top)
            {
                var d = p - mean;
                lat.Add(Vector2.Dot(d, side));
                float a = Vector2.Dot(d, axis);
                amin = Mathf.Min(amin, a); amax = Mathf.Max(amax, a);
            }
            if (TwoClusters(lat, out float l1, out float l2, out float err) && err < 0.003f && Mathf.Abs(l2 - l1) > 1.2f)
            {
                var c = mean + side * ((l1 + l2) * 0.5f);
                var p0 = c + axis * amin; var p1 = c + axis * amax;
                return new Shape { Pts = new[] { new Vector3(p0.x, y, p0.y), new Vector3(p1.x, y, p1.y) }, Bend = false };
            }

            // bend: centre where the rail heads fall on two circles 1.5 - 1.9 m apart
            Vector2 centre = Vector2.zero;
            float best = float.MaxValue;
            // the centre may lie far from the piece (trestle pieces are placed around it): coarse to fine search
            float[] radius = { 300f, 12f, 1.2f }, stepOf = { 6f, 0.4f, 0.03f };
            var rs = new List<float>(top.Count);
            for (int pass = 0; pass < 3; pass++)
            {
                var from = pass == 0 ? mean : centre;
                float r = radius[pass], step = stepOf[pass];
                float lo = pass == 0 ? 0.8f : 1.3f, hi = pass == 0 ? 3f : 2.1f;
                best = float.MaxValue;
                for (float x = -r; x <= r; x += step)
                for (float z = -r; z <= r; z += step)
                {
                    var cc = from + new Vector2(x, z);
                    rs.Clear();
                    foreach (var p in top) rs.Add((p - cc).magnitude);
                    if (!TwoClusters(rs, out float a1, out float a2, out float e2)) continue;
                    float g = Mathf.Abs(a2 - a1);
                    if (g < lo || g > hi) continue;
                    if (e2 < best) { best = e2; centre = cc; }
                }
                if (best == float.MaxValue) return null;
            }
            if (best == float.MaxValue) return null;
            var radii = new List<float>();
            foreach (var p in top) radii.Add((p - centre).magnitude);
            TwoClusters(radii, out float r1, out float r2, out _);
            float rc = (r1 + r2) * 0.5f;
            // angle range around the mean direction (no wrap-around)
            var md = (mean - centre).normalized;
            float baseAng = Mathf.Atan2(md.y, md.x);
            float tmin = float.MaxValue, tmax = float.MinValue;
            foreach (var p in top)
            {
                var d = p - centre;
                float t = Mathf.DeltaAngle(baseAng * Mathf.Rad2Deg, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg) * Mathf.Deg2Rad;
                tmin = Mathf.Min(tmin, t); tmax = Mathf.Max(tmax, t);
            }
            float arc = (tmax - tmin) * rc;
            int n = Mathf.Max(2, Mathf.CeilToInt(arc / 2f) + 1);
            var pts = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                float t = baseAng + Mathf.Lerp(tmin, tmax, i / (float)(n - 1));
                pts[i] = new Vector3(centre.x + Mathf.Cos(t) * rc, y, centre.y + Mathf.Sin(t) * rc);
            }
            return new Shape { Pts = pts, Bend = true };
        }

        /// Splits sorted values at the largest gap; error = mean squared distance to the two cluster means.
        static bool TwoClusters(List<float> v, out float m1, out float m2, out float err)
        {
            m1 = m2 = err = 0f;
            if (v.Count < 4) return false;
            var s = new List<float>(v);
            s.Sort();
            int split = 1; float gap = 0f;
            for (int i = 1; i < s.Count; i++) if (s[i] - s[i - 1] > gap) { gap = s[i] - s[i - 1]; split = i; }
            if (split < 2 || s.Count - split < 2) return false;
            for (int i = 0; i < split; i++) m1 += s[i];
            for (int i = split; i < s.Count; i++) m2 += s[i];
            m1 /= split; m2 /= s.Count - split;
            for (int i = 0; i < split; i++) err += (s[i] - m1) * (s[i] - m1);
            for (int i = split; i < s.Count; i++) err += (s[i] - m2) * (s[i] - m2);
            err /= s.Count;
            return true;
        }

        static float Length(Vector3[] p)
        {
            float l = 0f;
            for (int i = 1; i < p.Length; i++) l += Vector3.Distance(p[i - 1], p[i]);
            return l;
        }

        // ------------------------------------------------------------------ joining

        static bool Same(Piece a, Piece b) =>
            (Vector3.Distance(a.A, b.A) < 0.5f && Vector3.Distance(a.B, b.B) < 0.5f) ||
            (Vector3.Distance(a.A, b.B) < 0.5f && Vector3.Distance(a.B, b.A) < 0.5f);

        static Vector3 EndPos(List<Piece> ps, int e) => e % 2 == 0 ? ps[e / 2].A : ps[e / 2].B;
        static Vector3 Outward(List<Piece> ps, int e) => e % 2 == 0 ? ps[e / 2].OutA : ps[e / 2].OutB;

        /// Can end e continue into end f? (lower score = better)
        static bool Match(List<Piece> ps, int e, int f, out float score)
        {
            score = 0f;
            var pe = EndPos(ps, e);
            var v = EndPos(ps, f) - pe;
            v.y = 0f;
            float dist = v.magnitude;
            if (dist > MaxGap) return false;
            if (Mathf.Abs(EndPos(ps, f).y - pe.y) > 3f + 0.1f * dist) return false;   // not a line at another level
            var oe = Outward(ps, e);
            var of = Outward(ps, f);
            if (Vector3.Dot(oe, -of) < 0.5f) return false;         // the next piece must head on (within ~60°)
            float along = Vector3.Dot(v, oe);
            if (along < -MaxOverlap) return false;
            // overlapping pieces: how far end f lies from piece e's own centreline (a bend's end tangent overshoots)
            float lateral = along < 0f ? DistanceToPiece(ps[e / 2], EndPos(ps, f)) : (v - oe * along).magnitude;
            if (lateral > 3f + 1.2f * Mathf.Max(along, 0f)) return false;   // bends drift sideways
            score = dist + 2f * lateral + (ps[f / 2].Bend ? BendPenalty : 0f);
            return true;
        }

        static float DistanceToPiece(Piece p, Vector3 q)
        {
            float best = float.MaxValue;
            var q2 = new Vector2(q.x, q.z);
            for (int i = 0; i < p.Pts.Length - 1; i++)
            {
                var a = new Vector2(p.Pts[i].x, p.Pts[i].z);
                var ab = new Vector2(p.Pts[i + 1].x, p.Pts[i + 1].z) - a;
                float t = Mathf.Clamp01(Vector2.Dot(q2 - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-6f));
                best = Mathf.Min(best, (q2 - a - ab * t).magnitude);
            }
            return best;
        }

        static RailLine Build(List<(Piece p, bool reversed)> chain)
        {
            if (chain.Count == 0) return null;
            var raw = new List<Vector3>();
            var pin = new List<bool>();          // points on pieces that stay (bridges): not moved by smoothing
            float skip = 0f;   // metres of the next piece already covered by the previous one (overlap)
            for (int i = 0; i < chain.Count; i++)
            {
                var (p, rev) = chain[i];
                var cl = Oriented(p, rev);
                AddPolyline(raw, pin, cl, skip, p.Keep);
                skip = 0f;
                if (i == chain.Count - 1) break;

                var ex = cl[cl.Length - 1];
                var dir = Flat(ex - cl[cl.Length - 2]);
                var (q, qrev) = chain[i + 1];
                var cl2 = Oriented(q, qrev);
                var en2 = cl2[0];
                var dir2 = Flat(cl2[1] - cl2[0]);
                float along = Vector3.Dot(en2 - ex, dir);
                if (along < 1f)
                {
                    // overlapping pieces: carry on along the next piece from where this one ends
                    skip = Mathf.Max(0f, Vector3.Dot(ex - en2, dir2)) + 1f;
                    continue;
                }
                // gap between two pieces: smooth Hermite curve
                float g = Vector3.Distance(ex, en2);
                int steps = Mathf.Max(1, Mathf.CeilToInt(g / 6f));
                for (int k = 1; k < steps; k++) { raw.Add(Hermite(ex, dir * g, en2, dir2 * g, k / (float)steps)); pin.Add(false); }
            }
            if (raw.Count < 2) return null;

            // Smooth: the pieces' ends do not quite meet (steps of a few cm to tens of cm), which jolts the train
            // and shows as kinks. Resample finely, then Taubin smoothing (no shrinking of the real curves).
            Resample(raw, pin, SmoothStep, out var pts, out var fixedPt);
            Taubin(pts, fixedPt, SmoothIterations);

            var line = new RailLine();
            // control points every 2nd sample (4 m), always the ends
            for (int i = 0; i < pts.Count; i += 2) line.Points.Add(pts[i]);
            if ((pts.Count - 1) % 2 != 0) line.Points.Add(pts[pts.Count - 1]);
            // the stretches on kept pieces (runs of pinned samples)
            for (int i = 0; i < pts.Count; i++)
            {
                if (!fixedPt[i]) continue;
                int j = i;
                while (j + 1 < pts.Count && fixedPt[j + 1]) j++;
                if (j > i) line.Keep.Add(new KeyValuePair<Vector3, Vector3>(pts[i], pts[j]));
                i = j;
            }
            for (int i = 1; i < line.Points.Count; i++) line.Length += Vector3.Distance(line.Points[i - 1], line.Points[i]);
            return line;
        }

        const float SmoothStep = 2f;
        const int SmoothIterations = 40;

        static void Resample(List<Vector3> src, List<bool> pin, float step, out List<Vector3> dst, out List<bool> fixedPt)
        {
            dst = new List<Vector3>();
            fixedPt = new List<bool>();
            dst.Add(src[0]); fixedPt.Add(true);                      // the line's ends stay where they are
            float carry = 0f;
            for (int i = 0; i < src.Count - 1; i++)
            {
                var a = src[i]; var b = src[i + 1];
                float seg = Vector3.Distance(a, b);
                float t = step - carry;
                while (t <= seg)
                {
                    dst.Add(Vector3.Lerp(a, b, t / seg));
                    fixedPt.Add(pin[i] && pin[i + 1]);
                    t += step;
                }
                carry = seg - (t - step);
            }
            if (Vector3.Distance(dst[dst.Count - 1], src[src.Count - 1]) > 0.5f) { dst.Add(src[src.Count - 1]); fixedPt.Add(true); }
            else { dst[dst.Count - 1] = src[src.Count - 1]; fixedPt[fixedPt.Count - 1] = true; }
        }

        /// Taubin smoothing (shrink, then inflate): removes the small steps and kinks, keeps the shape of the bends.
        static void Taubin(List<Vector3> p, List<bool> fixedPt, int iterations)
        {
            var tmp = new Vector3[p.Count];
            for (int it = 0; it < iterations; it++)
            {
                for (int pass = 0; pass < 2; pass++)
                {
                    float f = pass == 0 ? 0.5f : -0.53f;
                    for (int i = 0; i < p.Count; i++)
                    {
                        if (i == 0 || i == p.Count - 1 || fixedPt[i]) { tmp[i] = p[i]; continue; }
                        tmp[i] = p[i] + f * ((p[i - 1] + p[i + 1]) * 0.5f - p[i]);
                    }
                    for (int i = 0; i < p.Count; i++) p[i] = tmp[i];
                }
            }
        }

        /// Hides the original pieces the line's new rail replaces (their rails no longer meet the smoothed line).
        /// Pieces the game draws itself (no renderers) are switched off.
        public static void HideReplaced(RailLine line)
        {
            foreach (var t in line.Replaced)
            {
                if (t == null) continue;
                bool any = false;
                foreach (var r in t.GetComponentsInChildren<Renderer>(true)) { r.enabled = false; any = true; }
                var lg = t.GetComponent<LODGroup>();
                if (lg != null) lg.enabled = false;
                if (!any)
                {
                    // drawn by the game's instancing: the picture is hidden there, the collider here
                    InstancedHider.Add(t);
                    t.gameObject.SetActive(false);
                }
            }
        }

        // ------------------------------------------------------------------ saved lines

        public const int LineVersion = 2;
        static readonly System.Text.Json.JsonSerializerOptions s_Json = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        static string LinePath(string scene) => System.IO.Path.Combine(RouteStore.FolderPath, "Lines", scene + ".json");
        static PieceRef Ref(Transform t, bool keep) => new PieceRef { Name = t.name, Pos = V(t.position), Keep = keep };
        static float[] V(Vector3 v) => new[] { (float)System.Math.Round(v.x, 3), (float)System.Math.Round(v.y, 3), (float)System.Math.Round(v.z, 3) };
        static Vector3 V(float[] a, int o = 0) => new Vector3(a[o], a[o + 1], a[o + 2]);

        /// The region's line file as it is on disk (for the track editor), or null.
        public static LineFile ReadLineFile(string scene)
        {
            var path = LinePath(scene);
            if (!System.IO.File.Exists(path)) return null;
            try { return System.Text.Json.JsonSerializer.Deserialize<LineFile>(System.IO.File.ReadAllText(path), s_Json); }
            catch { return null; }
        }

        /// Writes a line file changed by hand: it is locked, so a newer line finder never replaces it.
        public static void WriteLineFile(string scene, LineFile f)
        {
            f.Locked = true;
            var path = LinePath(scene);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(f, s_Json));
        }

        public static string LineFileText(string scene)
        {
            var path = LinePath(scene);
            return System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : null;
        }

        public static void RestoreLineFileText(string scene, string text)
        {
            var path = LinePath(scene);
            if (text == null) { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); return; }
            System.IO.File.WriteAllText(path, text);
        }

        public static void SaveLines(string scene, List<RailLine> lines)
        {
            try
            {
                var f = new LineFile { Version = LineVersion };
                foreach (var l in lines)
                {
                    var d = new LineData();
                    foreach (var p in l.Points) d.Points.Add(V(p));
                    foreach (var k in l.Keep) { var a = V(k.Key); var b = V(k.Value); d.Keep.Add(new[] { a[0], a[1], a[2], b[0], b[1], b[2] }); }
                    d.Pieces.AddRange(l.Used);
                    f.Lines.Add(d);
                }
                var path = LinePath(scene);
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(f, s_Json));
                SteamRailwayMod.Debug($"Lines of {scene} written to {path}.");
            }
            catch (System.Exception e) { SteamRailwayMod.Log.Warning($"Writing the lines of {scene} failed: {e.Message}"); }
        }

        /// The region's saved lines, or null (none yet, or written by an older line finder and not locked).
        public static List<RailLine> LoadLines(string scene)
        {
            var path = LinePath(scene);
            if (!System.IO.File.Exists(path)) return null;
            LineFile f;
            try { f = System.Text.Json.JsonSerializer.Deserialize<LineFile>(System.IO.File.ReadAllText(path), s_Json); }
            catch (System.Exception e) { SteamRailwayMod.Log.Warning($"{path} could not be read ({e.Message}); the lines are found again."); return null; }
            if (f == null || f.Lines == null || f.Lines.Count == 0) return null;
            if (!f.Locked && f.Version != LineVersion) return null;

            // the original pieces by name, including the ones already switched off
            var byName = new Dictionary<string, List<Transform>>();
            foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (t.gameObject.scene.name == null || !t.name.StartsWith("OBJ_RailTrack")) continue;
                if (!byName.TryGetValue(t.name, out var l)) byName[t.name] = l = new List<Transform>();
                l.Add(t);
            }
            s_UsedPieces.Clear();
            var lines = new List<RailLine>();
            int missing = 0;
            foreach (var d in f.Lines)
            {
                if (d.Points == null || d.Points.Count < 2) continue;
                var line = new RailLine();
                foreach (var p in d.Points) line.Points.Add(V(p));
                if (d.Keep != null) foreach (var k in d.Keep) line.Keep.Add(new KeyValuePair<Vector3, Vector3>(V(k, 0), V(k, 3)));
                if (d.Pieces != null)
                    foreach (var pr in d.Pieces)
                    {
                        line.Used.Add(pr);
                        Transform found = null;
                        if (pr.Name != null && pr.Pos != null && byName.TryGetValue(pr.Name, out var cands))
                            foreach (var c in cands) if ((c.position - V(pr.Pos)).sqrMagnitude < 0.01f) { found = c; break; }
                        if (found == null) { missing++; continue; }
                        s_UsedPieces.Add(found.GetInstanceID());
                        if (!pr.Keep) line.Replaced.Add(found);
                    }
                for (int i = 1; i < line.Points.Count; i++) line.Length += Vector3.Distance(line.Points[i - 1], line.Points[i]);
                lines.Add(line);
            }
            SteamRailwayMod.Debug($"Lines of {scene} read from {path}: {lines.Count} line(s)" + (missing > 0 ? $", {missing} piece(s) not found." : "."));
            return lines.Count > 0 ? lines : null;
        }

        static Vector3[] Oriented(Piece p, bool rev)
        {
            if (!rev) return p.Pts;
            var r = (Vector3[])p.Pts.Clone();
            System.Array.Reverse(r);
            return r;
        }

        /// Adds a piece's centreline (resampled to at most PointSpacing), skipping its first `skip` metres.
        static void AddPolyline(List<Vector3> pts, List<bool> pin, Vector3[] cl, float skip, bool pinned)
        {
            float d = 0f, next = skip;
            for (int i = 0; i < cl.Length - 1; i++)
            {
                var a = cl[i]; var b = cl[i + 1];
                float seg = Vector3.Distance(a, b);
                while (next <= d + seg)
                {
                    var p = Vector3.Lerp(a, b, seg > 1e-4f ? (next - d) / seg : 0f);
                    if (pts.Count == 0 || Vector3.Distance(pts[pts.Count - 1], p) > 1f) { pts.Add(p); pin.Add(pinned); }
                    next += PointSpacing;
                }
                d += seg;
            }
            var end = cl[cl.Length - 1];
            if (d > skip && (pts.Count == 0 || Vector3.Distance(pts[pts.Count - 1], end) > 1f)) { pts.Add(end); pin.Add(pinned); }
        }

        static Vector3 Hermite(Vector3 p0, Vector3 m0, Vector3 p1, Vector3 m1, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return (2f * t3 - 3f * t2 + 1f) * p0 + (t3 - 2f * t2 + t) * m0 + (-2f * t3 + 3f * t2) * p1 + (t3 - t2) * m1;
        }

        /// Clears the line for the new rail: original track pieces the line does not run on (broken scenery rails,
        /// stray bends) and small fallen rocks are hidden where they lie on the track. Only the loaded region in
        /// memory changes. Pieces the line runs on, bridges and big rock faces stay.
        static List<Transform> s_PieceCache;

        /// The region's rail pieces, searched once for all the lines of a build (the scene search is slow).
        public static void BeginClear()
        {
            s_PieceCache = new List<Transform>();
            foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (t.gameObject.scene.name == null) continue;
                string n = t.name;
                if (n.StartsWith("OBJ_RailTrack") && n.Contains("Prefab") && !n.Contains("Group")) s_PieceCache.Add(t);
            }
        }

        public static void EndClear() => s_PieceCache = null;

        public static void ClearLine(BuiltTrack track)
        {
            int hidden = 0, rocks = 0;
            if (s_PieceCache == null) BeginClear();
            foreach (var t in s_PieceCache)
            {
                if (t == null || !t.gameObject.activeInHierarchy) continue;
                {
                    if (s_UsedPieces.Contains(t.GetInstanceID())) continue;
                    if (!OnTrack(track, t, 2.5f, 1.5f)) continue;
                    Hide(t, false);
                    hidden++;
                }
            }
            rocks = ClearObstacles(track);
            if (hidden + rocks > 0)
                SteamRailwayMod.Debug($"Line {track.Id}: hid {hidden} stray rail piece(s) and {rocks} rock(s) lying on the track.");
        }

        static bool IsLooseRock(string n) =>
            n.StartsWith("TRN_Rock") || n.StartsWith("OBJ_Rock") || n.StartsWith("TRN_Boulder") || n.Contains("Rubble");

        /// Rocks and fallen trunks the train would run into: physics boxes the size of the engine are slid along
        /// the track, and the rock or log objects they really touch are hidden (renderers and colliders). Rock
        /// faces and cliffs (bigger than 25 m) are never touched.
        static int ClearObstacles(BuiltTrack track)
        {
            var path = track.Path;
            var roots = new HashSet<int>();
            var found = new List<Transform>();
            for (float s = 0f; s < path.Length; s += 1.5f)
            {
                path.Sample(s, out var p, out var t);
                var rot = Quaternion.LookRotation(new Vector3(t.x, 0f, t.z).sqrMagnitude > 1e-6f ? new Vector3(t.x, 0f, t.z) : Vector3.forward, Vector3.up);
                var hits = Physics.OverlapBox(p + Vector3.up * 2.1f, new Vector3(1.35f, 1.95f, 0.8f), rot);
                for (int i = 0; i < hits.Length; i++)
                {
                    var c = hits[i];
                    if (c == null || c.isTrigger) continue;
                    var root = ObstacleRoot(c.transform);
                    if (root == null || !roots.Add(root.GetInstanceID())) continue;
                    found.Add(root);
                }
            }
            int n = 0;
            foreach (var root in found)
            {
                var b = TotalBounds(root);
                bool log = IsFallenTree(root.name);
                if (!log && (b.size.x > 25f || b.size.z > 25f || b.size.y > 20f)) continue;
                Hide(root, true);
                n++;
            }
            return n;
        }

        /// The rock or log prefab a collider belongs to (null if it is something else).
        static Transform ObstacleRoot(Transform t)
        {
            for (var x = t; x != null; x = x.parent)
            {
                string n = x.name;
                if (n.Contains("Prefab") && (IsLooseRock(n) || IsFallenTree(n))) return x;
                if (n.StartsWith("SteamRailway") || n.StartsWith("Terrain")) return null;
            }
            return null;
        }

        static Bounds TotalBounds(Transform root)
        {
            bool any = false;
            var b = new Bounds(root.position, Vector3.zero);
            foreach (var r in root.GetComponentsInChildren<Renderer>(false))
            {
                if (!any) { b = r.bounds; any = true; }
                else b.Encapsulate(r.bounds);
            }
            return b;
        }

        /// Fallen trunks and logs lying in the ravine (standing trees are not touched: they are tall).
        static bool IsFallenTree(string n) =>
            (n.Contains("Fallen") || n.Contains("TreeLog") || n.Contains("Deadfall") || n.StartsWith("TRN_Log") || n.StartsWith("OBJ_Log"))
            && !n.Contains("RailCar");

        /// Does any renderer of t reach into the track corridor (within `reach` m of the rail centre, from just
        /// below the rails up to the train's height)? Objects bigger than maxSize (cliffs) never count.
        static bool OnTrack(BuiltTrack track, Transform t, float reach, float below, float maxSize = 18f)
        {
            var path = track.Path;
            foreach (var r in t.GetComponentsInChildren<Renderer>(false))
            {
                if (!r.enabled) continue;
                var b = r.bounds;
                if (b.size.x > maxSize || b.size.z > maxSize || b.size.y > 12f) return false;
                // the corridor along the object's whole length (a fallen trunk may cross the track anywhere)
                int steps = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(b.size.x, b.size.z) / 2f), 1, 24);
                var a0 = new Vector3(b.min.x, b.center.y, b.min.z);
                var a1 = new Vector3(b.max.x, b.center.y, b.max.z);
                for (int i = 0; i <= steps; i++)
                {
                    var q = Vector3.Lerp(a0, a1, i / (float)steps);
                    float s = path.NearestDistance(q, out float d);
                    if (d > reach + Mathf.Min(b.extents.x, b.extents.z) + 2f) continue;
                    path.Sample(s, out var p, out _);
                    if (b.max.y < p.y - 0.25f - below || b.min.y > p.y + 4.5f) continue;
                    var closest = b.ClosestPoint(p);
                    var dd = closest - p; dd.y = 0f;
                    if (dd.magnitude <= reach) return true;
                }
            }
            return false;
        }

        static void Hide(Transform t, bool colliders)
        {
            bool any = false;
            foreach (var r in t.GetComponentsInChildren<Renderer>(true)) { r.enabled = false; any = true; }
            if (!any) InstancedHider.Add(t);
            var lg = t.GetComponent<LODGroup>();
            if (lg != null) lg.enabled = false;
            if (colliders) foreach (var c in t.GetComponentsInChildren<Collider>(true)) c.enabled = false;
        }
    }
}
