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
                if (Input.GetKeyDown(KeyCode.C)) { var otherChild = ObjectEdits.AddCopy(copy); if (otherChild != null) CarryCopy(otherChild, true); return; }
                return;
            }
            if (Input.GetKeyDown(KeyCode.C) && s_Sel != null)
            {
                var child = ObjectEdits.AddCopy(s_Sel);
                if (child != null) CarryCopy(child, true);
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
            var firstPersonCamera = GameManager.GetVpFPSCamera();
            return firstPersonCamera != null ? firstPersonCamera.transform : null;
        }

        static bool Ray(out RaycastHit hit, Transform ignore)
        {
            hit = default;
            var cameraTransform = Camera();
            if (cameraTransform == null) return false;
            var hits = Physics.RaycastAll(cameraTransform.position, cameraTransform.forward, Reach, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            bool any = false;
            foreach (var h in hits)
            {
                var hitTransform = h.collider.transform;
                if (h.distance >= best || IsPlayer(hitTransform) || (ignore != null && hitTransform.IsChildOf(ignore))) continue;
                best = h.distance; hit = h; any = true;
            }
            return any;
        }

        static bool IsPlayer(Transform transform)
        {
            for (var node = transform; node != null; node = node.parent)
                if (node.name.StartsWith("CHARACTER_FPSPlayer") || node.name.StartsWith("SteamRailway") || (node.name.StartsWith("SR_") && !node.name.StartsWith("SR_Copy_"))) return true;
            return false;
        }

        static void Pick()
        {
            Transform hitTransform = null;
            float progressDelta = float.MaxValue;
            if (Ray(out var hit, null) && !(hit.collider is TerrainCollider)) { hitTransform = hit.collider.transform; progressDelta = hit.distance; }

            // an instanced object nearer than anything solid (it may have no collider: the player walks through it)
            if (Time.time >= s_NextInst)
            {
                s_NextInst = Time.time + 0.25f;
                var cameraTransform = Camera();
                s_InstCand = null;
                if (cameraTransform != null)
                {
                    try { s_InstCand = InstancedHider.Pick(new Ray(cameraTransform.position, cameraTransform.forward), Mathf.Min(Reach, progressDelta + 0.5f)); }
                    catch (System.Exception e) { SteamRailwayMod.Debug($"Instance pick: {e.Message}"); }
                }
            }
            var instanceCopyPick = s_InstCand;
            if (instanceCopyPick != null && progressDelta < float.MaxValue && instanceCopyPick.Dist > progressDelta - 1f) instanceCopyPick = null;      // the solid object is the same thing or nearer
            if (instanceCopyPick != null)
            {
                if (s_Inst != null && s_Inst.Key == instanceCopyPick.Key && s_Hit == null && s_Sel == null) { s_Inst = instanceCopyPick; return; }
                s_Inst = instanceCopyPick; s_Hit = null; s_Sel = null; s_Chain.Clear(); s_Level = 0;
                var sz = instanceCopyPick.World.size;
                s_Info = $"{instanceCopyPick.Prefix}   ({sz.x:F1} x {sz.y:F1} x {sz.z:F1} m)  [GPU]";
                s_Warn = L.T("Toplu çizilen nesne: çarpışması olmayabilir", "Instanced object: it may have no collision");
                return;
            }
            if (s_Inst != null) { s_Inst = null; s_Hit = null; s_Sel = null; s_Info = ""; s_Warn = ""; }
            if (hitTransform == s_Hit) return;
            s_Hit = hitTransform;
            s_Chain.Clear();
            s_Level = 0;
            if (hitTransform != null)
            {
                // from the hit part up to the whole object; region groups (many children) are never taken
                Transform root = null;
                for (var transform = hitTransform; transform != null && transform.parent != null; transform = transform.parent)
                {
                    if (transform.childCount > 40) break;
                    s_Chain.Add(transform);
                    if (transform.name.Contains("_Prefab") || transform.GetComponent<LODGroup>() != null) root = transform;
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
            var bounds = Bounds(s_Sel);
            s_Info = $"{s_Sel.name}   ({bounds.size.x:F1} x {bounds.size.y:F1} x {bounds.size.z:F1} m)";
            if (s_Sel.GetComponentsInChildren<Container>(true).Length > 0 || s_Sel.GetComponentsInChildren<GearItem>(true).Length > 0)
                s_Warn = L.T("İçinde eşya var: kaldırılırsa ulaşılamaz", "Holds items: they cannot be reached once it is removed");
            else if (s_Sel.GetComponentsInChildren<BaseInteraction>(true).Length > 0)
                s_Warn = L.T("Etkileşimli nesne", "Interactive object");
            if (ObjectEdits.IsStatic(s_Sel)) s_Info += L.T("  [sabit model: taşınırken kopyası yapılır]", "  [static model: copied when moved]");
        }

        /// A part with nothing to see in it (and not an object the game draws by instancing).
        static bool NeedsParent(Transform transform)
        {
            if (transform.name.Contains("_Prefab") || transform.GetComponent<LODGroup>() != null) return false;
            return transform.GetComponentsInChildren<Renderer>(true).Length == 0;
        }

        static Bounds Bounds(Transform transform)
        {
            bool any = false;
            var otherBounds = new Bounds(transform.position, Vector3.zero);
            foreach (var renderer in transform.GetComponentsInChildren<Renderer>(false))
            {
                if (!renderer.enabled) continue;
                if (!any) { otherBounds = renderer.bounds; any = true; } else otherBounds.Encapsulate(renderer.bounds);
            }
            if (!any)
                foreach (var collider in transform.GetComponentsInChildren<Collider>(false))
                {
                    if (!collider.enabled) continue;
                    if (!any) { otherBounds = collider.bounds; any = true; } else otherBounds.Encapsulate(collider.bounds);
                }
            return otherBounds;
        }

        // ------------------------------------------------------------------ actions

        static void RemoveInst(InstancedHider.InstPick instancePick)
        {
            s_Undo.Add(new UndoItem { P = instancePick, Before = ObjectEdits.InstRecord(instancePick) });
            ObjectEdits.SetInstance(instancePick, ObjectEdits.InstHidden(instancePick));
            CabControls.Message(L.T($"Kaldırıldı: {instancePick.Prefix}  (Ctrl+Z geri alır)", $"Removed: {instancePick.Prefix}  (Ctrl+Z undoes)"));
            s_Inst = null; s_InstCand = null;
        }

        static void ResetInst(InstancedHider.InstPick instancePick)
        {
            var editRecord = ObjectEdits.InstRecord(instancePick);
            if (editRecord == null) return;
            s_Undo.Add(new UndoItem { P = instancePick, Before = editRecord });
            ObjectEdits.SetInstance(instancePick, null);
            CabControls.Message(L.T($"Orijinal hâline döndü: {instancePick.Prefix}", $"Back as the region made it: {instancePick.Prefix}"));
        }

        static void PickUpInst(InstancedHider.InstPick instancePick)
        {
            s_CarryInst = instancePick;
            s_CarryBefore = ObjectEdits.InstRecord(instancePick);
            s_CarryStartRot = instancePick.Rot;
            s_Yaw = 0f; s_Lift = 0f;
            s_PivotAbove = Mathf.Max(0f, instancePick.Pos.y - instancePick.World.min.y);
            CabControls.Message(L.T("Taşınıyor: G bırakır, X iptal, tekerlek döndürür, PageUp/PageDown yükseklik.",
                                    "Carrying: G puts down, X cancels, wheel turns, PageUp/PageDown height."));
        }

        static void Remove(Transform transform)
        {
            s_Undo.Add(new UndoItem { T = transform, Before = ObjectEdits.RecordOf(transform) });
            ObjectEdits.Set(transform, ObjectEdits.Hidden(transform));
            CabControls.Message(L.T($"Kaldırıldı: {transform.name}  (Ctrl+Z geri alır)", $"Removed: {transform.name}  (Ctrl+Z undoes)"));
            s_Hit = null;
        }

        static void Reset(Transform transform)
        {
            if (ObjectEdits.RecordOf(transform) == null) return;
            s_Undo.Add(new UndoItem { T = transform, Before = ObjectEdits.RecordOf(transform) });
            ObjectEdits.Set(transform, null);
            CabControls.Message(L.T($"Orijinal hâline döndü: {transform.name}", $"Back as the region made it: {transform.name}"));
        }

        static void Undo()
        {
            if (s_Undo.Count == 0) { CabControls.Message(L.T("Geri alınacak bir şey yok.", "Nothing to undo.")); return; }
            var undoItem = s_Undo[s_Undo.Count - 1];
            s_Undo.RemoveAt(s_Undo.Count - 1);
            if (undoItem.P != null)
            {
                ObjectEdits.SetInstance(undoItem.P, undoItem.Before);
                CabControls.Message(L.T($"Geri alındı: {undoItem.P.Prefix}", $"Undone: {undoItem.P.Prefix}"));
                s_Inst = null; s_InstCand = null;
                return;
            }
            var transform = undoItem.T;
            if (transform == null) return;
            ObjectEdits.Set(transform, undoItem.Before);
            CabControls.Message(L.T($"Geri alındı: {transform.name}", $"Undone: {transform.name}"));
            s_Hit = null;
        }

        /// Taking hold of something to carry (no edit of the original object behind it).
        static void PickUpCommon(Transform transform)
        {
            s_Carry = transform;
            s_CarryStartPos = transform.position;
            s_CarryStartRot = transform.rotation;
            s_Yaw = 0f; s_Lift = 0f;
            s_PivotAbove = transform.position.y - Bounds(transform).min.y;
            s_CarryColliders.Clear();
            foreach (var collider in transform.GetComponentsInChildren<Collider>(false)) if (collider.enabled) { collider.enabled = false; s_CarryColliders.Add(collider); }
        }

        static void PickUp(Transform transform)
        {
            s_Carry = transform;
            s_CarryBefore = ObjectEdits.RecordOf(transform);
            s_CarryStartPos = transform.position;
            s_CarryStartRot = transform.rotation;
            s_Yaw = 0f; s_Lift = 0f;
            s_PivotAbove = transform.position.y - Bounds(transform).min.y;
            ObjectEdits.BeginMove(transform);
            // its own colliders must not catch the crosshair while it is carried
            s_CarryColliders.Clear();
            foreach (var collider in transform.GetComponentsInChildren<Collider>(false)) if (collider.enabled) { collider.enabled = false; s_CarryColliders.Add(collider); }
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

            var cameraTransform = Camera();
            if (cameraTransform == null) return;
            Vector3 point = Ray(out var hit, s_Carry) ? hit.point : cameraTransform.position + cameraTransform.forward * 15f;
            var pos = new Vector3(point.x, point.y + s_PivotAbove + s_Lift, point.z);
            var rotation = Quaternion.Euler(0f, s_Yaw, 0f) * s_CarryStartRot;
            string cname;
            if (s_CarryInst != null) { ObjectEdits.PreviewInstance(s_CarryInst, pos, rotation); s_CarryPos = pos; s_CarryRot = rotation; cname = s_CarryInst.Prefix; }
            else if (s_CarryCopy != null) { s_Carry.SetPositionAndRotation(pos, rotation); cname = s_Carry.name; }
            else { ObjectEdits.Preview(s_Carry, pos, rotation); cname = s_Carry.name; }
            s_Info = $"{cname}   " + L.T($"dönüş {s_Yaw:F0}°  yükseklik {s_Lift:+0.0;-0.0} m", $"turn {s_Yaw:F0}°  height {s_Lift:+0.0;-0.0} m");
        }

        static Vector3 s_CarryPos;
        static Quaternion s_CarryRot;

        static Transform s_CarryCopy;
        static bool s_CarryCopyNew;

        /// The copy (made with C) a selected part belongs to.
        static Transform CopyOf(Transform transform)
        {
            for (var node = transform; node != null; node = node.parent) if (ObjectEdits.IsCopy(node)) return node;
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
                var child = s_CarryCopy;
                s_CarryCopy = null;
                s_Carry = null;
                foreach (var collider in s_CarryColliders) if (collider != null) collider.enabled = true;
                s_CarryColliders.Clear();
                if (keep) { ObjectEdits.MoveCopy(child); CabControls.Message(L.T("Kopya yerleştirildi.", "Copy placed.")); }
                else if (s_CarryCopyNew) { ObjectEdits.RemoveCopy(child); CabControls.Message(L.T("Kopya iptal edildi.", "Copy cancelled.")); }
                else { ObjectEdits.ResetCopy(child); CabControls.Message(L.T("İptal edildi.", "Cancelled.")); }
                s_Hit = null;
                return;
            }
            if (s_CarryInst != null)
            {
                var instancePick = s_CarryInst;
                s_CarryInst = null;
                if (keep)
                {
                    s_Undo.Add(new UndoItem { P = instancePick, Before = s_CarryBefore });
                    ObjectEdits.SetInstance(instancePick, ObjectEdits.InstMoved(instancePick, s_CarryPos, s_CarryRot));
                    CabControls.Message(L.T($"Yerleştirildi: {instancePick.Prefix}", $"Placed: {instancePick.Prefix}"));
                }
                else
                {
                    ObjectEdits.SetInstance(instancePick, s_CarryBefore);
                    CabControls.Message(L.T("İptal edildi.", "Cancelled."));
                }
                s_Inst = null; s_InstCand = null;
                return;
            }
            var transform = s_Carry;
            s_Carry = null;
            ObjectEdits.EndMove();
            foreach (var collider in s_CarryColliders) if (collider != null) collider.enabled = true;
            s_CarryColliders.Clear();
            if (transform == null) return;
            if (keep)
            {
                s_Undo.Add(new UndoItem { T = transform, Before = s_CarryBefore });
                ObjectEdits.Set(transform, ObjectEdits.Moved(transform, transform.position, transform.rotation));
                CabControls.Message(L.T($"Yerleştirildi: {transform.name}", $"Placed: {transform.name}"));
            }
            else
            {
                ObjectEdits.Set(transform, s_CarryBefore);
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
            float weight = Screen.width;
            GUI.Label(new Rect(weight * 0.5f - 12f, Screen.height * 0.5f - 14f, 24f, 28f), "+", s_Style);
            string head = s_Carry != null ? s_Info : (s_Sel != null || s_Warn != "" ? s_Info : L.T("(nesne yok)", "(no object)"));
            GUI.Label(new Rect(0f, 30f, weight, 28f), L.T("NESNE DÜZENLEYİCİ  ", "OBJECT EDITOR  ") + head, s_Style);
            if (s_Warn != "") GUI.Label(new Rect(0f, 58f, weight, 24f), s_Warn, s_Small);
            string keys = s_Carry != null
                ? L.T("G bırak   X iptal   tekerlek döndür (Shift 15°, Ctrl 1°)   PageUp/PageDown yükseklik",
                      "G put down   X cancel   wheel turn (Shift 15°, Ctrl 1°)   PageUp/PageDown height")
                : L.T("Del kaldır   G taşı   C kopyala   [ ] büyük/küçük parça   Home orijinal   Ctrl+Z geri al   F11 sonraki mod",
                      "Del remove   G move   C copy   [ ] larger/smaller part   Home original   Ctrl+Z undo   F11 next mode");
            GUI.Label(new Rect(0f, Screen.height - 60f, weight, 24f), keys, s_Small);
        }
    }
}
