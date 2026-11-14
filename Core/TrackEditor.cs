using System.Collections.Generic;
using UnityEngine;

namespace SteamRailway
{
    /// Draws new lines the way a city builder lays roads: the line runs from the last point to the crosshair and
    /// bends with it, keeping its direction smooth; a click lays that stretch and the next one starts there.
    ///   left click  lay the stretch (the first click sets the start)
    ///   Shift       straight on along the line's direction
    ///   PageUp / PageDown  rail height at the crosshair (Shift: 1 m)
    ///   F  snow embankment under the line on / off      Backspace  take back the last stretch
    ///   X  finish the line (it is built)                 Del (twice)  delete the drawn line in the crosshair
    ///   Ctrl+Z  undo the last finished / deleted line
    /// Starting or ending at the end of another line joins the two into one line; starting or ending on the
    /// middle of a line puts a switch there.
    internal static class TrackEditor
    {
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
            public Vector3 P;
            public Vector3 Tan;            // along the line (increasing metres)
            public Vector3 Out;            // End: out of the line
        }

        sealed class Stretch
        {
            public List<Vector3> Pts;
            public Vector3 PrevStart;
            public Vector3? PrevTan;
            public bool PrevTanFree;
            public Snap PrevStartSnap;
        }

        static bool s_Started;
        static Vector3 s_Start;
        static Vector3? s_Tan;             // direction the next stretch must leave in
        static bool s_TanFree;             // the start lies on the middle of a line: either way along it
        static Snap s_StartSnap;
        static readonly List<Stretch> s_Done = new List<Stretch>();
        static bool s_Fill = true;
        static float s_Lift;

        static List<Vector3> s_Preview;
        static Vector3 s_PrevEndTan;
        static Snap s_EndSnap;
        static bool s_Valid;
        static string s_Info = "", s_Warn = "";

        static string s_DelId;
        static float s_DelUntil;
        static readonly List<(string auto, string custom)> s_Undo = new List<(string, string)>();

        static readonly EditorHub.Line s_RailL = new EditorHub.Line("RailL", 0.09f);
        static readonly EditorHub.Line s_RailR = new EditorHub.Line("RailR", 0.09f);
        static readonly EditorHub.Line s_Laid = new EditorHub.Line("Laid", 0.18f);
        static readonly EditorHub.Line s_Mark = new EditorHub.Line("Mark", 0.08f);

        static readonly Color Good = new Color(0.35f, 1f, 0.35f), Bad = new Color(1f, 0.3f, 0.25f),
                              Join = new Color(1f, 0.9f, 0.2f), LaidCol = new Color(0.3f, 0.75f, 1f);

        public static void Leave()
        {
            ClearSelection();
            HideSelection();
            Reset();
            s_RailL.Hide(); s_RailR.Hide(); s_Laid.Hide(); s_Mark.Hide();
        }

        static void Reset()
        {
            s_Started = false; s_Tan = null; s_TanFree = false; s_StartSnap = null;
            s_Done.Clear(); s_Preview = null; s_EndSnap = null;
        }

        public static void Update()
        {
            string scene = RailwayManager.CurrentScene;
            if (!s_Started && SelectionUpdate(scene)) { Draw(); DrawSelection(); return; }
            HideSelection();
            if (Input.GetKeyDown(KeyCode.PageUp)) s_Lift += EditorHub.Shift ? 1f : 0.25f;
            if (Input.GetKeyDown(KeyCode.PageDown)) s_Lift -= EditorHub.Shift ? 1f : 0.25f;
            if (Input.GetKeyDown(KeyCode.F))
            {
                s_Fill = !s_Fill;
                CabControls.Message(s_Fill ? L.T("Hattın altına kar dolgusu: açık", "Snow embankment under the line: on")
                                           : L.T("Hattın altına kar dolgusu: kapalı", "Snow embankment under the line: off"));
            }
            if (EditorHub.Ctrl && Input.GetKeyDown(KeyCode.Z)) { Undo(scene); return; }
            if (Input.GetKeyDown(KeyCode.Backspace)) TakeBack();
            if (Input.GetKeyDown(KeyCode.X)) { Finish(scene, null); return; }

            if (!EditorHub.Aim(out var hit))
            {
                s_Preview = null; s_Info = L.T("(zemin yok)", "(no ground)"); s_Warn = "";
                Draw();
                return;
            }
            if (Input.GetKeyDown(KeyCode.Delete)) Delete(scene, hit);
            if (!s_Started && Input.GetKeyDown(KeyCode.T)) Tunnel(scene, hit);
            if (!s_Started && Input.GetKeyDown(KeyCode.P)) Portal(scene, hit);

            var snap = FindSnap(hit);
            if (!s_Started)
            {
                s_Preview = null;
                var point = snap != null ? snap.P : hit + Vector3.up * (RailAbove + s_Lift);
                s_Mark.Set(EditorHub.Ring(point, 1.2f, 24), snap != null ? Join : Good);
                s_Info = snap == null ? L.T("Başlangıç için sol tık", "Left click to start")
                       : snap.Kind == SnapKind.End ? L.T("Hattın ucundan devam (sol tık)", "Carry on from the line's end (left click)")
                       : L.T("Hattan makasla ayrıl (sol tık)", "Branch off the line with a switch (left click)");
                s_Warn = "";
                if (Input.GetMouseButtonDown(0)) Begin(point, snap);
                Draw();
                return;
            }

            Preview(hit, snap);
            if (Input.GetMouseButtonDown(0))
            {
                if (s_Valid) Lay(scene);
                else CabControls.Message(L.T("Bu parça döşenemez: ", "This stretch cannot be laid: ") + s_Warn);
            }
            Draw();
        }

        // ------------------------------------------------------------------ snapping

        /// The nearest line within reach of the crosshair: its end or a point on its middle.
        static Snap FindSnap(Vector3 hit)
        {
            Snap best = null; float bestDistance = SnapDist;
            foreach (var track in RailwayManager.Tracks)
            {
                if (track?.Path == null) continue;
                float arcLength = track.Path.NearestDistance(hit, out float distance);
                if (distance >= bestDistance) continue;
                track.Path.Sample(arcLength, out var point, out var tangent);
                tangent.y = 0f; tangent.Normalize();
                // the line we started from does not catch the crosshair near the start
                if (s_Started && s_StartSnap != null && s_StartSnap.Track == track && (point - s_Start).magnitude < 12f) continue;
                var snap = new Snap { Track = track, P = point, Tan = tangent };
                float length = track.Path.Length;
                if (arcLength < EndZone || arcLength > length - EndZone)
                {
                    snap.Kind = SnapKind.End;
                    bool atEnd = arcLength > length * 0.5f;
                    snap.P = atEnd ? track.Path[track.Path.Count - 1] : track.Path[0];
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
            if (snap == null) { s_Tan = null; s_TanFree = false; }
            else if (snap.Kind == SnapKind.End) { s_Tan = snap.Out; s_TanFree = false; }
            else { s_Tan = snap.Tan; s_TanFree = true; }
        }

        // ------------------------------------------------------------------ the stretch under the crosshair

        static Vector3 Flat(Vector3 vector) { vector.y = 0f; return vector; }

        static void Preview(Vector3 hit, Snap snap)
        {
            s_EndSnap = snap;
            var startPoint = s_Start;
            Vector3 endPoint;
            Vector3? t1 = null;
            Vector3 chord;
            if (snap != null)
            {
                endPoint = snap.P;
                chord = Flat(endPoint - startPoint).normalized;
                if (snap.Kind == SnapKind.End) t1 = -snap.Out;                         // into the line's end
                else t1 = snap.Tan * (Vector3.Dot(snap.Tan, chord) >= 0f ? 1f : -1f);   // along the line
            }
            else
            {
                endPoint = hit + Vector3.up * (RailAbove + s_Lift);
                chord = Flat(endPoint - startPoint).normalized;
                if (EditorHub.Shift && s_Tan.HasValue)
                {
                    // straight on: the crosshair slides along the start direction
                    var t = s_Tan.Value;
                    if (s_TanFree && Vector3.Dot(t, chord) < 0f) t = -t;
                    float along = Vector3.Dot(Flat(endPoint - startPoint), t);
                    var otherPoint = startPoint + t * Mathf.Max(0f, along);
                    otherPoint.y = H.TerrainHeight(otherPoint, endPoint.y) + RailAbove + s_Lift;
                    endPoint = otherPoint;
                    chord = Flat(endPoint - startPoint).normalized;
                }
            }
            float length = Flat(endPoint - startPoint).magnitude;
            Vector3 t0 = chord;
            if (s_Tan.HasValue)
            {
                t0 = s_Tan.Value;
                if (s_TanFree && Vector3.Dot(t0, chord) < 0f) t0 = -t0;
            }
            Vector3 te = t1 ?? (s_Tan.HasValue ? 2f * Vector3.Dot(t0, chord) * chord - t0 : chord);   // a circular arc
            te = Flat(te).normalized;
            s_PrevEndTan = te;

            s_Preview = Curve(startPoint, endPoint, t0, te, length, 1f);
            // checks
            s_Valid = true; s_Warn = "";
            float radius = MinRadiusOf(s_Preview);
            float grade = length > 0.1f ? (endPoint.y - startPoint.y) / length * 100f : 0f;
            if (length < 3f) { s_Valid = false; s_Warn = L.T("çok kısa", "too short"); }
            else if (length > MaxStretch) { s_Valid = false; s_Warn = L.T($"bir parça en çok {MaxStretch:F0} m", $"a stretch is at most {MaxStretch:F0} m"); }
            else if (radius < MinRadius) { s_Valid = false; s_Warn = L.T($"viraj çok keskin (en az {MinRadius:F0} m yarıçap)", $"bend too sharp (at least {MinRadius:F0} m radius)"); }
            else if (Mathf.Abs(grade) > MaxGrade) { s_Valid = false; s_Warn = L.T($"eğim çok dik (en çok %{MaxGrade:F0}; PageUp/PageDown ile yüksekliği ayarla)", $"too steep (at most {MaxGrade:F0}%; set the height with PageUp/PageDown)"); }
            else if (Mathf.Abs(grade) > SteepGrade) s_Warn = L.T("dik eğim: ağır tren zorlanabilir", "steep: a heavy train may struggle");

            string rad = radius > 5000f ? L.T("düz", "straight") : $"{radius:F0} m";
            s_Info = L.T($"uzunluk {length:F0} m   yarıçap {rad}   eğim %{grade:F1}   yükseklik {s_Lift:+0.00;-0.00} m   dolgu {(s_Fill ? "açık" : "kapalı")}",
                         $"length {length:F0} m   radius {rad}   grade {grade:F1}%   height {s_Lift:+0.00;-0.00} m   embankment {(s_Fill ? "on" : "off")}");
            if (snap != null)
                s_Info += snap.Kind == SnapKind.End ? L.T("   → hattın ucuna bağlanır", "   → joins the line's end")
                                                    : L.T("   → makasla bağlanır", "   → joins with a switch");
        }

        /// Hermite curve in the ground plane, the height rising evenly (a steady grade).
        static List<Vector3> Curve(Vector3 startPoint, Vector3 endPoint, Vector3 t0, Vector3 t1, float length, float step)
        {
            int count = Mathf.Max(2, Mathf.CeilToInt(length / step) + 1);
            var results = new List<Vector3>(count);
            var start = Flat(startPoint); var end = Flat(endPoint);
            var m0 = t0 * length; var m1 = t1 * length;
            for (int index = 0; index < count; index++)
            {
                float fraction = index / (float)(count - 1), t2 = fraction * fraction, t3 = t2 * fraction;
                var otherPoint = (2f * t3 - 3f * t2 + 1f) * start + (t3 - 2f * t2 + fraction) * m0 + (-2f * t3 + 3f * t2) * end + (t3 - t2) * m1;
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
                if (area2 < 1e-5f) continue;
                float radius = (end - start).magnitude * (center - end).magnitude * (center - start).magnitude / (2f * area2);
                best = Mathf.Min(best, radius);
            }
            return best;
        }

        // ------------------------------------------------------------------ laying, finishing

        static void Lay(string scene)
        {
            var points = Resample(s_Preview, Step);
            s_Done.Add(new Stretch { Pts = points, PrevStart = s_Start, PrevTan = s_Tan, PrevTanFree = s_TanFree, PrevStartSnap = s_StartSnap });
            var end = s_Preview[s_Preview.Count - 1];
            var endSnap = s_EndSnap;
            s_Start = end;
            s_Tan = s_PrevEndTan;
            s_TanFree = false;
            if (endSnap != null) Finish(scene, endSnap);   // it ran into another line: the line is complete
        }

        static List<Vector3> Resample(List<Vector3> src, float step)
        {
            var results = new List<Vector3> { src[0] };
            float accumulator = 0f;
            for (int index = 1; index < src.Count; index++)
            {
                accumulator += Vector3.Distance(src[index - 1], src[index]);
                if (accumulator >= step || index == src.Count - 1) { results.Add(src[index]); accumulator = 0f; }
            }
            return results;
        }

        static void TakeBack()
        {
            if (s_Done.Count > 0)
            {
                var last = s_Done[s_Done.Count - 1];
                s_Done.RemoveAt(s_Done.Count - 1);
                s_Start = last.PrevStart; s_Tan = last.PrevTan; s_TanFree = last.PrevTanFree; s_StartSnap = last.PrevStartSnap;
            }
            else if (s_Started) Reset();
        }

        static void Finish(string scene, Snap endSnap)
        {
            if (!s_Started) return;
            if (s_Done.Count == 0) { Reset(); return; }
            var data = new CustomLineData { Id = CustomLines.NextId(scene), Fill = s_Fill };
            foreach (var stretch in s_Done)
                for (int index = 0; index < stretch.Pts.Count; index++)
                {
                    if (data.Points.Count > 0 && index == 0) continue;
                    data.Points.Add(CustomLines.F(stretch.Pts[index]));
                }
            var first = s_Done[0];
            if (first.PrevStartSnap != null && first.PrevStartSnap.Kind == SnapKind.Middle) data.Switches.Add(data.Points[0]);
            if (endSnap != null && endSnap.Kind == SnapKind.Middle) data.Switches.Add(data.Points[data.Points.Count - 1]);
            s_Undo.Add(LineEdits.Snapshot(scene));
            CustomLines.Add(scene, data);
            float length = 0f;
            for (int index = 1; index < data.Points.Count; index++) length += Vector3.Distance(CustomLines.V(data.Points[index - 1]), CustomLines.V(data.Points[index]));
            Reset();
            CabControls.Message(L.T($"Hat kaydedildi ({length:F0} m); raylar kuruluyor...", $"Line saved ({length:F0} m); laying the rails..."));
            Rebuild();
        }

        /// T on a line: a tunnel mouth there (the tunnel runs on away from you); T at a placed tunnel removes it.
        static void Tunnel(string scene, Vector3 hit)
        {
            if (Tunnels.RemoveNear(scene, hit, 15f)) { CabControls.Message(L.T("Tünel kaldırıldı.", "Tunnel removed.")); return; }
            if (!LineAt(hit, out var track, out float arcLength)) { CabControls.Message(L.T("Tünel ağzı için nişangâhı rayın üstüne getir.", "Aim at the line where the tunnel mouth should be.")); return; }
            var cameraTransform = EditorHub.Camera();
            CabControls.Message(Tunnels.PlaceAt(scene, track, arcLength, cameraTransform != null ? cameraTransform.position : hit));
        }

        /// P on a line: one end of a rail transition (first here, then in the other region); Shift+P deletes one.
        static void Portal(string scene, Vector3 hit)
        {
            if (EditorHub.Shift) { CabControls.Message(CustomPortals.RemoveNear(scene, hit)); return; }
            if (!LineAt(hit, out var track, out float arcLength)) { CabControls.Message(L.T("Geçiş için nişangâhı rayın üstüne getir.", "Aim at the line for the transition.")); return; }
            track.Path.Sample(arcLength, out var point, out var tangent);
            tangent.y = 0f; tangent.Normalize();
            var cameraTransform = EditorHub.Camera();
            var away = cameraTransform != null && Vector3.Dot(tangent, point - cameraTransform.position) < 0f ? -tangent : tangent;
            CabControls.Message(CustomPortals.Mark(scene, point, away));
        }

        static void Delete(string scene, Vector3 hit)
        {
            var customLine = CustomLines.Nearest(scene, hit, SnapDist);
            if (customLine == null) { CabControls.Message(L.T("Burada çizilmiş bir hat yok (bölgenin kendi hatları silinemez).", "No drawn line here (the region's own lines cannot be deleted).")); return; }
            if (s_DelId != customLine.Id || Time.time > s_DelUntil)
            {
                s_DelId = customLine.Id; s_DelUntil = Time.time + 3f;
                CabControls.Message(L.T($"{customLine.Id} silinsin mi? Onay için Del'e tekrar bas.", $"Delete {customLine.Id}? Press Del again to confirm."));
                return;
            }
            s_DelId = null;
            s_Undo.Add(LineEdits.Snapshot(scene));
            CustomLines.Remove(scene, customLine.Id);
            CabControls.Message(L.T($"{customLine.Id} silindi (Ctrl+Z geri getirir).", $"{customLine.Id} deleted (Ctrl+Z brings it back)."));
            Rebuild();
        }

        static void Undo(string scene)
        {
            if (s_Started) { TakeBack(); return; }
            if (s_Undo.Count == 0) { CabControls.Message(L.T("Geri alınacak bir şey yok.", "Nothing to undo.")); return; }
            LineEdits.Restore(scene, s_Undo[s_Undo.Count - 1]);
            s_Undo.RemoveAt(s_Undo.Count - 1);
            CabControls.Message(L.T("Geri alındı; raylar yeniden kuruluyor...", "Undone; laying the rails again..."));
            Rebuild();
        }

        static void Rebuild()
        {
            if (!RailwayManager.RebuildForEditor())
                CabControls.Message(L.T("Hat kaydedildi; raylar trenden inince kurulur (F9 ya da bölgeye yeniden gir).",
                                        "Line saved; the rails are laid once you leave the train (F9 or enter the region again)."));
        }

        // ------------------------------------------------------------------ editing built lines
        //   right click twice: select a stretch of a built line (any line, the region's own too)
        //   Del  cut the stretch out     N  smooth it (softer bends, even grade)     G  take hold of a point and drag
        //   (wheel: how far the drag reaches, PageUp / PageDown: height)     Enter  apply     X  drop the selection

        const float BufStep = 2f;
        static BuiltTrack s_SelT;
        static float s_SelA = -1f, s_SelB = -1f;
        static List<Vector3> s_Buf, s_BufBase;
        static bool s_BufDirty;
        static int s_Drag = -1;
        static float s_DragLift, s_Reach = 20f;
        static readonly EditorHub.Line s_SelL = new EditorHub.Line("SelL", 0.1f);
        static readonly EditorHub.Line s_SelR = new EditorHub.Line("SelR", 0.1f);
        static readonly EditorHub.Line s_BufLine = new EditorHub.Line("Shape", 0.14f);
        static readonly EditorHub.Line s_DragMark = new EditorHub.Line("DragMark", 0.08f);
        static readonly Color SelCol = new Color(1f, 0.35f, 0.3f), ShapeCol = new Color(1f, 0.85f, 0.2f);

        static void ClearSelection()
        {
            s_SelT = null; s_SelA = s_SelB = -1f; s_Buf = null; s_BufBase = null; s_BufDirty = false; s_Drag = -1;
        }

        static void HideSelection() { s_SelL.Hide(); s_SelR.Hide(); s_BufLine.Hide(); s_DragMark.Hide(); }

        /// The nearest built line under the crosshair (any part of it).
        static bool LineAt(Vector3 hit, out BuiltTrack track, out float arcLength)
        {
            // aimed straight at one of our rails: that line (where lines cross, the one actually aimed at)
            var cameraTransform = EditorHub.Camera();
            if (cameraTransform != null)
            {
                float bestHit = float.MaxValue; BuiltTrack aimed = null; Vector3 at = Vector3.zero;
                foreach (var h in Physics.RaycastAll(cameraTransform.position, cameraTransform.forward, 400f, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (h.distance >= bestHit) continue;
                    for (var node = h.collider.transform; node != null; node = node.parent)
                    {
                        if (node.name.StartsWith("CHARACTER_FPSPlayer")) break;
                        if (!node.name.StartsWith("SteamRailway_Track_")) continue;
                        string identifier = node.name.Substring("SteamRailway_Track_".Length);
                        foreach (var t in RailwayManager.Tracks) if (t != null && t.Id == identifier) { aimed = t; bestHit = h.distance; at = h.point; }
                        break;
                    }
                }
                if (aimed != null)
                {
                    // nothing solid in front of the rail?
                    if (!EditorHub.Aim(out var ground) || (ground - cameraTransform.position).magnitude + 0.5f >= bestHit)
                    {
                        track = aimed;
                        arcLength = aimed.Path.NearestDistance3D(at, out _);
                        return true;
                    }
                }
            }
            track = null; arcLength = 0f; float bestDistance = SnapDist;
            foreach (var t in RailwayManager.Tracks)
            {
                if (t?.Path == null) continue;
                float ss = t.Path.NearestDistance(hit, out float distance);
                if (distance < bestDistance) { bestDistance = distance; track = t; arcLength = ss; }
            }
            return track != null;
        }

        static bool SelectionUpdate(string scene)
        {
            // undo is for the last applied change: the selection goes
            if (EditorHub.Ctrl && Input.GetKeyDown(KeyCode.Z)) { ClearSelection(); return false; }
            bool aimed = EditorHub.Aim(out var hit);
            if (Input.GetMouseButtonDown(1))
            {
                if (!aimed || !LineAt(hit, out var track, out float arcLength))
                {
                    CabControls.Message(L.T("Nişangâhta ray yok.", "No line in the crosshair."));
                    return s_SelT != null;
                }
                if (s_SelT == null || s_SelB >= 0f || track != s_SelT)
                {
                    ClearSelection();
                    s_SelT = track; s_SelA = arcLength;
                }
                else
                {
                    s_SelB = arcLength;
                    if (s_SelB < s_SelA) (s_SelA, s_SelB) = (s_SelB, s_SelA);
                    if (s_SelB - s_SelA < 2f) { s_SelB = -1f; CabControls.Message(L.T("Seçim çok kısa.", "Selection too short.")); }
                    else MakeBuffer();
                }
                return true;
            }
            if (s_SelT == null) return false;
            if (s_SelT.Path == null || s_SelT.Root == null) { ClearSelection(); return false; }   // rebuilt meanwhile
            if (Input.GetKeyDown(KeyCode.X))
            {
                ClearSelection();
                CabControls.Message(L.T("Seçim bırakıldı.", "Selection dropped."));
                return true;
            }
            if (s_SelB < 0f)
            {
                s_Info = L.T("Seçimin ikinci ucunu sağ tıkla seç (X iptal)", "Right click the other end of the stretch (X cancels)");
                s_Warn = "";
                return true;
            }

            float length = s_SelB - s_SelA;
            if (s_Drag >= 0) { Drag(aimed, hit); }
            else
            {
                if (Input.GetKeyDown(KeyCode.Delete)) { CutSelection(scene); return true; }
                if (Input.GetKeyDown(KeyCode.N)) { Smooth(); }
                if (Input.GetKeyDown(KeyCode.G) && aimed) StartDrag(hit);
                if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) { Apply(scene); return true; }
            }
            float radius = MinRadiusOf(s_Buf);
            string rad = radius > 5000f ? L.T("düz", "straight") : $"{radius:F0} m";
            s_Info = L.T($"seçili {length:F0} m   en dar viraj {rad}", $"selected {length:F0} m   tightest bend {rad}")
                   + (s_BufDirty ? L.T("   (değişti: Enter uygular)", "   (changed: Enter applies)") : "")
                   + (s_Drag >= 0 ? L.T($"   etki {s_Reach:F0} m   yükseklik {s_DragLift:+0.00;-0.00} m", $"   reach {s_Reach:F0} m   height {s_DragLift:+0.00;-0.00} m") : "");
            s_Warn = radius < MinRadius ? L.T($"viraj {MinRadius:F0} m'den dar: tren sarsılabilir (N ile yumuşat)", $"bend tighter than {MinRadius:F0} m: the train may lurch (N smooths)") : "";
            return true;
        }

        static void MakeBuffer()
        {
            s_Buf = new List<Vector3>();
            int count = Mathf.Max(2, Mathf.CeilToInt((s_SelB - s_SelA) / BufStep) + 1);
            for (int index = 0; index < count; index++)
            {
                s_SelT.Path.Sample(Mathf.Lerp(s_SelA, s_SelB, index / (float)(count - 1)), out var point, out _);
                s_Buf.Add(point);
            }
            s_BufDirty = false;
        }

        /// The new shape at path metres s (between the selection's ends).
        static Vector3 ShapeAt(float arcLength)
        {
            int count = s_Buf.Count;
            float factor = (arcLength - s_SelA) / Mathf.Max(1e-3f, s_SelB - s_SelA) * (count - 1);
            int index = Mathf.Clamp(Mathf.FloorToInt(factor), 0, count - 2);
            return Vector3.Lerp(s_Buf[index], s_Buf[index + 1], Mathf.Clamp01(factor - index));
        }

        /// Softer bends and an even grade; the two ends (and the direction there) stay.
        static void Smooth()
        {
            if (s_Buf == null || s_Buf.Count < 6) return;
            var buffer = new Vector3[s_Buf.Count];
            for (int item = 0; item < 8; item++)
            {
                for (int index = 0; index < s_Buf.Count; index++)
                    buffer[index] = (index < 2 || index > s_Buf.Count - 3) ? s_Buf[index] : Vector3.Lerp(s_Buf[index], (s_Buf[index - 1] + s_Buf[index + 1]) * 0.5f, 0.5f);
                for (int index = 0; index < s_Buf.Count; index++) s_Buf[index] = buffer[index];
            }
            s_BufDirty = true;
        }

        static void StartDrag(Vector3 hit)
        {
            int best = -1; float bestDistance = 8f * 8f;
            for (int index = 0; index < s_Buf.Count; index++)
            {
                float deltaX = s_Buf[index].x - hit.x, deltaZ = s_Buf[index].z - hit.z, distance = deltaX * deltaX + deltaZ * deltaZ;
                if (distance < bestDistance) { bestDistance = distance; best = index; }
            }
            if (best < 0) { CabControls.Message(L.T("Nişangâhı seçili rayın üstüne getir.", "Aim at the selected line.")); return; }
            s_Drag = best;
            s_BufBase = new List<Vector3>(s_Buf);
            s_DragLift = 0f;
            CabControls.Message(L.T("Nokta tutuldu: nişangâhla taşı, tekerlek etki alanı, PageUp/PageDown yükseklik, G ya da sol tık bırakır.",
                                    "Point held: move it with the crosshair, wheel = reach, PageUp/PageDown = height, G or left click lets go."));
        }

        static void Drag(bool aimed, Vector3 hit)
        {
            float wheel = Input.mouseScrollDelta.y;
            if (wheel != 0f) s_Reach = Mathf.Clamp(s_Reach + Mathf.Sign(wheel) * (EditorHub.Shift ? 10f : 2f), 4f, 150f);
            if (Input.GetKeyDown(KeyCode.PageUp)) s_DragLift += EditorHub.Shift ? 1f : 0.25f;
            if (Input.GetKeyDown(KeyCode.PageDown)) s_DragLift -= EditorHub.Shift ? 1f : 0.25f;
            if (Input.GetKeyDown(KeyCode.G) || Input.GetMouseButtonDown(0)) { s_Drag = -1; s_BufBase = null; return; }
            if (!aimed) return;
            var end = s_BufBase[s_Drag];
            var target = new Vector3(hit.x, end.y + s_DragLift, hit.z);
            var delta = target - end;
            int count = s_Buf.Count;
            float sk = s_Drag * BufStep;
            for (int index = 0; index < count; index++)
            {
                float si = index * BufStep;
                float weight = Mathf.SmoothStep(0f, 1f, 1f - Mathf.Abs(si - sk) / s_Reach);
                // the ends of the selection stay where they are (the line carries on there)
                weight *= Mathf.Clamp01(Mathf.Min(si, (count - 1) * BufStep - si) / 6f);
                s_Buf[index] = s_BufBase[index] + delta * weight;
            }
            s_BufDirty = true;
        }

        static void Apply(string scene)
        {
            if (!s_BufDirty) { CabControls.Message(L.T("Değişiklik yok (N yumuşatır, G nokta taşır).", "Nothing changed (N smooths, G drags a point).")); return; }
            var snap = LineEdits.Snapshot(scene);
            int moved = LineEdits.Reshape(scene, s_SelT, s_SelA, s_SelB, ShapeAt);
            if (moved == 0) { CabControls.Message(L.T("Bu kısımda değiştirilebilecek nokta yok.", "No points to change on this stretch.")); return; }
            s_Undo.Add(snap);
            ClearSelection();
            CabControls.Message(L.T($"Ray yeniden şekillendi ({moved} nokta); kuruluyor... (Ctrl+Z geri alır)", $"Line reshaped ({moved} points); laying... (Ctrl+Z undoes)"));
            Rebuild();
        }

        static void CutSelection(string scene)
        {
            var snap = LineEdits.Snapshot(scene);
            int removed = LineEdits.Cut(scene, s_SelT, s_SelA, s_SelB);
            if (removed == 0) { CabControls.Message(L.T("Bu kısımda silinecek nokta yok (seçimi biraz uzat).", "Nothing to cut here (make the selection a little longer).")); return; }
            s_Undo.Add(snap);
            ClearSelection();
            CabControls.Message(L.T("Ray parçası silindi; kuruluyor... (Ctrl+Z geri alır)", "Stretch of line cut out; laying... (Ctrl+Z undoes)"));
            Rebuild();
        }

        static void DrawSelection()
        {
            if (s_SelT == null || s_SelT.Path == null) { HideSelection(); return; }
            if (s_SelB < 0f)
            {
                s_SelT.Path.Sample(s_SelA, out var point, out _);
                s_DragMark.Set(EditorHub.Ring(point, 1.5f, 24), SelCol, true);
                s_SelL.Hide(); s_SelR.Hide(); s_BufLine.Hide();
                return;
            }
            var points = new List<Vector3>(); var results = new List<Vector3>();
            for (float arcLength = s_SelA; arcLength <= s_SelB + 0.01f; arcLength += 1f)
            {
                s_SelT.Path.Sample(Mathf.Min(arcLength, s_SelB), out var point, out var t);
                var side = Vector3.Cross(Vector3.up, Flat(t)).normalized * (Gauge + 0.15f);
                points.Add(point - side + Vector3.up * 0.12f); results.Add(point + side + Vector3.up * 0.12f);
            }
            s_SelL.Set(points, SelCol); s_SelR.Set(results, SelCol);
            if (s_Buf != null && s_BufDirty)
            {
                var c = new List<Vector3>(s_Buf.Count);
                foreach (var point in s_Buf) c.Add(point + Vector3.up * 0.15f);
                s_BufLine.Set(c, ShapeCol);
            }
            else s_BufLine.Hide();
            if (s_Drag >= 0 && s_Buf != null) s_DragMark.Set(EditorHub.Ring(s_Buf[s_Drag], s_Reach, 48), ShapeCol, true);
            else s_DragMark.Hide();
        }

        // ------------------------------------------------------------------ drawing

        static void Draw()
        {
            // laid stretches of the line being drawn
            var laid = new List<Vector3>();
            foreach (var stretch in s_Done) foreach (var point in stretch.Pts) laid.Add(point + Vector3.up * 0.1f);
            if (laid.Count >= 2) s_Laid.Set(laid, LaidCol); else s_Laid.Hide();

            if (s_Preview == null || s_Preview.Count < 2) { s_RailL.Hide(); s_RailR.Hide(); if (s_Started) s_Mark.Hide(); return; }
            var points = new List<Vector3>(s_Preview.Count);
            var results = new List<Vector3>(s_Preview.Count);
            for (int index = 0; index < s_Preview.Count; index++)
            {
                var start = s_Preview[Mathf.Max(0, index - 1)]; var end = s_Preview[Mathf.Min(s_Preview.Count - 1, index + 1)];
                var side = Vector3.Cross(Vector3.up, Flat(end - start)).normalized * Gauge;
                var center = s_Preview[index] + Vector3.up * 0.08f;
                points.Add(center - side); results.Add(center + side);
            }
            var color = !s_Valid ? Bad : s_EndSnap != null ? Join : Good;
            s_RailL.Set(points, color); s_RailR.Set(results, color);
            if (s_EndSnap != null) s_Mark.Set(EditorHub.Ring(s_EndSnap.P, 1.2f, 24), Join); else s_Mark.Hide();
        }

        public static void OnGUI()
        {
            string keys = s_Started
                ? L.T("Sol tık döşe   Shift düz devam   PageUp/PageDown yükseklik   F dolgu   Backspace son parçayı geri al   X bitir\nCtrl+Z geri al   F11 sonraki mod",
                      "Left click lay   Shift straight on   PageUp/PageDown height   F embankment   Backspace take back   X finish\nCtrl+Z undo   F11 next mode")
                : s_SelT != null
                ? L.T("Sağ tık seçim ucu   Del seçili parçayı sil   N yumuşat   G nokta tut/bırak (tekerlek etki, PageUp/PageDown yükseklik)\nEnter uygula   X seçimi bırak   Ctrl+Z geri al   F11 sonraki mod",
                      "Right click selection end   Del cut the stretch   N smooth   G hold/let go a point (wheel reach, PageUp/PageDown height)\nEnter apply   X drop selection   Ctrl+Z undo   F11 next mode")
                : L.T("Sol tık çizmeye başla (hattın ucuna/ortasına tıklarsan ona bağlanır)   Sağ tık iki kez: kurulmuş raydan parça seç   T tünel ağzı\nPageUp/PageDown yükseklik   F dolgu   Del (iki kez) çizilmiş hattı sil   Ctrl+Z geri al   F11 sonraki mod",
                      "Left click start drawing (on a line's end/middle to join it)   Right click twice: select a stretch of a built line   T tunnel mouth\nPageUp/PageDown height   F embankment   Del (twice) delete a drawn line   Ctrl+Z undo   F11 next mode");
            EditorHub.Panel(L.T("RAY DÜZENLEYİCİ", "TRACK EDITOR"), s_Info, s_Warn, keys);
        }
    }
}
