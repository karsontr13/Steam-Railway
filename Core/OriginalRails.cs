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
                var worldPoints = new Vector3[shape.Pts.Length];
                for (int index = 0; index < worldPoints.Length; index++) worldPoints[index] = space.TransformPoint(shape.Pts[index]);
                var piece = new Piece { Pts = worldPoints, Bend = shape.Bend, Keep = shape.Keep, Id = pieceTransform.GetInstanceID(), T = pieceTransform };
                var twin = pieces.Find(other => Same(other, piece));
                if (twin != null) { twin.Dups.Add(pieceTransform); continue; }
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
                if (line != null && line.Length >= MinLineLength)
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
                s_UsedPieces.Add(piece.Id);
                line.Used.Add(Ref(piece.T, piece.Keep));
                if (!piece.Keep) line.Replaced.Add(piece.T);
                foreach (var duplicate in piece.Dups)
                {
                    s_UsedPieces.Add(duplicate.GetInstanceID());
                    line.Used.Add(Ref(duplicate, piece.Keep));
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
                return StraightA();
            }
            space = meshFilter.transform;
            var mesh = meshFilter.sharedMesh;
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
            var start = new Vector3(RailPieces.CentreX, RailPieces.RailTop, RailPieces.SrcZ0);
            var end = new Vector3(RailPieces.CentreX, RailPieces.RailTop, RailPieces.SrcZ1);
            return s_StraightA = new Shape { Pts = new[] { start, end }, Bend = false };
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
            var verts = mesh.vertices;
            float ymax = float.MinValue;
            for (int index = 0; index < verts.Length; index++) ymax = Mathf.Max(ymax, verts[index].y);
            var top = new List<Vector2>();
            for (int index = 0; index < verts.Length; index++)
                if (verts[index].y > ymax - 0.03f) top.Add(new Vector2(verts[index].x, verts[index].z));
            if (top.Count < 8) return null;
            float y = ymax;

            // straight? principal axis of the rail-head points
            Vector2 mean = Vector2.zero;
            foreach (var point2D in top) mean += point2D;
            mean /= top.Count;
            float sxx = 0, sxz = 0, szz = 0;
            foreach (var point2D in top) { var direction2D = point2D - mean; sxx += direction2D.x * direction2D.x; sxz += direction2D.x * direction2D.y; szz += direction2D.y * direction2D.y; }
            float angle = 0.5f * Mathf.Atan2(2f * sxz, sxx - szz);
            var axis = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            var side = new Vector2(-axis.y, axis.x);
            var lateralOffsets = new List<float>();
            float amin = float.MaxValue, amax = float.MinValue;
            foreach (var point2D in top)
            {
                var direction2D = point2D - mean;
                lateralOffsets.Add(Vector2.Dot(direction2D, side));
                float firstValue = Vector2.Dot(direction2D, axis);
                amin = Mathf.Min(amin, firstValue); amax = Mathf.Max(amax, firstValue);
            }
            if (TwoClusters(lateralOffsets, out float l1, out float l2, out float error) && error < 0.003f && Mathf.Abs(l2 - l1) > 1.2f)
            {
                var center2D = mean + side * ((l1 + l2) * 0.5f);
                var p0 = center2D + axis * amin; var p1 = center2D + axis * amax;
                return new Shape { Pts = new[] { new Vector3(p0.x, y, p0.y), new Vector3(p1.x, y, p1.y) }, Bend = false };
            }

            // bend: centre where the rail heads fall on two circles 1.5 - 1.9 m apart
            Vector2 centre = Vector2.zero;
            float best = float.MaxValue;
            // the centre may lie far from the piece (trestle pieces are placed around it): coarse to fine search
            float[] radius = { 300f, 12f, 1.2f }, stepOf = { 6f, 0.4f, 0.03f };
            var results = new List<float>(top.Count);
            for (int pass = 0; pass < 3; pass++)
            {
                var from = pass == 0 ? mean : centre;
                float r = radius[pass], step = stepOf[pass];
                float lowerValue = pass == 0 ? 0.8f : 1.3f, upperValue = pass == 0 ? 3f : 2.1f;
                best = float.MaxValue;
                for (float x = -r; x <= r; x += step)
                for (float z = -r; z <= r; z += step)
                {
                    var cc = from + new Vector2(x, z);
                    results.Clear();
                    foreach (var point2D in top) results.Add((point2D - cc).magnitude);
                    if (!TwoClusters(results, out float a1, out float a2, out float e2)) continue;
                    float gain = Mathf.Abs(a2 - a1);
                    if (gain < lowerValue || gain > upperValue) continue;
                    if (e2 < best) { best = e2; centre = cc; }
                }
                if (best == float.MaxValue) return null;
            }
            if (best == float.MaxValue) return null;
            var radii = new List<float>();
            foreach (var point2D in top) radii.Add((point2D - centre).magnitude);
            TwoClusters(radii, out float r1, out float r2, out _);
            float rc = (r1 + r2) * 0.5f;
            // angle range around the mean direction (no wrap-around)
            var md = (mean - centre).normalized;
            float baseAng = Mathf.Atan2(md.y, md.x);
            float tmin = float.MaxValue, tmax = float.MinValue;
            foreach (var point2D in top)
            {
                var direction2D = point2D - centre;
                float fraction = Mathf.DeltaAngle(baseAng * Mathf.Rad2Deg, Mathf.Atan2(direction2D.y, direction2D.x) * Mathf.Rad2Deg) * Mathf.Deg2Rad;
                tmin = Mathf.Min(tmin, fraction); tmax = Mathf.Max(tmax, fraction);
            }
            float arc = (tmax - tmin) * rc;
            int count = Mathf.Max(2, Mathf.CeilToInt(arc / 2f) + 1);
            var points = new Vector3[count];
            for (int index = 0; index < count; index++)
            {
                float fraction = baseAng + Mathf.Lerp(tmin, tmax, index / (float)(count - 1));
                points[index] = new Vector3(centre.x + Mathf.Cos(fraction) * rc, y, centre.y + Mathf.Sin(fraction) * rc);
            }
            return new Shape { Pts = points, Bend = true };
        }

        /// Splits sorted values at the largest gap; error = mean squared distance to the two cluster means.
        static bool TwoClusters(List<float> v, out float m1, out float m2, out float error)
        {
            m1 = m2 = error = 0f;
            if (v.Count < 4) return false;
            var sides = new List<float>(v);
            sides.Sort();
            int split = 1; float gap = 0f;
            for (int index = 1; index < sides.Count; index++) if (sides[index] - sides[index - 1] > gap) { gap = sides[index] - sides[index - 1]; split = index; }
            if (split < 2 || sides.Count - split < 2) return false;
            for (int index = 0; index < split; index++) m1 += sides[index];
            for (int index = split; index < sides.Count; index++) m2 += sides[index];
            m1 /= split; m2 /= sides.Count - split;
            for (int index = 0; index < split; index++) error += (sides[index] - m1) * (sides[index] - m1);
            for (int index = split; index < sides.Count; index++) error += (sides[index] - m2) * (sides[index] - m2);
            error /= sides.Count;
            return true;
        }

        static float Length(Vector3[] p)
        {
            float l = 0f;
            for (int index = 1; index < p.Length; index++) l += Vector3.Distance(p[index - 1], p[index]);
            return l;
        }

        // ------------------------------------------------------------------ joining

        static bool Same(Piece firstPiece, Piece secondPiece) =>
            (Vector3.Distance(firstPiece.A, secondPiece.A) < 0.5f && Vector3.Distance(firstPiece.B, secondPiece.B) < 0.5f) ||
            (Vector3.Distance(firstPiece.A, secondPiece.B) < 0.5f && Vector3.Distance(firstPiece.B, secondPiece.A) < 0.5f);

        static Vector3 EndPos(List<Piece> pieces, int end) => end % 2 == 0 ? pieces[end / 2].A : pieces[end / 2].B;
        static Vector3 Outward(List<Piece> pieces, int end) => end % 2 == 0 ? pieces[end / 2].OutA : pieces[end / 2].OutB;

        /// Can the first end continue into the second? (lower score = better)
        static bool Match(List<Piece> pieces, int firstEnd, int secondEnd, out float score)
        {
            score = 0f;
            var firstPosition = EndPos(pieces, firstEnd);
            var secondPosition = EndPos(pieces, secondEnd);
            var gap = secondPosition - firstPosition;
            gap.y = 0f;
            float distance = gap.magnitude;
            if (distance > MaxGap) return false;
            if (Mathf.Abs(secondPosition.y - firstPosition.y) > 3f + 0.1f * distance) return false;   // not a line at another level
            var firstOutward = Outward(pieces, firstEnd);
            var secondOutward = Outward(pieces, secondEnd);
            if (Vector3.Dot(firstOutward, -secondOutward) < 0.5f) return false;         // the next piece must head on (within ~60°)
            float along = Vector3.Dot(gap, firstOutward);
            if (along < -MaxOverlap) return false;
            // overlapping pieces: how far the second end lies from the first piece's own centreline (a bend's end tangent overshoots)
            float lateral = along < 0f ? DistanceToPiece(pieces[firstEnd / 2], secondPosition) : (gap - firstOutward * along).magnitude;
            if (lateral > 3f + 1.2f * Mathf.Max(along, 0f)) return false;   // bends drift sideways
            score = distance + 2f * lateral + (pieces[secondEnd / 2].Bend ? BendPenalty : 0f);
            return true;
        }

        static float DistanceToPiece(Piece piece, Vector3 otherPoint)
        {
            float best = float.MaxValue;
            var q2 = new Vector2(otherPoint.x, otherPoint.z);
            for (int index = 0; index < piece.Pts.Length - 1; index++)
            {
                var firstPoint2D = new Vector2(piece.Pts[index].x, piece.Pts[index].z);
                var segment = new Vector2(piece.Pts[index + 1].x, piece.Pts[index + 1].z) - firstPoint2D;
                float fraction = Mathf.Clamp01(Vector2.Dot(q2 - firstPoint2D, segment) / Mathf.Max(segment.sqrMagnitude, 1e-6f));
                best = Mathf.Min(best, (q2 - firstPoint2D - segment * fraction).magnitude);
            }
            return best;
        }

        static RailLine Build(List<(Piece p, bool reversed)> chain)
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
                if (along < 1f)
                {
                    // overlapping pieces: carry on along the next piece from where this one ends
                    skipMeters = Mathf.Max(0f, Vector3.Dot(exitPoint - nextEntry, nextDirection)) + 1f;
                    continue;
                }
                // gap between two pieces: smooth Hermite curve
                float gapLength = Vector3.Distance(exitPoint, nextEntry);
                int steps = Mathf.Max(1, Mathf.CeilToInt(gapLength / 6f));
                for (int deepIndex = 1; deepIndex < steps; deepIndex++) { rawPoints.Add(Hermite(exitPoint, exitDirection * gapLength, nextEntry, nextDirection * gapLength, deepIndex / (float)steps)); pinned.Add(false); }
            }
            if (rawPoints.Count < 2) return null;

            // Smooth: the pieces' ends do not quite meet (steps of a few cm to tens of cm), which jolts the train
            // and shows as kinks. Resample finely, then Taubin smoothing (no shrinking of the real curves).
            Resample(rawPoints, pinned, SmoothStep, out var points, out var fixedPt);
            Taubin(points, fixedPt, SmoothIterations);

            var line = new RailLine();
            // control points every 2nd sample (4 m), always the ends
            for (int index = 0; index < points.Count; index += 2) line.Points.Add(points[index]);
            if ((points.Count - 1) % 2 != 0) line.Points.Add(points[points.Count - 1]);
            // the stretches on kept pieces (runs of pinned samples)
            for (int index = 0; index < points.Count; index++)
            {
                if (!fixedPt[index]) continue;
                int innerIndex = index;
                while (innerIndex + 1 < points.Count && fixedPt[innerIndex + 1]) innerIndex++;
                if (innerIndex > index) line.Keep.Add(new KeyValuePair<Vector3, Vector3>(points[index], points[innerIndex]));
                index = innerIndex;
            }
            for (int index = 1; index < line.Points.Count; index++) line.Length += Vector3.Distance(line.Points[index - 1], line.Points[index]);
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
            for (int index = 0; index < src.Count - 1; index++)
            {
                var start = src[index]; var end = src[index + 1];
                float segmentLength = Vector3.Distance(start, end);
                float fraction = step - carry;
                while (fraction <= segmentLength)
                {
                    dst.Add(Vector3.Lerp(start, end, fraction / segmentLength));
                    fixedPt.Add(pin[index] && pin[index + 1]);
                    fraction += step;
                }
                carry = segmentLength - (fraction - step);
            }
            if (Vector3.Distance(dst[dst.Count - 1], src[src.Count - 1]) > 0.5f) { dst.Add(src[src.Count - 1]); fixedPt.Add(true); }
            else { dst[dst.Count - 1] = src[src.Count - 1]; fixedPt[fixedPt.Count - 1] = true; }
        }

        /// Taubin smoothing (shrink, then inflate): removes the small steps and kinks, keeps the shape of the bends.
        static void Taubin(List<Vector3> points, List<bool> fixedPt, int iterations)
        {
            var buffer = new Vector3[points.Count];
            for (int item = 0; item < iterations; item++)
            {
                for (int pass = 0; pass < 2; pass++)
                {
                    float factor = pass == 0 ? 0.5f : -0.53f;
                    for (int index = 0; index < points.Count; index++)
                    {
                        if (index == 0 || index == points.Count - 1 || fixedPt[index]) { buffer[index] = points[index]; continue; }
                        buffer[index] = points[index] + factor * ((points[index - 1] + points[index + 1]) * 0.5f - points[index]);
                    }
                    for (int index = 0; index < points.Count; index++) points[index] = buffer[index];
                }
            }
        }

        /// Hides the original pieces the line's new rail replaces (their rails no longer meet the smoothed line).
        /// Pieces the game draws itself (no renderers) are switched off.
        public static void HideReplaced(RailLine line)
        {
            foreach (var transform in line.Replaced)
            {
                if (transform == null) continue;
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
        static PieceRef Ref(Transform transform, bool keep) => new PieceRef { Name = transform.name, Pos = V(transform.position), Keep = keep };
        static float[] V(Vector3 vector) => new[] { (float)System.Math.Round(vector.x, 3), (float)System.Math.Round(vector.y, 3), (float)System.Math.Round(vector.z, 3) };
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
                    foreach (var point in line.Points) lineData.Points.Add(V(point));
                    foreach (var pair in line.Keep) { var a = V(pair.Key); var b = V(pair.Value); lineData.Keep.Add(new[] { a[0], a[1], a[2], b[0], b[1], b[2] }); }
                    lineData.Pieces.AddRange(line.Used);
                    lineFile.Lines.Add(lineData);
                }
                var path = LinePath(scene);
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(lineFile, s_Json));
                SteamRailwayMod.Debug($"Lines of {scene} written to {path}.");
            }
            catch (System.Exception e) { SteamRailwayMod.Log.Warning($"Writing the lines of {scene} failed: {e.Message}"); }
        }

        /// The region's saved lines, or null (none yet, or written by an older line finder and not locked).
        public static List<RailLine> LoadLines(string scene)
        {
            var path = LinePath(scene);
            if (!System.IO.File.Exists(path)) return null;
            LineFile lineFile;
            try { lineFile = System.Text.Json.JsonSerializer.Deserialize<LineFile>(System.IO.File.ReadAllText(path), s_Json); }
            catch (System.Exception e) { SteamRailwayMod.Log.Warning($"{path} could not be read ({e.Message}); the lines are found again."); return null; }
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
                foreach (var p in lineData.Points) line.Points.Add(V(p));
                if (lineData.Keep != null) foreach (var k in lineData.Keep) line.Keep.Add(new KeyValuePair<Vector3, Vector3>(V(k, 0), V(k, 3)));
                if (lineData.Pieces != null)
                    foreach (var pieceRef in lineData.Pieces)
                    {
                        line.Used.Add(pieceRef);
                        Transform found = null;
                        if (pieceRef.Name != null && pieceRef.Pos != null && byName.TryGetValue(pieceRef.Name, out var cands))
                            foreach (var child in cands) if ((child.position - V(pieceRef.Pos)).sqrMagnitude < 0.01f) { found = child; break; }
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
            if (!reversed) return piece.Pts;
            var result = (Vector3[])piece.Pts.Clone();
            System.Array.Reverse(result);
            return result;
        }

        /// Adds a piece's centreline (resampled to at most PointSpacing), skipping its first `skip` metres.
        static void AddPolyline(List<Vector3> points, List<bool> pin, Vector3[] cl, float skip, bool pinned)
        {
            float distance = 0f, next = skip;
            for (int index = 0; index < cl.Length - 1; index++)
            {
                var start = cl[index]; var secondPoint = cl[index + 1];
                float segmentLength = Vector3.Distance(start, secondPoint);
                while (next <= distance + segmentLength)
                {
                    var point = Vector3.Lerp(start, secondPoint, segmentLength > 1e-4f ? (next - distance) / segmentLength : 0f);
                    if (points.Count == 0 || Vector3.Distance(points[points.Count - 1], point) > 1f) { points.Add(point); pin.Add(pinned); }
                    next += PointSpacing;
                }
                distance += segmentLength;
            }
            var end = cl[cl.Length - 1];
            if (distance > skip && (points.Count == 0 || Vector3.Distance(points[points.Count - 1], end) > 1f)) { points.Add(end); pin.Add(pinned); }
        }

        static Vector3 Hermite(Vector3 startPoint, Vector3 m0, Vector3 endPoint, Vector3 m1, float fraction)
        {
            float t2 = fraction * fraction, t3 = t2 * fraction;
            return (2f * t3 - 3f * t2 + 1f) * startPoint + (t3 - 2f * t2 + fraction) * m0 + (-2f * t3 + 3f * t2) * endPoint + (t3 - t2) * m1;
        }

        /// Clears the line for the new rail: original track pieces the line does not run on (broken scenery rails,
        /// stray bends) and small fallen rocks are hidden where they lie on the track. Only the loaded region in
        /// memory changes. Pieces the line runs on, bridges and big rock faces stay.
        static List<Transform> s_PieceCache;

        /// The region's rail pieces, searched once for all the lines of a build (the scene search is slow).
        public static void BeginClear()
        {
            s_PieceCache = new List<Transform>();
            foreach (var transform in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (transform.gameObject.scene.name == null) continue;
                string n = transform.name;
                if (n.StartsWith("OBJ_RailTrack") && n.Contains("Prefab") && !n.Contains("Group")) s_PieceCache.Add(transform);
            }
        }

        public static void EndClear() => s_PieceCache = null;

        public static void ClearLine(BuiltTrack track)
        {
            int hidden = 0, rocks = 0;
            if (s_PieceCache == null) BeginClear();
            foreach (var transform in s_PieceCache)
            {
                if (transform == null || !transform.gameObject.activeInHierarchy) continue;
                {
                    if (s_UsedPieces.Contains(transform.GetInstanceID())) continue;
                    if (!OnTrack(track, transform, 2.5f, 1.5f)) continue;
                    Hide(transform, false);
                    hidden++;
                }
            }
            rocks = ClearObstacles(track);
            if (hidden + rocks > 0)
                SteamRailwayMod.Debug($"Line {track.Id}: hid {hidden} stray rail piece(s) and {rocks} rock(s) lying on the track.");
        }

        static bool IsLooseRock(string name) =>
            name.StartsWith("TRN_Rock") || name.StartsWith("OBJ_Rock") || name.StartsWith("TRN_Boulder") || name.Contains("Rubble");

        /// Rocks and fallen trunks the train would run into: physics boxes the size of the engine are slid along
        /// the track, and the rock or log objects they really touch are hidden (renderers and colliders). Rock
        /// faces and cliffs (bigger than 25 m) are never touched.
        static int ClearObstacles(BuiltTrack track)
        {
            var path = track.Path;
            var roots = new HashSet<int>();
            var found = new List<Transform>();
            for (float arcLength = 0f; arcLength < path.Length; arcLength += 1.5f)
            {
                path.Sample(arcLength, out var point, out var t);
                var rotation = Quaternion.LookRotation(new Vector3(t.x, 0f, t.z).sqrMagnitude > 1e-6f ? new Vector3(t.x, 0f, t.z) : Vector3.forward, Vector3.up);
                var hits = Physics.OverlapBox(point + Vector3.up * 2.1f, new Vector3(1.35f, 1.95f, 0.8f), rotation);
                for (int index = 0; index < hits.Length; index++)
                {
                    var collider = hits[index];
                    if (collider == null || collider.isTrigger) continue;
                    var root = ObstacleRoot(collider.transform);
                    if (root == null || !roots.Add(root.GetInstanceID())) continue;
                    found.Add(root);
                }
            }
            int count = 0;
            foreach (var root in found)
            {
                var bounds = TotalBounds(root);
                bool log = IsFallenTree(root.name);
                if (!log && (bounds.size.x > 25f || bounds.size.z > 25f || bounds.size.y > 20f)) continue;
                Hide(root, true);
                count++;
            }
            return count;
        }

        /// The rock or log prefab a collider belongs to (null if it is something else).
        static Transform ObstacleRoot(Transform transform)
        {
            for (var node = transform; node != null; node = node.parent)
            {
                string n = node.name;
                if (n.Contains("Prefab") && (IsLooseRock(n) || IsFallenTree(n))) return node;
                if (n.StartsWith("SteamRailway") || n.StartsWith("Terrain")) return null;
            }
            return null;
        }

        static Bounds TotalBounds(Transform root)
        {
            bool any = false;
            var otherBounds = new Bounds(root.position, Vector3.zero);
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(false))
            {
                if (!any) { otherBounds = renderer.bounds; any = true; }
                else otherBounds.Encapsulate(renderer.bounds);
            }
            return otherBounds;
        }

        /// Fallen trunks and logs lying in the ravine (standing trees are not touched: they are tall).
        static bool IsFallenTree(string name) =>
            (name.Contains("Fallen") || name.Contains("TreeLog") || name.Contains("Deadfall") || name.StartsWith("TRN_Log") || name.StartsWith("OBJ_Log"))
            && !name.Contains("RailCar");

        /// Does any renderer of t reach into the track corridor (within `reach` m of the rail centre, from just
        /// below the rails up to the train's height)? Objects bigger than maxSize (cliffs) never count.
        static bool OnTrack(BuiltTrack track, Transform transform, float reach, float below, float maxSize = 18f)
        {
            var path = track.Path;
            foreach (var renderer in transform.GetComponentsInChildren<Renderer>(false))
            {
                if (!renderer.enabled) continue;
                var otherBounds = renderer.bounds;
                if (otherBounds.size.x > maxSize || otherBounds.size.z > maxSize || otherBounds.size.y > 12f) return false;
                // the corridor along the object's whole length (a fallen trunk may cross the track anywhere)
                int steps = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(otherBounds.size.x, otherBounds.size.z) / 2f), 1, 24);
                var a0 = new Vector3(otherBounds.min.x, otherBounds.center.y, otherBounds.min.z);
                var a1 = new Vector3(otherBounds.max.x, otherBounds.center.y, otherBounds.max.z);
                for (int index = 0; index <= steps; index++)
                {
                    var otherPoint = Vector3.Lerp(a0, a1, index / (float)steps);
                    float arcLength = path.NearestDistance(otherPoint, out float distance);
                    if (distance > reach + Mathf.Min(otherBounds.extents.x, otherBounds.extents.z) + 2f) continue;
                    path.Sample(arcLength, out var point, out _);
                    if (otherBounds.max.y < point.y - 0.25f - below || otherBounds.min.y > point.y + 4.5f) continue;
                    var closest = otherBounds.ClosestPoint(point);
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
