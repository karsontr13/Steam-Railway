using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace SteamRailway
{
    /// A densely sampled uniform Catmull-Rom curve through the track's control points (the same curve type the
    /// Curvy spline uses), used to bend rail pieces and to find the track corridor. Points are rail-top centre points.
    internal sealed class CurvePath
    {
        const float Step = 0.5f;
        readonly List<Vector3> m_P = new List<Vector3>();
        readonly List<float> m_D = new List<float>();
        Vector3[] m_T;                                  // smooth tangent at each sample (central difference)

        public float Length => m_D.Count > 0 ? m_D[m_D.Count - 1] : 0f;
        public int Count => m_P.Count;
        public Vector3 this[int i] => m_P[i];
        public float DistanceAt(int i) => m_D[i];

        public static CurvePath CatmullRom(IList<Vector3> cps)
        {
            var c = new CurvePath();
            int n = cps.Count;
            for (int i = 0; i < n - 1; i++)
            {
                Vector3 p1 = cps[i], p2 = cps[i + 1];
                Vector3 p0 = i > 0 ? cps[i - 1] : p1;            // Curvy's automatic end tangents
                Vector3 p3 = i + 2 < n ? cps[i + 2] : p2;
                int steps = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(p1, p2) / Step));
                for (int k = 0; k < steps; k++) c.Add(CR(p0, p1, p2, p3, k / (float)steps));
            }
            c.Add(cps[n - 1]);
            c.BuildTangents();
            return c;
        }

        static Vector3 CR(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * (2f * p1 + (p2 - p0) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (3f * p1 - p0 - 3f * p2 + p3) * t3);
        }

        void Add(Vector3 p)
        {
            if (m_P.Count > 0 && Vector3.Distance(m_P[m_P.Count - 1], p) < 0.01f) return;
            m_D.Add(m_P.Count == 0 ? 0f : m_D[m_D.Count - 1] + Vector3.Distance(m_P[m_P.Count - 1], p));
            m_P.Add(p);
        }

        void BuildTangents()
        {
            int n = m_P.Count;
            m_T = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                var a = m_P[Mathf.Max(0, i - 1)];
                var b = m_P[Mathf.Min(n - 1, i + 1)];
                m_T[i] = (b - a).normalized;
            }
        }

        public void Sample(float s, out Vector3 pos, out Vector3 tangent)
        {
            s = Mathf.Clamp(s, 0f, Length);
            int lo = 0, hi = m_D.Count - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (m_D[mid] <= s) lo = mid; else hi = mid;
            }
            float seg = m_D[hi] - m_D[lo];
            float t = seg > 1e-5f ? (s - m_D[lo]) / seg : 0f;
            pos = Vector3.Lerp(m_P[lo], m_P[hi], t);
            // turning smoothly between the samples (a per-segment direction kinks bent rails)
            tangent = m_T != null ? Vector3.Lerp(m_T[lo], m_T[hi], t).normalized : (m_P[hi] - m_P[lo]).normalized;
        }

        /// Distance along the curve of the point closest to p in 3D (lines crossing at different heights).
        public float NearestDistance3D(Vector3 p, out float distance)
        {
            float best = float.MaxValue, bestS = 0f;
            for (int i = 0; i < m_P.Count; i++)
            {
                float d = (m_P[i] - p).sqrMagnitude;
                if (d < best) { best = d; bestS = m_D[i]; }
            }
            distance = Mathf.Sqrt(best);
            return bestS;
        }

        /// Distance along the curve of the point closest to p (horizontal distance).
        public float NearestDistance(Vector3 p, out float horizontalDistance)
        {
            float best = float.MaxValue, bestS = 0f;
            for (int i = 0; i < m_P.Count; i++)
            {
                float dx = m_P[i].x - p.x, dz = m_P[i].z - p.z;
                float d = dx * dx + dz * dz;
                if (d < best) { best = d; bestS = m_D[i]; }
            }
            horizontalDistance = Mathf.Sqrt(best);
            return bestS;
        }
    }

    /// What to lay of the rail piece (turnouts): both rails or one, sleepers or not, sleepers lengthened to one
    /// side (the long timbers under the points that carry both tracks).
    internal sealed class LayOptions
    {
        public bool Rails = true;
        public int RailSide;                               // 0 both rails, -1 only the rail at -x, +1 only +x
        public bool Sleepers = true;
        public System.Func<float, float> SleeperExt;       // path metres -> metres added to the sleeper ends (+x side if > 0)
        public System.Func<float, float> SleeperYaw;       // path metres -> along-track shift per metre out (turns the sleepers, rails stay)
        public System.Func<float, float> SleeperCut;
        public System.Func<float, bool> SleeperKeep;       // path metres -> a sleeper here is laid (null: all)
        public System.Func<float, bool> RailKeep;          // path metres -> the rails here are laid (null: all)       // path metres -> the sleeper ends on one side reach at most this far (+x side if > 0; 0 = whole)
        public float YOffset;
        public Transform Parent;                           // lay under this instead (moving switch blades)
    }

    /// The game's own rail piece (OBJ_RailTrackStraitA, 33.5 m) bent along a path. The meshes and materials are
    /// taken from the loaded region at run time; nothing is shipped with the mod.
    internal static class RailPieces
    {
        public const float RailTop = 0.402f;          // rail head above the piece origin
        public const float CentreX = -0.025f;         // centre between the two rails in piece space
        public const float SrcZ0 = -0.106f, SrcZ1 = -33.6f;
        public const float SrcLength = SrcZ0 - SrcZ1; // 33.494 m
        static readonly string[] MeshNames = { "OBJ_RailTrackStraitA_LOD00", "OBJ_RailTrackStraitA_LOD01", "OBJ_RailTrackStraitA_LOD02" };
        static readonly float[] LodHeights = { 0.25f, 0.07f, 0.008f };

        static Mesh[] s_Src;
        static Material[][] s_Mats;

        /// The full 33.5 m straight piece. The game has other meshes with the same name (a short broken piece
        /// modelled along x), so the geometry decides, not the name.
        static bool IsFullStraight(Mesh m)
        {
            if (m == null || !m.isReadable) return false;
            var b = m.bounds;
            return b.size.z > 32f && b.size.z < 35f && b.size.x > 2.5f && b.size.x < 3.5f && b.max.z < 1f;
        }

        public static bool Ensure()
        {
            bool ok = s_Src != null && s_Mats != null;
            if (ok)
                for (int i = 0; i < s_Src.Length && ok; i++)
                {
                    if (s_Src[i] == null) ok = false;
                    else foreach (var mat in s_Mats[i]) if (mat == null) { ok = false; break; }
                }
            if (ok) return true;
            s_Src = new Mesh[MeshNames.Length];
            foreach (var m in Resources.FindObjectsOfTypeAll<Mesh>())
                for (int i = 0; i < MeshNames.Length; i++)
                    if (m.name == MeshNames[i] && IsFullStraight(m)) s_Src[i] = m;
            Material mix = null, green = null;
            foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (m.name == "GLB_MixBig_C05") mix = m;
                else if (m.name == "GLB_Green_A05") green = m;
            }
            // the layer the region's own rail pieces are on (footsteps, what the player can stand on)
            foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
                if (t.gameObject.scene.name != null && t.name.StartsWith("OBJ_RailTrackStraitA_Prefab")) { s_Layer = t.gameObject.layer; break; }
            if (s_Src[0] == null || green == null)
            {
                SteamRailwayMod.Log.Warning("The game's rail meshes are not loaded in this region; rails cannot be laid.");
                s_Src = null;
                return false;
            }
            s_Mats = new Material[MeshNames.Length][];
            for (int i = 0; i < MeshNames.Length; i++)
            {
                var src = s_Src[i] ?? s_Src[0];
                s_Src[i] = src;
                s_Mats[i] = src.subMeshCount >= 2 ? new[] { mix ?? green, green } : new[] { green };
            }
            return true;
        }

        // Sleepers of the straight piece, in metres from its start: 37 at ~0.91 m, the first 0.30 m in, the last
        // 0.41 m before the end.
        const float SleeperFirst = 0.30f, SleeperLastToEnd = 0.41f, SleeperPitch = 0.9106f;

        /// Lays rail along [s0, s1] of the path at the game's sleeper pitch: the sleepers are spaced evenly from the
        /// last sleeper before the gap to the first one after it (barely stretched), the rails cut exactly at both ends.
        /// Long gaps get several copies of the piece one after another.
        public static void Lay(Transform parent, CurvePath path, float s0, float s1, LayOptions o = null)
        {
            if (o != null && o.Parent != null) parent = o.Parent;
            float gap = s1 - s0;
            if (gap < 0.4f || !Ensure()) return;
            float span = gap + SleeperLastToEnd + SleeperFirst;
            int k = Mathf.Max(0, Mathf.RoundToInt(span / SleeperPitch) - 1);   // sleepers inside the gap
            float d = span / (k + 1);                                          // their spacing
            float stretch = d / SleeperPitch;
            // source sleeper 1 becomes the first sleeper in the gap, at d - 0.41 m past s0
            float src1 = SleeperFirst + SleeperPitch;
            float srcOffset = src1 - (d - SleeperLastToEnd) / stretch;
            for (int m = 0; m < 400; m++)
            {
                float off = srcOffset - m * SrcLength;          // copy m continues where copy m-1 ended
                float lo = -off * stretch, hi = (SrcLength - off) * stretch;
                if (lo >= gap) break;
                if (hi <= 0f) continue;
                // only the copies at the two ends need cutting
                Piece(parent, path, s0, stretch, off, lo < 0f ? 0f : float.MinValue, hi > gap ? gap : float.MaxValue, o);
            }
        }

        /// One piece: source metres u (from the piece start) map to path distance start + (u - srcOffset) * stretch,
        /// cut to [clipLo, clipHi] (in metres past start).
        static void Piece(Transform parent, CurvePath path, float start, float stretch, float srcOffset, float clipLo, float clipHi, LayOptions o)
        {
            path.Sample(start + Mathf.Max(0f, -srcOffset * stretch), out var origin, out _);   // near this copy
            float slice = SliceFor(path, start, stretch, srcOffset, clipLo, clipHi);
            var go = new GameObject("RailPiece");
            go.transform.SetParent(parent, false);
            go.transform.position = origin;
            var lods = new List<LOD>();
            GameObject lastGo = null; Mesh lastMesh = null;
            for (int l = 0; l < s_Src.Length; l++)
            {
                var mesh = Deform(s_Src[l], path, start, stretch, srcOffset, clipLo, clipHi, origin, o, slice * (1 << l));
                if (mesh == null) continue;
                var lgo = new GameObject("LOD" + l);
                lgo.transform.SetParent(go.transform, false);
                lgo.layer = s_Layer;
                lastGo = lgo; lastMesh = mesh;
                lgo.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = lgo.AddComponent<MeshRenderer>();
                mr.sharedMaterials = new Il2CppReferenceArray<Material>(s_Mats[l]);
                lods.Add(new LOD(LodHeights[l], new Il2CppReferenceArray<Renderer>(new Renderer[] { mr })));
            }
            if (lods.Count == 0) { Object.Destroy(go); return; }
            var lg = go.AddComponent<LODGroup>();
            lg.SetLODs(new Il2CppReferenceArray<LOD>(lods.ToArray()));
            lg.RecalculateBounds();
            // solid like the region's own rails (the simplest level is enough to walk on)
            go.layer = s_Layer;
            if (lastGo != null)
            {
                try { lastGo.AddComponent<MeshCollider>().sharedMesh = lastMesh; }
                catch (System.Exception e) { SteamRailwayMod.Debug($"Rail collider: {e.Message}"); }
            }
        }

        static int s_Layer;

        /// Keeps the part of a polygon with sign * along <= sign * limit (Sutherland-Hodgman against one plane);
        /// vertices made on the cut are appended to the attribute lists.
        static List<int> ClipPoly(List<int> poly, float limit, float sign, List<Vector3> P, List<Vector3> N,
                                  List<Vector4> T, List<Vector2> U, List<bool> R, System.Func<int, float> along)
        {
            if (poly.Count < 3 || limit == float.MaxValue || limit == float.MinValue) return poly;
            var res = new List<int>(poly.Count + 1);
            for (int e = 0; e < poly.Count; e++)
            {
                int p0 = poly[e], p1 = poly[(e + 1) % poly.Count];
                float a0 = along(p0), a1 = along(p1);
                bool in0 = sign * a0 <= sign * limit, in1 = sign * a1 <= sign * limit;
                if (in0) res.Add(p0);
                if (in0 != in1)
                {
                    float t = (limit - a0) / (a1 - a0);
                    P.Add(Vector3.Lerp(P[p0], P[p1], t));
                    N.Add(Vector3.Lerp(N[p0], N[p1], t).normalized);
                    T.Add(Vector4.Lerp(T[p0], T[p1], t));
                    U.Add(Vector2.Lerp(U[p0], U[p1], t));
                    R.Add(R[p0]);
                    res.Add(P.Count - 1);
                }
            }
            return res;
        }

        /// The source mesh copied out of Il2Cpp once (reading it element by element through interop is slow).
        sealed class SrcData
        {
            public Vector3[] V, N; public Vector4[] T; public Vector2[] U; public int[][] Tris;
            public bool HasN, HasT, HasUv;
            public bool[] Rail;            // vertex belongs to a rail (not to a sleeper)
        }
        static readonly Dictionary<int, SrcData> s_Data = new Dictionary<int, SrcData>();

        static SrcData DataOf(Mesh src)
        {
            int id = src.GetInstanceID();
            if (s_Data.TryGetValue(id, out var d)) return d;
            d = new SrcData();
            d.V = src.vertices; var n = src.normals; var t = src.tangents; var u = src.uv;
            d.HasN = n.Length == d.V.Length; d.HasT = t.Length == d.V.Length; d.HasUv = u.Length == d.V.Length;
            d.N = d.HasN ? (Vector3[])n : null; d.T = d.HasT ? (Vector4[])t : null; d.U = d.HasUv ? (Vector2[])u : null;
            d.Tris = new int[src.subMeshCount][];
            for (int s = 0; s < src.subMeshCount; s++) d.Tris[s] = src.GetTriangles(s);
            d.Rail = new bool[d.V.Length];
            if (src.subMeshCount >= 2) foreach (var k in d.Tris[0]) d.Rail[k] = true;
            else for (int k = 0; k < d.V.Length; k++) d.Rail[k] = d.V[k].y > 0.29f;
            s_Data[id] = d;
            return d;
        }

        // rails: submesh 0 of the full piece (y 0.165 - 0.402); a one-submesh LOD is split by height
        const float RailMinY = 0.29f;
        const float RailSlice = 0.5f;   // m: the shortest slice rail faces are cut into before they are bent

        /// How short the rail faces of one copy must be cut to follow its bend (1 cm off the true curve at most);
        /// a straight stretch is not cut at all.
        static float SliceFor(CurvePath path, float start, float stretch, float srcOffset, float clipLo, float clipHi)
        {
            float a = start + Mathf.Max(-srcOffset * stretch, clipLo > float.MinValue ? clipLo : float.MinValue);
            float b = start + Mathf.Min((SrcLength - srcOffset) * stretch, clipHi < float.MaxValue ? clipHi : float.MaxValue);
            a = Mathf.Clamp(a, 0f, path.Length); b = Mathf.Clamp(b, 0f, path.Length);
            if (b - a < 0.5f) return 1000f;
            float turn = 0f;
            path.Sample(a, out _, out var t0);
            const int n = 8;
            for (int k = 1; k <= n; k++)
            {
                path.Sample(Mathf.Lerp(a, b, k / (float)n), out _, out var t1);
                var f0 = new Vector3(t0.x, 0f, t0.z); var f1 = new Vector3(t1.x, 0f, t1.z);
                turn = Mathf.Max(turn, Vector3.Angle(f0, f1) / ((b - a) / n));   // degrees per metre
                // a change of grade bends the rails too
                turn = Mathf.Max(turn, Mathf.Abs(Mathf.Asin(Mathf.Clamp(t1.y, -1f, 1f)) - Mathf.Asin(Mathf.Clamp(t0.y, -1f, 1f))) * Mathf.Rad2Deg / ((b - a) / n));
                t0 = t1;
            }
            if (turn < 0.01f) return 1000f;
            float radius = 1f / (turn * Mathf.Deg2Rad);
            return Mathf.Clamp(Mathf.Sqrt(8f * radius * 0.01f), RailSlice, 8f);
        }
        const float SleeperEndFrom = 1.05f, SleeperEndTo = 1.50f;   // |x| of the sleeper ends that are lengthened

        static Mesh Deform(Mesh src, CurvePath path, float start, float stretch, float srcOffset, float clipLo, float clipHi, Vector3 origin, LayOptions o, float slice)
        {
            var data = DataOf(src);
            var sv = data.V; var sn = data.N; var st = data.T; var suv = data.U;
            int n0 = sv.Length;
            bool hasN = data.HasN, hasT = data.HasT, hasUv = data.HasUv;

            // source attributes (new vertices are added where triangles are cut at the clip distance)
            var P = new List<Vector3>(n0); var N = new List<Vector3>(n0); var T = new List<Vector4>(n0); var U = new List<Vector2>(n0);
            var R = new List<bool>(n0);    // rail vertex (bent with the line) or sleeper vertex (kept rigid)
            for (int i = 0; i < n0; i++)
            {
                P.Add(sv[i]);
                R.Add(data.Rail[i]);
                N.Add(hasN ? sn[i] : Vector3.up);
                T.Add(hasT ? st[i] : new Vector4(1, 0, 0, 1));
                U.Add(hasUv ? suv[i] : Vector2.zero);
            }
            float Along(int k) => (SrcZ0 - P[k].z - srcOffset) * stretch;
            bool clips = clipLo > float.MinValue || clipHi < float.MaxValue;

            var subs = new List<List<int>>();
            int kept = 0;
            bool filter = o != null && (!o.Rails || !o.Sleepers || o.RailSide != 0);
            bool split = src.subMeshCount >= 2;
            for (int s = 0; s < src.subMeshCount; s++)
            {
                var tris = data.Tris[s];
                var outTris = new List<int>(tris.Length);
                for (int i = 0; i + 2 < tris.Length; i += 3)
                {
                    int a = tris[i], b = tris[i + 1], c = tris[i + 2];
                    var cen = (sv[a] + sv[b] + sv[c]) / 3f;
                    bool rail = split ? s == 0 : cen.y > RailMinY;
                    if (filter)
                    {
                        if (rail ? !o.Rails : !o.Sleepers) continue;
                        if (rail && o.RailSide != 0 && Mathf.Sign(cen.x - CentreX) != o.RailSide) continue;
                    }
                    float a0 = Along(a), a1 = Along(b), a2 = Along(c);
                    float lo = Mathf.Min(a0, Mathf.Min(a1, a2)), hi = Mathf.Max(a0, Mathf.Max(a1, a2));
                    float mid = start + (a0 + a1 + a2) / 3f;
                    if (!rail && o?.SleeperKeep != null && !o.SleeperKeep(mid)) continue;
                    if (rail && hi - lo <= slice && o?.RailKeep != null && !o.RailKeep(mid)) continue;
                    if (!clips && !(rail && hi - lo > slice)) { outTris.Add(a); outTris.Add(b); outTris.Add(c); continue; }
                    var poly = new List<int> { a, b, c };
                    poly = ClipPoly(poly, clipHi, +1f, P, N, T, U, R, Along);
                    poly = ClipPoly(poly, clipLo, -1f, P, N, T, U, R, Along);
                    if (poly.Count < 3) continue;
                    if (rail && hi - lo > slice)
                    {
                        // a long rail face is cut into short slices, so it can follow a bend smoothly
                        int b0 = Mathf.FloorToInt(Mathf.Max(lo, clips && clipLo > float.MinValue ? clipLo : lo) / slice);
                        int b1 = Mathf.CeilToInt(Mathf.Min(hi, clips && clipHi < float.MaxValue ? clipHi : hi) / slice);
                        for (int band = b0; band < b1; band++)
                        {
                            if (o?.RailKeep != null && !o.RailKeep(start + (band + 0.5f) * slice)) continue;
                            var piece = ClipPoly(poly, (band + 1) * slice, +1f, P, N, T, U, R, Along);
                            piece = ClipPoly(piece, band * slice, -1f, P, N, T, U, R, Along);
                            for (int k = 1; k + 1 < piece.Count; k++) { outTris.Add(piece[0]); outTris.Add(piece[k]); outTris.Add(piece[k + 1]); }
                        }
                        continue;
                    }
                    for (int k = 1; k + 1 < poly.Count; k++) { outTris.Add(poly[0]); outTris.Add(poly[k]); outTris.Add(poly[k + 1]); }
                }
                kept += outTris.Count;
                subs.Add(outTris);
            }
            if (kept == 0) return null;

            int n = P.Count;
            var v = new Vector3[n];
            var nn = new Vector3[n];
            var tt = new Vector4[n];
            var uv = new Vector2[n];
            var ext = o?.SleeperExt;
            var cut = o?.SleeperCut;
            var yaw = o?.SleeperYaw;
            float yOff = o != null ? o.YOffset : 0f;
            for (int i = 0; i < n; i++)
            {
                var p = P[i];
                float sPath = start + Along(i);
                if (ext != null)
                {
                    // the sleeper ends on one side reach out under the other track
                    float e = ext(sPath), dx = p.x - CentreX;
                    if (e != 0f && Mathf.Sign(dx) == Mathf.Sign(e))
                        p.x += e * Mathf.Clamp01((Mathf.Abs(dx) - SleeperEndFrom) / (SleeperEndTo - SleeperEndFrom));
                }
                if (cut != null)
                {
                    // two tracks side by side as they part: each one's sleepers stop at the line between them
                    float lim = cut(sPath), cdx = p.x - CentreX;
                    if (lim != 0f && Mathf.Sign(cdx) == Mathf.Sign(lim) && Mathf.Abs(cdx) > Mathf.Abs(lim))
                        p.x = CentreX + Mathf.Sign(cdx) * Mathf.Abs(lim);
                }
                bool isRail = R[i];
                float du = 0f;   // sleepers: metres along the line from the sleeper's own centre (kept rigid)
                if (!isRail)
                {
                    // a sleeper stays a straight block square to the line at its centre; only the rails bend
                    float u = SrcZ0 - p.z;
                    int idx = Mathf.RoundToInt((u - SleeperFirst) / SleeperPitch);
                    float uc = SleeperFirst + idx * SleeperPitch;
                    float sCentre = start + (uc - srcOffset) * stretch;
                    du = (sPath - sCentre) / Mathf.Max(0.01f, stretch);
                    sPath = sCentre;
                    // the long sleepers of a turnout fan out between the two tracks
                    if (yaw != null) du += yaw(sPath) * (p.x - CentreX);
                }
                path.Sample(sPath, out var c, out var t);
                p.y += yOff;
                // piece space: +y up, the track runs along -z; build the same frame on the path
                var zw = -t;
                var xw = Vector3.Cross(Vector3.up, zw).normalized;
                var yw = Vector3.Cross(zw, xw);
                v[i] = c + xw * (p.x - CentreX) + yw * (p.y - RailTop) + t * du - origin;
                var q = N[i]; nn[i] = (xw * q.x + yw * q.y + zw * q.z).normalized;
                var r = T[i]; var d = xw * r.x + yw * r.y + zw * r.z; tt[i] = new Vector4(d.x, d.y, d.z, r.w);
                uv[i] = U[i];
            }

            var mesh = new Mesh();
            mesh.name = src.name + "_Bent";
            if (n > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(new Il2CppStructArray<Vector3>(v));
            if (hasN) mesh.SetNormals(new Il2CppStructArray<Vector3>(nn));
            if (hasT) mesh.SetTangents(new Il2CppStructArray<Vector4>(tt));
            if (hasUv) mesh.SetUVs(0, new Il2CppStructArray<Vector2>(uv));
            mesh.subMeshCount = subs.Count;
            for (int s = 0; s < subs.Count; s++) mesh.SetTriangles(new Il2CppStructArray<int>(subs[s].ToArray()), s);
            mesh.RecalculateBounds();
            return mesh;
        }
    }

    /// Lowers the snow (terrain) along the track so the rails stand clear of it, like the cleared lines of the
    /// railway regions. Only the loaded terrain in memory is changed; the region's files are untouched.
    internal static class TerrainCarve
    {
        const float Inner = 2.2f;       // fully lowered within this distance of the track centre
        const float Outer = 6.0f;       // blended back to the original ground at this distance
        const float BedAboveBase = 0.05f; // ground level relative to the bottom of the rail piece (sleepers just show)
        const float MaxCut = 8.0f;      // deeper cuts are left alone (deep cuttings, tunnel mouths); rockfall snow is cleared

        public static void Lower(CurvePath path)
        {
            if (path == null || path.Count < 2) return;
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            for (int i = 0; i < path.Count; i++)
            {
                var p = path[i];
                minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
                minZ = Mathf.Min(minZ, p.z); maxZ = Mathf.Max(maxZ, p.z);
            }
            minX -= Outer; minZ -= Outer; maxX += Outer; maxZ += Outer;

            foreach (var terrain in Terrain.activeTerrains)
            {
                try { LowerOn(terrain, path, minX, minZ, maxX, maxZ); }
                catch (System.Exception e) { SteamRailwayMod.Log.Warning($"Lowering snow on {terrain.name} failed: {e.Message}"); }
            }
        }

        const float FillInner = 2.4f;    // the embankment's top, either side of the track centre
        const float FillSlope = 1.5f;    // its sides: 1.5 m out for every metre down
        const float MaxFill = 15f;       // deeper hollows need a bridge, not snow
        const float FillReach = 26f;

        /// A snow embankment under a drawn line: where the ground lies below the track bed it is raised to it, with
        /// sloping sides. Raises only; carving (Lower) is done separately.
        public static void Fill(CurvePath path)
        {
            if (path == null || path.Count < 2) return;
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            for (int i = 0; i < path.Count; i++)
            {
                var p = path[i];
                minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
                minZ = Mathf.Min(minZ, p.z); maxZ = Mathf.Max(maxZ, p.z);
            }
            minX -= FillReach; minZ -= FillReach; maxX += FillReach; maxZ += FillReach;
            foreach (var terrain in Terrain.activeTerrains)
            {
                try { FillOn(terrain, path, minX, minZ, maxX, maxZ); }
                catch (System.Exception e) { SteamRailwayMod.Log.Warning($"Snow embankment on {terrain.name} failed: {e.Message}"); }
            }
        }

        static void FillOn(Terrain terrain, CurvePath path, float minX, float minZ, float maxX, float maxZ)
        {
            var td = terrain.terrainData;
            var tp = terrain.transform.position;
            var size = td.size;
            int res = td.heightmapResolution;
            float cell = size.x / (res - 1), cellZ = size.z / (res - 1);
            int x0 = Mathf.Clamp(Mathf.FloorToInt((minX - tp.x) / cell), 0, res - 1);
            int x1 = Mathf.Clamp(Mathf.CeilToInt((maxX - tp.x) / cell), 0, res - 1);
            int z0 = Mathf.Clamp(Mathf.FloorToInt((minZ - tp.z) / cellZ), 0, res - 1);
            int z1 = Mathf.Clamp(Mathf.CeilToInt((maxZ - tp.z) / cellZ), 0, res - 1);
            int w = x1 - x0 + 1, h = z1 - z0 + 1;
            if (w <= 1 || h <= 1) return;

            var best = new float[w * h];
            var railY = new float[w * h];
            for (int i = 0; i < best.Length; i++) best[i] = float.MaxValue;
            const int Stride = 4;
            for (int i = 0; i < path.Count - 1; i += Stride)
            {
                var a = path[i];
                var b = path[Mathf.Min(i + Stride, path.Count - 1)];
                int ix0 = Mathf.Max(x0, Mathf.FloorToInt((Mathf.Min(a.x, b.x) - FillReach - tp.x) / cell));
                int ix1 = Mathf.Min(x1, Mathf.CeilToInt((Mathf.Max(a.x, b.x) + FillReach - tp.x) / cell));
                int iz0 = Mathf.Max(z0, Mathf.FloorToInt((Mathf.Min(a.z, b.z) - FillReach - tp.z) / cellZ));
                int iz1 = Mathf.Min(z1, Mathf.CeilToInt((Mathf.Max(a.z, b.z) + FillReach - tp.z) / cellZ));
                var ab = new Vector2(b.x - a.x, b.z - a.z);
                float len2 = Mathf.Max(ab.sqrMagnitude, 1e-6f);
                for (int iz = iz0; iz <= iz1; iz++)
                for (int ix = ix0; ix <= ix1; ix++)
                {
                    var q = new Vector2(tp.x + ix * cell - a.x, tp.z + iz * cellZ - a.z);
                    float t = Mathf.Clamp01(Vector2.Dot(q, ab) / len2);
                    float d = (q - ab * t).magnitude;
                    int idx = (iz - z0) * w + (ix - x0);
                    if (d < best[idx]) { best[idx] = d; railY[idx] = Mathf.Lerp(a.y, b.y, t); }
                }
            }

            var obj = td.GetHeights(x0, z0, w, h);
            var heights = new Il2CppStructArray<float>(obj.Pointer);
            if (heights.Length < w * h) return;
            int changed = 0;
            for (int idx = 0; idx < w * h; idx++)
            {
                float d = best[idx];
                if (d >= FillReach) continue;
                float cur = tp.y + heights[idx] * size.y;
                float bed = railY[idx] - RailPieces.RailTop + BedAboveBase;
                if (bed - cur > MaxFill || cur >= bed) continue;
                float want = d <= FillInner ? bed : bed - (d - FillInner) / FillSlope;
                if (want <= cur) continue;
                heights[idx] = (want - tp.y) / size.y;
                changed++;
            }
            if (changed == 0) return;
            td.SetHeights(x0, z0, obj);
            SteamRailwayMod.Debug($"Snow embankment on {terrain.name}: {changed} height samples.");
        }

        static void LowerOn(Terrain terrain, CurvePath path, float minX, float minZ, float maxX, float maxZ)
        {
            var td = terrain.terrainData;
            var tp = terrain.transform.position;
            var size = td.size;
            int res = td.heightmapResolution;
            float cell = size.x / (res - 1), cellZ = size.z / (res - 1);
            int x0 = Mathf.Clamp(Mathf.FloorToInt((minX - tp.x) / cell), 0, res - 1);
            int x1 = Mathf.Clamp(Mathf.CeilToInt((maxX - tp.x) / cell), 0, res - 1);
            int z0 = Mathf.Clamp(Mathf.FloorToInt((minZ - tp.z) / cellZ), 0, res - 1);
            int z1 = Mathf.Clamp(Mathf.CeilToInt((maxZ - tp.z) / cellZ), 0, res - 1);
            int w = x1 - x0 + 1, h = z1 - z0 + 1;
            if (w <= 1 || h <= 1) return;

            // nearest track segment for every height sample in the box
            var best = new float[w * h];
            var railY = new float[w * h];
            for (int i = 0; i < best.Length; i++) best[i] = float.MaxValue;
            const int Stride = 4;   // 2 m segments
            for (int i = 0; i < path.Count - 1; i += Stride)
            {
                var a = path[i];
                var b = path[Mathf.Min(i + Stride, path.Count - 1)];
                int ix0 = Mathf.Max(x0, Mathf.FloorToInt((Mathf.Min(a.x, b.x) - Outer - tp.x) / cell));
                int ix1 = Mathf.Min(x1, Mathf.CeilToInt((Mathf.Max(a.x, b.x) + Outer - tp.x) / cell));
                int iz0 = Mathf.Max(z0, Mathf.FloorToInt((Mathf.Min(a.z, b.z) - Outer - tp.z) / cellZ));
                int iz1 = Mathf.Min(z1, Mathf.CeilToInt((Mathf.Max(a.z, b.z) + Outer - tp.z) / cellZ));
                var ab = new Vector2(b.x - a.x, b.z - a.z);
                float len2 = Mathf.Max(ab.sqrMagnitude, 1e-6f);
                for (int iz = iz0; iz <= iz1; iz++)
                for (int ix = ix0; ix <= ix1; ix++)
                {
                    var q = new Vector2(tp.x + ix * cell - a.x, tp.z + iz * cellZ - a.z);
                    float t = Mathf.Clamp01(Vector2.Dot(q, ab) / len2);
                    float d = (q - ab * t).magnitude;
                    int idx = (iz - z0) * w + (ix - x0);
                    if (d < best[idx]) { best[idx] = d; railY[idx] = Mathf.Lerp(a.y, b.y, t); }
                }
            }

            var obj = td.GetHeights(x0, z0, w, h);
            var heights = new Il2CppStructArray<float>(obj.Pointer);
            if (heights.Length < w * h) return;
            int changed = 0;
            for (int idx = 0; idx < w * h; idx++)
            {
                float d = best[idx];
                if (d >= Outer) continue;
                float cur = tp.y + heights[idx] * size.y;
                float target = railY[idx] - RailPieces.RailTop + BedAboveBase;
                if (cur <= target || cur - target > MaxCut) continue;
                float k = d <= Inner ? 1f : Mathf.SmoothStep(1f, 0f, (d - Inner) / (Outer - Inner));
                float y = Mathf.Lerp(cur, target, k);
                heights[idx] = (y - tp.y) / size.y;
                changed++;
            }
            if (changed == 0) return;
            td.SetHeights(x0, z0, obj);
            SteamRailwayMod.Debug($"Snow lowered along the track on {terrain.name}: {changed} height samples.");
        }
    }
}
