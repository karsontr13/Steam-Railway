using System.Collections.Generic;
using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace SteamRailway
{
    /// Hides or moves objects the game draws through GPU instancing by writing zero or moved matrices into its
    /// compute buffers, and writes them again regularly because the game re-uploads some of them.
    internal static class InstancedHider
    {
        sealed class Target
        {
            public string Prefix;
            public Vector3 OriginalPosition;
            public bool Moved;
            public Matrix4x4 Delta;       // moved: new = Delta * original
            public bool Matched;
        }

        /// One instance of a target in a compute buffer.
        sealed class InstanceMatch
        {
            public Target OwnerTarget;
            public RenderObjectInstanceBatches InstanceBatches;
            public System.IntPtr BatchesPointer;
            public ComputeBuffer InstancedMatrices, InstancedInverses;
            public int Index;
            public Matrix4x4 OriginalMatrix;
            public Unity.Mathematics.float4 OriginalCullingPosition;
            public Bounds OriginalBounds;
            public string Name;           // the batch ("Prefix_LOD0")
            public Mesh Mesh;
        }

        static readonly List<Target> s_Targets = new List<Target>();
        static readonly List<InstanceMatch> s_InstanceMatches = new List<InstanceMatch>();
        static readonly Dictionary<System.IntPtr, System.IntPtr> s_DoneBatchesByInstance = new Dictionary<System.IntPtr, System.IntPtr>();   // instance -> its batches
        static float s_NextCheck, s_NextWrite;
        const float MatchDistanceInMeters = 1.0f;
        const float TargetPositionToleranceInSquareMeters = 0.01f;
        const float HorizontalMatchScaleSquared = 2.25f;
        const float MaximumVerticalMatchOffsetInMeters = 3f;
        const int MatrixSizeInBytes = 64;
        const int MinimumZeroMatrixCount = 64;
        const float ParallelRayEpsilon = 1e-9f;
        const float MinimumForwardSquaredMagnitude = 1e-8f;
        const float RebuildCheckIntervalInSeconds = 2f;
        const float WriteIntervalInSeconds = 1f;
        const int BoundsCornerCount = 8;
        const int TriangleVertexCount = 3;
        const int LastVertexOffsetInTriangle = 2;
        const int CornerMaskX = 1;
        const int CornerMaskY = 2;
        const int CornerMaskZ = 4;

        static Il2CppStructArray<Matrix4x4> s_OneMatrixArray;
        static Il2CppSystem.Array s_OneMatrixIl2CppArray;

        public static void Clear()
        {
            s_Targets.Clear();
            s_InstanceMatches.Clear();
            s_DoneBatchesByInstance.Clear();
        }

        /// The art name the instancing batches use: "OBJ_RailTrackStraitA_Prefab (3)" -> "OBJ_RailTrackStraitA".
        public static string Prefix(string name)
        {
            int index = name.IndexOf("_Prefab", System.StringComparison.Ordinal);
            if (index > 0) return name.Substring(0, index);
            index = name.IndexOf(" (", System.StringComparison.Ordinal);
            return index > 0 ? name.Substring(0, index) : name;
        }

        static Target FindTarget(string prefix, Vector3 position)
        {
            foreach (var target in s_Targets) if (target.Prefix == prefix && (target.OriginalPosition - position).sqrMagnitude < TargetPositionToleranceInSquareMeters) return target;
            return null;
        }

        /// Hides an object drawn by instancing (its transform carries no renderer).
        public static void Add(Transform objectTransform)
        {
            if (objectTransform == null) return;
            Set(Prefix(objectTransform.name), objectTransform.position, false, Matrix4x4.identity);
        }

        /// Hides (moved = false) or moves an object; origPos = where the region placed it.
        public static void Set(string prefix, Vector3 originalPosition, bool moved, Matrix4x4 delta)
        {
            var target = FindTarget(prefix, originalPosition);
            if (target == null) { target = new Target { Prefix = prefix, OriginalPosition = originalPosition }; s_Targets.Add(target); }
            target.Moved = moved;
            target.Delta = delta;
            if (target.Matched) WriteTarget(target);
        }

        /// Puts an object back as the region made it.
        public static void Remove(string prefix, Vector3 originalPosition)
        {
            var target = FindTarget(prefix, originalPosition);
            if (target == null) return;
            s_Targets.Remove(target);
            foreach (var instanceMatch in s_InstanceMatches)
            {
                if (instanceMatch.OwnerTarget != target) continue;
                try { WriteOne(instanceMatch, instanceMatch.OriginalMatrix); RestoreCulling(instanceMatch); } catch { }
            }
            s_InstanceMatches.RemoveAll(instanceMatch => instanceMatch.OwnerTarget == target);
        }

        public static bool Has(string prefix, Vector3 origPos) => FindTarget(prefix, origPos) != null;

        /// Finds the instances of targets not found yet (reading the matrices back once) and writes them.
        public static void Apply()
        {
            int found = 0;
            try
            {
                var instanceList = RenderObjectInstance.s_RenderObjectInstanceList;
                if (instanceList == null) return;
                bool hasUnmatchedTarget = false;
                foreach (var target in s_Targets) if (!target.Matched) { hasUnmatchedTarget = true; break; }
                foreach (var instance in instanceList)
                {
                    if (instance == null) continue;
                    var batches = instance.m_Batches;
                    if (batches == null || !batches.IsAllocated()) continue;
                    if (hasUnmatchedTarget) found += Find(batches);
                    s_DoneBatchesByInstance[instance.Pointer] = batches.Pointer;
                }
                foreach (var target in s_Targets) if (!target.Matched) target.Matched = true;   // searched: absent here
                WriteAll();
            }
            catch (System.Exception exception)
            {
                SteamRailwayMod.Log.Warning($"Instanced objects: {exception.Message}");
            }
            if (found > 0) SteamRailwayMod.Debug($"Instanced objects: {found} copies of {s_Targets.Count} object(s) hidden or moved.");
        }

        static int Find(RenderObjectInstanceBatches instanceBatches)
        {
            var names = instanceBatches.m_Names;
            if (names == null) return 0;
            var instancedMatrices = instanceBatches.m_Objects.m_InstancedMatrices;
            var instancedInverses = instanceBatches.m_Objects.m_InstancedInverses;
            if (instancedMatrices == null) return 0;
            int batches = Mathf.Min(names.Count, instanceBatches.GetBatchCount());
            int found = 0;
            for (int index = 0; index < batches; index++)
            {
                string name = names[index];
                if (name == null) continue;
                List<Target> matchingTargets = null;
                foreach (var target in s_Targets)
                    if (!target.Matched && name.StartsWith(target.Prefix + "_", System.StringComparison.Ordinal)) (matchingTargets ??= new List<Target>()).Add(target);
                if (matchingTargets == null) continue;
                int start = instanceBatches.m_Batches.m_StartIndexes[index];
                int count = instanceBatches.m_Batches.m_ObjectCounts[index];
                if (count <= 0 || start < 0 || start + count > instancedMatrices.count) continue;
                Mesh batchMesh = null;
                try { var renderInfo = instanceBatches.m_Batches.m_RenderInfos[index]; if (renderInfo != null) batchMesh = renderInfo.m_Mesh; } catch { }
                var matrixArray = new Il2CppStructArray<Matrix4x4>(count);
                instancedMatrices.InternalGetData(new Il2CppSystem.Array(matrixArray.Pointer), 0, start, count, MatrixSizeInBytes);
                for (int innerIndex = 0; innerIndex < count; innerIndex++)
                {
                    var matrix = matrixArray[innerIndex];
                    if (matrix.m33 == 0f) continue;
                    var position = new Vector3(matrix.m03, matrix.m13, matrix.m23);
                    foreach (var target in matchingTargets)
                    {
                        // the most detailed level may sit off the object's own point
                        var directionDelta = target.OriginalPosition - position;
                        if (directionDelta.x * directionDelta.x + directionDelta.z * directionDelta.z > MatchDistanceInMeters * MatchDistanceInMeters * HorizontalMatchScaleSquared || Mathf.Abs(directionDelta.y) > MaximumVerticalMatchOffsetInMeters) continue;
                        int globalIndex = start + innerIndex;
                        s_InstanceMatches.Add(new InstanceMatch
                        {
                            OwnerTarget = target, InstanceBatches = instanceBatches, BatchesPointer = instanceBatches.Pointer, InstancedMatrices = instancedMatrices, InstancedInverses = instancedInverses, Index = globalIndex, OriginalMatrix = matrix,
                            OriginalCullingPosition = instanceBatches.m_Objects.m_Positions[globalIndex], OriginalBounds = instanceBatches.m_Objects.m_Bounds[globalIndex],
                            Name = name, Mesh = batchMesh,
                        });
                        found++;
                        break;
                    }
                }
            }
            return found;
        }

        static void WriteOne(InstanceMatch instanceMatch, Matrix4x4 matrix)
        {
            if (s_OneMatrixArray == null) { s_OneMatrixArray = new Il2CppStructArray<Matrix4x4>(1); s_OneMatrixIl2CppArray = new Il2CppSystem.Array(s_OneMatrixArray.Pointer); }
            s_OneMatrixArray[0] = matrix;
            instanceMatch.InstancedMatrices.InternalSetData(s_OneMatrixIl2CppArray, 0, instanceMatch.Index, 1, MatrixSizeInBytes);
            if (instanceMatch.InstancedInverses != null)
            {
                s_OneMatrixArray[0] = matrix.m33 == 0f ? matrix : matrix.inverse;
                instanceMatch.InstancedInverses.InternalSetData(s_OneMatrixIl2CppArray, 0, instanceMatch.Index, 1, MatrixSizeInBytes);
            }
        }

        static void WriteHit(InstanceMatch instanceMatch)
        {
            if (instanceMatch.InstancedMatrices == null) return;
            if (!instanceMatch.OwnerTarget.Moved) { WriteOne(instanceMatch, new Matrix4x4()); return; }   // all zero: nothing is drawn
            WriteOne(instanceMatch, instanceMatch.OwnerTarget.Delta * instanceMatch.OriginalMatrix);
            // the game culls by these: the object must be drawn where it stands now
            var point = instanceMatch.OwnerTarget.Delta.MultiplyPoint3x4(new Vector3(instanceMatch.OriginalCullingPosition.x, instanceMatch.OriginalCullingPosition.y, instanceMatch.OriginalCullingPosition.z));
            instanceMatch.InstanceBatches.m_Objects.m_Positions[instanceMatch.Index] = new Unity.Mathematics.float4(point.x, point.y, point.z, instanceMatch.OriginalCullingPosition.w);
            var otherBounds = instanceMatch.OriginalBounds;
            otherBounds.center = instanceMatch.OwnerTarget.Delta.MultiplyPoint3x4(otherBounds.center);
            instanceMatch.InstanceBatches.m_Objects.m_Bounds[instanceMatch.Index] = otherBounds;
        }

        static void RestoreCulling(InstanceMatch instanceMatch)
        {
            instanceMatch.InstanceBatches.m_Objects.m_Positions[instanceMatch.Index] = instanceMatch.OriginalCullingPosition;
            instanceMatch.InstanceBatches.m_Objects.m_Bounds[instanceMatch.Index] = instanceMatch.OriginalBounds;
        }

        static void WriteTarget(Target target)
        {
            foreach (var instanceMatch in s_InstanceMatches) if (instanceMatch.OwnerTarget == target) { try { WriteHit(instanceMatch); } catch { } }
        }

        static readonly List<InstanceMatch> s_HiddenMatches = new List<InstanceMatch>();
        static Il2CppSystem.Array s_ZeroMatrices;
        static int s_ZeroMatrixCount;

        /// Hidden instances in runs of neighbouring indices (one upload each), moved ones one by one.
        static void WriteAll()
        {
            var hidden = s_HiddenMatches;
            hidden.Clear();
            foreach (var instanceMatch in s_InstanceMatches)
            {
                if (instanceMatch.InstancedMatrices == null) continue;
                if (instanceMatch.OwnerTarget.Moved) WriteHit(instanceMatch); else hidden.Add(instanceMatch);
            }
            if (hidden.Count == 0) return;
            hidden.Sort((firstMatch, secondMatch) => firstMatch.InstancedMatrices.Pointer != secondMatch.InstancedMatrices.Pointer ? firstMatch.InstancedMatrices.Pointer.ToInt64().CompareTo(secondMatch.InstancedMatrices.Pointer.ToInt64()) : firstMatch.Index.CompareTo(secondMatch.Index));
            int index = 0;
            while (index < hidden.Count)
            {
                int innerIndex = index;
                while (innerIndex + 1 < hidden.Count && hidden[innerIndex + 1].InstancedMatrices.Pointer == hidden[index].InstancedMatrices.Pointer && hidden[innerIndex + 1].Index == hidden[innerIndex].Index + 1) innerIndex++;
                int count = innerIndex - index + 1;
                if (s_ZeroMatrices == null || s_ZeroMatrixCount < count)
                {
                    s_ZeroMatrixCount = Mathf.Max(count, MinimumZeroMatrixCount);
                    s_ZeroMatrices = new Il2CppSystem.Array(new Il2CppStructArray<Matrix4x4>(s_ZeroMatrixCount).Pointer);
                }
                hidden[index].InstancedMatrices.InternalSetData(s_ZeroMatrices, 0, hidden[index].Index, count, MatrixSizeInBytes);
                if (hidden[index].InstancedInverses != null) hidden[index].InstancedInverses.InternalSetData(s_ZeroMatrices, 0, hidden[index].Index, count, MatrixSizeInBytes);
                index = innerIndex + 1;
            }
        }

        // ------------------------------------------------------------------ picking

        /// An object drawn by instancing that the crosshair points at (it may have no collider at all).
        internal sealed class InstancedObjectPick
        {
            public string Prefix;
            public Vector3 OriginalPosition;       // where the region placed it (the key of its edit)
            public Quaternion OriginalRotation;
            public Vector3 CurrentPosition;           // where it stands now
            public Quaternion CurrentRotation;
            public Bounds WorldBounds;
            public float DistanceInMeters;
            public string Key => Prefix + "@" + OriginalPosition.x.ToString("F2") + "," + OriginalPosition.z.ToString("F2");
        }

        struct PickCandidate { public ComputeBuffer InstancedMatrices; public int Index; public Bounds CullingBounds; public string Name; public Mesh Mesh; }

        static readonly List<PickCandidate> s_PickCandidates = new List<PickCandidate>();
        static readonly Dictionary<System.IntPtr, System.IntPtr> s_PickCandidateBatches = new Dictionary<System.IntPtr, System.IntPtr>();
        static readonly Dictionary<(long, int), Matrix4x4> s_MatrixCache = new Dictionary<(long, int), Matrix4x4>();

        sealed class TriangleData { public Vector3[] Vertices; public int[] TriangleIndices; }
        static readonly Dictionary<int, TriangleData> s_TriangleDataByMeshId = new Dictionary<int, TriangleData>();

        /// The pickable objects: every instance of the most detailed batches, with its (generous) culling bounds.
        static void BuildCands()
        {
            var list = RenderObjectInstance.s_RenderObjectInstanceList;
            if (list == null) return;
            bool allBatchesUnchanged = s_PickCandidates.Count > 0;
            foreach (var instance in list)
            {
                if (instance == null || instance.m_Batches == null || !instance.m_Batches.IsAllocated()) continue;
                if (!s_PickCandidateBatches.TryGetValue(instance.Pointer, out var knownBatchesPointer) || knownBatchesPointer != instance.m_Batches.Pointer) allBatchesUnchanged = false;
            }
            if (allBatchesUnchanged) return;
            s_PickCandidates.Clear(); s_PickCandidateBatches.Clear(); s_MatrixCache.Clear();
            foreach (var instance in list)
            {
                if (instance == null) continue;
                var instanceBatches = instance.m_Batches;
                if (instanceBatches == null || !instanceBatches.IsAllocated() || instanceBatches.m_Names == null) continue;
                var perObject = instanceBatches.m_Objects;
                var instancedMatrices = perObject.m_InstancedMatrices;
                if (instancedMatrices == null) continue;
                int batches = Mathf.Min(instanceBatches.m_Names.Count, instanceBatches.GetBatchCount());
                for (int index = 0; index < batches; index++)
                {
                    string name = instanceBatches.m_Names[index];
                    if (name == null || !IsLod0(name)) continue;
                    Mesh mesh = null;
                    try { var renderInfo = instanceBatches.m_Batches.m_RenderInfos[index]; if (renderInfo != null) mesh = renderInfo.m_Mesh; } catch { }
                    if (mesh == null) continue;
                    int startIndex = instanceBatches.m_Batches.m_StartIndexes[index], count = instanceBatches.m_Batches.m_ObjectCounts[index];
                    for (int innerIndex = 0; innerIndex < count; innerIndex++)
                        s_PickCandidates.Add(new PickCandidate { InstancedMatrices = instancedMatrices, Index = startIndex + innerIndex, CullingBounds = perObject.m_Bounds[startIndex + innerIndex], Name = name, Mesh = mesh });
                }
                s_PickCandidateBatches[instance.Pointer] = instanceBatches.Pointer;
            }
        }

        static InstanceMatch FindInstanceMatch(ComputeBuffer instancedMatrices, int index)
        {
            foreach (var instanceMatch in s_InstanceMatches) if (instanceMatch.Index == index && instanceMatch.InstancedMatrices != null && instanceMatch.InstancedMatrices.Pointer == instancedMatrices.Pointer) return instanceMatch;
            return null;
        }

        static Matrix4x4 ReadMatrix(ComputeBuffer instancedMatrices, int index)
        {
            var key = (instancedMatrices.Pointer.ToInt64(), index);
            if (s_MatrixCache.TryGetValue(key, out var matrix)) return matrix;
            if (s_OneMatrixArray == null) { s_OneMatrixArray = new Il2CppStructArray<Matrix4x4>(1); s_OneMatrixIl2CppArray = new Il2CppSystem.Array(s_OneMatrixArray.Pointer); }
            instancedMatrices.InternalGetData(s_OneMatrixIl2CppArray, 0, index, 1, MatrixSizeInBytes);
            matrix = s_OneMatrixArray[0];
            s_MatrixCache[key] = matrix;
            return matrix;
        }

        static TriangleData TrianglesOfMesh(Mesh mesh)
        {
            int identifier = mesh.GetInstanceID();
            if (s_TriangleDataByMeshId.TryGetValue(identifier, out var cachedTriangleData)) return cachedTriangleData;
            if (mesh.isReadable) cachedTriangleData = new TriangleData { Vertices = mesh.vertices, TriangleIndices = mesh.triangles };
            s_TriangleDataByMeshId[identifier] = cachedTriangleData;
            return cachedTriangleData;
        }

        /// Ray against a mesh's triangles (Moller-Trumbore); the ray is in the mesh's own space, t is kept in world units.
        static bool RayHitsMesh(TriangleData triangles, Vector3 origin, Vector3 direction, out float best)
        {
            best = float.MaxValue;
            var vertices = triangles.Vertices; var triangleIndices = triangles.TriangleIndices;
            for (int index = 0; index + LastVertexOffsetInTriangle < triangleIndices.Length; index += TriangleVertexCount)
            {
                var start = vertices[triangleIndices[index]]; var firstEdge = vertices[triangleIndices[index + 1]] - start; var secondEdge = vertices[triangleIndices[index + 2]] - start;
                var directionCrossSecondEdge = Vector3.Cross(direction, secondEdge);
                float determinant = Vector3.Dot(firstEdge, directionCrossSecondEdge);
                if (determinant > -ParallelRayEpsilon && determinant < ParallelRayEpsilon) continue;
                float inverse = 1f / determinant;
                var originToStart = origin - start;
                float firstBarycentricCoordinate = Vector3.Dot(originToStart, directionCrossSecondEdge) * inverse;
                if (firstBarycentricCoordinate < 0f || firstBarycentricCoordinate > 1f) continue;
                var originCrossFirstEdge = Vector3.Cross(originToStart, firstEdge);
                float weight = Vector3.Dot(direction, originCrossFirstEdge) * inverse;
                if (weight < 0f || firstBarycentricCoordinate + weight > 1f) continue;
                float hitDistance = Vector3.Dot(secondEdge, originCrossFirstEdge) * inverse;
                if (hitDistance > 0f && hitDistance < best) best = hitDistance;
            }
            return best < float.MaxValue;
        }

        /// The nearest instanced object along the ray (null if none within maxDist).
        public static InstancedObjectPick Pick(Ray ray, float maxDistanceInMeters)
        {
            BuildCands();
            InstancedObjectPick best = null;
            float bestDistance = maxDistanceInMeters;
            ray.direction = ray.direction.normalized;

            void TestCandidate(PickCandidate candidate, bool prefilter)
            {
                if (prefilter)
                {
                    if (!candidate.CullingBounds.IntersectRay(ray, out float boundsEntryDistance)) return;
                    if (boundsEntryDistance > bestDistance) return;
                }
                var instanceMatch = FindInstanceMatch(candidate.InstancedMatrices, candidate.Index);
                Matrix4x4 matrix;
                Vector3 originalPosition; Quaternion originalRotation;
                if (instanceMatch != null)
                {
                    if (!instanceMatch.OwnerTarget.Moved) return;                    // hidden
                    matrix = instanceMatch.OwnerTarget.Delta * instanceMatch.OriginalMatrix;
                    originalPosition = instanceMatch.OwnerTarget.OriginalPosition; originalRotation = RotationOfMatrix(instanceMatch.OriginalMatrix);
                }
                else
                {
                    matrix = ReadMatrix(candidate.InstancedMatrices, candidate.Index);
                    if (matrix.m33 == 0f) return;
                    originalPosition = new Vector3(matrix.m03, matrix.m13, matrix.m23); originalRotation = RotationOfMatrix(matrix);
                }
                var triangles = TrianglesOfMesh(candidate.Mesh);
                var inverseMatrix = matrix.inverse;
                var localOrigin = inverseMatrix.MultiplyPoint3x4(ray.origin);
                var localDirection = inverseMatrix.MultiplyVector(ray.direction);
                float hitDistance;
                if (triangles != null) { if (!RayHitsMesh(triangles, localOrigin, localDirection, out hitDistance)) return; }
                else
                {
                    var localBounds = candidate.Mesh.bounds;
                    if (!localBounds.IntersectRay(new Ray(localOrigin, localDirection), out hitDistance) || hitDistance < 0f) return;
                }
                if (hitDistance >= bestDistance) return;
                bestDistance = hitDistance;
                var combinedBounds = candidate.Mesh.bounds;
                var worldBounds = new Bounds(matrix.MultiplyPoint3x4(combinedBounds.center), Vector3.zero);
                for (int index = 0; index < BoundsCornerCount; index++)
                    worldBounds.Encapsulate(matrix.MultiplyPoint3x4(combinedBounds.center + Vector3.Scale(combinedBounds.extents, new Vector3((index & CornerMaskX) == 0 ? -1 : 1, (index & CornerMaskY) == 0 ? -1 : 1, (index & CornerMaskZ) == 0 ? -1 : 1))));
                string name = Base(candidate.Name);
                best = new InstancedObjectPick { Prefix = name, OriginalPosition = originalPosition, OriginalRotation = originalRotation, CurrentPosition = new Vector3(matrix.m03, matrix.m13, matrix.m23), CurrentRotation = RotationOfMatrix(matrix), WorldBounds = worldBounds, DistanceInMeters = hitDistance };
            }

            foreach (var candidate in s_PickCandidates) TestCandidate(candidate, true);
            // moved ones have left their culling bounds
            foreach (var instanceMatch in s_InstanceMatches)
            {
                if (!instanceMatch.OwnerTarget.Moved || instanceMatch.Mesh == null || instanceMatch.Name == null || !IsLod0(instanceMatch.Name)) continue;
                TestCandidate(new PickCandidate { InstancedMatrices = instanceMatch.InstancedMatrices, Index = instanceMatch.Index, Name = instanceMatch.Name, Mesh = instanceMatch.Mesh }, false);
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

        static Quaternion RotationOfMatrix(Matrix4x4 matrix)
        {
            var forward = new Vector3(matrix.m02, matrix.m12, matrix.m22);
            var upDirection = new Vector3(matrix.m01, matrix.m11, matrix.m21);
            return forward.sqrMagnitude > MinimumForwardSquaredMagnitude ? Quaternion.LookRotation(forward, upDirection) : Quaternion.identity;
        }

        public static void Update()
        {
            if (s_Targets.Count == 0) return;
            try
            {
                if (Time.time >= s_NextCheck)
                {
                    // the game rebuilt its batches (quality settings, re-enabling): find the instances again
                    s_NextCheck = Time.time + RebuildCheckIntervalInSeconds;
                    var list = RenderObjectInstance.s_RenderObjectInstanceList;
                    if (list != null)
                        foreach (var instance in list)
                        {
                            if (instance == null || instance.m_Batches == null || !instance.m_Batches.IsAllocated()) continue;
                            if (!s_DoneBatchesByInstance.TryGetValue(instance.Pointer, out var knownBatchesPointer) || knownBatchesPointer != instance.m_Batches.Pointer)
                            {
                                s_InstanceMatches.Clear();
                                s_DoneBatchesByInstance.Clear();
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
                    s_NextWrite = Time.time + WriteIntervalInSeconds;
                    WriteAll();
                }
            }
            catch { }
        }
    }
}
