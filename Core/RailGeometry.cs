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
        public Vector3 this[int index] => m_P[index];
        public float DistanceAt(int index) => m_D[index];

        public static CurvePath CatmullRom(IList<Vector3> cps)
        {
            var curve = new CurvePath();
            int count = cps.Count;
            for (int index = 0; index < count - 1; index++)
            {
                Vector3 endPoint = cps[index], thirdPoint = cps[index + 1];
                Vector3 startPoint = index > 0 ? cps[index - 1] : endPoint;            // Curvy's automatic end tangents
                Vector3 fourthPoint = index + 2 < count ? cps[index + 2] : thirdPoint;
                int steps = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(endPoint, thirdPoint) / Step));
                for (int deepIndex = 0; deepIndex < steps; deepIndex++) curve.Add(CR(startPoint, endPoint, thirdPoint, fourthPoint, deepIndex / (float)steps));
            }
            curve.Add(cps[count - 1]);
            curve.BuildTangents();
            return curve;
        }

        static Vector3 CR(Vector3 startPoint, Vector3 endPoint, Vector3 thirdPoint, Vector3 fourthPoint, float fraction)
        {
            float t2 = fraction * fraction, t3 = t2 * fraction;
            return 0.5f * (2f * endPoint + (thirdPoint - startPoint) * fraction + (2f * startPoint - 5f * endPoint + 4f * thirdPoint - fourthPoint) * t2 + (3f * endPoint - startPoint - 3f * thirdPoint + fourthPoint) * t3);
        }

        void Add(Vector3 point)
        {
            if (m_P.Count > 0 && Vector3.Distance(m_P[m_P.Count - 1], point) < 0.01f) return;
            m_D.Add(m_P.Count == 0 ? 0f : m_D[m_D.Count - 1] + Vector3.Distance(m_P[m_P.Count - 1], point));
            m_P.Add(point);
        }

        void BuildTangents()
        {
            int count = m_P.Count;
            m_T = new Vector3[count];
            for (int index = 0; index < count; index++)
            {
                var start = m_P[Mathf.Max(0, index - 1)];
                var end = m_P[Mathf.Min(count - 1, index + 1)];
                m_T[index] = (end - start).normalized;
            }
        }

        public void Sample(float arcLength, out Vector3 position, out Vector3 tangent)
        {
            arcLength = Mathf.Clamp(arcLength, 0f, Length);
            int lower = 0, upper = m_D.Count - 1;
            while (upper - lower > 1)
            {
                int mid = (lower + upper) >> 1;
                if (m_D[mid] <= arcLength) lower = mid; else upper = mid;
            }
            float segmentLength = m_D[upper] - m_D[lower];
            float fraction = segmentLength > 1e-5f ? (arcLength - m_D[lower]) / segmentLength : 0f;
            position = Vector3.Lerp(m_P[lower], m_P[upper], fraction);
            // turning smoothly between the samples (a per-segment direction kinks bent rails)
            tangent = m_T != null ? Vector3.Lerp(m_T[lower], m_T[upper], fraction).normalized : (m_P[upper] - m_P[lower]).normalized;
        }

        /// Distance along the curve of the point closest to p in 3D (lines crossing at different heights).
        public float NearestDistance3D(Vector3 point, out float distance)
        {
            float best = float.MaxValue, bestS = 0f;
            for (int index = 0; index < m_P.Count; index++)
            {
                float d = (m_P[index] - point).sqrMagnitude;
                if (d < best) { best = d; bestS = m_D[index]; }
            }
            distance = Mathf.Sqrt(best);
            return bestS;
        }

        /// Distance along the curve of the point closest to p (horizontal distance).
        public float NearestDistance(Vector3 point, out float horizontalDistance)
        {
            float best = float.MaxValue, bestS = 0f;
            for (int index = 0; index < m_P.Count; index++)
            {
                float deltaX = m_P[index].x - point.x, deltaZ = m_P[index].z - point.z;
                float distance = deltaX * deltaX + deltaZ * deltaZ;
                if (distance < best) { best = distance; bestS = m_D[index]; }
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
        static bool IsFullStraight(Mesh mesh)
        {
            if (mesh == null || !mesh.isReadable) return false;
            var otherBounds = mesh.bounds;
            return otherBounds.size.z > 32f && otherBounds.size.z < 35f && otherBounds.size.x > 2.5f && otherBounds.size.x < 3.5f && otherBounds.max.z < 1f;
        }

        public static bool Ensure()
        {
            bool succeeded = s_Src != null && s_Mats != null;
            if (succeeded)
                for (int index = 0; index < s_Src.Length && succeeded; index++)
                {
                    if (s_Src[index] == null) succeeded = false;
                    else foreach (var material in s_Mats[index]) if (material == null) { succeeded = false; break; }
                }
            if (succeeded) return true;
            s_Src = new Mesh[MeshNames.Length];
            foreach (var mesh in Resources.FindObjectsOfTypeAll<Mesh>())
                for (int index = 0; index < MeshNames.Length; index++)
                    if (mesh.name == MeshNames[index] && IsFullStraight(mesh)) s_Src[index] = mesh;
            Material mix = null, green = null;
            foreach (var material in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (material.name == "GLB_MixBig_C05") mix = material;
                else if (material.name == "GLB_Green_A05") green = material;
            }
            // the layer the region's own rail pieces are on (footsteps, what the player can stand on)
            foreach (var transform in Resources.FindObjectsOfTypeAll<Transform>())
                if (transform.gameObject.scene.name != null && transform.name.StartsWith("OBJ_RailTrackStraitA_Prefab")) { s_Layer = transform.gameObject.layer; break; }
            if (s_Src[0] == null || green == null)
            {
                SteamRailwayMod.Log.Warning("The game's rail meshes are not loaded in this region; rails cannot be laid.");
                s_Src = null;
                return false;
            }
            s_Mats = new Material[MeshNames.Length][];
            for (int index = 0; index < MeshNames.Length; index++)
            {
                var sourceMesh = s_Src[index] ?? s_Src[0];
                s_Src[index] = sourceMesh;
                s_Mats[index] = sourceMesh.subMeshCount >= 2 ? new[] { mix ?? green, green } : new[] { green };
            }
            return true;
        }

        // Sleepers of the straight piece, in metres from its start: 37 at ~0.91 m, the first 0.30 m in, the last
        // 0.41 m before the end.
        const float SleeperFirst = 0.30f, SleeperLastToEnd = 0.41f, SleeperPitch = 0.9106f;

        /// Lays rail along [s0, s1] of the path at the game's sleeper pitch: the sleepers are spaced evenly from the
        /// last sleeper before the gap to the first one after it (barely stretched), the rails cut exactly at both ends.
        /// Long gaps get several copies of the piece one after another.
        public static void Lay(Transform parent, CurvePath path, float startArc, float endArc, LayOptions options = null)
        {
            if (options != null && options.Parent != null) parent = options.Parent;
            float gap = endArc - startArc;
            if (gap < 0.4f || !Ensure()) return;
            float span = gap + SleeperLastToEnd + SleeperFirst;
            int deepIndex = Mathf.Max(0, Mathf.RoundToInt(span / SleeperPitch) - 1);   // sleepers inside the gap
            float distance = span / (deepIndex + 1);                                          // their spacing
            float stretch = distance / SleeperPitch;
            // source sleeper 1 becomes the first sleeper in the gap, at d - 0.41 m past s0
            float src1 = SleeperFirst + SleeperPitch;
            float srcOffset = src1 - (distance - SleeperLastToEnd) / stretch;
            for (int m = 0; m < 400; m++)
            {
                float off = srcOffset - m * SrcLength;          // copy m continues where copy m-1 ended
                float lowerValue = -off * stretch, upperValue = (SrcLength - off) * stretch;
                if (lowerValue >= gap) break;
                if (upperValue <= 0f) continue;
                // only the copies at the two ends need cutting
                Piece(parent, path, startArc, stretch, off, lowerValue < 0f ? 0f : float.MinValue, upperValue > gap ? gap : float.MaxValue, options);
            }
        }

        /// One piece: source metres u (from the piece start) map to path distance start + (u - srcOffset) * stretch,
        /// cut to [clipLo, clipHi] (in metres past start).
        static void Piece(Transform parent, CurvePath path, float start, float stretch, float srcOffset, float clipLo, float clipHi, LayOptions options)
        {
            path.Sample(start + Mathf.Max(0f, -srcOffset * stretch), out var origin, out _);   // near this copy
            float slice = SliceFor(path, start, stretch, srcOffset, clipLo, clipHi);
            var targetObject = new GameObject("RailPiece");
            targetObject.transform.SetParent(parent, false);
            targetObject.transform.position = origin;
            var lods = new List<LOD>();
            GameObject lastGo = null; Mesh lastMesh = null;
            for (int l = 0; l < s_Src.Length; l++)
            {
                var mesh = Deform(s_Src[l], path, start, stretch, srcOffset, clipLo, clipHi, origin, options, slice * (1 << l));
                if (mesh == null) continue;
                var lightObject = new GameObject("LOD" + l);
                lightObject.transform.SetParent(targetObject.transform, false);
                lightObject.layer = s_Layer;
                lastGo = lightObject; lastMesh = mesh;
                lightObject.AddComponent<MeshFilter>().sharedMesh = mesh;
                var meshRenderer = lightObject.AddComponent<MeshRenderer>();
                meshRenderer.sharedMaterials = new Il2CppReferenceArray<Material>(s_Mats[l]);
                lods.Add(new LOD(LodHeights[l], new Il2CppReferenceArray<Renderer>(new Renderer[] { meshRenderer })));
            }
            if (lods.Count == 0) { Object.Destroy(targetObject); return; }
            var lodGroup = targetObject.AddComponent<LODGroup>();
            lodGroup.SetLODs(new Il2CppReferenceArray<LOD>(lods.ToArray()));
            lodGroup.RecalculateBounds();
            // solid like the region's own rails (the simplest level is enough to walk on)
            targetObject.layer = s_Layer;
            if (lastGo != null)
            {
                try { lastGo.AddComponent<MeshCollider>().sharedMesh = lastMesh; }
                catch (System.Exception e) { SteamRailwayMod.Debug($"Rail collider: {e.Message}"); }
            }
        }

        static int s_Layer;

        /// Keeps the part of a polygon with sign * along <= sign * limit (Sutherland-Hodgman against one plane);
        /// vertices made on the cut are appended to the attribute lists.
        static List<int> ClipPoly(List<int> poly, float limit, float sign, List<Vector3> points, List<Vector3> normals,
                                  List<Vector4> tangents, List<Vector2> U, List<bool> flags, System.Func<int, float> along)
        {
            if (poly.Count < 3 || limit == float.MaxValue || limit == float.MinValue) return poly;
            var results = new List<int>(poly.Count + 1);
            for (int endIndex = 0; endIndex < poly.Count; endIndex++)
            {
                int p0 = poly[endIndex], p1 = poly[(endIndex + 1) % poly.Count];
                float a0 = along(p0), a1 = along(p1);
                bool in0 = sign * a0 <= sign * limit, in1 = sign * a1 <= sign * limit;
                if (in0) results.Add(p0);
                if (in0 != in1)
                {
                    float fraction = (limit - a0) / (a1 - a0);
                    points.Add(Vector3.Lerp(points[p0], points[p1], fraction));
                    normals.Add(Vector3.Lerp(normals[p0], normals[p1], fraction).normalized);
                    tangents.Add(Vector4.Lerp(tangents[p0], tangents[p1], fraction));
                    U.Add(Vector2.Lerp(U[p0], U[p1], fraction));
                    flags.Add(flags[p0]);
                    results.Add(points.Count - 1);
                }
            }
            return results;
        }

        /// The source mesh copied out of Il2Cpp once (reading it element by element through interop is slow).
        sealed class SrcData
        {
            public Vector3[] V, N; public Vector4[] T; public Vector2[] U; public int[][] Tris;
            public bool HasN, HasT, HasUv;
            public bool[] Rail;            // vertex belongs to a rail (not to a sleeper)
        }
        static readonly Dictionary<int, SrcData> s_Data = new Dictionary<int, SrcData>();

        static SrcData DataOf(Mesh sourceMesh)
        {
            int identifier = sourceMesh.GetInstanceID();
            if (s_Data.TryGetValue(identifier, out var d)) return d;
            d = new SrcData();
            d.V = sourceMesh.vertices; var n = sourceMesh.normals; var t = sourceMesh.tangents; var u = sourceMesh.uv;
            d.HasN = n.Length == d.V.Length; d.HasT = t.Length == d.V.Length; d.HasUv = u.Length == d.V.Length;
            d.N = d.HasN ? (Vector3[])n : null; d.T = d.HasT ? (Vector4[])t : null; d.U = d.HasUv ? (Vector2[])u : null;
            d.Tris = new int[sourceMesh.subMeshCount][];
            for (int s = 0; s < sourceMesh.subMeshCount; s++) d.Tris[s] = sourceMesh.GetTriangles(s);
            d.Rail = new bool[d.V.Length];
            if (sourceMesh.subMeshCount >= 2) foreach (var deepIndex in d.Tris[0]) d.Rail[deepIndex] = true;
            else for (int deepIndex = 0; deepIndex < d.V.Length; deepIndex++) d.Rail[deepIndex] = d.V[deepIndex].y > 0.29f;
            s_Data[identifier] = d;
            return d;
        }

        // rails: submesh 0 of the full piece (y 0.165 - 0.402); a one-submesh LOD is split by height
        const float RailMinY = 0.29f;
        const float RailSlice = 0.5f;   // m: the shortest slice rail faces are cut into before they are bent

        /// How short the rail faces of one copy must be cut to follow its bend (1 cm off the true curve at most);
        /// a straight stretch is not cut at all.
        static float SliceFor(CurvePath path, float start, float stretch, float srcOffset, float clipLo, float clipHi)
        {
            float firstValue = start + Mathf.Max(-srcOffset * stretch, clipLo > float.MinValue ? clipLo : float.MinValue);
            float secondValue = start + Mathf.Min((SrcLength - srcOffset) * stretch, clipHi < float.MaxValue ? clipHi : float.MaxValue);
            firstValue = Mathf.Clamp(firstValue, 0f, path.Length); secondValue = Mathf.Clamp(secondValue, 0f, path.Length);
            if (secondValue - firstValue < 0.5f) return 1000f;
            float turn = 0f;
            path.Sample(firstValue, out _, out var t0);
            const int count = 8;
            for (int deepIndex = 1; deepIndex <= count; deepIndex++)
            {
                path.Sample(Mathf.Lerp(firstValue, secondValue, deepIndex / (float)count), out _, out var t1);
                var f0 = new Vector3(t0.x, 0f, t0.z); var f1 = new Vector3(t1.x, 0f, t1.z);
                turn = Mathf.Max(turn, Vector3.Angle(f0, f1) / ((secondValue - firstValue) / count));   // degrees per metre
                // a change of grade bends the rails too
                turn = Mathf.Max(turn, Mathf.Abs(Mathf.Asin(Mathf.Clamp(t1.y, -1f, 1f)) - Mathf.Asin(Mathf.Clamp(t0.y, -1f, 1f))) * Mathf.Rad2Deg / ((secondValue - firstValue) / count));
                t0 = t1;
            }
            if (turn < 0.01f) return 1000f;
            float radius = 1f / (turn * Mathf.Deg2Rad);
            return Mathf.Clamp(Mathf.Sqrt(8f * radius * 0.01f), RailSlice, 8f);
        }
        const float SleeperEndFrom = 1.05f, SleeperEndTo = 1.50f;   // |x| of the sleeper ends that are lengthened

        static Mesh Deform(Mesh sourceMesh, CurvePath path, float start, float stretch, float srcOffset, float clipLo, float clipHi, Vector3 origin, LayOptions options, float slice)
        {
            var data = DataOf(sourceMesh);
            var vertices = data.V; var normals = data.N; var tangents = data.T; var uvs = data.U;
            int n0 = vertices.Length;
            bool hasN = data.HasN, hasT = data.HasT, hasUv = data.HasUv;

            // source attributes (new vertices are added where triangles are cut at the clip distance)
            var points = new List<Vector3>(n0); var N = new List<Vector3>(n0); var T = new List<Vector4>(n0); var U = new List<Vector2>(n0);
            var flags = new List<bool>(n0);    // rail vertex (bent with the line) or sleeper vertex (kept rigid)
            for (int index = 0; index < n0; index++)
            {
                points.Add(vertices[index]);
                flags.Add(data.Rail[index]);
                N.Add(hasN ? normals[index] : Vector3.up);
                T.Add(hasT ? tangents[index] : new Vector4(1, 0, 0, 1));
                U.Add(hasUv ? uvs[index] : Vector2.zero);
            }
            float Along(int deepIndex) => (SrcZ0 - points[deepIndex].z - srcOffset) * stretch;
            bool clips = clipLo > float.MinValue || clipHi < float.MaxValue;

            var subs = new List<List<int>>();
            int kept = 0;
            bool filter = options != null && (!options.Rails || !options.Sleepers || options.RailSide != 0);
            bool split = sourceMesh.subMeshCount >= 2;
            for (int s = 0; s < sourceMesh.subMeshCount; s++)
            {
                var tris = data.Tris[s];
                var outTris = new List<int>(tris.Length);
                for (int index = 0; index + 2 < tris.Length; index += 3)
                {
                    int a = tris[index], b = tris[index + 1], c = tris[index + 2];
                    var cen = (vertices[a] + vertices[b] + vertices[c]) / 3f;
                    bool rail = split ? s == 0 : cen.y > RailMinY;
                    if (filter)
                    {
                        if (rail ? !options.Rails : !options.Sleepers) continue;
                        if (rail && options.RailSide != 0 && Mathf.Sign(cen.x - CentreX) != options.RailSide) continue;
                    }
                    float a0 = Along(a), a1 = Along(b), a2 = Along(c);
                    float lowerValue = Mathf.Min(a0, Mathf.Min(a1, a2)), upperValue = Mathf.Max(a0, Mathf.Max(a1, a2));
                    float mid = start + (a0 + a1 + a2) / 3f;
                    if (!rail && options?.SleeperKeep != null && !options.SleeperKeep(mid)) continue;
                    if (rail && upperValue - lowerValue <= slice && options?.RailKeep != null && !options.RailKeep(mid)) continue;
                    if (!clips && !(rail && upperValue - lowerValue > slice)) { outTris.Add(a); outTris.Add(b); outTris.Add(c); continue; }
                    var poly = new List<int> { a, b, c };
                    poly = ClipPoly(poly, clipHi, +1f, points, N, T, U, flags, Along);
                    poly = ClipPoly(poly, clipLo, -1f, points, N, T, U, flags, Along);
                    if (poly.Count < 3) continue;
                    if (rail && upperValue - lowerValue > slice)
                    {
                        // a long rail face is cut into short slices, so it can follow a bend smoothly
                        int b0 = Mathf.FloorToInt(Mathf.Max(lowerValue, clips && clipLo > float.MinValue ? clipLo : lowerValue) / slice);
                        int b1 = Mathf.CeilToInt(Mathf.Min(upperValue, clips && clipHi < float.MaxValue ? clipHi : upperValue) / slice);
                        for (int band = b0; band < b1; band++)
                        {
                            if (options?.RailKeep != null && !options.RailKeep(start + (band + 0.5f) * slice)) continue;
                            var piece = ClipPoly(poly, (band + 1) * slice, +1f, points, N, T, U, flags, Along);
                            piece = ClipPoly(piece, band * slice, -1f, points, N, T, U, flags, Along);
                            for (int deepIndex = 1; deepIndex + 1 < piece.Count; deepIndex++) { outTris.Add(piece[0]); outTris.Add(piece[deepIndex]); outTris.Add(piece[deepIndex + 1]); }
                        }
                        continue;
                    }
                    for (int deepIndex = 1; deepIndex + 1 < poly.Count; deepIndex++) { outTris.Add(poly[0]); outTris.Add(poly[deepIndex]); outTris.Add(poly[deepIndex + 1]); }
                }
                kept += outTris.Count;
                subs.Add(outTris);
            }
            if (kept == 0) return null;

            int count = points.Count;
            var v = new Vector3[count];
            var nn = new Vector3[count];
            var tt = new Vector4[count];
            var uv = new Vector2[count];
            var ext = options?.SleeperExt;
            var cut = options?.SleeperCut;
            var yaw = options?.SleeperYaw;
            float yOff = options != null ? options.YOffset : 0f;
            for (int index = 0; index < count; index++)
            {
                var point = points[index];
                float sPath = start + Along(index);
                if (ext != null)
                {
                    // the sleeper ends on one side reach out under the other track
                    float error = ext(sPath), deltaX = point.x - CentreX;
                    if (error != 0f && Mathf.Sign(deltaX) == Mathf.Sign(error))
                        point.x += error * Mathf.Clamp01((Mathf.Abs(deltaX) - SleeperEndFrom) / (SleeperEndTo - SleeperEndFrom));
                }
                if (cut != null)
                {
                    // two tracks side by side as they part: each one's sleepers stop at the line between them
                    float limit = cut(sPath), cdx = point.x - CentreX;
                    if (limit != 0f && Mathf.Sign(cdx) == Mathf.Sign(limit) && Mathf.Abs(cdx) > Mathf.Abs(limit))
                        point.x = CentreX + Mathf.Sign(cdx) * Mathf.Abs(limit);
                }
                bool isRail = flags[index];
                float du = 0f;   // sleepers: metres along the line from the sleeper's own centre (kept rigid)
                if (!isRail)
                {
                    // a sleeper stays a straight block square to the line at its centre; only the rails bend
                    float u = SrcZ0 - point.z;
                    int idx = Mathf.RoundToInt((u - SleeperFirst) / SleeperPitch);
                    float uc = SleeperFirst + idx * SleeperPitch;
                    float sCentre = start + (uc - srcOffset) * stretch;
                    du = (sPath - sCentre) / Mathf.Max(0.01f, stretch);
                    sPath = sCentre;
                    // the long sleepers of a turnout fan out between the two tracks
                    if (yaw != null) du += yaw(sPath) * (point.x - CentreX);
                }
                path.Sample(sPath, out var center, out var t);
                point.y += yOff;
                // piece space: +y up, the track runs along -z; build the same frame on the path
                var zw = -t;
                var xw = Vector3.Cross(Vector3.up, zw).normalized;
                var yw = Vector3.Cross(zw, xw);
                v[index] = center + xw * (point.x - CentreX) + yw * (point.y - RailTop) + t * du - origin;
                var otherPoint = N[index]; nn[index] = (xw * otherPoint.x + yw * otherPoint.y + zw * otherPoint.z).normalized;
                var r = T[index]; var direction = xw * r.x + yw * r.y + zw * r.z; tt[index] = new Vector4(direction.x, direction.y, direction.z, r.w);
                uv[index] = U[index];
            }

            var mesh = new Mesh();
            mesh.name = sourceMesh.name + "_Bent";
            if (count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
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
            for (int index = 0; index < path.Count; index++)
            {
                var point = path[index];
                minX = Mathf.Min(minX, point.x); maxX = Mathf.Max(maxX, point.x);
                minZ = Mathf.Min(minZ, point.z); maxZ = Mathf.Max(maxZ, point.z);
            }
            minX -= Outer; minZ -= Outer; maxX += Outer; maxZ += Outer;

            foreach (var terrain in Terrain.activeTerrains)
            {
                try { LowerOn(terrain, path, minX, minZ, maxX, maxZ); }
                catch (System.Exception e) { SteamRailwayMod.Log.Warning($"Lowering snow on {terrain.name} failed: {e}"); }
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
            for (int index = 0; index < path.Count; index++)
            {
                var point = path[index];
                minX = Mathf.Min(minX, point.x); maxX = Mathf.Max(maxX, point.x);
                minZ = Mathf.Min(minZ, point.z); maxZ = Mathf.Max(maxZ, point.z);
            }
            minX -= FillReach; minZ -= FillReach; maxX += FillReach; maxZ += FillReach;
            foreach (var terrain in Terrain.activeTerrains)
            {
                try { FillOn(terrain, path, minX, minZ, maxX, maxZ); }
                catch (System.Exception e) { SteamRailwayMod.Log.Warning($"Snow embankment on {terrain.name} failed: {e}"); }
            }
        }

        static void FillOn(Terrain terrain, CurvePath path, float minX, float minZ, float maxX, float maxZ)
        {
            var td = terrain.terrainData;
            var targetPoint = terrain.transform.position;
            var size = td.size;
            int resolution = td.heightmapResolution;
            float cell = size.x / (resolution - 1), cellZ = size.z / (resolution - 1);
            int startX = Mathf.Clamp(Mathf.FloorToInt((minX - targetPoint.x) / cell), 0, resolution - 1);
            int endX = Mathf.Clamp(Mathf.CeilToInt((maxX - targetPoint.x) / cell), 0, resolution - 1);
            int startZ = Mathf.Clamp(Mathf.FloorToInt((minZ - targetPoint.z) / cellZ), 0, resolution - 1);
            int endZ = Mathf.Clamp(Mathf.CeilToInt((maxZ - targetPoint.z) / cellZ), 0, resolution - 1);
            int width = endX - startX + 1, height = endZ - startZ + 1;
            if (width <= 1 || height <= 1) return;

            var best = new float[width * height];
            var railY = new float[width * height];
            for (int index = 0; index < best.Length; index++) best[index] = float.MaxValue;
            const int Stride = 4;
            for (int index = 0; index < path.Count - 1; index += Stride)
            {
                var start = path[index];
                var end = path[Mathf.Min(index + Stride, path.Count - 1)];
                int firstColumn = Mathf.Max(startX, Mathf.FloorToInt((Mathf.Min(start.x, end.x) - FillReach - targetPoint.x) / cell));
                int lastColumn = Mathf.Min(endX, Mathf.CeilToInt((Mathf.Max(start.x, end.x) + FillReach - targetPoint.x) / cell));
                int firstRow = Mathf.Max(startZ, Mathf.FloorToInt((Mathf.Min(start.z, end.z) - FillReach - targetPoint.z) / cellZ));
                int lastRow = Mathf.Min(endZ, Mathf.CeilToInt((Mathf.Max(start.z, end.z) + FillReach - targetPoint.z) / cellZ));
                var segment = new Vector2(end.x - start.x, end.z - start.z);
                float len2 = Mathf.Max(segment.sqrMagnitude, 1e-6f);
                for (int rowIndex = firstRow; rowIndex <= lastRow; rowIndex++)
                for (int columnIndex = firstColumn; columnIndex <= lastColumn; columnIndex++)
                {
                    var otherPoint2D = new Vector2(targetPoint.x + columnIndex * cell - start.x, targetPoint.z + rowIndex * cellZ - start.z);
                    float fraction = Mathf.Clamp01(Vector2.Dot(otherPoint2D, segment) / len2);
                    float distance = (otherPoint2D - segment * fraction).magnitude;
                    int idx = (rowIndex - startZ) * width + (columnIndex - startX);
                    if (distance < best[idx]) { best[idx] = distance; railY[idx] = Mathf.Lerp(start.y, end.y, fraction); }
                }
            }

            var heights = HeightmapAccess.Read(td, startX, startZ, width, height);

            if (heights.Length < width * height) return;
            int changed = 0;
            for (int index = 0; index < width * height; index++)
            {
                float distance = best[index];
                if (distance >= FillReach) continue;
                float current = targetPoint.y + heights[index] * size.y;
                float bed = railY[index] - RailPieces.RailTop + BedAboveBase;
                if (bed - current > MaxFill || current >= bed) continue;
                float want = distance <= FillInner ? bed : bed - (distance - FillInner) / FillSlope;
                if (want <= current) continue;
                heights[index] = (want - targetPoint.y) / size.y;
                changed++;
            }
            if (changed == 0) return;
            HeightmapAccess.Write(td, startX, startZ, heights);
            SteamRailwayMod.Debug($"Snow embankment on {terrain.name}: {changed} height samples.");
        }

        static void LowerOn(Terrain terrain, CurvePath path, float minX, float minZ, float maxX, float maxZ)
        {
            var td = terrain.terrainData;
            var targetPoint = terrain.transform.position;
            var size = td.size;
            int resolution = td.heightmapResolution;
            float cell = size.x / (resolution - 1), cellZ = size.z / (resolution - 1);
            int startX = Mathf.Clamp(Mathf.FloorToInt((minX - targetPoint.x) / cell), 0, resolution - 1);
            int endX = Mathf.Clamp(Mathf.CeilToInt((maxX - targetPoint.x) / cell), 0, resolution - 1);
            int startZ = Mathf.Clamp(Mathf.FloorToInt((minZ - targetPoint.z) / cellZ), 0, resolution - 1);
            int endZ = Mathf.Clamp(Mathf.CeilToInt((maxZ - targetPoint.z) / cellZ), 0, resolution - 1);
            int width = endX - startX + 1, height = endZ - startZ + 1;
            if (width <= 1 || height <= 1) return;

            // nearest track segment for every height sample in the box
            var best = new float[width * height];
            var railY = new float[width * height];
            for (int index = 0; index < best.Length; index++) best[index] = float.MaxValue;
            const int Stride = 4;   // 2 m segments
            for (int index = 0; index < path.Count - 1; index += Stride)
            {
                var start = path[index];
                var end = path[Mathf.Min(index + Stride, path.Count - 1)];
                int firstColumn = Mathf.Max(startX, Mathf.FloorToInt((Mathf.Min(start.x, end.x) - Outer - targetPoint.x) / cell));
                int lastColumn = Mathf.Min(endX, Mathf.CeilToInt((Mathf.Max(start.x, end.x) + Outer - targetPoint.x) / cell));
                int firstRow = Mathf.Max(startZ, Mathf.FloorToInt((Mathf.Min(start.z, end.z) - Outer - targetPoint.z) / cellZ));
                int lastRow = Mathf.Min(endZ, Mathf.CeilToInt((Mathf.Max(start.z, end.z) + Outer - targetPoint.z) / cellZ));
                var segment = new Vector2(end.x - start.x, end.z - start.z);
                float len2 = Mathf.Max(segment.sqrMagnitude, 1e-6f);
                for (int rowIndex = firstRow; rowIndex <= lastRow; rowIndex++)
                for (int columnIndex = firstColumn; columnIndex <= lastColumn; columnIndex++)
                {
                    var otherPoint2D = new Vector2(targetPoint.x + columnIndex * cell - start.x, targetPoint.z + rowIndex * cellZ - start.z);
                    float fraction = Mathf.Clamp01(Vector2.Dot(otherPoint2D, segment) / len2);
                    float distance = (otherPoint2D - segment * fraction).magnitude;
                    int idx = (rowIndex - startZ) * width + (columnIndex - startX);
                    if (distance < best[idx]) { best[idx] = distance; railY[idx] = Mathf.Lerp(start.y, end.y, fraction); }
                }
            }

            var heights = HeightmapAccess.Read(td, startX, startZ, width, height);

            if (heights.Length < width * height) return;
            int changed = 0;
            for (int index = 0; index < width * height; index++)
            {
                float distance = best[index];
                if (distance >= Outer) continue;
                float current = targetPoint.y + heights[index] * size.y;
                float target = railY[index] - RailPieces.RailTop + BedAboveBase;
                if (current <= target || current - target > MaxCut) continue;
                float scale = distance <= Inner ? 1f : Mathf.SmoothStep(1f, 0f, (distance - Inner) / (Outer - Inner));
                float y = Mathf.Lerp(current, target, scale);
                heights[index] = (y - targetPoint.y) / size.y;
                changed++;
            }
            if (changed == 0) return;
            HeightmapAccess.Write(td, startX, startZ, heights);
            SteamRailwayMod.Debug($"Snow lowered along the track on {terrain.name}: {changed} height samples.");
        }
    }
}
