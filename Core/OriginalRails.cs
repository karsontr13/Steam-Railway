using System.Collections.Generic;
using System.Text.Json.Serialization;
using UnityEngine;

namespace SteamRailway
{
    /// One railway line found in a region: control points along the smoothed rail-top centre and the stretches
    /// where an original piece carrying a structure (a trestle bridge) stays; elsewhere new rail replaces the pieces.
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
        [JsonPropertyName("Pos")] public float[] Position { get; set; }
        public bool Keep { get; set; }
    }

    /// A region's lines as written to Mods\SteamRailway\Lines\<region>.json; Locked keeps hand edits when the
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

    /// Reads the game's own track pieces in the loaded region and joins them into lines; each centreline comes from
    /// the piece's own mesh (the rail heads are the highest vertices: two parallel lines or two concentric arcs).
    internal static class OriginalRails
    {
        const float RailHeadHeightToleranceInMeters = 0.03f;
        static readonly float[] SearchRadiiInMeters = { 300f, 12f, 1.2f };
        static readonly float[] SearchStepsInMeters = { 6f, 0.4f, 0.03f };
        const int MinimumRailHeadPointCount = 8;
        const float MaximumStraightFitError = 0.003f;
        const float MinimumRailSeparationInMeters = 1.2f;
        const int SearchPassCount = 3;
        const float CoarseMinimumRingGapInMeters = 0.8f;
        const float FineMinimumRingGapInMeters = 1.3f;
        const float CoarseMaximumRingGapInMeters = 3f;
        const float FineMaximumRingGapInMeters = 2.1f;
        const float BendPointSpacingInMeters = 2f;
        const float BuiltInStructureMinimumY = -1.0f;
        const int MinimumClusterValueCount = 4;
        const int MinimumClusterSize = 2;
        const float SamePieceToleranceInMeters = 0.5f;
        const float LevelToleranceInMeters = 3f;
        const float LevelToleranceGrowthPerMeter = 0.1f;
        const float MinimumHeadOnDot = 0.5f;
        const float LateralToleranceInMeters = 3f;
        const float LateralToleranceGrowthPerMeter = 1.2f;
        const float LateralScoreWeight = 2f;
        const float MinimumSegmentLengthSquared = 1e-6f;
        const float OverlapMarginInMeters = 1f;
        const float GapCurveStepInMeters = 6f;
        const int ControlPointStride = 2;
        const float ResampleEndToleranceInMeters = 0.5f;
        const float TaubinShrinkFactor = 0.5f;
        const float TaubinInflateFactor = -0.53f;
        const int KeepEndOffset = 3;
        const float PieceMatchToleranceInSquareMeters = 0.01f;
        const float MinimumSegmentLengthInMeters = 1e-4f;
        const float MinimumPointSpacingInMeters = 1f;
        const float StrayPieceReachInMeters = 2.5f;
        const float StrayPieceBelowInMeters = 1.5f;
        const float MinimumPathLengthInMeters = 0.01f;
        const float ObstacleProbeStepInMeters = 1.5f;
        const float ProbeBoxCenterHeightInMeters = 2.1f;
        const float ProbeBoxHalfWidthInMeters = 1.35f;
        const float ProbeBoxHalfHeightInMeters = 1.95f;
        const float ProbeBoxHalfLengthInMeters = 0.8f;
        const float MaximumClearableWidthInMeters = 25f;
        const float MaximumClearableHeightInMeters = 20f;
        const int Vector3ComponentCount = 3;
        const float MarkedTreeMatchDistanceInMeters = 0.6f;
        const float DefaultMaximumObjectSizeInMeters = 18f;
        const float MaximumObjectHeightInMeters = 12f;
        const float CorridorSampleSpacingInMeters = 2f;
        const int MaximumCorridorSamples = 24;
        const float CorridorMarginInMeters = 2f;
        const float CorridorBelowRailInMeters = 0.25f;
        const float CorridorHeightAboveRailInMeters = 4.5f;
        const int RoundingDecimalPlaces = 3;
        const float MaximumGapInMeters = 80f;          // farthest two pieces may be apart and still belong to one line
        const float MaximumOverlapInMeters = 30f;      // the artists overlap neighbouring pieces by up to ~15 m
        const float PointSpacingInMeters = 8f;
        const float MinimumLineLengthInMeters = 30f;     // one original piece is enough (a short siding behind a switch)
        const float BendPenalty = 25f;     // at a switch the straight road wins over the bend into the siding

        /// Centreline of one piece type in its own space, start to end, at rail-head height.
        sealed class Shape
        {
            public Vector3[] Points;
            public bool Bend;
            public bool Keep;              // carries a structure (bridge) besides the rails: never replaced
        }

        sealed class Piece
        {
            public int InstanceId;                 // the piece's transform
            public Transform PieceTransform;
            public Vector3[] WorldPoints;          // world centreline, A = Pts[0], B = Pts[^1]
            public bool Bend;
            public bool Keep;
            public readonly List<Transform> Duplicates = new List<Transform>();   // the same piece placed again
            public Vector3 StartPoint => WorldPoints[0];
            public Vector3 EndPoint => WorldPoints[WorldPoints.Length - 1];
            public Vector3 StartOutwardDirection => Flat(WorldPoints[0] - WorldPoints[1]);
            public Vector3 EndOutwardDirection => Flat(WorldPoints[WorldPoints.Length - 1] - WorldPoints[WorldPoints.Length - 2]);
        }

        static readonly Dictionary<int, Shape> s_Shapes = new Dictionary<int, Shape>();   // by mesh instance (names repeat)
        static readonly HashSet<int> s_UsedPieces = new HashSet<int>();                    // piece transforms on a line

        static Vector3 Flat(Vector3 vector) { vector.y = 0f; return vector.normalized; }

        /// Track pieces the train can run on (broken rails, buffer stops and groups are scenery).
        static bool IsTrackPiece(string name) =>
            name.StartsWith("OBJ_RailTrack") && name.Contains("Prefab") &&
            !name.Contains("Broken") && !name.Contains("Stop") && !name.Contains("Group");

        public static List<RailLine> Find()
        {
            var pieces = CollectPieces();
            var lines = new List<RailLine>();
            s_UsedPieces.Clear();
            if (pieces.Count == 0) return lines;

            int[] linkedEnd = LinkPieceEnds(pieces);
            WalkChains(pieces, linkedEnd, lines);
            lines.Sort((first, second) => second.Length.CompareTo(first.Length));
            SteamRailwayMod.Debug($"Original rails: {pieces.Count} pieces -> {lines.Count} line(s).");
            return lines;
        }

        /// Every active track piece in the scene with its centreline in world space; a piece placed twice on the
        /// same spot is kept once (it would split the line) and remembered as a duplicate.
        static List<Piece> CollectPieces()
        {
            var pieces = new List<Piece>();
            foreach (var pieceTransform in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (pieceTransform.gameObject.scene.name == null || !pieceTransform.gameObject.activeInHierarchy || !IsTrackPiece(pieceTransform.name)) continue;
                var shape = ShapeOf(pieceTransform, out var space);
                if (shape == null) continue;
                var worldPoints = new Vector3[shape.Points.Length];
                for (int index = 0; index < worldPoints.Length; index++) worldPoints[index] = space.TransformPoint(shape.Points[index]);
                var piece = new Piece { WorldPoints = worldPoints, Bend = shape.Bend, Keep = shape.Keep, InstanceId = pieceTransform.GetInstanceID(), PieceTransform = pieceTransform };
                var twin = pieces.Find(other => Same(other, piece));
                if (twin != null) { twin.Duplicates.Add(pieceTransform); continue; }
                pieces.Add(piece);
            }
            return pieces;
        }

        /// Pairs up piece ends that continue into each other. An end is numbered piece * 2 + side (side 0 = A, 1 = B);
        /// the result holds, per end, the end it is joined to or -1.
        static int[] LinkPieceEnds(List<Piece> pieces)
        {
            int endCount = pieces.Count * 2;
            var linkedEnd = new int[endCount];
            for (int index = 0; index < endCount; index++) linkedEnd[index] = -1;

            var candidates = new List<(float score, int firstEnd, int secondEnd)>();
            for (int firstEnd = 0; firstEnd < endCount; firstEnd++)
            for (int secondEnd = firstEnd + 1; secondEnd < endCount; secondEnd++)
            {
                if (firstEnd / 2 == secondEnd / 2) continue;
                if (Match(pieces, firstEnd, secondEnd, out float forwardScore) && Match(pieces, secondEnd, firstEnd, out float backwardScore))
                    candidates.Add((forwardScore + backwardScore, firstEnd, secondEnd));
            }
            candidates.Sort((first, second) => first.score.CompareTo(second.score));
            foreach (var candidate in candidates)
                if (linkedEnd[candidate.firstEnd] < 0 && linkedEnd[candidate.secondEnd] < 0)
                {
                    linkedEnd[candidate.firstEnd] = candidate.secondEnd;
                    linkedEnd[candidate.secondEnd] = candidate.firstEnd;
                }
            return linkedEnd;
        }

        /// Follows the joined ends from a loose end of each piece (second pass: from anywhere, for closed loops)
        /// and turns every chain long enough into a line.
        static void WalkChains(List<Piece> pieces, int[] linkedEnd, List<RailLine> lines)
        {
            var visited = new bool[pieces.Count];
            for (int pass = 0; pass < 2; pass++)
            for (int startPiece = 0; startPiece < pieces.Count; startPiece++)
            {
                if (visited[startPiece]) continue;
                int entrySide;
                if (linkedEnd[startPiece * 2] < 0) entrySide = 0;
                else if (linkedEnd[startPiece * 2 + 1] < 0) entrySide = 1;
                else if (pass == 1) entrySide = 0;
                else continue;

                var chain = new List<(Piece piece, bool reversed)>();
                int currentPiece = startPiece, side = entrySide;
                while (currentPiece >= 0 && !visited[currentPiece])
                {
                    visited[currentPiece] = true;
                    chain.Add((pieces[currentPiece], side == 1));
                    int nextEnd = linkedEnd[currentPiece * 2 + (1 - side)];
                    if (nextEnd < 0) break;
                    currentPiece = nextEnd / 2;
                    side = nextEnd % 2;
                }

                var line = Build(chain);
                if (line != null && line.Length >= MinimumLineLengthInMeters)
                {
                    lines.Add(line);
                    RecordLine(line, chain);
                }
            }
        }

        /// Notes which original pieces the line runs over, and which of them its new rail replaces.
        static void RecordLine(RailLine line, List<(Piece piece, bool reversed)> chain)
        {
            foreach (var link in chain)
            {
                var piece = link.piece;
                s_UsedPieces.Add(piece.InstanceId);
                line.Used.Add(ToPieceRef(piece.PieceTransform, piece.Keep));
                if (!piece.Keep) line.Replaced.Add(piece.PieceTransform);
                foreach (var duplicate in piece.Duplicates)
                {
                    s_UsedPieces.Add(duplicate.GetInstanceID());
                    line.Used.Add(ToPieceRef(duplicate, piece.Keep));
                    if (!piece.Keep) line.Replaced.Add(duplicate);
                }
            }
        }

        // ------------------------------------------------------------------ piece shapes

        /// The piece's centreline and the transform it is expressed in.
        static Shape ShapeOf(Transform piece, out Transform space)
        {
            space = piece;
            // Straight pieces whose visuals the game draws itself (the prefab only carries a collider).
            var meshFilter = FindMeshFilter(piece);
            if (meshFilter == null)
            {
                if (!piece.name.Contains("Strait") && !piece.name.Contains("Straight")) return null;
                return StraightPieceShape();
            }
            space = meshFilter.transform;
            var mesh = meshFilter.sharedMesh;
            if (s_Shapes.TryGetValue(mesh.GetInstanceID(), out var cached)) return cached;
            Shape shape = null;
            try { shape = Fit(mesh); }
            catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"Rail piece {mesh.name}: {exception.Message}"); }
            // geometry well below the rails = a bridge or trestle built into the piece
            if (shape != null) shape.Keep = mesh.bounds.min.y < BuiltInStructureMinimumY;
            s_Shapes[mesh.GetInstanceID()] = shape;
            if (shape != null)
                SteamRailwayMod.Debug($"Rail piece {mesh.name}: {(shape.Bend ? "bend" : "straight")}, {shape.Points.Length} points, {Length(shape.Points):F1} m.");
            return shape;
        }

        static Shape s_StraightPieceShape;
        static Shape StraightPieceShape()
        {
            if (s_StraightPieceShape != null) return s_StraightPieceShape;
            var start = new Vector3(RailPieces.CentreX, RailPieces.RailTop, RailPieces.SourceStartZ);
            var end = new Vector3(RailPieces.CentreX, RailPieces.RailTop, RailPieces.SourceEndZ);
            return s_StraightPieceShape = new Shape { Points = new[] { start, end }, Bend = false };
        }

        /// The most detailed readable mesh of the piece (LOD0).
        static MeshFilter FindMeshFilter(Transform piece)
        {
            MeshFilter best = null;
            foreach (var meshFilter in piece.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = meshFilter.sharedMesh;
                if (mesh == null || !mesh.isReadable) continue;
                if (best == null || mesh.vertexCount > best.sharedMesh.vertexCount) best = meshFilter;
            }
            return best;
        }

        /// Rail heads = the highest vertices. Two parallel lines -> straight; two concentric arcs -> bend.
        static Shape Fit(Mesh mesh)
        {
            var vertices = mesh.vertices;
            float highestY = float.MinValue;
            for (int index = 0; index < vertices.Length; index++) highestY = Mathf.Max(highestY, vertices[index].y);
            var top = new List<Vector2>();
            for (int index = 0; index < vertices.Length; index++)
                if (vertices[index].y > highestY - RailHeadHeightToleranceInMeters) top.Add(new Vector2(vertices[index].x, vertices[index].z));
            if (top.Count < MinimumRailHeadPointCount) return null;
            float railHeadY = highestY;

            // straight? principal axis of the rail-head points
            Vector2 mean = Vector2.zero;
            foreach (var planePoint in top) mean += planePoint;
            mean /= top.Count;
            float covarianceXX = 0, covarianceXZ = 0, covarianceZZ = 0;
            foreach (var planePoint in top) { var planeDirection = planePoint - mean; covarianceXX += planeDirection.x * planeDirection.x; covarianceXZ += planeDirection.x * planeDirection.y; covarianceZZ += planeDirection.y * planeDirection.y; }
            float angle = 0.5f * Mathf.Atan2(2f * covarianceXZ, covarianceXX - covarianceZZ);
            var axis = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            var side = new Vector2(-axis.y, axis.x);
            var lateralOffsets = new List<float>();
            float axisMinimum = float.MaxValue, axisMaximum = float.MinValue;
            foreach (var planePoint in top)
            {
                var planeDirection = planePoint - mean;
                lateralOffsets.Add(Vector2.Dot(planeDirection, side));
                float axisPosition = Vector2.Dot(planeDirection, axis);
                axisMinimum = Mathf.Min(axisMinimum, axisPosition); axisMaximum = Mathf.Max(axisMaximum, axisPosition);
            }
            if (TwoClusters(lateralOffsets, out float firstRailOffset, out float secondRailOffset, out float fitError) && fitError < MaximumStraightFitError && Mathf.Abs(secondRailOffset - firstRailOffset) > MinimumRailSeparationInMeters)
            {
                var planeCenter = mean + side * ((firstRailOffset + secondRailOffset) * 0.5f);
                var lineStart = planeCenter + axis * axisMinimum; var lineEnd = planeCenter + axis * axisMaximum;
                return new Shape { Points = new[] { new Vector3(lineStart.x, railHeadY, lineStart.y), new Vector3(lineEnd.x, railHeadY, lineEnd.y) }, Bend = false };
            }

            // bend: centre where the rail heads fall on two circles 1.5 - 1.9 m apart
            Vector2 centre = Vector2.zero;
            float best = float.MaxValue;
            // the centre may lie far from the piece (trestle pieces are placed around it): coarse to fine search
            var results = new List<float>(top.Count);
            for (int pass = 0; pass < SearchPassCount; pass++)
            {
                var searchOrigin = pass == 0 ? mean : centre;
                float searchRadius = SearchRadiiInMeters[pass], searchStep = SearchStepsInMeters[pass];
                float minimumRingGap = pass == 0 ? CoarseMinimumRingGapInMeters : FineMinimumRingGapInMeters, maximumRingGap = pass == 0 ? CoarseMaximumRingGapInMeters : FineMaximumRingGapInMeters;
                best = float.MaxValue;
                for (float offsetX = -searchRadius; offsetX <= searchRadius; offsetX += searchStep)
                for (float offsetZ = -searchRadius; offsetZ <= searchRadius; offsetZ += searchStep)
                {
                    var candidateCentre = searchOrigin + new Vector2(offsetX, offsetZ);
                    results.Clear();
                    foreach (var planePoint in top) results.Add((planePoint - candidateCentre).magnitude);
                    if (!TwoClusters(results, out float firstRadius, out float secondRadius, out float candidateError)) continue;
                    float ringGap = Mathf.Abs(secondRadius - firstRadius);
                    if (ringGap < minimumRingGap || ringGap > maximumRingGap) continue;
                    if (candidateError < best) { best = candidateError; centre = candidateCentre; }
                }
                if (best == float.MaxValue) return null;
            }
            if (best == float.MaxValue) return null;
            var radii = new List<float>();
            foreach (var planePoint in top) radii.Add((planePoint - centre).magnitude);
            TwoClusters(radii, out float innerRadius, out float outerRadius, out _);
            float centerRadius = (innerRadius + outerRadius) * 0.5f;
            // angle range around the mean direction (no wrap-around)
            var meanDirection = (mean - centre).normalized;
            float baseAngle = Mathf.Atan2(meanDirection.y, meanDirection.x);
            float minimumAngle = float.MaxValue, maximumAngle = float.MinValue;
            foreach (var planePoint in top)
            {
                var planeDirection = planePoint - centre;
                float angleOffset = Mathf.DeltaAngle(baseAngle * Mathf.Rad2Deg, Mathf.Atan2(planeDirection.y, planeDirection.x) * Mathf.Rad2Deg) * Mathf.Deg2Rad;
                minimumAngle = Mathf.Min(minimumAngle, angleOffset); maximumAngle = Mathf.Max(maximumAngle, angleOffset);
            }
            float arc = (maximumAngle - minimumAngle) * centerRadius;
            int count = Mathf.Max(2, Mathf.CeilToInt(arc / BendPointSpacingInMeters) + 1);
            var points = new Vector3[count];
            for (int index = 0; index < count; index++)
            {
                float pointAngle = baseAngle + Mathf.Lerp(minimumAngle, maximumAngle, index / (float)(count - 1));
                points[index] = new Vector3(centre.x + Mathf.Cos(pointAngle) * centerRadius, railHeadY, centre.y + Mathf.Sin(pointAngle) * centerRadius);
            }
            return new Shape { Points = points, Bend = true };
        }

        /// Splits sorted values at the largest gap; error = mean squared distance to the two cluster means.
        static bool TwoClusters(List<float> values, out float firstMean, out float secondMean, out float meanSquaredError)
        {
            firstMean = secondMean = meanSquaredError = 0f;
            if (values.Count < MinimumClusterValueCount) return false;
            var sortedValues = new List<float>(values);
            sortedValues.Sort();
            int split = 1; float gap = 0f;
            for (int index = 1; index < sortedValues.Count; index++) if (sortedValues[index] - sortedValues[index - 1] > gap) { gap = sortedValues[index] - sortedValues[index - 1]; split = index; }
            if (split < MinimumClusterSize || sortedValues.Count - split < MinimumClusterSize) return false;
            for (int index = 0; index < split; index++) firstMean += sortedValues[index];
            for (int index = split; index < sortedValues.Count; index++) secondMean += sortedValues[index];
            firstMean /= split; secondMean /= sortedValues.Count - split;
            for (int index = 0; index < split; index++) meanSquaredError += (sortedValues[index] - firstMean) * (sortedValues[index] - firstMean);
            for (int index = split; index < sortedValues.Count; index++) meanSquaredError += (sortedValues[index] - secondMean) * (sortedValues[index] - secondMean);
            meanSquaredError /= sortedValues.Count;
            return true;
        }

        static float Length(Vector3[] points)
        {
            float totalLength = 0f;
            for (int index = 1; index < points.Length; index++) totalLength += Vector3.Distance(points[index - 1], points[index]);
            return totalLength;
        }

        // ------------------------------------------------------------------ joining

        static bool Same(Piece firstPiece, Piece secondPiece) =>
            (Vector3.Distance(firstPiece.StartPoint, secondPiece.StartPoint) < SamePieceToleranceInMeters && Vector3.Distance(firstPiece.EndPoint, secondPiece.EndPoint) < SamePieceToleranceInMeters) ||
            (Vector3.Distance(firstPiece.StartPoint, secondPiece.EndPoint) < SamePieceToleranceInMeters && Vector3.Distance(firstPiece.EndPoint, secondPiece.StartPoint) < SamePieceToleranceInMeters);

        static Vector3 EndPosition(List<Piece> pieces, int end) => end % 2 == 0 ? pieces[end / 2].StartPoint : pieces[end / 2].EndPoint;
        static Vector3 Outward(List<Piece> pieces, int end) => end % 2 == 0 ? pieces[end / 2].StartOutwardDirection : pieces[end / 2].EndOutwardDirection;

        /// Can the first end continue into the second? (lower score = better)
        static bool Match(List<Piece> pieces, int firstEnd, int secondEnd, out float score)
        {
            score = 0f;
            var firstPosition = EndPosition(pieces, firstEnd);
            var secondPosition = EndPosition(pieces, secondEnd);
            var gap = secondPosition - firstPosition;
            gap.y = 0f;
            float distance = gap.magnitude;
            if (distance > MaximumGapInMeters) return false;
            if (Mathf.Abs(secondPosition.y - firstPosition.y) > LevelToleranceInMeters + LevelToleranceGrowthPerMeter * distance) return false;   // not a line at another level
            var firstOutward = Outward(pieces, firstEnd);
            var secondOutward = Outward(pieces, secondEnd);
            if (Vector3.Dot(firstOutward, -secondOutward) < MinimumHeadOnDot) return false;         // the next piece must head on (within ~60°)
            float along = Vector3.Dot(gap, firstOutward);
            if (along < -MaximumOverlapInMeters) return false;
            // overlapping pieces: how far the second end lies from the first piece's own centreline (a bend's end tangent overshoots)
            float lateral = along < 0f ? DistanceToPiece(pieces[firstEnd / 2], secondPosition) : (gap - firstOutward * along).magnitude;
            if (lateral > LateralToleranceInMeters + LateralToleranceGrowthPerMeter * Mathf.Max(along, 0f)) return false;   // bends drift sideways
            score = distance + LateralScoreWeight * lateral + (pieces[secondEnd / 2].Bend ? BendPenalty : 0f);
            return true;
        }

        static float DistanceToPiece(Piece piece, Vector3 queryPoint)
        {
            float best = float.MaxValue;
            var queryPlanePoint = new Vector2(queryPoint.x, queryPoint.z);
            for (int index = 0; index < piece.WorldPoints.Length - 1; index++)
            {
                var segmentStart = new Vector2(piece.WorldPoints[index].x, piece.WorldPoints[index].z);
                var segment = new Vector2(piece.WorldPoints[index + 1].x, piece.WorldPoints[index + 1].z) - segmentStart;
                float fraction = Mathf.Clamp01(Vector2.Dot(queryPlanePoint - segmentStart, segment) / Mathf.Max(segment.sqrMagnitude, MinimumSegmentLengthSquared));
                best = Mathf.Min(best, (queryPlanePoint - segmentStart - segment * fraction).magnitude);
            }
            return best;
        }

        static RailLine Build(List<(Piece piece, bool reversed)> chain)
        {
            if (chain.Count == 0) return null;
            var rawPoints = new List<Vector3>();
            var pinned = new List<bool>();          // points on pieces that stay (bridges): not moved by smoothing
            float skipMeters = 0f;   // metres of the next piece already covered by the previous one (overlap)
            for (int index = 0; index < chain.Count; index++)
            {
                var (piece, reversed) = chain[index];
                var centerline = Oriented(piece, reversed);
                AddPolyline(rawPoints, pinned, centerline, skipMeters, piece.Keep);
                skipMeters = 0f;
                if (index == chain.Count - 1) break;

                var exitPoint = centerline[centerline.Length - 1];
                var exitDirection = Flat(exitPoint - centerline[centerline.Length - 2]);
                var (nextPiece, nextReversed) = chain[index + 1];
                var nextCenterline = Oriented(nextPiece, nextReversed);
                var nextEntry = nextCenterline[0];
                var nextDirection = Flat(nextCenterline[1] - nextCenterline[0]);
                float along = Vector3.Dot(nextEntry - exitPoint, exitDirection);
                if (along < OverlapMarginInMeters)
                {
                    // overlapping pieces: carry on along the next piece from where this one ends
                    skipMeters = Mathf.Max(0f, Vector3.Dot(exitPoint - nextEntry, nextDirection)) + OverlapMarginInMeters;
                    continue;
                }
                // gap between two pieces: smooth Hermite curve
                float gapLength = Vector3.Distance(exitPoint, nextEntry);
                int steps = Mathf.Max(1, Mathf.CeilToInt(gapLength / GapCurveStepInMeters));
                for (int innerIndex = 1; innerIndex < steps; innerIndex++) { rawPoints.Add(Hermite(exitPoint, exitDirection * gapLength, nextEntry, nextDirection * gapLength, innerIndex / (float)steps)); pinned.Add(false); }
            }
            if (rawPoints.Count < 2) return null;

            // Smooth: the pieces' ends do not quite meet (steps of a few cm to tens of cm), which jolts the train
            // and shows as kinks. Resample finely, then Taubin smoothing (no shrinking of the real curves).
            Resample(rawPoints, pinned, SmoothStepInMeters, out var points, out var pinnedSamples);
            Taubin(points, pinnedSamples, SmoothIterations);

            var line = new RailLine();
            // control points every 2nd sample (4 m), always the ends
            for (int index = 0; index < points.Count; index += ControlPointStride) line.Points.Add(points[index]);
            if ((points.Count - 1) % ControlPointStride != 0) line.Points.Add(points[points.Count - 1]);
            // the stretches on kept pieces (runs of pinned samples)
            for (int index = 0; index < points.Count; index++)
            {
                if (!pinnedSamples[index]) continue;
                int innerIndex = index;
                while (innerIndex + 1 < points.Count && pinnedSamples[innerIndex + 1]) innerIndex++;
                if (innerIndex > index) line.Keep.Add(new KeyValuePair<Vector3, Vector3>(points[index], points[innerIndex]));
                index = innerIndex;
            }
            for (int index = 1; index < line.Points.Count; index++) line.Length += Vector3.Distance(line.Points[index - 1], line.Points[index]);
            return line;
        }

        const float SmoothStepInMeters = 2f;
        const int SmoothIterations = 40;

        static void Resample(List<Vector3> sourcePoints, List<bool> pinFlags, float step, out List<Vector3> resampledPoints, out List<bool> pinnedSamples)
        {
            resampledPoints = new List<Vector3>();
            pinnedSamples = new List<bool>();
            resampledPoints.Add(sourcePoints[0]); pinnedSamples.Add(true);                      // the line's ends stay where they are
            float carry = 0f;
            for (int index = 0; index < sourcePoints.Count - 1; index++)
            {
                var start = sourcePoints[index]; var end = sourcePoints[index + 1];
                float segmentLength = Vector3.Distance(start, end);
                float fraction = step - carry;
                while (fraction <= segmentLength)
                {
                    resampledPoints.Add(Vector3.Lerp(start, end, fraction / segmentLength));
                    pinnedSamples.Add(pinFlags[index] && pinFlags[index + 1]);
                    fraction += step;
                }
                carry = segmentLength - (fraction - step);
            }
            if (Vector3.Distance(resampledPoints[resampledPoints.Count - 1], sourcePoints[sourcePoints.Count - 1]) > ResampleEndToleranceInMeters) { resampledPoints.Add(sourcePoints[sourcePoints.Count - 1]); pinnedSamples.Add(true); }
            else { resampledPoints[resampledPoints.Count - 1] = sourcePoints[sourcePoints.Count - 1]; pinnedSamples[pinnedSamples.Count - 1] = true; }
        }

        /// Taubin smoothing (shrink, then inflate): removes the small steps and kinks, keeps the shape of the bends.
        static void Taubin(List<Vector3> points, List<bool> pinnedSamples, int iterations)
        {
            var buffer = new Vector3[points.Count];
            for (int iteration = 0; iteration < iterations; iteration++)
            {
                for (int pass = 0; pass < 2; pass++)
                {
                    float smoothingFactor = pass == 0 ? TaubinShrinkFactor : TaubinInflateFactor;
                    for (int index = 0; index < points.Count; index++)
                    {
                        if (index == 0 || index == points.Count - 1 || pinnedSamples[index]) { buffer[index] = points[index]; continue; }
                        buffer[index] = points[index] + smoothingFactor * ((points[index - 1] + points[index + 1]) * 0.5f - points[index]);
                    }
                    for (int index = 0; index < points.Count; index++) points[index] = buffer[index];
                }
            }
        }

        /// Hides the original pieces the line's new rail replaces (their rails no longer meet the smoothed line).
        /// Pieces the game draws itself (no renderers) are switched off.
        public static void HideReplaced(RailLine line, ObstacleData onlyWithin = null)
        {
            foreach (var transform in line.Replaced)
            {
                if (transform == null) continue;
                if (onlyWithin != null && !Obstacles.Contains(onlyWithin, transform.position.x, transform.position.z)) continue;
                // hard mode: the original pieces at an unrepaired broken bridge stay as the region has them
                if (Obstacles.IsInUnrepairedBridge(transform.position.x, transform.position.z)) continue;
                bool any = false;
                foreach (var renderer in transform.GetComponentsInChildren<Renderer>(true)) { renderer.enabled = false; any = true; }
                var lodGroup = transform.GetComponent<LODGroup>();
                if (lodGroup != null) lodGroup.enabled = false;
                if (!any)
                {
                    // drawn by the game's instancing: the picture is hidden there, the collider here
                    InstancedHider.Add(transform);
                    transform.gameObject.SetActive(false);
                }
            }
        }

        // ------------------------------------------------------------------ saved lines

        public const int LineVersion = 2;
        static readonly System.Text.Json.JsonSerializerOptions s_Json = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        static string LinePath(string scene) => System.IO.Path.Combine(RouteStore.FolderPath, "Lines", scene + ".json");
        static PieceRef ToPieceRef(Transform transform, bool keep) => new PieceRef { Name = transform.name, Position = RoundedComponents(transform.position), Keep = keep };
        static float[] RoundedComponents(Vector3 vector) => new[] { (float)System.Math.Round(vector.x, RoundingDecimalPlaces), (float)System.Math.Round(vector.y, RoundingDecimalPlaces), (float)System.Math.Round(vector.z, RoundingDecimalPlaces) };
        static Vector3 ToVector3(float[] components, int offset = 0) => new Vector3(components[offset], components[offset + 1], components[offset + 2]);

        /// The region's line file as it is on disk (for the track editor), or null.
        public static LineFile ReadLineFile(string scene)
        {
            var path = LinePath(scene);
            if (!System.IO.File.Exists(path)) return null;
            try { return System.Text.Json.JsonSerializer.Deserialize<LineFile>(System.IO.File.ReadAllText(path), s_Json); }
            catch { return null; }
        }

        /// Writes a line file changed by hand: it is locked, so a newer line finder never replaces it.
        public static void WriteLineFile(string scene, LineFile lineFile)
        {
            lineFile.Locked = true;
            var path = LinePath(scene);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(lineFile, s_Json));
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
                var lineFile = new LineFile { Version = LineVersion };
                foreach (var line in lines)
                {
                    var lineData = new LineData();
                    foreach (var point in line.Points) lineData.Points.Add(RoundedComponents(point));
                    foreach (var pair in line.Keep) { var keepStart = RoundedComponents(pair.Key); var keepEnd = RoundedComponents(pair.Value); lineData.Keep.Add(new[] { keepStart[0], keepStart[1], keepStart[2], keepEnd[0], keepEnd[1], keepEnd[2] }); }
                    lineData.Pieces.AddRange(line.Used);
                    lineFile.Lines.Add(lineData);
                }
                var path = LinePath(scene);
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(lineFile, s_Json));
                SteamRailwayMod.Debug($"Lines of {scene} written to {path}.");
            }
            catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"Writing the lines of {scene} failed: {exception.Message}"); }
        }

        /// The region's saved lines, or null (none yet, or written by an older line finder and not locked).
        public static List<RailLine> LoadLines(string scene)
        {
            var path = LinePath(scene);
            if (!System.IO.File.Exists(path)) return null;
            LineFile lineFile;
            try { lineFile = System.Text.Json.JsonSerializer.Deserialize<LineFile>(System.IO.File.ReadAllText(path), s_Json); }
            catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"{path} could not be read ({exception.Message}); the lines are found again."); return null; }
            if (lineFile == null || lineFile.Lines == null || lineFile.Lines.Count == 0) return null;
            if (!lineFile.Locked && lineFile.Version != LineVersion) return null;

            // the original pieces by name, including the ones already switched off
            var byName = new Dictionary<string, List<Transform>>();
            foreach (var transform in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (transform.gameObject.scene.name == null || !transform.name.StartsWith("OBJ_RailTrack")) continue;
                if (!byName.TryGetValue(transform.name, out var points)) byName[transform.name] = points = new List<Transform>();
                points.Add(transform);
            }
            s_UsedPieces.Clear();
            var lines = new List<RailLine>();
            int missing = 0;
            foreach (var lineData in lineFile.Lines)
            {
                if (lineData.Points == null || lineData.Points.Count < 2) continue;
                var line = new RailLine();
                foreach (var savedPoint in lineData.Points) line.Points.Add(ToVector3(savedPoint));
                if (lineData.Keep != null) foreach (var savedKeep in lineData.Keep) line.Keep.Add(new KeyValuePair<Vector3, Vector3>(ToVector3(savedKeep, 0), ToVector3(savedKeep, KeepEndOffset)));
                if (lineData.Pieces != null)
                    foreach (var pieceRef in lineData.Pieces)
                    {
                        line.Used.Add(pieceRef);
                        Transform found = null;
                        if (pieceRef.Name != null && pieceRef.Position != null && byName.TryGetValue(pieceRef.Name, out var candidates))
                            foreach (var child in candidates) if ((child.position - ToVector3(pieceRef.Position)).sqrMagnitude < PieceMatchToleranceInSquareMeters) { found = child; break; }
                        if (found == null) { missing++; continue; }
                        s_UsedPieces.Add(found.GetInstanceID());
                        if (!pieceRef.Keep) line.Replaced.Add(found);
                    }
                for (int index = 1; index < line.Points.Count; index++) line.Length += Vector3.Distance(line.Points[index - 1], line.Points[index]);
                lines.Add(line);
            }
            SteamRailwayMod.Debug($"Lines of {scene} read from {path}: {lines.Count} line(s)" + (missing > 0 ? $", {missing} piece(s) not found." : "."));
            return lines.Count > 0 ? lines : null;
        }

        static Vector3[] Oriented(Piece piece, bool reversed)
        {
            if (!reversed) return piece.WorldPoints;
            var result = (Vector3[])piece.WorldPoints.Clone();
            System.Array.Reverse(result);
            return result;
        }

        /// Adds a piece's centreline (resampled to at most PointSpacing), skipping its first `skip` metres.
        static void AddPolyline(List<Vector3> points, List<bool> pinFlags, Vector3[] centerline, float skipInMeters, bool pinned)
        {
            float distance = 0f, nextSampleDistance = skipInMeters;
            for (int index = 0; index < centerline.Length - 1; index++)
            {
                var start = centerline[index]; var secondPoint = centerline[index + 1];
                float segmentLength = Vector3.Distance(start, secondPoint);
                while (nextSampleDistance <= distance + segmentLength)
                {
                    var point = Vector3.Lerp(start, secondPoint, segmentLength > MinimumSegmentLengthInMeters ? (nextSampleDistance - distance) / segmentLength : 0f);
                    if (points.Count == 0 || Vector3.Distance(points[points.Count - 1], point) > MinimumPointSpacingInMeters) { points.Add(point); pinFlags.Add(pinned); }
                    nextSampleDistance += PointSpacingInMeters;
                }
                distance += segmentLength;
            }
            var end = centerline[centerline.Length - 1];
            if (distance > skipInMeters && (points.Count == 0 || Vector3.Distance(points[points.Count - 1], end) > MinimumPointSpacingInMeters)) { points.Add(end); pinFlags.Add(pinned); }
        }

        static Vector3 Hermite(Vector3 startPoint, Vector3 startTangent, Vector3 endPoint, Vector3 endTangent, float fraction)
        {
            float fractionSquared = fraction * fraction, fractionCubed = fractionSquared * fraction;
            return (2f * fractionCubed - 3f * fractionSquared + 1f) * startPoint + (fractionCubed - 2f * fractionSquared + fraction) * startTangent + (-2f * fractionCubed + 3f * fractionSquared) * endPoint + (fractionCubed - fractionSquared) * endTangent;
        }

        /// The region's rail pieces found by BeginClear and shared by the lines of one build.
        static List<Transform> s_PieceCache;

        /// The region's rail pieces, searched once for all the lines of a build (the scene search is slow).
        public static void BeginClear()
        {
            s_PieceCache = new List<Transform>();
            foreach (var transform in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (transform.gameObject.scene.name == null) continue;
                string objectName = transform.name;
                if (objectName.StartsWith("OBJ_RailTrack") && objectName.Contains("Prefab") && !objectName.Contains("Group")) s_PieceCache.Add(transform);
            }
        }

        public static void EndClear() => s_PieceCache = null;

        /// Hides stray original pieces and small rocks lying on the track; pieces the line runs on, bridges and
        /// big rock faces stay. Only the loaded region in memory changes.
        public static void ClearLine(BuiltTrack track)
        {
            int hidden = 0, rocks = 0;
            if (s_PieceCache == null) BeginClear();
            foreach (var transform in s_PieceCache)
            {
                if (transform == null || !transform.gameObject.activeInHierarchy) continue;
                {
                    if (s_UsedPieces.Contains(transform.GetInstanceID())) continue;
                    if (!OnTrack(track, transform, StrayPieceReachInMeters, StrayPieceBelowInMeters)) continue;
                    Hide(transform, false);
                    hidden++;
                }
            }
            rocks = ClearObstacles(track);
            if (hidden + rocks > 0)
                SteamRailwayMod.Debug($"Line {track.Identifier}: hid {hidden} stray rail piece(s) and {rocks} rock(s) lying on the track.");
        }

        static bool IsLooseRock(string name) =>
            name.StartsWith("TRN_Rock") || name.StartsWith("OBJ_Rock") || name.StartsWith("TRN_Boulder") || name.Contains("Rubble");

        /// Hides the rocks and fallen trunks that engine-sized boxes slid along the track touch; rock faces and
        /// cliffs bigger than 25 m are never touched.
        static int ClearObstacles(BuiltTrack track) => ClearObstacles(track, null);

        /// A rock reaching into the line from just outside the circle is still found (the search boxes stand on the line).
        const float ObstacleSearchMarginInMeters = 5f;

        /// An obstacle was cleared: the rocks and logs on this line inside its circle are hidden now.
        public static void ClearObstaclesWithin(BuiltTrack track, ObstacleData obstacle)
        {
            int rocks = ClearObstacles(track, obstacle);
            if (rocks > 0) SteamRailwayMod.Debug($"Line {track.Identifier}: hid {rocks} rock(s) inside {obstacle.Identifier}.");
        }

        /// `within` limits the search to the part of the line inside that obstacle's circle (null: the whole line).
        static int ClearObstacles(BuiltTrack track, ObstacleData within)
        {
            var found = new List<Transform>();
            FindLineObstacles(track, within, found);
            int count = 0;
            foreach (var root in found)
            {
                // hard mode: rocks and logs on the line stay until their obstacle is cleared (or a train knocks the tree away)
                if (Obstacles.IsHeldBackObject(root.name, ObjectEdits.OriginalPosition(root))) continue;
                if (!IsClearable(root)) continue;
                Hide(root, true);
                count++;
            }
            return count;
        }

        /// The rocks and logs on this line inside the obstacle's circle that wait for it: clearing the obstacle hides them.
        public static void CollectHeldWithin(BuiltTrack track, ObstacleData obstacle, List<Transform> result)
        {
            var found = new List<Transform>();
            FindLineObstacles(track, obstacle, found);
            foreach (var root in found)
                if (Obstacles.IsHeldBackObject(root.name, ObjectEdits.OriginalPosition(root)) && IsClearable(root)) result.Add(root);
        }

        /// Where the rocks and logs that still wait for the obstacle lie on this line: for each, the stretch of the line
        /// (spline metres, from, to) its search boxes touched. A train stops at the first of them on its way.
        public static void BlockingStretches(BuiltTrack track, ObstacleData obstacle, List<Vector2> stretches)
        {
            var found = new List<Transform>();
            var arcs = new List<Vector2>();
            FindLineObstacles(track, obstacle, found, arcs);
            float splinePerPath = track.Spline.Length / Mathf.Max(MinimumPathLengthInMeters, track.Path.Length);
            for (int index = 0; index < found.Count; index++)
                if (Obstacles.IsHeldBackObject(found[index].name, ObjectEdits.OriginalPosition(found[index])) && IsClearable(found[index]))
                    stretches.Add(arcs[index] * splinePerPath);
        }

        /// Rock and log prefabs a train-sized box slid along the line touches (each once). `arcs`, when given, gets for each
        /// the stretch of the line (path metres) its boxes touched.
        static void FindLineObstacles(BuiltTrack track, ObstacleData within, List<Transform> found, List<Vector2> arcs = null)
        {
            var path = track.Path;
            var roots = new Dictionary<int, int>();
            var center = within != null ? Obstacles.CenterOf(within) : Vector3.zero;
            float reach = within != null ? within.Radius + ObstacleSearchMarginInMeters : 0f;
            for (float arcLength = 0f; arcLength < path.Length; arcLength += ObstacleProbeStepInMeters)
            {
                path.Sample(arcLength, out var point, out var tangent);
                if (within != null && (point.x - center.x) * (point.x - center.x) + (point.z - center.z) * (point.z - center.z) > reach * reach) continue;
                var rotation = Quaternion.LookRotation(new Vector3(tangent.x, 0f, tangent.z).sqrMagnitude > MinimumSegmentLengthSquared ? new Vector3(tangent.x, 0f, tangent.z) : Vector3.forward, Vector3.up);
                var hits = Physics.OverlapBox(point + Vector3.up * ProbeBoxCenterHeightInMeters, new Vector3(ProbeBoxHalfWidthInMeters, ProbeBoxHalfHeightInMeters, ProbeBoxHalfLengthInMeters), rotation);
                for (int index = 0; index < hits.Length; index++)
                {
                    var collider = hits[index];
                    if (collider == null || collider.isTrigger) continue;
                    var root = ObstacleRoot(collider.transform);
                    if (root == null) continue;
                    int identifier = root.GetInstanceID();
                    if (roots.TryGetValue(identifier, out int position))
                    {
                        if (arcs != null) arcs[position] = new Vector2(Mathf.Min(arcs[position].x, arcLength), Mathf.Max(arcs[position].y, arcLength));
                        continue;
                    }
                    roots[identifier] = found.Count;
                    found.Add(root);
                    if (arcs != null) arcs.Add(new Vector2(arcLength, arcLength));
                }
            }
        }

        /// Rock faces and cliffs (bigger than 25 m) are never touched; fallen logs always may be.
        static bool IsClearable(Transform root)
        {
            if (IsFallenTree(root.name)) return true;
            var bounds = TotalBounds(root);
            return !(bounds.size.x > MaximumClearableWidthInMeters || bounds.size.z > MaximumClearableWidthInMeters || bounds.size.y > MaximumClearableHeightInMeters);
        }

        /// Where a marked fallen tree lies across this line: the stretch (spline metres, from, to) where a train-sized box slid
        /// along the line touches one of the tree's colliders. False when it does not touch the line at all.
        public static bool TreeStretch(BuiltTrack track, IReadOnlyList<Transform> treeObjects, out Vector2 stretch)
        {
            stretch = default;
            if (treeObjects.Count == 0) return false;
            bool any = false;
            var bounds = new Bounds(treeObjects[0].position, Vector3.zero);
            foreach (var treeObject in treeObjects)
                foreach (var collider in treeObject.GetComponentsInChildren<Collider>(false))
                {
                    if (!collider.enabled || collider.isTrigger) continue;
                    if (!any) { bounds = collider.bounds; any = true; } else bounds.Encapsulate(collider.bounds);
                }
            if (!any) return false;
            bounds.Expand(TreeSearchMarginInMeters * 2f);
            var path = track.Path;
            float firstArc = float.MaxValue, lastArc = float.MinValue;
            for (float arcLength = 0f; arcLength < path.Length; arcLength += 1f)
            {
                path.Sample(arcLength, out var point, out var tangent);
                if (point.x < bounds.min.x || point.x > bounds.max.x || point.z < bounds.min.z || point.z > bounds.max.z) continue;
                var rotation = Quaternion.LookRotation(new Vector3(tangent.x, 0f, tangent.z).sqrMagnitude > MinimumSegmentLengthSquared ? new Vector3(tangent.x, 0f, tangent.z) : Vector3.forward, Vector3.up);
                var hits = Physics.OverlapBox(point + Vector3.up * ProbeBoxCenterHeightInMeters, new Vector3(ProbeBoxHalfWidthInMeters, ProbeBoxHalfHeightInMeters, ProbeBoxHalfLengthInMeters), rotation);
                for (int index = 0; index < hits.Length; index++)
                {
                    var hitCollider = hits[index];
                    if (hitCollider == null || hitCollider.isTrigger) continue;
                    bool ofTree = false;
                    foreach (var treeObject in treeObjects) if (hitCollider.transform.IsChildOf(treeObject)) { ofTree = true; break; }
                    if (!ofTree) continue;
                    firstArc = Mathf.Min(firstArc, arcLength);
                    lastArc = Mathf.Max(lastArc, arcLength);
                    break;
                }
            }
            if (firstArc > lastArc) return false;
            float splinePerPath = track.Spline.Length / Mathf.Max(MinimumPathLengthInMeters, path.Length);
            stretch = new Vector2(firstArc, lastArc) * splinePerPath;
            return true;
        }

        const float TreeSearchMarginInMeters = 3f;

        /// Hides the one object of this name and original position (a fallen tree a train has run into).
        public static void HideObject(string name, float[] position)
        {
            if (position == null || position.Length < Vector3ComponentCount) return;
            var point = new Vector3(position[0], position[1], position[2]);
            // normally known since the region was built; the whole-scene search below is the fallback
            var known = new List<Transform>();
            if (ObjectEdits.TryGetMarkedTreeObjects(name, position, known))
            {
                foreach (var transform in known) Hide(transform, true);
                return;
            }
            foreach (var transform in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (transform.name != name || transform.gameObject.scene.name == null) continue;
                if (Vector3.Distance(ObjectEdits.OriginalPosition(transform), point) > MarkedTreeMatchDistanceInMeters) continue;
                Hide(transform, true);
            }
        }

        /// The rock or log prefab a collider belongs to (null if it is something else).
        static Transform ObstacleRoot(Transform transform)
        {
            for (var node = transform; node != null; node = node.parent)
            {
                string objectName = node.name;
                if (objectName.Contains("Prefab") && (IsLooseRock(objectName) || IsFallenTree(objectName))) return node;
                if (objectName.StartsWith("SteamRailway") || objectName.StartsWith("Terrain")) return null;
            }
            return null;
        }

        static Bounds TotalBounds(Transform root)
        {
            bool any = false;
            var combinedBounds = new Bounds(root.position, Vector3.zero);
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(false))
            {
                if (!any) { combinedBounds = renderer.bounds; any = true; }
                else combinedBounds.Encapsulate(renderer.bounds);
            }
            return combinedBounds;
        }

        /// Fallen trunks and logs lying in the ravine (standing trees are not touched: they are tall).
        static bool IsFallenTree(string name) =>
            (name.Contains("Fallen") || name.Contains("Felled") || name.Contains("TreeLog") || name.Contains("Deadfall") || name.StartsWith("TRN_Log") || name.StartsWith("OBJ_Log"))
            && !name.Contains("RailCar");

        /// Does any renderer of t reach into the track corridor (within `reach` m of the rail centre, from just
        /// below the rails up to the train's height)? Objects bigger than maxSize (cliffs) never count.
        static bool OnTrack(BuiltTrack track, Transform transform, float reach, float below, float maxSize = DefaultMaximumObjectSizeInMeters)
        {
            var path = track.Path;
            foreach (var renderer in transform.GetComponentsInChildren<Renderer>(false))
            {
                if (!renderer.enabled) continue;
                var rendererBounds = renderer.bounds;
                if (rendererBounds.size.x > maxSize || rendererBounds.size.z > maxSize || rendererBounds.size.y > MaximumObjectHeightInMeters) return false;
                // the corridor along the object's whole length (a fallen trunk may cross the track anywhere)
                int steps = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(rendererBounds.size.x, rendererBounds.size.z) / CorridorSampleSpacingInMeters), 1, MaximumCorridorSamples);
                var boundsStartCorner = new Vector3(rendererBounds.min.x, rendererBounds.center.y, rendererBounds.min.z);
                var boundsEndCorner = new Vector3(rendererBounds.max.x, rendererBounds.center.y, rendererBounds.max.z);
                for (int index = 0; index <= steps; index++)
                {
                    var samplePoint = Vector3.Lerp(boundsStartCorner, boundsEndCorner, index / (float)steps);
                    float arcLength = path.NearestDistance(samplePoint, out float distance);
                    if (distance > reach + Mathf.Min(rendererBounds.extents.x, rendererBounds.extents.z) + CorridorMarginInMeters) continue;
                    path.Sample(arcLength, out var point, out _);
                    if (rendererBounds.max.y < point.y - CorridorBelowRailInMeters - below || rendererBounds.min.y > point.y + CorridorHeightAboveRailInMeters) continue;
                    var closest = rendererBounds.ClosestPoint(point);
                    var directionDelta = closest - point; directionDelta.y = 0f;
                    if (directionDelta.magnitude <= reach) return true;
                }
            }
            return false;
        }

        static void Hide(Transform transform, bool colliders)
        {
            bool any = false;
            foreach (var renderer in transform.GetComponentsInChildren<Renderer>(true)) { renderer.enabled = false; any = true; }
            if (!any) InstancedHider.Add(transform);
            var lodGroup = transform.GetComponent<LODGroup>();
            if (lodGroup != null) lodGroup.enabled = false;
            if (colliders) foreach (var collider in transform.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
        }
    }
}
