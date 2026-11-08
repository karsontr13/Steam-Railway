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
                var p = snap != null ? snap.P : hit + Vector3.up * (RailAbove + s_Lift);
                s_Mark.Set(EditorHub.Ring(p, 1.2f, 24), snap != null ? Join : Good);
                s_Info = snap == null ? L.T("Başlangıç için sol tık", "Left click to start")
                       : snap.Kind == SnapKind.End ? L.T("Hattın ucundan devam (sol tık)", "Carry on from the line's end (left click)")
                       : L.T("Hattan makasla ayrıl (sol tık)", "Branch off the line with a switch (left click)");
                s_Warn = "";
                if (Input.GetMouseButtonDown(0)) Begin(p, snap);
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
            Snap best = null; float bd = SnapDist;
            foreach (var t in RailwayManager.Tracks)
            {
                if (t?.Path == null) continue;
                float s = t.Path.NearestDistance(hit, out float d);
                if (d >= bd) continue;
                t.Path.Sample(s, out var p, out var tan);
                tan.y = 0f; tan.Normalize();
                // the line we started from does not catch the crosshair near the start
                if (s_Started && s_StartSnap != null && s_StartSnap.Track == t && (p - s_Start).magnitude < 12f) continue;
                var sn = new Snap { Track = t, P = p, Tan = tan };
                float len = t.Path.Length;
                if (s < EndZone || s > len - EndZone)
                {
                    sn.Kind = SnapKind.End;
                    bool atEnd = s > len * 0.5f;
                    sn.P = atEnd ? t.Path[t.Path.Count - 1] : t.Path[0];
                    sn.Out = atEnd ? tan : -tan;
                }
                else sn.Kind = SnapKind.Middle;
                best = sn; bd = d;
            }
            return best;
        }

        static void Begin(Vector3 p, Snap snap)
        {
            s_Started = true;
            s_Start = p;
            s_StartSnap = snap;
            s_Done.Clear();
            if (snap == null) { s_Tan = null; s_TanFree = false; }
            else if (snap.Kind == SnapKind.End) { s_Tan = snap.Out; s_TanFree = false; }
            else { s_Tan = snap.Tan; s_TanFree = true; }
        }

        // ------------------------------------------------------------------ the stretch under the crosshair

        static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

        static void Preview(Vector3 hit, Snap snap)
        {
            s_EndSnap = snap;
            var p0 = s_Start;
            Vector3 p1;
            Vector3? t1 = null;
            Vector3 chord;
            if (snap != null)
            {
                p1 = snap.P;
                chord = Flat(p1 - p0).normalized;
                if (snap.Kind == SnapKind.End) t1 = -snap.Out;                         // into the line's end
                else t1 = snap.Tan * (Vector3.Dot(snap.Tan, chord) >= 0f ? 1f : -1f);   // along the line
            }
            else
            {
                p1 = hit + Vector3.up * (RailAbove + s_Lift);
                chord = Flat(p1 - p0).normalized;
                if (EditorHub.Shift && s_Tan.HasValue)
                {
                    // straight on: the crosshair slides along the start direction
                    var t = s_Tan.Value;
                    if (s_TanFree && Vector3.Dot(t, chord) < 0f) t = -t;
                    float along = Vector3.Dot(Flat(p1 - p0), t);
                    var q = p0 + t * Mathf.Max(0f, along);
                    q.y = H.TerrainHeight(q, p1.y) + RailAbove + s_Lift;
                    p1 = q;
                    chord = Flat(p1 - p0).normalized;
                }
            }
            float len = Flat(p1 - p0).magnitude;
            Vector3 t0 = chord;
            if (s_Tan.HasValue)
            {
                t0 = s_Tan.Value;
                if (s_TanFree && Vector3.Dot(t0, chord) < 0f) t0 = -t0;
            }
            Vector3 te = t1 ?? (s_Tan.HasValue ? 2f * Vector3.Dot(t0, chord) * chord - t0 : chord);   // a circular arc
            te = Flat(te).normalized;
            s_PrevEndTan = te;

            s_Preview = Curve(p0, p1, t0, te, len, 1f);
            // checks
            s_Valid = true; s_Warn = "";
            float r = MinRadiusOf(s_Preview);
            float grade = len > 0.1f ? (p1.y - p0.y) / len * 100f : 0f;
            if (len < 3f) { s_Valid = false; s_Warn = L.T("çok kısa", "too short"); }
            else if (len > MaxStretch) { s_Valid = false; s_Warn = L.T($"bir parça en çok {MaxStretch:F0} m", $"a stretch is at most {MaxStretch:F0} m"); }
            else if (r < MinRadius) { s_Valid = false; s_Warn = L.T($"viraj çok keskin (en az {MinRadius:F0} m yarıçap)", $"bend too sharp (at least {MinRadius:F0} m radius)"); }
            else if (Mathf.Abs(grade) > MaxGrade) { s_Valid = false; s_Warn = L.T($"eğim çok dik (en çok %{MaxGrade:F0}; PageUp/PageDown ile yüksekliği ayarla)", $"too steep (at most {MaxGrade:F0}%; set the height with PageUp/PageDown)"); }
            else if (Mathf.Abs(grade) > SteepGrade) s_Warn = L.T("dik eğim: ağır tren zorlanabilir", "steep: a heavy train may struggle");

            string rad = r > 5000f ? L.T("düz", "straight") : $"{r:F0} m";
            s_Info = L.T($"uzunluk {len:F0} m   yarıçap {rad}   eğim %{grade:F1}   yükseklik {s_Lift:+0.00;-0.00} m   dolgu {(s_Fill ? "açık" : "kapalı")}",
                         $"length {len:F0} m   radius {rad}   grade {grade:F1}%   height {s_Lift:+0.00;-0.00} m   embankment {(s_Fill ? "on" : "off")}");
            if (snap != null)
                s_Info += snap.Kind == SnapKind.End ? L.T("   → hattın ucuna bağlanır", "   → joins the line's end")
                                                    : L.T("   → makasla bağlanır", "   → joins with a switch");
        }

        /// Hermite curve in the ground plane, the height rising evenly (a steady grade).
        static List<Vector3> Curve(Vector3 p0, Vector3 p1, Vector3 t0, Vector3 t1, float len, float step)
        {
            int n = Mathf.Max(2, Mathf.CeilToInt(len / step) + 1);
            var res = new List<Vector3>(n);
            var a = Flat(p0); var b = Flat(p1);
            var m0 = t0 * len; var m1 = t1 * len;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)(n - 1), t2 = t * t, t3 = t2 * t;
                var q = (2f * t3 - 3f * t2 + 1f) * a + (t3 - 2f * t2 + t) * m0 + (-2f * t3 + 3f * t2) * b + (t3 - t2) * m1;
                q.y = Mathf.Lerp(p0.y, p1.y, t);
                res.Add(q);
            }
            return res;
        }

        static float MinRadiusOf(List<Vector3> p)
        {
            float best = float.MaxValue;
            const int k = 3;
            for (int i = k; i + k < p.Count; i++)
            {
                var a = Flat(p[i - k]); var b = Flat(p[i]); var c = Flat(p[i + k]);
                float area2 = Vector3.Cross(b - a, c - a).magnitude;
                if (area2 < 1e-5f) continue;
                float r = (b - a).magnitude * (c - b).magnitude * (c - a).magnitude / (2f * area2);
                best = Mathf.Min(best, r);
            }
            return best;
        }

        // ------------------------------------------------------------------ laying, finishing

        static void Lay(string scene)
        {
            var pts = Resample(s_Preview, Step);
            s_Done.Add(new Stretch { Pts = pts, PrevStart = s_Start, PrevTan = s_Tan, PrevTanFree = s_TanFree, PrevStartSnap = s_StartSnap });
            var end = s_Preview[s_Preview.Count - 1];
            var endSnap = s_EndSnap;
            s_Start = end;
            s_Tan = s_PrevEndTan;
            s_TanFree = false;
            if (endSnap != null) Finish(scene, endSnap);   // it ran into another line: the line is complete
        }

        static List<Vector3> Resample(List<Vector3> src, float step)
        {
            var res = new List<Vector3> { src[0] };
            float acc = 0f;
            for (int i = 1; i < src.Count; i++)
            {
                acc += Vector3.Distance(src[i - 1], src[i]);
                if (acc >= step || i == src.Count - 1) { res.Add(src[i]); acc = 0f; }
            }
            return res;
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
            foreach (var st in s_Done)
                for (int i = 0; i < st.Pts.Count; i++)
                {
                    if (data.Points.Count > 0 && i == 0) continue;
                    data.Points.Add(CustomLines.F(st.Pts[i]));
                }
            var first = s_Done[0];
            if (first.PrevStartSnap != null && first.PrevStartSnap.Kind == SnapKind.Middle) data.Switches.Add(data.Points[0]);
            if (endSnap != null && endSnap.Kind == SnapKind.Middle) data.Switches.Add(data.Points[data.Points.Count - 1]);
            s_Undo.Add(LineEdits.Snapshot(scene));
            CustomLines.Add(scene, data);
            float length = 0f;
            for (int i = 1; i < data.Points.Count; i++) length += Vector3.Distance(CustomLines.V(data.Points[i - 1]), CustomLines.V(data.Points[i]));
            Reset();
            CabControls.Message(L.T($"Hat kaydedildi ({length:F0} m); raylar kuruluyor...", $"Line saved ({length:F0} m); laying the rails..."));
            Rebuild();
        }

        /// T on a line: a tunnel mouth there (the tunnel runs on away from you); T at a placed tunnel removes it.
        static void Tunnel(string scene, Vector3 hit)
        {
            if (Tunnels.RemoveNear(scene, hit, 15f)) { CabControls.Message(L.T("Tünel kaldırıldı.", "Tunnel removed.")); return; }
            if (!LineAt(hit, out var t, out float s)) { CabControls.Message(L.T("Tünel ağzı için nişangâhı rayın üstüne getir.", "Aim at the line where the tunnel mouth should be.")); return; }
            var cam = EditorHub.Camera();
            CabControls.Message(Tunnels.PlaceAt(scene, t, s, cam != null ? cam.position : hit));
        }

        /// P on a line: one end of a rail transition (first here, then in the other region); Shift+P deletes one.
        static void Portal(string scene, Vector3 hit)
        {
            if (EditorHub.Shift) { CabControls.Message(CustomPortals.RemoveNear(scene, hit)); return; }
            if (!LineAt(hit, out var t, out float s)) { CabControls.Message(L.T("Geçiş için nişangâhı rayın üstüne getir.", "Aim at the line for the transition.")); return; }
            t.Path.Sample(s, out var p, out var tan);
            tan.y = 0f; tan.Normalize();
            var cam = EditorHub.Camera();
            var away = cam != null && Vector3.Dot(tan, p - cam.position) < 0f ? -tan : tan;
            CabControls.Message(CustomPortals.Mark(scene, p, away));
        }

        static void Delete(string scene, Vector3 hit)
        {
            var l = CustomLines.Nearest(scene, hit, SnapDist);
            if (l == null) { CabControls.Message(L.T("Burada çizilmiş bir hat yok (bölgenin kendi hatları silinemez).", "No drawn line here (the region's own lines cannot be deleted).")); return; }
            if (s_DelId != l.Id || Time.time > s_DelUntil)
            {
                s_DelId = l.Id; s_DelUntil = Time.time + 3f;
                CabControls.Message(L.T($"{l.Id} silinsin mi? Onay için Del'e tekrar bas.", $"Delete {l.Id}? Press Del again to confirm."));
                return;
            }
            s_DelId = null;
            s_Undo.Add(LineEdits.Snapshot(scene));
            CustomLines.Remove(scene, l.Id);
            CabControls.Message(L.T($"{l.Id} silindi (Ctrl+Z geri getirir).", $"{l.Id} deleted (Ctrl+Z brings it back)."));
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
        static bool LineAt(Vector3 hit, out BuiltTrack track, out float s)
        {
            // aimed straight at one of our rails: that line (where lines cross, the one actually aimed at)
            var cam = EditorHub.Camera();
            if (cam != null)
            {
                float bestHit = float.MaxValue; BuiltTrack aimed = null; Vector3 at = Vector3.zero;
                foreach (var h in Physics.RaycastAll(cam.position, cam.forward, 400f, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (h.distance >= bestHit) continue;
                    for (var x = h.collider.transform; x != null; x = x.parent)
                    {
                        if (x.name.StartsWith("CHARACTER_FPSPlayer")) break;
                        if (!x.name.StartsWith("SteamRailway_Track_")) continue;
                        string id = x.name.Substring("SteamRailway_Track_".Length);
                        foreach (var t in RailwayManager.Tracks) if (t != null && t.Id == id) { aimed = t; bestHit = h.distance; at = h.point; }
                        break;
                    }
                }
                if (aimed != null)
                {
                    // nothing solid in front of the rail?
                    if (!EditorHub.Aim(out var ground) || (ground - cam.position).magnitude + 0.5f >= bestHit)
                    {
                        track = aimed;
                        s = aimed.Path.NearestDistance3D(at, out _);
                        return true;
                    }
                }
            }
            track = null; s = 0f; float bd = SnapDist;
            foreach (var t in RailwayManager.Tracks)
            {
                if (t?.Path == null) continue;
                float ss = t.Path.NearestDistance(hit, out float d);
                if (d < bd) { bd = d; track = t; s = ss; }
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
                if (!aimed || !LineAt(hit, out var t, out float s))
                {
                    CabControls.Message(L.T("Nişangâhta ray yok.", "No line in the crosshair."));
                    return s_SelT != null;
                }
                if (s_SelT == null || s_SelB >= 0f || t != s_SelT)
                {
                    ClearSelection();
                    s_SelT = t; s_SelA = s;
                }
                else
                {
                    s_SelB = s;
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

            float len = s_SelB - s_SelA;
            if (s_Drag >= 0) { Drag(aimed, hit); }
            else
            {
                if (Input.GetKeyDown(KeyCode.Delete)) { CutSelection(scene); return true; }
                if (Input.GetKeyDown(KeyCode.N)) { Smooth(); }
                if (Input.GetKeyDown(KeyCode.G) && aimed) StartDrag(hit);
                if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) { Apply(scene); return true; }
            }
            float r = MinRadiusOf(s_Buf);
            string rad = r > 5000f ? L.T("düz", "straight") : $"{r:F0} m";
            s_Info = L.T($"seçili {len:F0} m   en dar viraj {rad}", $"selected {len:F0} m   tightest bend {rad}")
                   + (s_BufDirty ? L.T("   (değişti: Enter uygular)", "   (changed: Enter applies)") : "")
                   + (s_Drag >= 0 ? L.T($"   etki {s_Reach:F0} m   yükseklik {s_DragLift:+0.00;-0.00} m", $"   reach {s_Reach:F0} m   height {s_DragLift:+0.00;-0.00} m") : "");
            s_Warn = r < MinRadius ? L.T($"viraj {MinRadius:F0} m'den dar: tren sarsılabilir (N ile yumuşat)", $"bend tighter than {MinRadius:F0} m: the train may lurch (N smooths)") : "";
            return true;
        }

        static void MakeBuffer()
        {
            s_Buf = new List<Vector3>();
            int n = Mathf.Max(2, Mathf.CeilToInt((s_SelB - s_SelA) / BufStep) + 1);
            for (int i = 0; i < n; i++)
            {
                s_SelT.Path.Sample(Mathf.Lerp(s_SelA, s_SelB, i / (float)(n - 1)), out var p, out _);
                s_Buf.Add(p);
            }
            s_BufDirty = false;
        }

        /// The new shape at path metres s (between the selection's ends).
        static Vector3 ShapeAt(float s)
        {
            int n = s_Buf.Count;
            float f = (s - s_SelA) / Mathf.Max(1e-3f, s_SelB - s_SelA) * (n - 1);
            int i = Mathf.Clamp(Mathf.FloorToInt(f), 0, n - 2);
            return Vector3.Lerp(s_Buf[i], s_Buf[i + 1], Mathf.Clamp01(f - i));
        }

        /// Softer bends and an even grade; the two ends (and the direction there) stay.
        static void Smooth()
        {
            if (s_Buf == null || s_Buf.Count < 6) return;
            var tmp = new Vector3[s_Buf.Count];
            for (int it = 0; it < 8; it++)
            {
                for (int i = 0; i < s_Buf.Count; i++)
                    tmp[i] = (i < 2 || i > s_Buf.Count - 3) ? s_Buf[i] : Vector3.Lerp(s_Buf[i], (s_Buf[i - 1] + s_Buf[i + 1]) * 0.5f, 0.5f);
                for (int i = 0; i < s_Buf.Count; i++) s_Buf[i] = tmp[i];
            }
            s_BufDirty = true;
        }

        static void StartDrag(Vector3 hit)
        {
            int best = -1; float bd = 8f * 8f;
            for (int i = 0; i < s_Buf.Count; i++)
            {
                float dx = s_Buf[i].x - hit.x, dz = s_Buf[i].z - hit.z, d = dx * dx + dz * dz;
                if (d < bd) { bd = d; best = i; }
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
            var b = s_BufBase[s_Drag];
            var target = new Vector3(hit.x, b.y + s_DragLift, hit.z);
            var delta = target - b;
            int n = s_Buf.Count;
            float sk = s_Drag * BufStep;
            for (int i = 0; i < n; i++)
            {
                float si = i * BufStep;
                float w = Mathf.SmoothStep(0f, 1f, 1f - Mathf.Abs(si - sk) / s_Reach);
                // the ends of the selection stay where they are (the line carries on there)
                w *= Mathf.Clamp01(Mathf.Min(si, (n - 1) * BufStep - si) / 6f);
                s_Buf[i] = s_BufBase[i] + delta * w;
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
                s_SelT.Path.Sample(s_SelA, out var p, out _);
                s_DragMark.Set(EditorHub.Ring(p, 1.5f, 24), SelCol, true);
                s_SelL.Hide(); s_SelR.Hide(); s_BufLine.Hide();
                return;
            }
            var l = new List<Vector3>(); var r = new List<Vector3>();
            for (float s = s_SelA; s <= s_SelB + 0.01f; s += 1f)
            {
                s_SelT.Path.Sample(Mathf.Min(s, s_SelB), out var p, out var t);
                var side = Vector3.Cross(Vector3.up, Flat(t)).normalized * (Gauge + 0.15f);
                l.Add(p - side + Vector3.up * 0.12f); r.Add(p + side + Vector3.up * 0.12f);
            }
            s_SelL.Set(l, SelCol); s_SelR.Set(r, SelCol);
            if (s_Buf != null && s_BufDirty)
            {
                var c = new List<Vector3>(s_Buf.Count);
                foreach (var p in s_Buf) c.Add(p + Vector3.up * 0.15f);
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
            foreach (var st in s_Done) foreach (var p in st.Pts) laid.Add(p + Vector3.up * 0.1f);
            if (laid.Count >= 2) s_Laid.Set(laid, LaidCol); else s_Laid.Hide();

            if (s_Preview == null || s_Preview.Count < 2) { s_RailL.Hide(); s_RailR.Hide(); if (s_Started) s_Mark.Hide(); return; }
            var l = new List<Vector3>(s_Preview.Count);
            var r = new List<Vector3>(s_Preview.Count);
            for (int i = 0; i < s_Preview.Count; i++)
            {
                var a = s_Preview[Mathf.Max(0, i - 1)]; var b = s_Preview[Mathf.Min(s_Preview.Count - 1, i + 1)];
                var side = Vector3.Cross(Vector3.up, Flat(b - a)).normalized * Gauge;
                var c = s_Preview[i] + Vector3.up * 0.08f;
                l.Add(c - side); r.Add(c + side);
            }
            var col = !s_Valid ? Bad : s_EndSnap != null ? Join : Good;
            s_RailL.Set(l, col); s_RailR.Set(r, col);
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
