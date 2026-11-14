using System.Collections.Generic;
using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace SteamRailway
{
    /// Some regions (Mystery Lake) draw their track pieces, fallen logs and other art through the game's own
    /// GPU instancing (RenderObjectInstance): the object in the scene keeps only its collider, the picture comes
    /// from a matrix in a compute buffer (one per LOD). Hiding such an object means zeroing those matrices,
    /// moving it means writing moved ones. The game sends some matrices to the GPU again after the region has
    /// loaded, so ours are written again a few times a second. Only the loaded region in memory changes.
    internal static class InstancedHider
    {
        sealed class Target
        {
            public string Prefix;
            public Vector3 Pos;
            public bool Moved;
            public Matrix4x4 Delta;       // moved: new = Delta * original
            public bool Matched;
        }

        /// One instance of a target in a compute buffer.
        sealed class Hit
        {
            public Target T;
            public RenderObjectInstanceBatches B;
            public System.IntPtr Batches;
            public ComputeBuffer Mats, Invs;
            public int Index;
            public Matrix4x4 Orig;
            public Unity.Mathematics.float4 OrigPos;
            public Bounds OrigBounds;
            public string Name;           // the batch ("Prefix_LOD0")
            public Mesh Mesh;
        }

        static readonly List<Target> s_Targets = new List<Target>();
        static readonly List<Hit> s_Hits = new List<Hit>();
        static readonly Dictionary<System.IntPtr, System.IntPtr> s_Done = new Dictionary<System.IntPtr, System.IntPtr>();   // instance -> its batches
        static float s_NextCheck, s_NextWrite;
        const float MatchDist = 1.0f;     // m between the scene object and the instance (objects of one kind lie apart)

        static Il2CppStructArray<Matrix4x4> s_One;
        static Il2CppSystem.Array s_OneArr;

        public static void Clear()
        {
            s_Targets.Clear();
            s_Hits.Clear();
            s_Done.Clear();
        }

        /// The art name the instancing batches use: "OBJ_RailTrackStraitA_Prefab (3)" -> "OBJ_RailTrackStraitA".
        public static string Prefix(string name)
        {
            int index = name.IndexOf("_Prefab", System.StringComparison.Ordinal);
            if (index > 0) return name.Substring(0, index);
            index = name.IndexOf(" (", System.StringComparison.Ordinal);
            return index > 0 ? name.Substring(0, index) : name;
        }

        static Target Get(string prefix, Vector3 position)
        {
            foreach (var target in s_Targets) if (target.Prefix == prefix && (target.Pos - position).sqrMagnitude < 0.01f) return target;
            return null;
        }

        /// Hides an object drawn by instancing (its transform carries no renderer).
        public static void Add(Transform objectTransform)
        {
            if (objectTransform == null) return;
            Set(Prefix(objectTransform.name), objectTransform.position, false, Matrix4x4.identity);
        }

        /// Hides (moved = false) or moves an object; origPos = where the region placed it.
        public static void Set(string prefix, Vector3 origPos, bool moved, Matrix4x4 delta)
        {
            var target = Get(prefix, origPos);
            if (target == null) { target = new Target { Prefix = prefix, Pos = origPos }; s_Targets.Add(target); }
            target.Moved = moved;
            target.Delta = delta;
            if (target.Matched) WriteTarget(target);
        }

        /// Puts an object back as the region made it.
        public static void Remove(string prefix, Vector3 origPos)
        {
            var target = Get(prefix, origPos);
            if (target == null) return;
            s_Targets.Remove(target);
            foreach (var hit in s_Hits)
            {
                if (hit.T != target) continue;
                try { WriteOne(hit, hit.Orig); RestoreCulling(hit); } catch { }
            }
            s_Hits.RemoveAll(hit => hit.T == target);
        }

        public static bool Has(string prefix, Vector3 origPos) => Get(prefix, origPos) != null;

        /// Finds the instances of targets not found yet (reading the matrices back once) and writes them.
        public static void Apply()
        {
            int found = 0;
            try
            {
                var list = RenderObjectInstance.s_RenderObjectInstanceList;
                if (list == null) return;
                bool any = false;
                foreach (var target in s_Targets) if (!target.Matched) { any = true; break; }
                foreach (var instance in list)
                {
                    if (instance == null) continue;
                    var batches = instance.m_Batches;
                    if (batches == null || !batches.IsAllocated()) continue;
                    if (any) found += Find(batches);
                    s_Done[instance.Pointer] = batches.Pointer;
                }
                foreach (var target in s_Targets) if (!target.Matched) target.Matched = true;   // searched: absent here
                WriteAll();
            }
            catch (System.Exception e)
            {
                SteamRailwayMod.Log.Warning($"Instanced objects: {e.Message}");
            }
            if (found > 0) SteamRailwayMod.Debug($"Instanced objects: {found} copies of {s_Targets.Count} object(s) hidden or moved.");
        }

        static int Find(RenderObjectInstanceBatches b)
        {
            var names = b.m_Names;
            if (names == null) return 0;
            var mats = b.m_Objects.m_InstancedMatrices;
            var invs = b.m_Objects.m_InstancedInverses;
            if (mats == null) return 0;
            int batches = Mathf.Min(names.Count, b.GetBatchCount());
            int found = 0;
            for (int index = 0; index < batches; index++)
            {
                string name = names[index];
                if (name == null) continue;
                List<Target> mine = null;
                foreach (var target in s_Targets)
                    if (!target.Matched && name.StartsWith(target.Prefix + "_", System.StringComparison.Ordinal)) (mine ??= new List<Target>()).Add(target);
                if (mine == null) continue;
                int start = b.m_Batches.m_StartIndexes[index];
                int count = b.m_Batches.m_ObjectCounts[index];
                if (count <= 0 || start < 0 || start + count > mats.count) continue;
                Mesh batchMesh = null;
                try { var renderInfo = b.m_Batches.m_RenderInfos[index]; if (renderInfo != null) batchMesh = renderInfo.m_Mesh; } catch { }
                var arr = new Il2CppStructArray<Matrix4x4>(count);
                mats.InternalGetData(new Il2CppSystem.Array(arr.Pointer), 0, start, count, 64);
                for (int deepIndex = 0; deepIndex < count; deepIndex++)
                {
                    var matrix = arr[deepIndex];
                    if (matrix.m33 == 0f) continue;
                    var position = new Vector3(matrix.m03, matrix.m13, matrix.m23);
                    foreach (var target in mine)
                    {
                        // the most detailed level may sit off the object's own point (seen: 1 m aside, 2.4 m up)
                        var directionDelta = target.Pos - position;
                        if (directionDelta.x * directionDelta.x + directionDelta.z * directionDelta.z > MatchDist * MatchDist * 2.25f || Mathf.Abs(directionDelta.y) > 3f) continue;
                        int idx = start + deepIndex;
                        s_Hits.Add(new Hit
                        {
                            T = target, B = b, Batches = b.Pointer, Mats = mats, Invs = invs, Index = idx, Orig = matrix,
                            OrigPos = b.m_Objects.m_Positions[idx], OrigBounds = b.m_Objects.m_Bounds[idx],
                            Name = name, Mesh = batchMesh,
                        });
                        found++;
                        break;
                    }
                }
            }
            return found;
        }

        static void WriteOne(Hit hit, Matrix4x4 matrix)
        {
            if (s_One == null) { s_One = new Il2CppStructArray<Matrix4x4>(1); s_OneArr = new Il2CppSystem.Array(s_One.Pointer); }
            s_One[0] = matrix;
            hit.Mats.InternalSetData(s_OneArr, 0, hit.Index, 1, 64);
            if (hit.Invs != null)
            {
                s_One[0] = matrix.m33 == 0f ? matrix : matrix.inverse;
                hit.Invs.InternalSetData(s_OneArr, 0, hit.Index, 1, 64);
            }
        }

        static void WriteHit(Hit hit)
        {
            if (hit.Mats == null) return;
            if (!hit.T.Moved) { WriteOne(hit, new Matrix4x4()); return; }   // all zero: nothing is drawn
            WriteOne(hit, hit.T.Delta * hit.Orig);
            // the game culls by these: the object must be drawn where it stands now
            var point = hit.T.Delta.MultiplyPoint3x4(new Vector3(hit.OrigPos.x, hit.OrigPos.y, hit.OrigPos.z));
            hit.B.m_Objects.m_Positions[hit.Index] = new Unity.Mathematics.float4(point.x, point.y, point.z, hit.OrigPos.w);
            var otherBounds = hit.OrigBounds;
            otherBounds.center = hit.T.Delta.MultiplyPoint3x4(otherBounds.center);
            hit.B.m_Objects.m_Bounds[hit.Index] = otherBounds;
        }

        static void RestoreCulling(Hit hit)
        {
            hit.B.m_Objects.m_Positions[hit.Index] = hit.OrigPos;
            hit.B.m_Objects.m_Bounds[hit.Index] = hit.OrigBounds;
        }

        static void WriteTarget(Target target)
        {
            foreach (var hit in s_Hits) if (hit.T == target) { try { WriteHit(hit); } catch { } }
        }

        static Il2CppSystem.Array s_Zeros;
        static int s_ZeroLen;

        /// Hidden instances in runs of neighbouring indices (one upload each), moved ones one by one.
        static void WriteAll()
        {
            var hidden = new List<Hit>();
            foreach (var hit in s_Hits)
            {
                if (hit.Mats == null) continue;
                if (hit.T.Moved) WriteHit(hit); else hidden.Add(hit);
            }
            if (hidden.Count == 0) return;
            hidden.Sort((hit, otherHit) => hit.Mats.Pointer != otherHit.Mats.Pointer ? hit.Mats.Pointer.ToInt64().CompareTo(otherHit.Mats.Pointer.ToInt64()) : hit.Index.CompareTo(otherHit.Index));
            int index = 0;
            while (index < hidden.Count)
            {
                int innerIndex = index;
                while (innerIndex + 1 < hidden.Count && hidden[innerIndex + 1].Mats.Pointer == hidden[index].Mats.Pointer && hidden[innerIndex + 1].Index == hidden[innerIndex].Index + 1) innerIndex++;
                int count = innerIndex - index + 1;
                if (s_Zeros == null || s_ZeroLen < count)
                {
                    s_ZeroLen = Mathf.Max(count, 64);
                    s_Zeros = new Il2CppSystem.Array(new Il2CppStructArray<Matrix4x4>(s_ZeroLen).Pointer);
                }
                hidden[index].Mats.InternalSetData(s_Zeros, 0, hidden[index].Index, count, 64);
                if (hidden[index].Invs != null) hidden[index].Invs.InternalSetData(s_Zeros, 0, hidden[index].Index, count, 64);
                index = innerIndex + 1;
            }
        }

        // ------------------------------------------------------------------ picking

        /// An object drawn by instancing that the crosshair points at (it may have no collider at all).
        internal sealed class InstPick
        {
            public string Prefix;
            public Vector3 OrigPos;       // where the region placed it (the key of its edit)
            public Quaternion OrigRot;
            public Vector3 Pos;           // where it stands now
            public Quaternion Rot;
            public Bounds World;
            public float Dist;
            public string Key => Prefix + "@" + OrigPos.x.ToString("F2") + "," + OrigPos.z.ToString("F2");
        }

        struct Cand { public ComputeBuffer Mats; public int Index; public Bounds B; public string Name; public Mesh Mesh; }

        static readonly List<Cand> s_Cands = new List<Cand>();
        static readonly Dictionary<System.IntPtr, System.IntPtr> s_CandDone = new Dictionary<System.IntPtr, System.IntPtr>();
        static readonly Dictionary<(long, int), Matrix4x4> s_MatCache = new Dictionary<(long, int), Matrix4x4>();

        sealed class Tris { public Vector3[] V; public int[] T; }
        static readonly Dictionary<int, Tris> s_Tris = new Dictionary<int, Tris>();

        /// The pickable objects: every instance of the most detailed batches, with its (generous) culling bounds.
        static void BuildCands()
        {
            var list = RenderObjectInstance.s_RenderObjectInstanceList;
            if (list == null) return;
            bool same = s_Cands.Count > 0;
            foreach (var instance in list)
            {
                if (instance == null || instance.m_Batches == null || !instance.m_Batches.IsAllocated()) continue;
                if (!s_CandDone.TryGetValue(instance.Pointer, out var bp) || bp != instance.m_Batches.Pointer) same = false;
            }
            if (same) return;
            s_Cands.Clear(); s_CandDone.Clear(); s_MatCache.Clear();
            foreach (var instance in list)
            {
                if (instance == null) continue;
                var b = instance.m_Batches;
                if (b == null || !b.IsAllocated() || b.m_Names == null) continue;
                var perObject = b.m_Objects;
                var mats = perObject.m_InstancedMatrices;
                if (mats == null) continue;
                int batches = Mathf.Min(b.m_Names.Count, b.GetBatchCount());
                for (int index = 0; index < batches; index++)
                {
                    string name = b.m_Names[index];
                    if (name == null || !IsLod0(name)) continue;
                    Mesh mesh = null;
                    try { var renderInfo = b.m_Batches.m_RenderInfos[index]; if (renderInfo != null) mesh = renderInfo.m_Mesh; } catch { }
                    if (mesh == null) continue;
                    int st = b.m_Batches.m_StartIndexes[index], count = b.m_Batches.m_ObjectCounts[index];
                    for (int deepIndex = 0; deepIndex < count; deepIndex++)
                        s_Cands.Add(new Cand { Mats = mats, Index = st + deepIndex, B = perObject.m_Bounds[st + deepIndex], Name = name, Mesh = mesh });
                }
                s_CandDone[instance.Pointer] = b.Pointer;
            }
        }

        static Hit FindHit(ComputeBuffer mats, int index)
        {
            foreach (var hit in s_Hits) if (hit.Index == index && hit.Mats != null && hit.Mats.Pointer == mats.Pointer) return hit;
            return null;
        }

        static Matrix4x4 ReadMatrix(ComputeBuffer mats, int index)
        {
            var key = (mats.Pointer.ToInt64(), index);
            if (s_MatCache.TryGetValue(key, out var matrix)) return matrix;
            if (s_One == null) { s_One = new Il2CppStructArray<Matrix4x4>(1); s_OneArr = new Il2CppSystem.Array(s_One.Pointer); }
            mats.InternalGetData(s_OneArr, 0, index, 1, 64);
            matrix = s_One[0];
            s_MatCache[key] = matrix;
            return matrix;
        }

        static Tris TrisOf(Mesh mesh)
        {
            int identifier = mesh.GetInstanceID();
            if (s_Tris.TryGetValue(identifier, out var t)) return t;
            if (mesh.isReadable) t = new Tris { V = mesh.vertices, T = mesh.triangles };
            s_Tris[identifier] = t;
            return t;
        }

        /// Ray against a mesh's triangles (Moller-Trumbore); the ray is in the mesh's own space, t is kept in world units.
        static bool HitMesh(Tris triangles, Vector3 origin, Vector3 direction, out float best)
        {
            best = float.MaxValue;
            var v = triangles.V; var t = triangles.T;
            for (int index = 0; index + 2 < t.Length; index += 3)
            {
                var start = v[t[index]]; var firstEdge = v[t[index + 1]] - start; var secondEdge = v[t[index + 2]] - start;
                var pv = Vector3.Cross(direction, secondEdge);
                float det = Vector3.Dot(firstEdge, pv);
                if (det > -1e-9f && det < 1e-9f) continue;
                float inverse = 1f / det;
                var tv = origin - start;
                float u = Vector3.Dot(tv, pv) * inverse;
                if (u < 0f || u > 1f) continue;
                var qv = Vector3.Cross(tv, firstEdge);
                float weight = Vector3.Dot(direction, qv) * inverse;
                if (weight < 0f || u + weight > 1f) continue;
                float dist = Vector3.Dot(secondEdge, qv) * inverse;
                if (dist > 0f && dist < best) best = dist;
            }
            return best < float.MaxValue;
        }

        /// The nearest instanced object along the ray (null if none within maxDist).
        public static InstPick Pick(Ray ray, float maxDist)
        {
            BuildCands();
            InstPick best = null;
            float bestD = maxDist;
            ray.direction = ray.direction.normalized;

            void Test(Cand candidate, bool prefilter)
            {
                if (prefilter)
                {
                    if (!candidate.B.IntersectRay(ray, out float d0)) return;
                    if (d0 > bestD) return;
                }
                var hit = FindHit(candidate.Mats, candidate.Index);
                Matrix4x4 matrix;
                Vector3 oPos; Quaternion oRot;
                if (hit != null)
                {
                    if (!hit.T.Moved) return;                    // hidden
                    matrix = hit.T.Delta * hit.Orig;
                    oPos = hit.T.Pos; oRot = RotOf(hit.Orig);
                }
                else
                {
                    matrix = ReadMatrix(candidate.Mats, candidate.Index);
                    if (matrix.m33 == 0f) return;
                    oPos = new Vector3(matrix.m03, matrix.m13, matrix.m23); oRot = RotOf(matrix);
                }
                var triangles = TrisOf(candidate.Mesh);
                var inv = matrix.inverse;
                var o = inv.MultiplyPoint3x4(ray.origin);
                var dir = inv.MultiplyVector(ray.direction);
                float dist;
                if (triangles != null) { if (!HitMesh(triangles, o, dir, out dist)) return; }
                else
                {
                    var localBounds = candidate.Mesh.bounds;
                    if (!localBounds.IntersectRay(new Ray(o, dir), out dist) || dist < 0f) return;
                }
                if (dist >= bestD) return;
                bestD = dist;
                var combinedBounds = candidate.Mesh.bounds;
                var wb = new Bounds(matrix.MultiplyPoint3x4(combinedBounds.center), Vector3.zero);
                for (int index = 0; index < 8; index++)
                    wb.Encapsulate(matrix.MultiplyPoint3x4(combinedBounds.center + Vector3.Scale(combinedBounds.extents, new Vector3((index & 1) == 0 ? -1 : 1, (index & 2) == 0 ? -1 : 1, (index & 4) == 0 ? -1 : 1))));
                string name = Base(candidate.Name);
                best = new InstPick { Prefix = name, OrigPos = oPos, OrigRot = oRot, Pos = new Vector3(matrix.m03, matrix.m13, matrix.m23), Rot = RotOf(matrix), World = wb, Dist = dist };
            }

            foreach (var candidate in s_Cands) Test(candidate, true);
            // moved ones have left their culling bounds
            foreach (var hit in s_Hits)
            {
                if (!hit.T.Moved || hit.Mesh == null || hit.Name == null || !IsLod0(hit.Name)) continue;
                Test(new Cand { Mats = hit.Mats, Index = hit.Index, Name = hit.Name, Mesh = hit.Mesh }, false);
            }
            return best;
        }

        /// The most detailed level of a batch: "..._LOD0" or "..._LOD00".
        static bool IsLod0(string name) => name.EndsWith("_LOD0", System.StringComparison.Ordinal) || name.EndsWith("_LOD00", System.StringComparison.Ordinal);

        static string Base(string name)
        {
            int index = name.LastIndexOf("_LOD", System.StringComparison.Ordinal);
            return index > 0 ? name.Substring(0, index) : name;
        }

        static Quaternion RotOf(Matrix4x4 matrix)
        {
            var f = new Vector3(matrix.m02, matrix.m12, matrix.m22);
            var u = new Vector3(matrix.m01, matrix.m11, matrix.m21);
            return f.sqrMagnitude > 1e-8f ? Quaternion.LookRotation(f, u) : Quaternion.identity;
        }

        public static void Update()
        {
            if (s_Targets.Count == 0) return;
            try
            {
                if (Time.time >= s_NextCheck)
                {
                    // the game rebuilt its batches (quality settings, re-enabling): find the instances again
                    s_NextCheck = Time.time + 2f;
                    var list = RenderObjectInstance.s_RenderObjectInstanceList;
                    if (list != null)
                        foreach (var instance in list)
                        {
                            if (instance == null || instance.m_Batches == null || !instance.m_Batches.IsAllocated()) continue;
                            if (!s_Done.TryGetValue(instance.Pointer, out var bp) || bp != instance.m_Batches.Pointer)
                            {
                                s_Hits.Clear();
                                s_Done.Clear();
                                foreach (var target in s_Targets) target.Matched = false;
                                Apply();
                                return;
                            }
                        }
                    bool pending = false;
                    foreach (var target in s_Targets) if (!target.Matched) { pending = true; break; }
                    if (pending) Apply();
                }
                if (Time.time >= s_NextWrite)
                {
                    s_NextWrite = Time.time + 1f;
                    WriteAll();
                }
            }
            catch { }
        }
    }
}
