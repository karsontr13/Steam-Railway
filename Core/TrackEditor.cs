using System.Collections.Generic;
using UnityEngine;

namespace SteamRailway
{
    /// Draws new lines like a city builder lays roads: left click lays a stretch, Shift goes straight on, PageUp/PageDown
    /// set the height, F toggles the embankment, Backspace undoes a stretch, X finishes, Del twice deletes, Ctrl+Z undoes.
    internal static class TrackEditor
    {
        const float DefaultTemplateAngleInDegrees = 30f;
        const float SmallLiftStepInMeters = 0.25f;
        const float SnapMarkRadiusInMeters = 1.2f;
        const int MarkSegmentCount = 24;
        const float SnapSkipDistanceInMeters = 12f;
        const float WheelDeadZone = 0.01f;
        const float MinimumGradeLengthInMeters = 0.1f;
        const float MinimumSegmentLengthInMeters = 3f;
        const float StraightRadiusInMeters = 5000f;
        const float HermiteQuadraticCoefficient = 3f;
        const float MinimumAreaInSquareMeters = 1e-5f;
        const float TunnelRemoveRadiusInMeters = 15f;
        const float DeleteConfirmSeconds = 3f;
        const float DefaultReachInMeters = 20f;
        const float AimReachInMeters = 400f;
        const float MinimumSelectionLengthInMeters = 1e-3f;
        const int MinimumShapePointCount = 6;
        const int SmoothingPassCount = 8;
        const int SmoothingEdgeMargin = 3;
        const float DragPickRadiusInMeters = 8f;
        const float BigReachStepInMeters = 10f;
        const float MinimumReachInMeters = 4f;
        const float MaximumReachInMeters = 150f;
        const float DragFalloffLengthInMeters = 6f;
        const float DragMarkRadiusInMeters = 1.5f;
        const float SelectionEndToleranceInMeters = 0.01f;
        const float SelectionRailMarginInMeters = 0.15f;
        const float SelectionLiftInMeters = 0.12f;
        const float ShapeLiftInMeters = 0.15f;
        const int ReachRingSegmentCount = 48;
        const float LaidLiftInMeters = 0.1f;
        const float PreviewLiftInMeters = 0.08f;
        const float SnapDist = 4f;
        const float EndZone = 4f;          // m from a line's end that count as its end
        const float MinRadius = 25f;
        const float MaxStretch = 250f;
        const float Step = 2f;
        const float RailAbove = 0.35f;     // rail top above the ground where the line is laid freely
        const float MaxGrade = 15f;        // % (gaps under the line are filled with snow)
        const float SteepGrade = 6f;       // % above which a warning is shown
        const float Gauge = 0.76f;         // half the distance between the rails (preview)

        enum SnapKind { None, End, Middle }

        sealed class Snap
        {
            public SnapKind Kind;
            public BuiltTrack Track;
            public Vector3 Point;
            public Vector3 Tangent;            // along the line (increasing metres)
            public Vector3 Out;            // End: out of the line
        }

        sealed class Stretch
        {
            public List<Vector3> Points;
            public Vector3 PrevStart;
            public Vector3? PreviousTangent;
            public bool PreviousTangentFree;
            public Snap PrevStartSnap;
        }

        static bool s_Started;
        static Vector3 s_Start;
        static Vector3? s_Tangent;             // direction the next stretch must leave in
        static bool s_TangentFree;             // the start lies on the middle of a line: either way along it
        static Snap s_StartSnap;
        static readonly List<Stretch> s_Done = new List<Stretch>();
        static bool s_Fill = true;
        static bool s_Template;
        static int s_TemplateRadiusIndex = 1;
        static float s_TemplateAngle = DefaultTemplateAngleInDegrees;
        static float s_Lift;

        static List<Vector3> s_Preview;
        static Vector3 s_PreviousEndTangent;
        static Snap s_EndSnap;
        static bool s_Valid;
        static string s_InfoText = "", s_Warn = "";

        static string s_DelId;
        static float s_DelUntil;
        static readonly List<(string auto, string custom)> s_Undo = new List<(string, string)>();

        static readonly EditorHub.Line s_LeftRailLine = new EditorHub.Line("RailL", 0.09f);
        static readonly EditorHub.Line s_RightRailLine = new EditorHub.Line("RailR", 0.09f);
        static readonly EditorHub.Line s_Laid = new EditorHub.Line("Laid", 0.18f);
        static readonly EditorHub.Line s_Mark = new EditorHub.Line("Mark", 0.08f);

        static readonly Color Good = new Color(0.35f, 1f, 0.35f), Bad = new Color(1f, 0.3f, 0.25f),
                              Join = new Color(1f, 0.9f, 0.2f), LaidColor = new Color(0.3f, 0.75f, 1f);

        public static void Leave()
        {
            ClearSelection();
            HideSelection();
            Reset();
            s_LeftRailLine.Hide(); s_RightRailLine.Hide(); s_Laid.Hide(); s_Mark.Hide();
        }

        static void Reset()
        {
            s_Started = false; s_Tangent = null; s_TangentFree = false; s_StartSnap = null; s_Template = false;
            s_Done.Clear(); s_Preview = null; s_EndSnap = null;
        }

        public static void Update()
        {
            string scene = RailwayManager.CurrentScene;
            if (!s_Started && SelectionUpdate(scene)) { Draw(); DrawSelection(); return; }
            HideSelection();
            if (Input.GetKeyDown(KeyCode.PageUp)) s_Lift += EditorHub.Shift ? 1f : SmallLiftStepInMeters;
            if (Input.GetKeyDown(KeyCode.PageDown)) s_Lift -= EditorHub.Shift ? 1f : SmallLiftStepInMeters;
            if (Input.GetKeyDown(KeyCode.F))
            {
                s_Fill = !s_Fill;
                CabControls.Message(s_Fill ? Localizer.Translate("Hattın altına kar dolgusu: açık", "Snow embankment under the line: on")
                                           : Localizer.Translate("Hattın altına kar dolgusu: kapalı", "Snow embankment under the line: off"));
            }
            if (EditorHub.Control && Input.GetKeyDown(KeyCode.Z)) { Undo(scene); return; }
            if (s_Started) TemplateKeys();
            if (Input.GetKeyDown(KeyCode.Backspace)) TakeBack();
            if (Input.GetKeyDown(KeyCode.X)) { Finish(scene, null); return; }

            if (!EditorHub.Aim(out var hitPoint))
            {
                s_Preview = null; s_InfoText = Localizer.Translate("(zemin yok)", "(no ground)"); s_Warn = "";
                Draw();
                return;
            }
            if (Input.GetKeyDown(KeyCode.Delete)) Delete(scene, hitPoint);
            if (!s_Started && Input.GetKeyDown(KeyCode.T)) Tunnel(scene, hitPoint);
            if (!s_Started && Input.GetKeyDown(KeyCode.P)) Portal(scene, hitPoint);

            var snap = FindSnap(hitPoint);
            if (!s_Started)
            {
                s_Preview = null;
                var point = snap != null ? snap.Point : hitPoint + Vector3.up * (RailAbove + s_Lift);
                s_Mark.Set(EditorHub.Ring(point, SnapMarkRadiusInMeters, MarkSegmentCount), snap != null ? Join : Good);
                s_InfoText = snap == null ? Localizer.Translate("Başlangıç için sol tık", "Left click to start")
                       : snap.Kind == SnapKind.End ? Localizer.Translate("Hattın ucundan devam (sol tık)", "Carry on from the line's end (left click)")
                       : Localizer.Translate("Hattan makasla ayrıl (sol tık)", "Branch off the line with a switch (left click)");
                s_Warn = "";
                if (Input.GetMouseButtonDown(0)) Begin(point, snap);
                Draw();
                return;
            }

            Preview(hitPoint, snap);
            if (Input.GetMouseButtonDown(0))
            {
                if (s_Valid) Lay(scene);
                else CabControls.Message(Localizer.Translate("Bu parça döşenemez: ", "This stretch cannot be laid: ") + s_Warn);
            }
            Draw();
        }

        // ------------------------------------------------------------------ snapping

        /// The nearest line within reach of the crosshair: its end or a point on its middle.
        static Snap FindSnap(Vector3 hitPoint)
        {
            Snap best = null; float bestDistance = SnapDist;
            foreach (var track in RailwayManager.Tracks)
            {
                if (track?.Path == null) continue;
                float arcLength = track.Path.NearestDistance(hitPoint, out float distance);
                if (distance >= bestDistance) continue;
                track.Path.Sample(arcLength, out var point, out var tangent);
                tangent.y = 0f; tangent.Normalize();
                // the line we started from does not catch the crosshair near the start
                if (s_Started && s_StartSnap != null && s_StartSnap.Track == track && (point - s_Start).magnitude < SnapSkipDistanceInMeters) continue;
                var snap = new Snap { Track = track, Point = point, Tangent = tangent };
                float length = track.Path.Length;
                if (arcLength < EndZone || arcLength > length - EndZone)
                {
                    snap.Kind = SnapKind.End;
                    bool atEnd = arcLength > length * 0.5f;
                    snap.Point = atEnd ? track.Path[track.Path.Count - 1] : track.Path[0];
                    snap.Out = atEnd ? tangent : -tangent;
                }
                else snap.Kind = SnapKind.Middle;
                best = snap; bestDistance = distance;
            }
            return best;
        }

        static void Begin(Vector3 point, Snap snap)
        {
            s_Started = true;
            s_Start = point;
            s_StartSnap = snap;
            s_Done.Clear();
            if (snap == null) { s_Tangent = null; s_TangentFree = false; }
            else if (snap.Kind == SnapKind.End) { s_Tangent = snap.Out; s_TangentFree = false; }
            else { s_Tangent = snap.Tangent; s_TangentFree = true; }
        }

        // ------------------------------------------------------------------ the stretch under the crosshair

        static Vector3 Flat(Vector3 vector) { vector.y = 0f; return vector; }

        static void TemplateKeys()
        {
            if (Input.GetKeyDown(KeyCode.C))
            {
                s_Template = !s_Template;
                CabControls.Message(s_Template ? Localizer.Translate("Viraj şablonu: açık", "Curve template: on") : Localizer.Translate("Viraj şablonu: kapalı", "Curve template: off"));
            }
            if (!s_Template) return;
            if (Input.GetKeyDown(KeyCode.R)) s_TemplateRadiusIndex = (s_TemplateRadiusIndex + 1) % CurveTemplates.Radii.Length;
            float wheel = Input.mouseScrollDelta.y;
            if (Mathf.Abs(wheel) > WheelDeadZone)
            {
                float step = EditorHub.Shift ? CurveTemplates.FastAngleStep : CurveTemplates.AngleStep;
                s_TemplateAngle = Mathf.Clamp(s_TemplateAngle + Mathf.Sign(wheel) * step, CurveTemplates.MinAngle, CurveTemplates.MaxAngle);
            }
        }

        /// The ready-made bend from the start point: its side follows the crosshair, its radius and angle are set with R and the wheel.
        static void PreviewTemplate(Vector3 hitPoint)
        {
            s_EndSnap = null;
            var startPoint = s_Start;
            var chord = Flat(hitPoint - startPoint).normalized;
            Vector3 startDirection = chord;
            if (s_Tangent.HasValue)
            {
                startDirection = s_Tangent.Value;
                if (s_TangentFree && Vector3.Dot(startDirection, chord) < 0f) startDirection = -startDirection;
            }
            startDirection = Flat(startDirection).normalized;
            bool turnRight = Vector3.Dot(chord, Vector3.Cross(Vector3.up, startDirection)) >= 0f;
            float radius = CurveTemplates.Radii[s_TemplateRadiusIndex];

            var flatPoints = CurveTemplates.Build(Flat(startPoint), startDirection, radius, s_TemplateAngle, turnRight, out var endDirection, out float length);
            var endPoint = flatPoints[flatPoints.Count - 1];
            endPoint.y = Il2CppHelpers.TerrainHeight(endPoint, startPoint.y) + RailAbove + s_Lift;
            s_Preview = new List<Vector3>(flatPoints.Count);
            for (int index = 0; index < flatPoints.Count; index++)
            {
                var point = flatPoints[index];
                point.y = Mathf.Lerp(startPoint.y, endPoint.y, index / (float)(flatPoints.Count - 1));
                s_Preview.Add(point);
            }
            s_PreviousEndTangent = endDirection;
            float grade = length > MinimumGradeLengthInMeters ? (endPoint.y - startPoint.y) / length * 100f : 0f;
            CheckStretch(length, radius, grade);

            string side = turnRight ? Localizer.Translate("sağa", "right") : Localizer.Translate("sola", "left");
            s_InfoText = Localizer.Translate($"VİRAJ ŞABLONU {side}   yarıçap {radius:F0} m (R)   açı {s_TemplateAngle:F0}° (tekerlek)   uzunluk {length:F0} m   eğim %{grade:F1}",
                         $"CURVE TEMPLATE {side}   radius {radius:F0} m (R)   angle {s_TemplateAngle:F0}° (wheel)   length {length:F0} m   grade {grade:F1}%");
        }

        /// Sets s_Valid and s_Warn for the stretch under the crosshair.
        static void CheckStretch(float length, float radius, float grade)
        {
            s_Valid = true; s_Warn = "";
            if (length < MinimumSegmentLengthInMeters) { s_Valid = false; s_Warn = Localizer.Translate("çok kısa", "too short"); }
            else if (length > MaxStretch) { s_Valid = false; s_Warn = Localizer.Translate($"bir parça en çok {MaxStretch:F0} m", $"a stretch is at most {MaxStretch:F0} m"); }
            else if (radius < MinRadius) { s_Valid = false; s_Warn = Localizer.Translate($"viraj çok keskin (en az {MinRadius:F0} m yarıçap)", $"bend too sharp (at least {MinRadius:F0} m radius)"); }
            else if (Mathf.Abs(grade) > MaxGrade) { s_Valid = false; s_Warn = Localizer.Translate($"eğim çok dik (en çok %{MaxGrade:F0}; PageUp/PageDown ile yüksekliği ayarla)", $"too steep (at most {MaxGrade:F0}%; set the height with PageUp/PageDown)"); }
            else if (Mathf.Abs(grade) > SteepGrade) s_Warn = Localizer.Translate("dik eğim: ağır tren zorlanabilir", "steep: a heavy train may struggle");
        }

        static void Preview(Vector3 hitPoint, Snap snap)
        {
            if (s_Template && snap == null) { PreviewTemplate(hitPoint); return; }
            s_EndSnap = snap;
            var startPoint = s_Start;
            Vector3 endPoint;
            Vector3? endTangentOverride = null;
            Vector3 chord;
            if (snap != null)
            {
                endPoint = snap.Point;
                chord = Flat(endPoint - startPoint).normalized;
                if (snap.Kind == SnapKind.End) endTangentOverride = -snap.Out;                         // into the line's end
                else endTangentOverride = snap.Tangent * (Vector3.Dot(snap.Tangent, chord) >= 0f ? 1f : -1f);   // along the line
            }
            else
            {
                endPoint = hitPoint + Vector3.up * (RailAbove + s_Lift);
                chord = Flat(endPoint - startPoint).normalized;
                if (EditorHub.Shift && s_Tangent.HasValue)
                {
                    // straight on: the crosshair slides along the start direction
                    var snapTangent = s_Tangent.Value;
                    if (s_TangentFree && Vector3.Dot(snapTangent, chord) < 0f) snapTangent = -snapTangent;
                    float along = Vector3.Dot(Flat(endPoint - startPoint), snapTangent);
                    var otherPoint = startPoint + snapTangent * Mathf.Max(0f, along);
                    otherPoint.y = Il2CppHelpers.TerrainHeight(otherPoint, endPoint.y) + RailAbove + s_Lift;
                    endPoint = otherPoint;
                    chord = Flat(endPoint - startPoint).normalized;
                }
            }
            float length = Flat(endPoint - startPoint).magnitude;
            Vector3 startTangent = chord;
            if (s_Tangent.HasValue)
            {
                startTangent = s_Tangent.Value;
                if (s_TangentFree && Vector3.Dot(startTangent, chord) < 0f) startTangent = -startTangent;
            }
            Vector3 endTangent = endTangentOverride ?? (s_Tangent.HasValue ? 2f * Vector3.Dot(startTangent, chord) * chord - startTangent : chord);   // a circular arc
            endTangent = Flat(endTangent).normalized;
            s_PreviousEndTangent = endTangent;

            s_Preview = Curve(startPoint, endPoint, startTangent, endTangent, length, 1f);
            float radius = MinRadiusOf(s_Preview);
            float grade = length > MinimumGradeLengthInMeters ? (endPoint.y - startPoint.y) / length * 100f : 0f;
            CheckStretch(length, radius, grade);

            string radiusText = radius > StraightRadiusInMeters ? Localizer.Translate("düz", "straight") : $"{radius:F0} m";
            s_InfoText = Localizer.Translate($"uzunluk {length:F0} m   yarıçap {radiusText}   eğim %{grade:F1}   yükseklik {s_Lift:+0.00;-0.00} m   dolgu {(s_Fill ? "açık" : "kapalı")}",
                         $"length {length:F0} m   radius {radiusText}   grade {grade:F1}%   height {s_Lift:+0.00;-0.00} m   embankment {(s_Fill ? "on" : "off")}");
            if (snap != null)
                s_InfoText += snap.Kind == SnapKind.End ? Localizer.Translate("   → hattın ucuna bağlanır", "   → joins the line's end")
                                                    : Localizer.Translate("   → makasla bağlanır", "   → joins with a switch");
        }

        /// Hermite curve in the ground plane, the height rising evenly (a steady grade).
        static List<Vector3> Curve(Vector3 startPoint, Vector3 endPoint, Vector3 startTangent, Vector3 endTangent, float length, float step)
        {
            int count = Mathf.Max(2, Mathf.CeilToInt(length / step) + 1);
            var results = new List<Vector3>(count);
            var start = Flat(startPoint); var end = Flat(endPoint);
            var startScaledTangent = startTangent * length; var endScaledTangent = endTangent * length;
            for (int index = 0; index < count; index++)
            {
                float fraction = index / (float)(count - 1), fractionSquared = fraction * fraction, fractionCubed = fractionSquared * fraction;
                var otherPoint = (2f * fractionCubed - HermiteQuadraticCoefficient * fractionSquared + 1f) * start + (fractionCubed - 2f * fractionSquared + fraction) * startScaledTangent + (-2f * fractionCubed + HermiteQuadraticCoefficient * fractionSquared) * end + (fractionCubed - fractionSquared) * endScaledTangent;
                otherPoint.y = Mathf.Lerp(startPoint.y, endPoint.y, fraction);
                results.Add(otherPoint);
            }
            return results;
        }

        static float MinRadiusOf(List<Vector3> points)
        {
            float best = float.MaxValue;
            const int deepIndex = 3;
            for (int index = deepIndex; index + deepIndex < points.Count; index++)
            {
                var start = Flat(points[index - deepIndex]); var end = Flat(points[index]); var center = Flat(points[index + deepIndex]);
                float area2 = Vector3.Cross(end - start, center - start).magnitude;
                if (area2 < MinimumAreaInSquareMeters) continue;
                float radius = (end - start).magnitude * (center - end).magnitude * (center - start).magnitude / (2f * area2);
                best = Mathf.Min(best, radius);
            }
            return best;
        }

        // ------------------------------------------------------------------ laying, finishing

        static void Lay(string scene)
        {
            var points = Resample(s_Preview, Step);
            s_Done.Add(new Stretch { Points = points, PrevStart = s_Start, PreviousTangent = s_Tangent, PreviousTangentFree = s_TangentFree, PrevStartSnap = s_StartSnap });
            var end = s_Preview[s_Preview.Count - 1];
            var endSnap = s_EndSnap;
            s_Start = end;
            s_Tangent = s_PreviousEndTangent;
            s_TangentFree = false;
            if (endSnap != null) Finish(scene, endSnap);   // it ran into another line: the line is complete
        }

        static List<Vector3> Resample(List<Vector3> sourcePoints, float step)
        {
            var results = new List<Vector3> { sourcePoints[0] };
            float accumulator = 0f;
            for (int index = 1; index < sourcePoints.Count; index++)
            {
                accumulator += Vector3.Distance(sourcePoints[index - 1], sourcePoints[index]);
                if (accumulator >= step || index == sourcePoints.Count - 1) { results.Add(sourcePoints[index]); accumulator = 0f; }
            }
            return results;
        }

        static void TakeBack()
        {
            if (s_Done.Count > 0)
            {
                var last = s_Done[s_Done.Count - 1];
                s_Done.RemoveAt(s_Done.Count - 1);
                s_Start = last.PrevStart; s_Tangent = last.PreviousTangent; s_TangentFree = last.PreviousTangentFree; s_StartSnap = last.PrevStartSnap;
            }
            else if (s_Started) Reset();
        }

        static void Finish(string scene, Snap endSnap)
        {
            if (!s_Started) return;
            if (s_Done.Count == 0) { Reset(); return; }
            var data = new CustomLineData { Identifier = CustomLines.NextId(scene), Fill = s_Fill };
            foreach (var stretch in s_Done)
                for (int index = 0; index < stretch.Points.Count; index++)
                {
                    if (data.Points.Count > 0 && index == 0) continue;
                    data.Points.Add(CustomLines.ToFloatArray(stretch.Points[index]));
                }
            var first = s_Done[0];
            if (first.PrevStartSnap != null && first.PrevStartSnap.Kind == SnapKind.Middle) data.Switches.Add(data.Points[0]);
            if (endSnap != null && endSnap.Kind == SnapKind.Middle) data.Switches.Add(data.Points[data.Points.Count - 1]);
            s_Undo.Add(LineEdits.Snapshot(scene));
            CustomLines.Add(scene, data);
            float length = 0f;
            for (int index = 1; index < data.Points.Count; index++) length += Vector3.Distance(CustomLines.ToVector3(data.Points[index - 1]), CustomLines.ToVector3(data.Points[index]));
            Reset();
            CabControls.Message(Localizer.Translate($"Hat kaydedildi ({length:F0} m); raylar kuruluyor...", $"Line saved ({length:F0} m); laying the rails..."));
            Rebuild();
        }

        /// T on a line: a tunnel mouth there (the tunnel runs on away from you); T at a placed tunnel removes it.
        static void Tunnel(string scene, Vector3 hitPoint)
        {
            if (Tunnels.RemoveNear(scene, hitPoint, TunnelRemoveRadiusInMeters)) { CabControls.Message(Localizer.Translate("Tünel kaldırıldı.", "Tunnel removed.")); return; }
            if (!LineAt(hitPoint, out var track, out float arcLength)) { CabControls.Message(Localizer.Translate("Tünel ağzı için nişangâhı rayın üstüne getir.", "Aim at the line where the tunnel mouth should be.")); return; }
            var cameraTransform = EditorHub.Camera();
            CabControls.Message(Tunnels.PlaceAt(scene, track, arcLength, cameraTransform != null ? cameraTransform.position : hitPoint));
        }

        /// P on a line: one end of a rail transition (first here, then in the other region); Shift+P deletes one.
        static void Portal(string scene, Vector3 hitPoint)
        {
            if (EditorHub.Shift) { CabControls.Message(CustomPortals.RemoveNear(scene, hitPoint)); return; }
            if (!LineAt(hitPoint, out var track, out float arcLength)) { CabControls.Message(Localizer.Translate("Geçiş için nişangâhı rayın üstüne getir.", "Aim at the line for the transition.")); return; }
            track.Path.Sample(arcLength, out var point, out var tangent);
            tangent.y = 0f; tangent.Normalize();
            var cameraTransform = EditorHub.Camera();
            var away = cameraTransform != null && Vector3.Dot(tangent, point - cameraTransform.position) < 0f ? -tangent : tangent;
            CabControls.Message(CustomPortals.Mark(scene, point, away));
        }

        static void Delete(string scene, Vector3 hitPoint)
        {
            var customLine = CustomLines.Nearest(scene, hitPoint, SnapDist);
            if (customLine == null) { CabControls.Message(Localizer.Translate("Burada çizilmiş bir hat yok (bölgenin kendi hatları silinemez).", "No drawn line here (the region's own lines cannot be deleted).")); return; }
            if (s_DelId != customLine.Identifier || Time.time > s_DelUntil)
            {
                s_DelId = customLine.Identifier; s_DelUntil = Time.time + DeleteConfirmSeconds;
                CabControls.Message(Localizer.Translate($"{customLine.Identifier} silinsin mi? Onay için Del'e tekrar bas.", $"Delete {customLine.Identifier}? Press Del again to confirm."));
                return;
            }
            s_DelId = null;
            s_Undo.Add(LineEdits.Snapshot(scene));
            CustomLines.Remove(scene, customLine.Identifier);
            CabControls.Message(Localizer.Translate($"{customLine.Identifier} silindi (Ctrl+Z geri getirir).", $"{customLine.Identifier} deleted (Ctrl+Z brings it back)."));
            Rebuild();
        }

        static void Undo(string scene)
        {
            if (s_Started) { TakeBack(); return; }
            if (s_Undo.Count == 0) { CabControls.Message(Localizer.Translate("Geri alınacak bir şey yok.", "Nothing to undo.")); return; }
            LineEdits.Restore(scene, s_Undo[s_Undo.Count - 1]);
            s_Undo.RemoveAt(s_Undo.Count - 1);
            CabControls.Message(Localizer.Translate("Geri alındı; raylar yeniden kuruluyor...", "Undone; laying the rails again..."));
            Rebuild();
        }

        static void Rebuild()
        {
            if (!RailwayManager.RebuildForEditor())
                CabControls.Message(Localizer.Translate("Hat kaydedildi; raylar trenden inince kurulur (F9 ya da bölgeye yeniden gir).",
                                        "Line saved; the rails are laid once you leave the train (F9 or enter the region again)."));
        }

        // Editing built lines: right click twice selects a stretch; Del cuts it, N smooths it, G drags a point,
        // Enter applies and X drops the selection.

        const float ShapeBufferStepInMeters = 2f;
        static BuiltTrack s_SelectedTrack;
        static float s_SelectionStartArc = -1f, s_SelectionEndArc = -1f;
        static List<Vector3> s_ShapeBuffer, s_ShapeBufferBase;
        static bool s_ShapeBufferDirty;
        static int s_Drag = -1;
        static float s_DragLift, s_Reach = DefaultReachInMeters;
        static readonly EditorHub.Line s_SelectionLeftLine = new EditorHub.Line("SelL", 0.1f);
        static readonly EditorHub.Line s_SelectionRightLine = new EditorHub.Line("SelR", 0.1f);
        static readonly EditorHub.Line s_ShapeLine = new EditorHub.Line("Shape", 0.14f);
        static readonly EditorHub.Line s_DragMark = new EditorHub.Line("DragMark", 0.08f);
        static readonly Color SelectionColor = new Color(1f, 0.35f, 0.3f), ShapeColor = new Color(1f, 0.85f, 0.2f);

        static void ClearSelection()
        {
            s_SelectedTrack = null; s_SelectionStartArc = s_SelectionEndArc = -1f; s_ShapeBuffer = null; s_ShapeBufferBase = null; s_ShapeBufferDirty = false; s_Drag = -1;
        }

        static void HideSelection() { s_SelectionLeftLine.Hide(); s_SelectionRightLine.Hide(); s_ShapeLine.Hide(); s_DragMark.Hide(); }

        /// The nearest built line under the crosshair (any part of it).
        static bool LineAt(Vector3 hitPoint, out BuiltTrack track, out float arcLength)
        {
            // aimed straight at one of our rails: that line (where lines cross, the one actually aimed at)
            var cameraTransform = EditorHub.Camera();
            if (cameraTransform != null)
            {
                float bestHit = float.MaxValue; BuiltTrack aimed = null; Vector3 aimedPoint = Vector3.zero;
                foreach (var raycastHit in Physics.RaycastAll(cameraTransform.position, cameraTransform.forward, AimReachInMeters, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (raycastHit.distance >= bestHit) continue;
                    for (var node = raycastHit.collider.transform; node != null; node = node.parent)
                    {
                        if (node.name.StartsWith("CHARACTER_FPSPlayer")) break;
                        if (!node.name.StartsWith("SteamRailway_Track_")) continue;
                        string identifier = node.name.Substring("SteamRailway_Track_".Length);
                        foreach (var candidateTrack in RailwayManager.Tracks) if (candidateTrack != null && candidateTrack.Identifier == identifier) { aimed = candidateTrack; bestHit = raycastHit.distance; aimedPoint = raycastHit.point; }
                        break;
                    }
                }
                if (aimed != null)
                {
                    // nothing solid in front of the rail?
                    if (!EditorHub.Aim(out var ground) || (ground - cameraTransform.position).magnitude + 0.5f >= bestHit)
                    {
                        track = aimed;
                        arcLength = aimed.Path.NearestDistanceIncludingHeight(aimedPoint, out _);
                        return true;
                    }
                }
            }
            track = null; arcLength = 0f; float bestDistance = SnapDist;
            foreach (var candidateTrack in RailwayManager.Tracks)
            {
                if (candidateTrack?.Path == null) continue;
                float candidateArc = candidateTrack.Path.NearestDistance(hitPoint, out float distance);
                if (distance < bestDistance) { bestDistance = distance; track = candidateTrack; arcLength = candidateArc; }
            }
            return track != null;
        }

        static bool SelectionUpdate(string scene)
        {
            // undo is for the last applied change: the selection goes
            if (EditorHub.Control && Input.GetKeyDown(KeyCode.Z)) { ClearSelection(); return false; }
            bool aimed = EditorHub.Aim(out var hitPoint);
            if (Input.GetMouseButtonDown(1))
            {
                if (!aimed || !LineAt(hitPoint, out var track, out float arcLength))
                {
                    CabControls.Message(Localizer.Translate("Nişangâhta ray yok.", "No line in the crosshair."));
                    return s_SelectedTrack != null;
                }
                if (s_SelectedTrack == null || s_SelectionEndArc >= 0f || track != s_SelectedTrack)
                {
                    ClearSelection();
                    s_SelectedTrack = track; s_SelectionStartArc = arcLength;
                }
                else
                {
                    s_SelectionEndArc = arcLength;
                    if (s_SelectionEndArc < s_SelectionStartArc) (s_SelectionStartArc, s_SelectionEndArc) = (s_SelectionEndArc, s_SelectionStartArc);
                    if (s_SelectionEndArc - s_SelectionStartArc < 2f) { s_SelectionEndArc = -1f; CabControls.Message(Localizer.Translate("Seçim çok kısa.", "Selection too short.")); }
                    else MakeBuffer();
                }
                return true;
            }
            if (s_SelectedTrack == null) return false;
            if (s_SelectedTrack.Path == null || s_SelectedTrack.Root == null) { ClearSelection(); return false; }   // rebuilt meanwhile
            if (Input.GetKeyDown(KeyCode.X))
            {
                ClearSelection();
                CabControls.Message(Localizer.Translate("Seçim bırakıldı.", "Selection dropped."));
                return true;
            }
            if (s_SelectionEndArc < 0f)
            {
                s_InfoText = Localizer.Translate("Seçimin ikinci ucunu sağ tıkla seç (X iptal)", "Right click the other end of the stretch (X cancels)");
                s_Warn = "";
                return true;
            }

            float length = s_SelectionEndArc - s_SelectionStartArc;
            if (s_Drag >= 0) { Drag(aimed, hitPoint); }
            else
            {
                if (Input.GetKeyDown(KeyCode.Delete)) { CutSelection(scene); return true; }
                if (Input.GetKeyDown(KeyCode.N)) { Smooth(); }
                if (Input.GetKeyDown(KeyCode.G) && aimed) StartDrag(hitPoint);
                if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) { Apply(scene); return true; }
            }
            float radius = MinRadiusOf(s_ShapeBuffer);
            string radiusText = radius > StraightRadiusInMeters ? Localizer.Translate("düz", "straight") : $"{radius:F0} m";
            s_InfoText = Localizer.Translate($"seçili {length:F0} m   en dar viraj {radiusText}", $"selected {length:F0} m   tightest bend {radiusText}")
                   + (s_ShapeBufferDirty ? Localizer.Translate("   (değişti: Enter uygular)", "   (changed: Enter applies)") : "")
                   + (s_Drag >= 0 ? Localizer.Translate($"   etki {s_Reach:F0} m   yükseklik {s_DragLift:+0.00;-0.00} m", $"   reach {s_Reach:F0} m   height {s_DragLift:+0.00;-0.00} m") : "");
            s_Warn = radius < MinRadius ? Localizer.Translate($"viraj {MinRadius:F0} m'den dar: tren sarsılabilir (N ile yumuşat)", $"bend tighter than {MinRadius:F0} m: the train may lurch (N smooths)") : "";
            return true;
        }

        static void MakeBuffer()
        {
            s_ShapeBuffer = new List<Vector3>();
            int count = Mathf.Max(2, Mathf.CeilToInt((s_SelectionEndArc - s_SelectionStartArc) / ShapeBufferStepInMeters) + 1);
            for (int index = 0; index < count; index++)
            {
                s_SelectedTrack.Path.Sample(Mathf.Lerp(s_SelectionStartArc, s_SelectionEndArc, index / (float)(count - 1)), out var point, out _);
                s_ShapeBuffer.Add(point);
            }
            s_ShapeBufferDirty = false;
        }

        /// The new shape at path metres s (between the selection's ends).
        static Vector3 ShapeAt(float arcLength)
        {
            int count = s_ShapeBuffer.Count;
            float factor = (arcLength - s_SelectionStartArc) / Mathf.Max(MinimumSelectionLengthInMeters, s_SelectionEndArc - s_SelectionStartArc) * (count - 1);
            int index = Mathf.Clamp(Mathf.FloorToInt(factor), 0, count - 2);
            return Vector3.Lerp(s_ShapeBuffer[index], s_ShapeBuffer[index + 1], Mathf.Clamp01(factor - index));
        }

        /// Softer bends and an even grade; the two ends (and the direction there) stay.
        static void Smooth()
        {
            if (s_ShapeBuffer == null || s_ShapeBuffer.Count < MinimumShapePointCount) return;
            var buffer = new Vector3[s_ShapeBuffer.Count];
            for (int item = 0; item < SmoothingPassCount; item++)
            {
                for (int index = 0; index < s_ShapeBuffer.Count; index++)
                    buffer[index] = (index < 2 || index > s_ShapeBuffer.Count - SmoothingEdgeMargin) ? s_ShapeBuffer[index] : Vector3.Lerp(s_ShapeBuffer[index], (s_ShapeBuffer[index - 1] + s_ShapeBuffer[index + 1]) * 0.5f, 0.5f);
                for (int index = 0; index < s_ShapeBuffer.Count; index++) s_ShapeBuffer[index] = buffer[index];
            }
            s_ShapeBufferDirty = true;
        }

        static void StartDrag(Vector3 hitPoint)
        {
            int best = -1; float bestDistance = DragPickRadiusInMeters * DragPickRadiusInMeters;
            for (int index = 0; index < s_ShapeBuffer.Count; index++)
            {
                float deltaX = s_ShapeBuffer[index].x - hitPoint.x, deltaZ = s_ShapeBuffer[index].z - hitPoint.z, distance = deltaX * deltaX + deltaZ * deltaZ;
                if (distance < bestDistance) { bestDistance = distance; best = index; }
            }
            if (best < 0) { CabControls.Message(Localizer.Translate("Nişangâhı seçili rayın üstüne getir.", "Aim at the selected line.")); return; }
            s_Drag = best;
            s_ShapeBufferBase = new List<Vector3>(s_ShapeBuffer);
            s_DragLift = 0f;
            CabControls.Message(Localizer.Translate("Nokta tutuldu: nişangâhla taşı, tekerlek etki alanı, PageUp/PageDown yükseklik, G ya da sol tık bırakır.",
                                    "Point held: move it with the crosshair, wheel = reach, PageUp/PageDown = height, G or left click lets go."));
        }

        static void Drag(bool aimed, Vector3 hitPoint)
        {
            float wheel = Input.mouseScrollDelta.y;
            if (wheel != 0f) s_Reach = Mathf.Clamp(s_Reach + Mathf.Sign(wheel) * (EditorHub.Shift ? BigReachStepInMeters : 2f), MinimumReachInMeters, MaximumReachInMeters);
            if (Input.GetKeyDown(KeyCode.PageUp)) s_DragLift += EditorHub.Shift ? 1f : SmallLiftStepInMeters;
            if (Input.GetKeyDown(KeyCode.PageDown)) s_DragLift -= EditorHub.Shift ? 1f : SmallLiftStepInMeters;
            if (Input.GetKeyDown(KeyCode.G) || Input.GetMouseButtonDown(0)) { s_Drag = -1; s_ShapeBufferBase = null; return; }
            if (!aimed) return;
            var end = s_ShapeBufferBase[s_Drag];
            var target = new Vector3(hitPoint.x, end.y + s_DragLift, hitPoint.z);
            var delta = target - end;
            int count = s_ShapeBuffer.Count;
            float dragOffsetInMeters = s_Drag * ShapeBufferStepInMeters;
            for (int index = 0; index < count; index++)
            {
                float shapeArc = index * ShapeBufferStepInMeters;
                float weight = Mathf.SmoothStep(0f, 1f, 1f - Mathf.Abs(shapeArc - dragOffsetInMeters) / s_Reach);
                // the ends of the selection stay where they are (the line carries on there)
                weight *= Mathf.Clamp01(Mathf.Min(shapeArc, (count - 1) * ShapeBufferStepInMeters - shapeArc) / DragFalloffLengthInMeters);
                s_ShapeBuffer[index] = s_ShapeBufferBase[index] + delta * weight;
            }
            s_ShapeBufferDirty = true;
        }

        static void Apply(string scene)
        {
            if (!s_ShapeBufferDirty) { CabControls.Message(Localizer.Translate("Değişiklik yok (N yumuşatır, G nokta taşır).", "Nothing changed (N smooths, G drags a point).")); return; }
            var snap = LineEdits.Snapshot(scene);
            int moved = LineEdits.Reshape(scene, s_SelectedTrack, s_SelectionStartArc, s_SelectionEndArc, ShapeAt);
            if (moved == 0) { CabControls.Message(Localizer.Translate("Bu kısımda değiştirilebilecek nokta yok.", "No points to change on this stretch.")); return; }
            s_Undo.Add(snap);
            ClearSelection();
            CabControls.Message(Localizer.Translate($"Ray yeniden şekillendi ({moved} nokta); kuruluyor... (Ctrl+Z geri alır)", $"Line reshaped ({moved} points); laying... (Ctrl+Z undoes)"));
            Rebuild();
        }

        static void CutSelection(string scene)
        {
            var snap = LineEdits.Snapshot(scene);
            int removed = LineEdits.Cut(scene, s_SelectedTrack, s_SelectionStartArc, s_SelectionEndArc);
            if (removed == 0) { CabControls.Message(Localizer.Translate("Bu kısımda silinecek nokta yok (seçimi biraz uzat).", "Nothing to cut here (make the selection a little longer).")); return; }
            s_Undo.Add(snap);
            ClearSelection();
            CabControls.Message(Localizer.Translate("Ray parçası silindi; kuruluyor... (Ctrl+Z geri alır)", "Stretch of line cut out; laying... (Ctrl+Z undoes)"));
            Rebuild();
        }

        static void DrawSelection()
        {
            if (s_SelectedTrack == null || s_SelectedTrack.Path == null) { HideSelection(); return; }
            if (s_SelectionEndArc < 0f)
            {
                s_SelectedTrack.Path.Sample(s_SelectionStartArc, out var point, out _);
                s_DragMark.Set(EditorHub.Ring(point, DragMarkRadiusInMeters, MarkSegmentCount), SelectionColor, true);
                s_SelectionLeftLine.Hide(); s_SelectionRightLine.Hide(); s_ShapeLine.Hide();
                return;
            }
            var points = new List<Vector3>(); var results = new List<Vector3>();
            for (float arcLength = s_SelectionStartArc; arcLength <= s_SelectionEndArc + SelectionEndToleranceInMeters; arcLength += 1f)
            {
                s_SelectedTrack.Path.Sample(Mathf.Min(arcLength, s_SelectionEndArc), out var point, out var tangent);
                var side = Vector3.Cross(Vector3.up, Flat(tangent)).normalized * (Gauge + SelectionRailMarginInMeters);
                points.Add(point - side + Vector3.up * SelectionLiftInMeters); results.Add(point + side + Vector3.up * SelectionLiftInMeters);
            }
            s_SelectionLeftLine.Set(points, SelectionColor); s_SelectionRightLine.Set(results, SelectionColor);
            if (s_ShapeBuffer != null && s_ShapeBufferDirty)
            {
                var shapedPoints = new List<Vector3>(s_ShapeBuffer.Count);
                foreach (var point in s_ShapeBuffer) shapedPoints.Add(point + Vector3.up * ShapeLiftInMeters);
                s_ShapeLine.Set(shapedPoints, ShapeColor);
            }
            else s_ShapeLine.Hide();
            if (s_Drag >= 0 && s_ShapeBuffer != null) s_DragMark.Set(EditorHub.Ring(s_ShapeBuffer[s_Drag], s_Reach, ReachRingSegmentCount), ShapeColor, true);
            else s_DragMark.Hide();
        }

        // ------------------------------------------------------------------ drawing

        static void Draw()
        {
            // laid stretches of the line being drawn
            var laid = new List<Vector3>();
            foreach (var stretch in s_Done) foreach (var point in stretch.Points) laid.Add(point + Vector3.up * LaidLiftInMeters);
            if (laid.Count >= 2) s_Laid.Set(laid, LaidColor); else s_Laid.Hide();

            if (s_Preview == null || s_Preview.Count < 2) { s_LeftRailLine.Hide(); s_RightRailLine.Hide(); if (s_Started) s_Mark.Hide(); return; }
            var points = new List<Vector3>(s_Preview.Count);
            var results = new List<Vector3>(s_Preview.Count);
            for (int index = 0; index < s_Preview.Count; index++)
            {
                var start = s_Preview[Mathf.Max(0, index - 1)]; var end = s_Preview[Mathf.Min(s_Preview.Count - 1, index + 1)];
                var side = Vector3.Cross(Vector3.up, Flat(end - start)).normalized * Gauge;
                var center = s_Preview[index] + Vector3.up * PreviewLiftInMeters;
                points.Add(center - side); results.Add(center + side);
            }
            var color = !s_Valid ? Bad : s_EndSnap != null ? Join : Good;
            s_LeftRailLine.Set(points, color); s_RightRailLine.Set(results, color);
            if (s_EndSnap != null) s_Mark.Set(EditorHub.Ring(s_EndSnap.Point, SnapMarkRadiusInMeters, MarkSegmentCount), Join); else s_Mark.Hide();
        }

        public static void OnGUI()
        {
            string keys = s_Started
                ? Localizer.Translate("Sol tık döşe   Shift düz devam   C viraj şablonu (R yarıçap, tekerlek açı)   PageUp/PageDown yükseklik   F dolgu   Backspace son parçayı geri al   X bitir\nCtrl+Z geri al   F11 sonraki mod",
                      "Left click lay   Shift straight on   C curve template (R radius, wheel angle)   PageUp/PageDown height   F embankment   Backspace take back   X finish\nCtrl+Z undo   F11 next mode")
                : s_SelectedTrack != null
                ? Localizer.Translate("Sağ tık seçim ucu   Del seçili parçayı sil   N yumuşat   G nokta tut/bırak (tekerlek etki, PageUp/PageDown yükseklik)\nEnter uygula   X seçimi bırak   Ctrl+Z geri al   F11 sonraki mod",
                      "Right click selection end   Del cut the stretch   N smooth   G hold/let go a point (wheel reach, PageUp/PageDown height)\nEnter apply   X drop selection   Ctrl+Z undo   F11 next mode")
                : Localizer.Translate("Sol tık çizmeye başla (hattın ucuna/ortasına tıklarsan ona bağlanır)   Sağ tık iki kez: kurulmuş raydan parça seç   T tünel ağzı\nPageUp/PageDown yükseklik   F dolgu   Del (iki kez) çizilmiş hattı sil   Ctrl+Z geri al   F11 sonraki mod",
                      "Left click start drawing (on a line's end/middle to join it)   Right click twice: select a stretch of a built line   T tunnel mouth\nPageUp/PageDown height   F embankment   Del (twice) delete a drawn line   Ctrl+Z undo   F11 next mode");
            EditorHub.Panel(Localizer.Translate("RAY DÜZENLEYİCİ", "TRACK EDITOR"), s_InfoText, s_Warn, keys);
        }
    }
}
