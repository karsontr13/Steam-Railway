using System.Collections.Generic;
using Il2Cpp;
using Il2CppTLD.Interactions;
using UnityEngine;

namespace SteamRailway
{
    /// Moves, turns and removes objects of a region (stray rail cars, vehicles, rocks ...). The changes are kept
    /// in the mod's Edits files and every game gets them (see ObjectEdits).
    ///   F11  editor on / off                 Del  remove the object in the crosshair
    ///   G    pick up / put down              X    put back where it was picked up (while carrying)
    ///   wheel turn (Shift 15°, Ctrl 1°)      PageUp / PageDown  raise / lower (Shift: more)
    ///   [ ]  select the larger / smaller part   Home  object as the region made it   Ctrl+Z  undo
    internal static class ObjectEditor
    {
        const float Reach = 60f;

        static bool s_On => EditorHub.Mode == EditorMode.Objects;
        static Transform s_Hit;                 // what the crosshair touches
        static readonly List<Transform> s_Chain = new List<Transform>();   // the hit up to the largest object
        static int s_Level;                     // index into s_Chain
        static Transform s_Sel;
        static string s_Info = "", s_Warn = "";

        // carrying
        static Transform s_Carry;
        static EditRecord s_CarryBefore;
        static Vector3 s_CarryStartPos;
        static Quaternion s_CarryStartRot;
        static float s_Yaw, s_Lift, s_PivotAbove;
        static readonly List<Collider> s_CarryColliders = new List<Collider>();

        sealed class UndoItem { public Transform T; public InstancedHider.InstPick P; public EditRecord Before; }
        static readonly List<UndoItem> s_Undo = new List<UndoItem>();

        // objects drawn by GPU instancing with no scene object (the crosshair finds them by their mesh)
        static InstancedHider.InstPick s_Inst, s_InstCand, s_CarryInst;
        static float s_NextInst;

        static GUIStyle s_Style, s_Small;

        public static bool Active => s_On;

        /// The editor is switched to another mode: whatever is carried goes back.
        public static void Leave()
        {
            if (s_Carry != null || s_CarryInst != null) Drop(false);
        }

        public static void Update()
        {
            if (!s_On || RailwayManager.CurrentScene == null) return;

            if (s_Carry != null || s_CarryInst != null) { Carry(); return; }
            Pick();
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (ctrl && Input.GetKeyDown(KeyCode.Z)) { Undo(); return; }
            if (Input.GetKeyDown(KeyCode.LeftBracket) && s_Level < s_Chain.Count - 1) { s_Level++; Select(); }
            if (Input.GetKeyDown(KeyCode.RightBracket) && s_Level > 0) { s_Level--; Select(); }
            if (s_Sel == null && s_Inst == null) return;
            // copies made with C: Del deletes them, G moves them
            var copy = CopyOf(s_Sel);
            if (copy != null)
            {
                if (Input.GetKeyDown(KeyCode.Delete)) { ObjectEdits.RemoveCopy(copy); CabControls.Message(L.T("Kopya silindi.", "Copy deleted.")); s_Hit = null; return; }
                if (Input.GetKeyDown(KeyCode.G)) { CarryCopy(copy, false); return; }
                if (Input.GetKeyDown(KeyCode.C)) { var c2 = ObjectEdits.AddCopy(copy); if (c2 != null) CarryCopy(c2, true); return; }
                return;
            }
            if (Input.GetKeyDown(KeyCode.C) && s_Sel != null)
            {
                var c = ObjectEdits.AddCopy(s_Sel);
                if (c != null) CarryCopy(c, true);
                else CabControls.Message(L.T("Bu nesne kopyalanamadı.", "This object could not be copied."));
                return;
            }
            if (Input.GetKeyDown(KeyCode.Delete)) { if (s_Sel != null) Remove(s_Sel); else RemoveInst(s_Inst); }
            else if (Input.GetKeyDown(KeyCode.G)) { if (s_Sel != null) PickUp(s_Sel); else PickUpInst(s_Inst); }
            else if (Input.GetKeyDown(KeyCode.Home)) { if (s_Sel != null) Reset(s_Sel); else ResetInst(s_Inst); }
        }

        // ------------------------------------------------------------------ selecting

        static Transform Camera()
        {
            var cam = GameManager.GetVpFPSCamera();
            return cam != null ? cam.transform : null;
        }

        static bool Ray(out RaycastHit hit, Transform ignore)
        {
            hit = default;
            var cam = Camera();
            if (cam == null) return false;
            var hits = Physics.RaycastAll(cam.position, cam.forward, Reach, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            bool any = false;
            foreach (var h in hits)
            {
                var t = h.collider.transform;
                if (h.distance >= best || IsPlayer(t) || (ignore != null && t.IsChildOf(ignore))) continue;
                best = h.distance; hit = h; any = true;
            }
            return any;
        }

        static bool IsPlayer(Transform t)
        {
            for (var x = t; x != null; x = x.parent)
                if (x.name.StartsWith("CHARACTER_FPSPlayer") || x.name.StartsWith("SteamRailway") || (x.name.StartsWith("SR_") && !x.name.StartsWith("SR_Copy_"))) return true;
            return false;
        }

        static void Pick()
        {
            Transform t = null;
            float pd = float.MaxValue;
            if (Ray(out var hit, null) && !(hit.collider is TerrainCollider)) { t = hit.collider.transform; pd = hit.distance; }

            // an instanced object nearer than anything solid (it may have no collider: the player walks through it)
            if (Time.time >= s_NextInst)
            {
                s_NextInst = Time.time + 0.25f;
                var cam = Camera();
                s_InstCand = null;
                if (cam != null)
                {
                    try { s_InstCand = InstancedHider.Pick(new Ray(cam.position, cam.forward), Mathf.Min(Reach, pd + 0.5f)); }
                    catch (System.Exception e) { SteamRailwayMod.Debug($"Instance pick: {e.Message}"); }
                }
            }
            var ic = s_InstCand;
            if (ic != null && pd < float.MaxValue && ic.Dist > pd - 1f) ic = null;      // the solid object is the same thing or nearer
            if (ic != null)
            {
                if (s_Inst != null && s_Inst.Key == ic.Key && s_Hit == null && s_Sel == null) { s_Inst = ic; return; }
                s_Inst = ic; s_Hit = null; s_Sel = null; s_Chain.Clear(); s_Level = 0;
                var sz = ic.World.size;
                s_Info = $"{ic.Prefix}   ({sz.x:F1} x {sz.y:F1} x {sz.z:F1} m)  [GPU]";
                s_Warn = L.T("Toplu çizilen nesne: çarpışması olmayabilir", "Instanced object: it may have no collision");
                return;
            }
            if (s_Inst != null) { s_Inst = null; s_Hit = null; s_Sel = null; s_Info = ""; s_Warn = ""; }
            if (t == s_Hit) return;
            s_Hit = t;
            s_Chain.Clear();
            s_Level = 0;
            if (t != null)
            {
                // from the hit part up to the whole object; region groups (many children) are never taken
                Transform root = null;
                for (var x = t; x != null && x.parent != null; x = x.parent)
                {
                    if (x.childCount > 40) break;
                    s_Chain.Add(x);
                    if (x.name.Contains("_Prefab") || x.GetComponent<LODGroup>() != null) root = x;
                }
                if (root != null) s_Level = s_Chain.IndexOf(root);
            }
            Select();
        }

        static void Select()
        {
            // a collider on its own (the picture is a sibling): the part that really holds the meshes is taken
            while (s_Level >= 0 && s_Level < s_Chain.Count - 1 && NeedsParent(s_Chain[s_Level])) s_Level++;
            s_Sel = s_Level >= 0 && s_Level < s_Chain.Count ? s_Chain[s_Level] : null;
            s_Warn = "";
            if (s_Sel == null) { s_Info = ""; return; }
            if (s_Sel.GetComponentsInChildren<LoadScene>(true).Length > 0)
            {
                s_Info = s_Sel.name;
                s_Warn = L.T("Bölge geçişi: düzenlenemez", "Region transition: cannot be edited");
                s_Sel = null;
                return;
            }
            var b = Bounds(s_Sel);
            s_Info = $"{s_Sel.name}   ({b.size.x:F1} x {b.size.y:F1} x {b.size.z:F1} m)";
            if (s_Sel.GetComponentsInChildren<Container>(true).Length > 0 || s_Sel.GetComponentsInChildren<GearItem>(true).Length > 0)
                s_Warn = L.T("İçinde eşya var: kaldırılırsa ulaşılamaz", "Holds items: they cannot be reached once it is removed");
            else if (s_Sel.GetComponentsInChildren<BaseInteraction>(true).Length > 0)
                s_Warn = L.T("Etkileşimli nesne", "Interactive object");
            if (ObjectEdits.IsStatic(s_Sel)) s_Info += L.T("  [sabit model: taşınırken kopyası yapılır]", "  [static model: copied when moved]");
        }

        /// A part with nothing to see in it (and not an object the game draws by instancing).
        static bool NeedsParent(Transform t)
        {
            if (t.name.Contains("_Prefab") || t.GetComponent<LODGroup>() != null) return false;
            return t.GetComponentsInChildren<Renderer>(true).Length == 0;
        }

        static Bounds Bounds(Transform t)
        {
            bool any = false;
            var b = new Bounds(t.position, Vector3.zero);
            foreach (var r in t.GetComponentsInChildren<Renderer>(false))
            {
                if (!r.enabled) continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            if (!any)
                foreach (var c in t.GetComponentsInChildren<Collider>(false))
                {
                    if (!c.enabled) continue;
                    if (!any) { b = c.bounds; any = true; } else b.Encapsulate(c.bounds);
                }
            return b;
        }

        // ------------------------------------------------------------------ actions

        static void RemoveInst(InstancedHider.InstPick p)
        {
            s_Undo.Add(new UndoItem { P = p, Before = ObjectEdits.InstRecord(p) });
            ObjectEdits.SetInstance(p, ObjectEdits.InstHidden(p));
            CabControls.Message(L.T($"Kaldırıldı: {p.Prefix}  (Ctrl+Z geri alır)", $"Removed: {p.Prefix}  (Ctrl+Z undoes)"));
            s_Inst = null; s_InstCand = null;
        }

        static void ResetInst(InstancedHider.InstPick p)
        {
            var rec = ObjectEdits.InstRecord(p);
            if (rec == null) return;
            s_Undo.Add(new UndoItem { P = p, Before = rec });
            ObjectEdits.SetInstance(p, null);
            CabControls.Message(L.T($"Orijinal hâline döndü: {p.Prefix}", $"Back as the region made it: {p.Prefix}"));
        }

        static void PickUpInst(InstancedHider.InstPick p)
        {
            s_CarryInst = p;
            s_CarryBefore = ObjectEdits.InstRecord(p);
            s_CarryStartRot = p.Rot;
            s_Yaw = 0f; s_Lift = 0f;
            s_PivotAbove = Mathf.Max(0f, p.Pos.y - p.World.min.y);
            CabControls.Message(L.T("Taşınıyor: G bırakır, X iptal, tekerlek döndürür, PageUp/PageDown yükseklik.",
                                    "Carrying: G puts down, X cancels, wheel turns, PageUp/PageDown height."));
        }

        static void Remove(Transform t)
        {
            s_Undo.Add(new UndoItem { T = t, Before = ObjectEdits.RecordOf(t) });
            ObjectEdits.Set(t, ObjectEdits.Hidden(t));
            CabControls.Message(L.T($"Kaldırıldı: {t.name}  (Ctrl+Z geri alır)", $"Removed: {t.name}  (Ctrl+Z undoes)"));
            s_Hit = null;
        }

        static void Reset(Transform t)
        {
            if (ObjectEdits.RecordOf(t) == null) return;
            s_Undo.Add(new UndoItem { T = t, Before = ObjectEdits.RecordOf(t) });
            ObjectEdits.Set(t, null);
            CabControls.Message(L.T($"Orijinal hâline döndü: {t.name}", $"Back as the region made it: {t.name}"));
        }

        static void Undo()
        {
            if (s_Undo.Count == 0) { CabControls.Message(L.T("Geri alınacak bir şey yok.", "Nothing to undo.")); return; }
            var u = s_Undo[s_Undo.Count - 1];
            s_Undo.RemoveAt(s_Undo.Count - 1);
            if (u.P != null)
            {
                ObjectEdits.SetInstance(u.P, u.Before);
                CabControls.Message(L.T($"Geri alındı: {u.P.Prefix}", $"Undone: {u.P.Prefix}"));
                s_Inst = null; s_InstCand = null;
                return;
            }
            var t = u.T;
            if (t == null) return;
            ObjectEdits.Set(t, u.Before);
            CabControls.Message(L.T($"Geri alındı: {t.name}", $"Undone: {t.name}"));
            s_Hit = null;
        }

        /// Taking hold of something to carry (no edit of the original object behind it).
        static void PickUpCommon(Transform t)
        {
            s_Carry = t;
            s_CarryStartPos = t.position;
            s_CarryStartRot = t.rotation;
            s_Yaw = 0f; s_Lift = 0f;
            s_PivotAbove = t.position.y - Bounds(t).min.y;
            s_CarryColliders.Clear();
            foreach (var c in t.GetComponentsInChildren<Collider>(false)) if (c.enabled) { c.enabled = false; s_CarryColliders.Add(c); }
        }

        static void PickUp(Transform t)
        {
            s_Carry = t;
            s_CarryBefore = ObjectEdits.RecordOf(t);
            s_CarryStartPos = t.position;
            s_CarryStartRot = t.rotation;
            s_Yaw = 0f; s_Lift = 0f;
            s_PivotAbove = t.position.y - Bounds(t).min.y;
            ObjectEdits.BeginMove(t);
            // its own colliders must not catch the crosshair while it is carried
            s_CarryColliders.Clear();
            foreach (var c in t.GetComponentsInChildren<Collider>(false)) if (c.enabled) { c.enabled = false; s_CarryColliders.Add(c); }
            CabControls.Message(L.T("Taşınıyor: G bırakır, X iptal, tekerlek döndürür, PageUp/PageDown yükseklik.",
                                    "Carrying: G puts down, X cancels, wheel turns, PageUp/PageDown height."));
        }

        static void Carry()
        {
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            float wheel = Input.mouseScrollDelta.y;
            if (wheel != 0f) s_Yaw += Mathf.Sign(wheel) * (shift ? 15f : ctrl ? 1f : 5f);
            if (Input.GetKeyDown(KeyCode.PageUp)) s_Lift += shift ? 0.5f : 0.1f;
            if (Input.GetKeyDown(KeyCode.PageDown)) s_Lift -= shift ? 0.5f : 0.1f;
            if (Input.GetKeyDown(KeyCode.X)) { Drop(false); return; }
            if (Input.GetKeyDown(KeyCode.G)) { Drop(true); return; }

            var cam = Camera();
            if (cam == null) return;
            Vector3 point = Ray(out var hit, s_Carry) ? hit.point : cam.position + cam.forward * 15f;
            var pos = new Vector3(point.x, point.y + s_PivotAbove + s_Lift, point.z);
            var rot = Quaternion.Euler(0f, s_Yaw, 0f) * s_CarryStartRot;
            string cname;
            if (s_CarryInst != null) { ObjectEdits.PreviewInstance(s_CarryInst, pos, rot); s_CarryPos = pos; s_CarryRot = rot; cname = s_CarryInst.Prefix; }
            else if (s_CarryCopy != null) { s_Carry.SetPositionAndRotation(pos, rot); cname = s_Carry.name; }
            else { ObjectEdits.Preview(s_Carry, pos, rot); cname = s_Carry.name; }
            s_Info = $"{cname}   " + L.T($"dönüş {s_Yaw:F0}°  yükseklik {s_Lift:+0.0;-0.0} m", $"turn {s_Yaw:F0}°  height {s_Lift:+0.0;-0.0} m");
        }

        static Vector3 s_CarryPos;
        static Quaternion s_CarryRot;

        static Transform s_CarryCopy;
        static bool s_CarryCopyNew;

        /// The copy (made with C) a selected part belongs to.
        static Transform CopyOf(Transform t)
        {
            for (var x = t; x != null; x = x.parent) if (ObjectEdits.IsCopy(x)) return x;
            return null;
        }

        static void CarryCopy(Transform copy, bool isNew)
        {
            PickUpCommon(copy);
            s_CarryCopy = copy;
            s_CarryCopyNew = isNew;
            CabControls.Message(isNew ? L.T("Kopya oluşturuldu: G bırakır, X iptal, tekerlek döndürür, PageUp/PageDown yükseklik.", "Copy made: G puts down, X cancels, wheel turns, PageUp/PageDown height.")
                                      : L.T("Kopya taşınıyor: G bırakır, X iptal.", "Carrying the copy: G puts down, X cancels."));
        }

        static void Drop(bool keep)
        {
            if (s_CarryCopy != null)
            {
                var c = s_CarryCopy;
                s_CarryCopy = null;
                s_Carry = null;
                foreach (var col in s_CarryColliders) if (col != null) col.enabled = true;
                s_CarryColliders.Clear();
                if (keep) { ObjectEdits.MoveCopy(c); CabControls.Message(L.T("Kopya yerleştirildi.", "Copy placed.")); }
                else if (s_CarryCopyNew) { ObjectEdits.RemoveCopy(c); CabControls.Message(L.T("Kopya iptal edildi.", "Copy cancelled.")); }
                else { ObjectEdits.ResetCopy(c); CabControls.Message(L.T("İptal edildi.", "Cancelled.")); }
                s_Hit = null;
                return;
            }
            if (s_CarryInst != null)
            {
                var p = s_CarryInst;
                s_CarryInst = null;
                if (keep)
                {
                    s_Undo.Add(new UndoItem { P = p, Before = s_CarryBefore });
                    ObjectEdits.SetInstance(p, ObjectEdits.InstMoved(p, s_CarryPos, s_CarryRot));
                    CabControls.Message(L.T($"Yerleştirildi: {p.Prefix}", $"Placed: {p.Prefix}"));
                }
                else
                {
                    ObjectEdits.SetInstance(p, s_CarryBefore);
                    CabControls.Message(L.T("İptal edildi.", "Cancelled."));
                }
                s_Inst = null; s_InstCand = null;
                return;
            }
            var t = s_Carry;
            s_Carry = null;
            ObjectEdits.EndMove();
            foreach (var c in s_CarryColliders) if (c != null) c.enabled = true;
            s_CarryColliders.Clear();
            if (t == null) return;
            if (keep)
            {
                s_Undo.Add(new UndoItem { T = t, Before = s_CarryBefore });
                ObjectEdits.Set(t, ObjectEdits.Moved(t, t.position, t.rotation));
                CabControls.Message(L.T($"Yerleştirildi: {t.name}", $"Placed: {t.name}"));
            }
            else
            {
                ObjectEdits.Set(t, s_CarryBefore);
                CabControls.Message(L.T("İptal edildi.", "Cancelled."));
            }
            s_Hit = null;
        }

        // ------------------------------------------------------------------ on screen

        public static void OnGUI()
        {
            if (!s_On) return;
            if (s_Style == null)
            {
                s_Style = new GUIStyle(GUI.skin.label) { fontSize = 18, alignment = TextAnchor.UpperCenter };
                s_Style.normal.textColor = Color.white;
                s_Small = new GUIStyle(GUI.skin.label) { fontSize = 14, alignment = TextAnchor.UpperCenter };
                s_Small.normal.textColor = new Color(1f, 0.8f, 0.3f);
            }
            float w = Screen.width;
            GUI.Label(new Rect(w * 0.5f - 12f, Screen.height * 0.5f - 14f, 24f, 28f), "+", s_Style);
            string head = s_Carry != null ? s_Info : (s_Sel != null || s_Warn != "" ? s_Info : L.T("(nesne yok)", "(no object)"));
            GUI.Label(new Rect(0f, 30f, w, 28f), L.T("NESNE DÜZENLEYİCİ  ", "OBJECT EDITOR  ") + head, s_Style);
            if (s_Warn != "") GUI.Label(new Rect(0f, 58f, w, 24f), s_Warn, s_Small);
            string keys = s_Carry != null
                ? L.T("G bırak   X iptal   tekerlek döndür (Shift 15°, Ctrl 1°)   PageUp/PageDown yükseklik",
                      "G put down   X cancel   wheel turn (Shift 15°, Ctrl 1°)   PageUp/PageDown height")
                : L.T("Del kaldır   G taşı   C kopyala   [ ] büyük/küçük parça   Home orijinal   Ctrl+Z geri al   F11 sonraki mod",
                      "Del remove   G move   C copy   [ ] larger/smaller part   Home original   Ctrl+Z undo   F11 next mode");
            GUI.Label(new Rect(0f, Screen.height - 60f, w, 24f), keys, s_Small);
        }
    }
}
