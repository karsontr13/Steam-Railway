using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace SteamRailway
{
    /// A densely sampled uniform Catmull-Rom curve through the track's control points (the curve type of the Curvy
    /// spline) that bends rail pieces and finds the track corridor. Points are rail-top centre points.
    internal sealed class CurvePath
    {
        const float SampleSpacingInMeters = 0.5f;
        const float MinimumPointSpacingInMeters = 0.01f;
        const float MinimumSegmentLengthInMeters = 1e-5f;
        readonly List<Vector3> m_Points = new List<Vector3>();
        readonly List<float> m_Distances = new List<float>();
        Vector3[] m_Tangents;                                  // smooth tangent at each sample (central difference)

        public float Length => m_Distances.Count > 0 ? m_Distances[m_Distances.Count - 1] : 0f;
        public int Count => m_Points.Count;
        public Vector3 this[int index] => m_Points[index];
        public float DistanceAt(int index) => m_Distances[index];

        public static CurvePath CatmullRom(IList<Vector3> controlPoints)
        {
            var curve = new CurvePath();
            int count = controlPoints.Count;
            for (int index = 0; index < count - 1; index++)
            {
                Vector3 endPoint = controlPoints[index], thirdPoint = controlPoints[index + 1];
                Vector3 startPoint = index > 0 ? controlPoints[index - 1] : endPoint;            // Curvy's automatic end tangents
                Vector3 fourthPoint = index + 2 < count ? controlPoints[index + 2] : thirdPoint;
                int steps = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(endPoint, thirdPoint) / SampleSpacingInMeters));
                for (int innerIndex = 0; innerIndex < steps; innerIndex++) curve.Add(CalculateCatmullRomPoint(startPoint, endPoint, thirdPoint, fourthPoint, innerIndex / (float)steps));
            }
            curve.Add(controlPoints[count - 1]);
            curve.BuildTangents();
            return curve;
        }

        static Vector3 CalculateCatmullRomPoint(Vector3 startPoint, Vector3 endPoint, Vector3 thirdPoint, Vector3 fourthPoint, float fraction)
        {
            float fractionSquared = fraction * fraction, fractionCubed = fractionSquared * fraction;
            return 0.5f * (2f * endPoint + (thirdPoint - startPoint) * fraction + (2f * startPoint - 5f * endPoint + 4f * thirdPoint - fourthPoint) * fractionSquared + (3f * endPoint - startPoint - 3f * thirdPoint + fourthPoint) * fractionCubed);
        }

        void Add(Vector3 point)
        {
            if (m_Points.Count > 0 && Vector3.Distance(m_Points[m_Points.Count - 1], point) < MinimumPointSpacingInMeters) return;
            m_Distances.Add(m_Points.Count == 0 ? 0f : m_Distances[m_Distances.Count - 1] + Vector3.Distance(m_Points[m_Points.Count - 1], point));
            m_Points.Add(point);
        }

        void BuildTangents()
        {
            int count = m_Points.Count;
            m_Tangents = new Vector3[count];
            for (int index = 0; index < count; index++)
            {
                var start = m_Points[Mathf.Max(0, index - 1)];
                var end = m_Points[Mathf.Min(count - 1, index + 1)];
                m_Tangents[index] = (end - start).normalized;
            }
        }

        public void Sample(float arcLength, out Vector3 position, out Vector3 tangent)
        {
            arcLength = Mathf.Clamp(arcLength, 0f, Length);
            int lower = 0, upper = m_Distances.Count - 1;
            while (upper - lower > 1)
            {
                int middleIndex = (lower + upper) >> 1;
                if (m_Distances[middleIndex] <= arcLength) lower = middleIndex; else upper = middleIndex;
            }
            float segmentLength = m_Distances[upper] - m_Distances[lower];
            float fraction = segmentLength > MinimumSegmentLengthInMeters ? (arcLength - m_Distances[lower]) / segmentLength : 0f;
            position = Vector3.Lerp(m_Points[lower], m_Points[upper], fraction);
            // turning smoothly between the samples (a per-segment direction kinks bent rails)
            tangent = m_Tangents != null ? Vector3.Lerp(m_Tangents[lower], m_Tangents[upper], fraction).normalized : (m_Points[upper] - m_Points[lower]).normalized;
        }

        /// Distance along the curve of the point closest to p in 3D (lines crossing at different heights).
        public float NearestDistanceIncludingHeight(Vector3 point, out float distance)
        {
            float best = float.MaxValue, bestArcLength = 0f;
            for (int index = 0; index < m_Points.Count; index++)
            {
                float squaredDistance = (m_Points[index] - point).sqrMagnitude;
                if (squaredDistance < best) { best = squaredDistance; bestArcLength = m_Distances[index]; }
            }
            distance = Mathf.Sqrt(best);
            return bestArcLength;
        }

        /// Distance along the curve of the point closest to p (horizontal distance).
        public float NearestDistance(Vector3 point, out float horizontalDistance)
        {
            float best = float.MaxValue, bestArcLength = 0f;
            for (int index = 0; index < m_Points.Count; index++)
            {
                float deltaX = m_Points[index].x - point.x, deltaZ = m_Points[index].z - point.z;
                float distance = deltaX * deltaX + deltaZ * deltaZ;
                if (distance < best) { best = distance; bestArcLength = m_Distances[index]; }
            }
            horizontalDistance = Mathf.Sqrt(best);
            return bestArcLength;
        }
    }

    /// What to lay of the rail piece (turnouts): both rails or one, sleepers or not, sleepers lengthened to one
    /// side (the long timbers under the points that carry both tracks).
    internal sealed class LayOptions
    {
        public bool Rails = true;
        public int RailSide;                               // 0 both rails, -1 only the rail at -x, +1 only +x
        public bool Sleepers = true;
        public System.Func<float, float> SleeperExtension;       // path metres -> metres added to the sleeper ends (+x side if > 0)
        public System.Func<float, float> SleeperYaw;       // path metres -> along-track shift per metre out (turns the sleepers, rails stay)
        public System.Func<float, float> SleeperCut;          // path metres -> the sleeper ends on one side reach at most this far (+x side if > 0; 0 = whole)
        public System.Func<float, bool> SleeperKeep;       // path metres -> a sleeper here is laid (null: all)
        public System.Func<float, bool> RailKeep;          // path metres -> the rails here are laid (null: all)
        public float YOffset;
        public Transform Parent;                           // lay under this instead (moving switch blades)
    }

    /// The game's own rail piece (OBJ_RailTrackStraitA, 33.5 m) bent along a path. The meshes and materials are
    /// taken from the loaded region at run time; nothing is shipped with the mod.
    internal static class RailPieces
    {
        public const float RailTop = 0.402f;          // rail head above the piece origin
        public const float CentreX = -0.025f;         // centre between the two rails in piece space
        const float FullStraightMinimumLengthInMeters = 32f;
        const float FullStraightMaximumLengthInMeters = 35f;
        const float FullStraightMinimumWidthInMeters = 2.5f;
        const float FullStraightMaximumWidthInMeters = 3.5f;
        const float FullStraightMaximumEndZ = 1f;
        const float MinimumGapInMeters = 0.4f;
        const int MaximumPieceCopies = 400;
        const int TriangleVertexCount = 3;
        const int LastVertexOffsetInTriangle = 2;
        const float MinimumStretch = 0.01f;
        const int MaximumSixteenBitVertexCount = 65000;
        const float StraightSliceInMeters = 1000f;
        const float MinimumCutSpanInMeters = 0.5f;
        const float MinimumTurnInDegreesPerMeter = 0.01f;
        const float MaximumChordErrorInMeters = 0.01f;
        const float ChordErrorFactor = 8f;
        const float MaximumSliceInMeters = 8f;
        const float MinimumSegmentLengthSquared = 1e-6f;

        public const float SourceStartZ = -0.106f, SourceEndZ = -33.6f;
        public const float SourceLengthInMeters = SourceStartZ - SourceEndZ; // 33.494 m
        static readonly string[] MeshNames = { "OBJ_RailTrackStraitA_LOD00", "OBJ_RailTrackStraitA_LOD01", "OBJ_RailTrackStraitA_LOD02" };
        static readonly float[] LodHeights = { 0.25f, 0.07f, 0.008f };

        static Mesh[] s_SourceMeshes;
        static Material[][] s_Materials;

        /// The full 33.5 m straight piece. The game has other meshes with the same name (a short broken piece
        /// modelled along x), so the geometry decides, not the name.
        static bool IsFullStraight(Mesh mesh)
        {
            if (mesh == null || !mesh.isReadable) return false;
            var meshBounds = mesh.bounds;
            return meshBounds.size.z > FullStraightMinimumLengthInMeters && meshBounds.size.z < FullStraightMaximumLengthInMeters && meshBounds.size.x > FullStraightMinimumWidthInMeters && meshBounds.size.x < FullStraightMaximumWidthInMeters && meshBounds.max.z < FullStraightMaximumEndZ;
        }

        public static bool Ensure()
        {
            bool succeeded = s_SourceMeshes != null && s_Materials != null;
            if (succeeded)
                for (int index = 0; index < s_SourceMeshes.Length && succeeded; index++)
                {
                    if (s_SourceMeshes[index] == null) succeeded = false;
                    else foreach (var material in s_Materials[index]) if (material == null) { succeeded = false; break; }
                }
            if (succeeded) return true;
            s_SourceMeshes = new Mesh[MeshNames.Length];
            foreach (var mesh in Resources.FindObjectsOfTypeAll<Mesh>())
                for (int index = 0; index < MeshNames.Length; index++)
                    if (mesh.name == MeshNames[index] && IsFullStraight(mesh)) s_SourceMeshes[index] = mesh;
            Material mix = null, green = null;
            foreach (var material in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (material.name == "GLB_MixBig_C05") mix = material;
                else if (material.name == "GLB_Green_A05") green = material;
            }
            // the layer the region's own rail pieces are on (footsteps, what the player can stand on)
            foreach (var transform in Resources.FindObjectsOfTypeAll<Transform>())
                if (transform.gameObject.scene.name != null && transform.name.StartsWith("OBJ_RailTrackStraitA_Prefab")) { s_Layer = transform.gameObject.layer; break; }
            if (s_SourceMeshes[0] == null || green == null)
            {
                SteamRailwayMod.Log.Warning("The game's rail meshes are not loaded in this region; rails cannot be laid.");
                s_SourceMeshes = null;
                return false;
            }
            s_Materials = new Material[MeshNames.Length][];
            for (int index = 0; index < MeshNames.Length; index++)
            {
                var sourceMesh = s_SourceMeshes[index] ?? s_SourceMeshes[0];
                s_SourceMeshes[index] = sourceMesh;
                s_Materials[index] = sourceMesh.subMeshCount >= 2 ? new[] { mix ?? green, green } : new[] { green };
            }
            return true;
        }

        // Sleepers of the straight piece, in metres from its start: 37 at ~0.91 m, the first 0.30 m in, the last
        // 0.41 m before the end.
        const float SleeperFirst = 0.30f, SleeperLastToEnd = 0.41f, SleeperPitch = 0.9106f;

        /// Lays rail along [startArc, endArc] of the path at the game's sleeper pitch: sleepers are spaced evenly across
        /// the gap and the rails are cut exactly at both ends. Long gaps get several copies of the piece.
        public static void Lay(Transform parent, CurvePath path, float startArc, float endArc, LayOptions options = null)
        {
            if (options != null && options.Parent != null) parent = options.Parent;
            float gap = endArc - startArc;
            if (gap < MinimumGapInMeters || !Ensure()) return;
            float span = gap + SleeperLastToEnd + SleeperFirst;
            int sleeperCountInGap = Mathf.Max(0, Mathf.RoundToInt(span / SleeperPitch) - 1);   // sleepers inside the gap
            float sleeperSpacing = span / (sleeperCountInGap + 1);                                          // their spacing
            float stretch = sleeperSpacing / SleeperPitch;
            // source sleeper 1 becomes the first sleeper in the gap, at d - 0.41 m past s0
            float sourceSecondSleeperOffset = SleeperFirst + SleeperPitch;
            float sourceOffset = sourceSecondSleeperOffset - (sleeperSpacing - SleeperLastToEnd) / stretch;
            for (int index = 0; index < MaximumPieceCopies; index++)
            {
                float copyShift = sourceOffset - index * SourceLengthInMeters;          // each copy continues where the previous one ended
                float copyStartInGap = -copyShift * stretch, copyEndInGap = (SourceLengthInMeters - copyShift) * stretch;
                if (copyStartInGap >= gap) break;
                if (copyEndInGap <= 0f) continue;
                // only the copies at the two ends need cutting
                Piece(parent, path, startArc, stretch, copyShift, copyStartInGap < 0f ? 0f : float.MinValue, copyEndInGap > gap ? gap : float.MaxValue, options);
            }
        }

        /// One piece: source metres u (from the piece start) map to path distance start + (u - srcOffset) * stretch,
        /// cut to [clipLo, clipHi] (in metres past start).
        static void Piece(Transform parent, CurvePath path, float start, float stretch, float sourceOffset, float clipStart, float clipEnd, LayOptions options)
        {
            path.Sample(start + Mathf.Max(0f, -sourceOffset * stretch), out var origin, out _);   // near this copy
            float slice = SliceFor(path, start, stretch, sourceOffset, clipStart, clipEnd);
            var targetObject = new GameObject("RailPiece");
            targetObject.transform.SetParent(parent, false);
            targetObject.transform.position = origin;
            var lods = new List<LOD>();
            GameObject lastLodObject = null; Mesh lastMesh = null;
            for (int index = 0; index < s_SourceMeshes.Length; index++)
            {
                var mesh = Deform(s_SourceMeshes[index], path, start, stretch, sourceOffset, clipStart, clipEnd, origin, options, slice * (1 << index));
                if (mesh == null) continue;
                var lightObject = new GameObject("LOD" + index);
                lightObject.transform.SetParent(targetObject.transform, false);
                lightObject.layer = s_Layer;
                lastLodObject = lightObject; lastMesh = mesh;
                lightObject.AddComponent<MeshFilter>().sharedMesh = mesh;
                var meshRenderer = lightObject.AddComponent<MeshRenderer>();
                meshRenderer.sharedMaterials = new Il2CppReferenceArray<Material>(s_Materials[index]);
                lods.Add(new LOD(LodHeights[index], new Il2CppReferenceArray<Renderer>(new Renderer[] { meshRenderer })));
            }
            if (lods.Count == 0) { Object.Destroy(targetObject); return; }
            var lodGroup = targetObject.AddComponent<LODGroup>();
            lodGroup.SetLODs(new Il2CppReferenceArray<LOD>(lods.ToArray()));
            lodGroup.RecalculateBounds();
            // solid like the region's own rails (the simplest level is enough to walk on)
            targetObject.layer = s_Layer;
            if (lastLodObject != null)
            {
                try { lastLodObject.AddComponent<MeshCollider>().sharedMesh = lastMesh; }
                catch (System.Exception exception) { SteamRailwayMod.Debug($"Rail collider: {exception.Message}"); }
            }
        }

        static int s_Layer;

        /// Keeps the part of a polygon with sign * along <= sign * limit (Sutherland-Hodgman against one plane);
        /// vertices made on the cut are appended to the attribute lists.
        static List<int> ClipPoly(List<int> polygon, float limit, float sign, List<Vector3> points, List<Vector3> normals,
                                  List<Vector4> tangents, List<Vector2> uvs, List<bool> flags, System.Func<int, float> along)
        {
            if (polygon.Count < TriangleVertexCount || limit == float.MaxValue || limit == float.MinValue) return polygon;
            var results = new List<int>(polygon.Count + 1);
            for (int endIndex = 0; endIndex < polygon.Count; endIndex++)
            {
                int startVertexIndex = polygon[endIndex], endVertexIndex = polygon[(endIndex + 1) % polygon.Count];
                float startAlong = along(startVertexIndex), endAlong = along(endVertexIndex);
                bool startInside = sign * startAlong <= sign * limit, endInside = sign * endAlong <= sign * limit;
                if (startInside) results.Add(startVertexIndex);
                if (startInside != endInside)
                {
                    float fraction = (limit - startAlong) / (endAlong - startAlong);
                    points.Add(Vector3.Lerp(points[startVertexIndex], points[endVertexIndex], fraction));
                    normals.Add(Vector3.Lerp(normals[startVertexIndex], normals[endVertexIndex], fraction).normalized);
                    tangents.Add(Vector4.Lerp(tangents[startVertexIndex], tangents[endVertexIndex], fraction));
                    uvs.Add(Vector2.Lerp(uvs[startVertexIndex], uvs[endVertexIndex], fraction));
                    flags.Add(flags[startVertexIndex]);
                    results.Add(points.Count - 1);
                }
            }
            return results;
        }

        /// The source mesh copied out of Il2Cpp once (reading it element by element through interop is slow).
        sealed class SourceMeshData
        {
            public Vector3[] Vertices, Normals; public Vector4[] Tangents; public Vector2[] Uvs; public int[][] Triangles;
            public bool HasNormals, HasTangents, HasUv;
            public bool[] IsRailVertex;            // vertex belongs to a rail (not to a sleeper)
        }
        static readonly Dictionary<int, SourceMeshData> s_SourceDataByMeshId = new Dictionary<int, SourceMeshData>();

        static SourceMeshData DataOf(Mesh sourceMesh)
        {
            int identifier = sourceMesh.GetInstanceID();
            if (s_SourceDataByMeshId.TryGetValue(identifier, out var sourceData)) return sourceData;
            sourceData = new SourceMeshData();
            sourceData.Vertices = sourceMesh.vertices; var normals = sourceMesh.normals; var tangents = sourceMesh.tangents; var uvs = sourceMesh.uv;
            sourceData.HasNormals = normals.Length == sourceData.Vertices.Length; sourceData.HasTangents = tangents.Length == sourceData.Vertices.Length; sourceData.HasUv = uvs.Length == sourceData.Vertices.Length;
            sourceData.Normals = sourceData.HasNormals ? (Vector3[])normals : null; sourceData.Tangents = sourceData.HasTangents ? (Vector4[])tangents : null; sourceData.Uvs = sourceData.HasUv ? (Vector2[])uvs : null;
            sourceData.Triangles = new int[sourceMesh.subMeshCount][];
            for (int subMeshIndex = 0; subMeshIndex < sourceMesh.subMeshCount; subMeshIndex++) sourceData.Triangles[subMeshIndex] = sourceMesh.GetTriangles(subMeshIndex);
            sourceData.IsRailVertex = new bool[sourceData.Vertices.Length];
            if (sourceMesh.subMeshCount >= 2) foreach (var triangleVertexIndex in sourceData.Triangles[0]) sourceData.IsRailVertex[triangleVertexIndex] = true;
            else for (int index = 0; index < sourceData.Vertices.Length; index++) sourceData.IsRailVertex[index] = sourceData.Vertices[index].y > RailMinY;
            s_SourceDataByMeshId[identifier] = sourceData;
            return sourceData;
        }

        // rails: submesh 0 of the full piece (y 0.165 - 0.402); a one-submesh LOD is split by height
        const float RailMinY = 0.29f;
        const float RailSlice = 0.5f;   // m: the shortest slice rail faces are cut into before they are bent

        /// How short the rail faces of one copy must be cut to follow its bend (1 cm off the true curve at most);
        /// a straight stretch is not cut at all.
        static float SliceFor(CurvePath path, float start, float stretch, float sourceOffset, float clipStart, float clipEnd)
        {
            float rangeStart = start + Mathf.Max(-sourceOffset * stretch, clipStart > float.MinValue ? clipStart : float.MinValue);
            float rangeEnd = start + Mathf.Min((SourceLengthInMeters - sourceOffset) * stretch, clipEnd < float.MaxValue ? clipEnd : float.MaxValue);
            rangeStart = Mathf.Clamp(rangeStart, 0f, path.Length); rangeEnd = Mathf.Clamp(rangeEnd, 0f, path.Length);
            if (rangeEnd - rangeStart < MinimumCutSpanInMeters) return StraightSliceInMeters;
            float turn = 0f;
            path.Sample(rangeStart, out _, out var startTangent);
            const int sampleCount = 8;
            for (int index = 1; index <= sampleCount; index++)
            {
                path.Sample(Mathf.Lerp(rangeStart, rangeEnd, index / (float)sampleCount), out _, out var endTangent);
                var startFlat = new Vector3(startTangent.x, 0f, startTangent.z); var endFlat = new Vector3(endTangent.x, 0f, endTangent.z);
                turn = Mathf.Max(turn, Vector3.Angle(startFlat, endFlat) / ((rangeEnd - rangeStart) / sampleCount));   // degrees per metre
                // a change of grade bends the rails too
                turn = Mathf.Max(turn, Mathf.Abs(Mathf.Asin(Mathf.Clamp(endTangent.y, -1f, 1f)) - Mathf.Asin(Mathf.Clamp(startTangent.y, -1f, 1f))) * Mathf.Rad2Deg / ((rangeEnd - rangeStart) / sampleCount));
                startTangent = endTangent;
            }
            if (turn < MinimumTurnInDegreesPerMeter) return StraightSliceInMeters;
            float radius = 1f / (turn * Mathf.Deg2Rad);
            return Mathf.Clamp(Mathf.Sqrt(ChordErrorFactor * radius * MaximumChordErrorInMeters), RailSlice, MaximumSliceInMeters);
        }
        const float SleeperEndFrom = 1.05f, SleeperEndTo = 1.50f;   // |x| of the sleeper ends that are lengthened

        static Mesh Deform(Mesh sourceMesh, CurvePath path, float start, float stretch, float sourceOffset, float clipStart, float clipEnd, Vector3 origin, LayOptions options, float slice)
        {
            var data = DataOf(sourceMesh);
            var vertices = data.Vertices; var normals = data.Normals; var tangents = data.Tangents; var uvs = data.Uvs;
            int sourceVertexCount = vertices.Length;
            bool hasNormals = data.HasNormals, hasTangents = data.HasTangents, hasUv = data.HasUv;

            // source attributes (new vertices are added where triangles are cut at the clip distance)
            var points = new List<Vector3>(sourceVertexCount); var normalList = new List<Vector3>(sourceVertexCount); var tangentList = new List<Vector4>(sourceVertexCount); var uvList = new List<Vector2>(sourceVertexCount);
            var flags = new List<bool>(sourceVertexCount);    // rail vertex (bent with the line) or sleeper vertex (kept rigid)
            for (int index = 0; index < sourceVertexCount; index++)
            {
                points.Add(vertices[index]);
                flags.Add(data.IsRailVertex[index]);
                normalList.Add(hasNormals ? normals[index] : Vector3.up);
                tangentList.Add(hasTangents ? tangents[index] : new Vector4(1, 0, 0, 1));
                uvList.Add(hasUv ? uvs[index] : Vector2.zero);
            }
            float Along(int vertexIndex) => (SourceStartZ - points[vertexIndex].z - sourceOffset) * stretch;
            bool clips = clipStart > float.MinValue || clipEnd < float.MaxValue;

            var subMeshTriangles = new List<List<int>>();
            int kept = 0;
            bool filter = options != null && (!options.Rails || !options.Sleepers || options.RailSide != 0);
            bool split = sourceMesh.subMeshCount >= 2;
            for (int subMeshIndex = 0; subMeshIndex < sourceMesh.subMeshCount; subMeshIndex++)
            {
                var sourceTriangles = data.Triangles[subMeshIndex];
                var outputTriangles = new List<int>(sourceTriangles.Length);
                for (int index = 0; index + LastVertexOffsetInTriangle < sourceTriangles.Length; index += TriangleVertexCount)
                {
                    int firstVertex = sourceTriangles[index], secondVertex = sourceTriangles[index + 1], thirdVertex = sourceTriangles[index + 2];
                    var centroid = (vertices[firstVertex] + vertices[secondVertex] + vertices[thirdVertex]) / (float)TriangleVertexCount;
                    bool rail = split ? subMeshIndex == 0 : centroid.y > RailMinY;
                    if (filter)
                    {
                        if (rail ? !options.Rails : !options.Sleepers) continue;
                        if (rail && options.RailSide != 0 && Mathf.Sign(centroid.x - CentreX) != options.RailSide) continue;
                    }
                    float firstAlong = Along(firstVertex), secondAlong = Along(secondVertex), thirdAlong = Along(thirdVertex);
                    float minimumAlong = Mathf.Min(firstAlong, Mathf.Min(secondAlong, thirdAlong)), maximumAlong = Mathf.Max(firstAlong, Mathf.Max(secondAlong, thirdAlong));
                    float middleArc = start + (firstAlong + secondAlong + thirdAlong) / (float)TriangleVertexCount;
                    if (!rail && options?.SleeperKeep != null && !options.SleeperKeep(middleArc)) continue;
                    if (rail && maximumAlong - minimumAlong <= slice && options?.RailKeep != null && !options.RailKeep(middleArc)) continue;
                    if (!clips && !(rail && maximumAlong - minimumAlong > slice)) { outputTriangles.Add(firstVertex); outputTriangles.Add(secondVertex); outputTriangles.Add(thirdVertex); continue; }
                    var polygon = new List<int> { firstVertex, secondVertex, thirdVertex };
                    polygon = ClipPoly(polygon, clipEnd, +1f, points, normalList, tangentList, uvList, flags, Along);
                    polygon = ClipPoly(polygon, clipStart, -1f, points, normalList, tangentList, uvList, flags, Along);
                    if (polygon.Count < TriangleVertexCount) continue;
                    if (rail && maximumAlong - minimumAlong > slice)
                    {
                        // a long rail face is cut into short slices, so it can follow a bend smoothly
                        int firstBand = Mathf.FloorToInt(Mathf.Max(minimumAlong, clips && clipStart > float.MinValue ? clipStart : minimumAlong) / slice);
                        int lastBand = Mathf.CeilToInt(Mathf.Min(maximumAlong, clips && clipEnd < float.MaxValue ? clipEnd : maximumAlong) / slice);
                        for (int band = firstBand; band < lastBand; band++)
                        {
                            if (options?.RailKeep != null && !options.RailKeep(start + (band + 0.5f) * slice)) continue;
                            var piece = ClipPoly(polygon, (band + 1) * slice, +1f, points, normalList, tangentList, uvList, flags, Along);
                            piece = ClipPoly(piece, band * slice, -1f, points, normalList, tangentList, uvList, flags, Along);
                            for (int innerIndex = 1; innerIndex + 1 < piece.Count; innerIndex++) { outputTriangles.Add(piece[0]); outputTriangles.Add(piece[innerIndex]); outputTriangles.Add(piece[innerIndex + 1]); }
                        }
                        continue;
                    }
                    for (int innerIndex = 1; innerIndex + 1 < polygon.Count; innerIndex++) { outputTriangles.Add(polygon[0]); outputTriangles.Add(polygon[innerIndex]); outputTriangles.Add(polygon[innerIndex + 1]); }
                }
                kept += outputTriangles.Count;
                subMeshTriangles.Add(outputTriangles);
            }
            if (kept == 0) return null;

            int count = points.Count;
            var bentVertices = new Vector3[count];
            var bentNormals = new Vector3[count];
            var bentTangents = new Vector4[count];
            var bentUvs = new Vector2[count];
            var extension = options?.SleeperExtension;
            var cutLimit = options?.SleeperCut;
            var yaw = options?.SleeperYaw;
            float verticalOffset = options != null ? options.YOffset : 0f;
            for (int index = 0; index < count; index++)
            {
                var point = points[index];
                float pathArc = start + Along(index);
                if (extension != null)
                {
                    // the sleeper ends on one side reach out under the other track
                    float extensionAmount = extension(pathArc), deltaX = point.x - CentreX;
                    if (extensionAmount != 0f && Mathf.Sign(deltaX) == Mathf.Sign(extensionAmount))
                        point.x += extensionAmount * Mathf.Clamp01((Mathf.Abs(deltaX) - SleeperEndFrom) / (SleeperEndTo - SleeperEndFrom));
                }
                if (cutLimit != null)
                {
                    // two tracks side by side as they part: each one's sleepers stop at the line between them
                    float limit = cutLimit(pathArc), cutDeltaX = point.x - CentreX;
                    if (limit != 0f && Mathf.Sign(cutDeltaX) == Mathf.Sign(limit) && Mathf.Abs(cutDeltaX) > Mathf.Abs(limit))
                        point.x = CentreX + Mathf.Sign(cutDeltaX) * Mathf.Abs(limit);
                }
                bool isRail = flags[index];
                float alongOffset = 0f;   // sleepers: metres along the line from the sleeper's own centre (kept rigid)
                if (!isRail)
                {
                    // a sleeper stays a straight block square to the line at its centre; only the rails bend
                    float sourceAlong = SourceStartZ - point.z;
                    int sleeperIndex = Mathf.RoundToInt((sourceAlong - SleeperFirst) / SleeperPitch);
                    float sleeperCentreAlong = SleeperFirst + sleeperIndex * SleeperPitch;
                    float sleeperCentreArc = start + (sleeperCentreAlong - sourceOffset) * stretch;
                    alongOffset = (pathArc - sleeperCentreArc) / Mathf.Max(MinimumStretch, stretch);
                    pathArc = sleeperCentreArc;
                    // the long sleepers of a turnout fan out between the two tracks
                    if (yaw != null) alongOffset += yaw(pathArc) * (point.x - CentreX);
                }
                path.Sample(pathArc, out var center, out var pathTangent);
                point.y += verticalOffset;
                // piece space: +y up, the track runs along -z; build the same frame on the path
                var worldAxisZ = -pathTangent;
                var worldAxisX = Vector3.Cross(Vector3.up, worldAxisZ).normalized;
                var worldAxisY = Vector3.Cross(worldAxisZ, worldAxisX);
                bentVertices[index] = center + worldAxisX * (point.x - CentreX) + worldAxisY * (point.y - RailTop) + pathTangent * alongOffset - origin;
                var sourceNormal = normalList[index]; bentNormals[index] = (worldAxisX * sourceNormal.x + worldAxisY * sourceNormal.y + worldAxisZ * sourceNormal.z).normalized;
                var sourceTangent = tangentList[index]; var direction = worldAxisX * sourceTangent.x + worldAxisY * sourceTangent.y + worldAxisZ * sourceTangent.z; bentTangents[index] = new Vector4(direction.x, direction.y, direction.z, sourceTangent.w);
                bentUvs[index] = uvList[index];
            }

            var mesh = new Mesh();
            mesh.name = sourceMesh.name + "_Bent";
            if (count > MaximumSixteenBitVertexCount) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(new Il2CppStructArray<Vector3>(bentVertices));
            if (hasNormals) mesh.SetNormals(new Il2CppStructArray<Vector3>(bentNormals));
            if (hasTangents) mesh.SetTangents(new Il2CppStructArray<Vector4>(bentTangents));
            if (hasUv) mesh.SetUVs(0, new Il2CppStructArray<Vector2>(bentUvs));
            mesh.subMeshCount = subMeshTriangles.Count;
            for (int subMeshIndex = 0; subMeshIndex < subMeshTriangles.Count; subMeshIndex++) mesh.SetTriangles(new Il2CppStructArray<int>(subMeshTriangles[subMeshIndex].ToArray()), subMeshIndex);
            mesh.RecalculateBounds();
            return mesh;
        }
    }

    /// Lowers the snow (terrain) along the track so the rails stand clear of it, like the cleared lines of the
    /// railway regions. Only the loaded terrain in memory is changed; the region's files are untouched.
    internal static class TerrainCarve
    {
        const float MinimumSegmentLengthSquared = 1e-6f;
        const float FullyLoweredDistanceInMeters = 2.2f;       // fully lowered within this distance of the track centre
        const float BlendEndDistanceInMeters = 6.0f;       // blended back to the original ground at this distance
        const float BedAboveRailBaseInMeters = 0.05f; // ground level relative to the bottom of the rail piece (sleepers just show)
        const float MaximumCutInMeters = 8.0f;      // deeper cuts are left alone (deep cuttings, tunnel mouths); rockfall snow is cleared

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
            minX -= BlendEndDistanceInMeters; minZ -= BlendEndDistanceInMeters; maxX += BlendEndDistanceInMeters; maxZ += BlendEndDistanceInMeters;

            foreach (var terrain in Terrain.activeTerrains)
            {
                try { LowerOn(terrain, path, minX, minZ, maxX, maxZ); }
                catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"Lowering snow on {terrain.name} failed: {exception}"); }
            }
        }

        const float EmbankmentTopHalfWidthInMeters = 2.4f;    // the embankment's top, either side of the track centre
        const float EmbankmentSlopeRatio = 1.5f;    // its sides: 1.5 m out for every metre down
        const float MaximumFillInMeters = 15f;       // deeper hollows need a bridge, not snow
        const float FillReachInMeters = 26f;

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
            minX -= FillReachInMeters; minZ -= FillReachInMeters; maxX += FillReachInMeters; maxZ += FillReachInMeters;
            foreach (var terrain in Terrain.activeTerrains)
            {
                try { FillOn(terrain, path, minX, minZ, maxX, maxZ); }
                catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"Snow embankment on {terrain.name} failed: {exception}"); }
            }
        }

        static void FillOn(Terrain terrain, CurvePath path, float minX, float minZ, float maxX, float maxZ)
        {
            var terrainData = terrain.terrainData;
            var terrainOrigin = terrain.transform.position;
            var size = terrainData.size;
            int resolution = terrainData.heightmapResolution;
            float cellSizeX = size.x / (resolution - 1), cellSizeZ = size.z / (resolution - 1);
            int startX = Mathf.Clamp(Mathf.FloorToInt((minX - terrainOrigin.x) / cellSizeX), 0, resolution - 1);
            int endX = Mathf.Clamp(Mathf.CeilToInt((maxX - terrainOrigin.x) / cellSizeX), 0, resolution - 1);
            int startZ = Mathf.Clamp(Mathf.FloorToInt((minZ - terrainOrigin.z) / cellSizeZ), 0, resolution - 1);
            int endZ = Mathf.Clamp(Mathf.CeilToInt((maxZ - terrainOrigin.z) / cellSizeZ), 0, resolution - 1);
            int width = endX - startX + 1, height = endZ - startZ + 1;
            if (width <= 1 || height <= 1) return;

            var best = new float[width * height];
            var railY = new float[width * height];
            for (int index = 0; index < best.Length; index++) best[index] = float.MaxValue;
            const int SegmentStride = 4;
            for (int index = 0; index < path.Count - 1; index += SegmentStride)
            {
                var start = path[index];
                var end = path[Mathf.Min(index + SegmentStride, path.Count - 1)];
                int firstColumn = Mathf.Max(startX, Mathf.FloorToInt((Mathf.Min(start.x, end.x) - FillReachInMeters - terrainOrigin.x) / cellSizeX));
                int lastColumn = Mathf.Min(endX, Mathf.CeilToInt((Mathf.Max(start.x, end.x) + FillReachInMeters - terrainOrigin.x) / cellSizeX));
                int firstRow = Mathf.Max(startZ, Mathf.FloorToInt((Mathf.Min(start.z, end.z) - FillReachInMeters - terrainOrigin.z) / cellSizeZ));
                int lastRow = Mathf.Min(endZ, Mathf.CeilToInt((Mathf.Max(start.z, end.z) + FillReachInMeters - terrainOrigin.z) / cellSizeZ));
                var segment = new Vector2(end.x - start.x, end.z - start.z);
                float segmentLengthSquared = Mathf.Max(segment.sqrMagnitude, MinimumSegmentLengthSquared);
                for (int rowIndex = firstRow; rowIndex <= lastRow; rowIndex++)
                for (int columnIndex = firstColumn; columnIndex <= lastColumn; columnIndex++)
                {
                    var sampleOffset = new Vector2(terrainOrigin.x + columnIndex * cellSizeX - start.x, terrainOrigin.z + rowIndex * cellSizeZ - start.z);
                    float fraction = Mathf.Clamp01(Vector2.Dot(sampleOffset, segment) / segmentLengthSquared);
                    float distance = (sampleOffset - segment * fraction).magnitude;
                    int sampleIndex = (rowIndex - startZ) * width + (columnIndex - startX);
                    if (distance < best[sampleIndex]) { best[sampleIndex] = distance; railY[sampleIndex] = Mathf.Lerp(start.y, end.y, fraction); }
                }
            }

            var heights = HeightmapAccess.Read(terrainData, startX, startZ, width, height);

            if (heights.Length < width * height) return;
            int changed = 0;
            for (int index = 0; index < width * height; index++)
            {
                float distance = best[index];
                if (distance >= FillReachInMeters) continue;
                float current = terrainOrigin.y + heights[index] * size.y;
                float bed = railY[index] - RailPieces.RailTop + BedAboveRailBaseInMeters;
                if (bed - current > MaximumFillInMeters || current >= bed) continue;
                float wantedHeight = distance <= EmbankmentTopHalfWidthInMeters ? bed : bed - (distance - EmbankmentTopHalfWidthInMeters) / EmbankmentSlopeRatio;
                if (wantedHeight <= current) continue;
                heights[index] = (wantedHeight - terrainOrigin.y) / size.y;
                changed++;
            }
            if (changed == 0) return;
            HeightmapAccess.Write(terrainData, startX, startZ, heights);
            SteamRailwayMod.Debug($"Snow embankment on {terrain.name}: {changed} height samples.");
        }

        static void LowerOn(Terrain terrain, CurvePath path, float minX, float minZ, float maxX, float maxZ)
        {
            var terrainData = terrain.terrainData;
            var terrainOrigin = terrain.transform.position;
            var size = terrainData.size;
            int resolution = terrainData.heightmapResolution;
            float cellSizeX = size.x / (resolution - 1), cellSizeZ = size.z / (resolution - 1);
            int startX = Mathf.Clamp(Mathf.FloorToInt((minX - terrainOrigin.x) / cellSizeX), 0, resolution - 1);
            int endX = Mathf.Clamp(Mathf.CeilToInt((maxX - terrainOrigin.x) / cellSizeX), 0, resolution - 1);
            int startZ = Mathf.Clamp(Mathf.FloorToInt((minZ - terrainOrigin.z) / cellSizeZ), 0, resolution - 1);
            int endZ = Mathf.Clamp(Mathf.CeilToInt((maxZ - terrainOrigin.z) / cellSizeZ), 0, resolution - 1);
            int width = endX - startX + 1, height = endZ - startZ + 1;
            if (width <= 1 || height <= 1) return;

            // nearest track segment for every height sample in the box
            var best = new float[width * height];
            var railY = new float[width * height];
            for (int index = 0; index < best.Length; index++) best[index] = float.MaxValue;
            const int SegmentStride = 4;   // 2 m segments
            for (int index = 0; index < path.Count - 1; index += SegmentStride)
            {
                var start = path[index];
                var end = path[Mathf.Min(index + SegmentStride, path.Count - 1)];
                int firstColumn = Mathf.Max(startX, Mathf.FloorToInt((Mathf.Min(start.x, end.x) - BlendEndDistanceInMeters - terrainOrigin.x) / cellSizeX));
                int lastColumn = Mathf.Min(endX, Mathf.CeilToInt((Mathf.Max(start.x, end.x) + BlendEndDistanceInMeters - terrainOrigin.x) / cellSizeX));
                int firstRow = Mathf.Max(startZ, Mathf.FloorToInt((Mathf.Min(start.z, end.z) - BlendEndDistanceInMeters - terrainOrigin.z) / cellSizeZ));
                int lastRow = Mathf.Min(endZ, Mathf.CeilToInt((Mathf.Max(start.z, end.z) + BlendEndDistanceInMeters - terrainOrigin.z) / cellSizeZ));
                var segment = new Vector2(end.x - start.x, end.z - start.z);
                float segmentLengthSquared = Mathf.Max(segment.sqrMagnitude, MinimumSegmentLengthSquared);
                for (int rowIndex = firstRow; rowIndex <= lastRow; rowIndex++)
                for (int columnIndex = firstColumn; columnIndex <= lastColumn; columnIndex++)
                {
                    var sampleOffset = new Vector2(terrainOrigin.x + columnIndex * cellSizeX - start.x, terrainOrigin.z + rowIndex * cellSizeZ - start.z);
                    float fraction = Mathf.Clamp01(Vector2.Dot(sampleOffset, segment) / segmentLengthSquared);
                    float distance = (sampleOffset - segment * fraction).magnitude;
                    int sampleIndex = (rowIndex - startZ) * width + (columnIndex - startX);
                    if (distance < best[sampleIndex]) { best[sampleIndex] = distance; railY[sampleIndex] = Mathf.Lerp(start.y, end.y, fraction); }
                }
            }

            var heights = HeightmapAccess.Read(terrainData, startX, startZ, width, height);

            if (heights.Length < width * height) return;
            int changed = 0;
            for (int index = 0; index < width * height; index++)
            {
                float distance = best[index];
                if (distance >= BlendEndDistanceInMeters) continue;
                float current = terrainOrigin.y + heights[index] * size.y;
                float target = railY[index] - RailPieces.RailTop + BedAboveRailBaseInMeters;
                if (current <= target || current - target > MaximumCutInMeters) continue;
                float scale = distance <= FullyLoweredDistanceInMeters ? 1f : Mathf.SmoothStep(1f, 0f, (distance - FullyLoweredDistanceInMeters) / (BlendEndDistanceInMeters - FullyLoweredDistanceInMeters));
                float loweredHeight = Mathf.Lerp(current, target, scale);
                heights[index] = (loweredHeight - terrainOrigin.y) / size.y;
                changed++;
            }
            if (changed == 0) return;
            HeightmapAccess.Write(terrainData, startX, startZ, heights);
            SteamRailwayMod.Debug($"Snow lowered along the track on {terrain.name}: {changed} height samples.");
        }
    }
}
