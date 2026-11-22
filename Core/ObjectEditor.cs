using System.Collections.Generic;
using Il2Cpp;
using Il2CppTLD.Interactions;
using UnityEngine;

namespace SteamRailway
{
    /// Moves, turns, copies and removes objects of a region with the crosshair; the changes are saved
    /// through ObjectEdits. The key list is shown on screen while the editor is on.
    internal static class ObjectEditor
    {
        const float ReachInMeters = 60f;
        const float InstancePickIntervalInSeconds = 0.25f;
        const float InstancePickDistanceMarginInMeters = 0.5f;
        const float SolidObjectSameDistanceToleranceInMeters = 1f;
        const float TreeReachMarginInMeters = 1.5f;
        const float DefaultTreeReachInMeters = 6f;
        const int MaximumChainChildCount = 40;
        const float YawStepFineInDegrees = 1f;
        const float YawStepNormalInDegrees = 5f;
        const float YawStepCoarseInDegrees = 15f;
        const float LiftStepNormalInMeters = 0.1f;
        const float LiftStepCoarseInMeters = 0.5f;
        const float CarryFallbackDistanceInMeters = 15f;
        const float CrosshairWidthInPixels = 24f;
        const float CrosshairHeightInPixels = 28f;
        const float TitleTopInPixels = 30f;
        const float TitleHeightInPixels = 28f;
        const float WarningTopInPixels = 58f;
        const float WarningHeightInPixels = 24f;
        const float KeysBottomMarginInPixels = 60f;
        const float KeysHeightInPixels = 24f;
        const int TitleFontSize = 18;
        const int HintFontSize = 14;
        const float HintColorGreen = 0.8f;
        const float HintColorBlue = 0.3f;

        static bool s_IsEditorOn => EditorHub.Mode == EditorMode.Objects;
        static Transform s_HitTransform;                 // what the crosshair touches
        static readonly List<Transform> s_Chain = new List<Transform>();   // the hit up to the largest object
        static int s_Level;                     // index into s_Chain
        static Transform s_Sel;
        static string s_InfoText = "", s_WarningText = "";

        // carrying
        static Transform s_Carry;
        static EditRecord s_CarryBefore;
        static Vector3 s_CarryStartPosition;
        static Quaternion s_CarryStartRotation;
        static float s_Yaw, s_Lift, s_PivotAbove;
        static readonly List<Collider> s_CarryColliders = new List<Collider>();

        sealed class UndoItem { public Transform EditedTransform; public InstancedHider.InstancedObjectPick PickedInstance; public EditRecord Before; }
        static readonly List<UndoItem> s_Undo = new List<UndoItem>();

        // objects drawn by GPU instancing with no scene object (the crosshair finds them by their mesh)
        static InstancedHider.InstancedObjectPick s_SelectedInstance, s_CandidateInstance, s_CarryInstance;
        static float s_NextInstancePickTime;

        static GUIStyle s_Style, s_SmallStyle;

        public static bool Active => s_IsEditorOn;

        /// The editor is switched to another mode: whatever is carried goes back.
        public static void Leave()
        {
            if (s_Carry != null || s_CarryInstance != null) Drop(false);
        }

        public static void Update()
        {
            if (!s_IsEditorOn || RailwayManager.CurrentScene == null) return;

            if (s_Carry != null || s_CarryInstance != null) { Carry(); return; }
            Pick();
            bool isControlHeld = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (isControlHeld && Input.GetKeyDown(KeyCode.Z)) { Undo(); return; }
            if (Input.GetKeyDown(KeyCode.LeftBracket) && s_Level < s_Chain.Count - 1) { s_Level++; Select(); }
            if (Input.GetKeyDown(KeyCode.RightBracket) && s_Level > 0) { s_Level--; Select(); }
            if (s_Sel == null && s_SelectedInstance == null) return;
            // copies made with C: Del deletes them, G moves them
            var copy = CopyOf(s_Sel);
            if (copy != null)
            {
                if (Input.GetKeyDown(KeyCode.Delete)) { ObjectEdits.RemoveCopy(copy); CabControls.Message(Localizer.Translate("Kopya silindi.", "Copy deleted.")); s_HitTransform = null; return; }
                if (Input.GetKeyDown(KeyCode.G)) { CarryCopy(copy, false); return; }
                if (Input.GetKeyDown(KeyCode.C)) { var newCopy = ObjectEdits.AddCopy(copy); if (newCopy != null) CarryCopy(newCopy, true); return; }
                return;
            }
            if (Input.GetKeyDown(KeyCode.C) && s_Sel != null)
            {
                var child = ObjectEdits.AddCopy(s_Sel);
                if (child != null) CarryCopy(child, true);
                else CabControls.Message(Localizer.Translate("Bu nesne kopyalanamadı.", "This object could not be copied."));
                return;
            }
            if (Input.GetKeyDown(KeyCode.V) && s_Sel != null)
            {
                bool marked = Obstacles.ToggleMovableWagon(s_Sel);
                CabControls.Message(marked ? Localizer.Translate("Hareketli vagon olarak işaretlendi: orijinal yerinde kalır, trenle itilip çekilir.", "Marked as a movable wagon: it stays in place and is pushed and pulled with the train.")
                                           : Localizer.Translate("Hareketli vagon işareti kaldırıldı.", "Movable wagon mark removed."));
                return;
            }
            if (Input.GetKeyDown(KeyCode.T) && s_Sel != null)
            {
                var treeBounds = ObjectEdits.BoundsOf(s_Sel, out bool hasBounds);
                float reach = hasBounds ? Mathf.Max(treeBounds.extents.x, treeBounds.extents.z) + TreeReachMarginInMeters : DefaultTreeReachInMeters;
                bool marked = Obstacles.ToggleTree(s_Sel.name, ObjectEdits.OriginalPosition(s_Sel), reach);
                CabControls.Message(marked ? Localizer.Translate("Devrilmiş ağaç olarak işaretlendi: Zor modda tren çarpınca kaybolur.", "Marked as a fallen tree: in hard mode it vanishes when a train runs into it.")
                                           : Localizer.Translate("Devrilmiş ağaç işareti kaldırıldı.", "Fallen tree mark removed."));
                return;
            }
            if (Input.GetKeyDown(KeyCode.Delete)) { if (s_Sel != null) Remove(s_Sel); else RemoveInst(s_SelectedInstance); }
            else if (Input.GetKeyDown(KeyCode.G)) { if (s_Sel != null) PickUp(s_Sel); else PickUpInst(s_SelectedInstance); }
            else if (Input.GetKeyDown(KeyCode.Home)) { if (s_Sel != null) Reset(s_Sel); else ResetInst(s_SelectedInstance); }
        }

        // ------------------------------------------------------------------ selecting

        static Transform Camera()
        {
            var firstPersonCamera = GameManager.GetVpFPSCamera();
            return firstPersonCamera != null ? firstPersonCamera.transform : null;
        }

        static bool Ray(out RaycastHit rayHit, Transform ignoredTransform)
        {
            rayHit = default;
            var cameraTransform = Camera();
            if (cameraTransform == null) return false;
            var hits = Physics.RaycastAll(cameraTransform.position, cameraTransform.forward, ReachInMeters, ~0, QueryTriggerInteraction.Ignore);
            float nearestDistance = float.MaxValue;
            bool foundHit = false;
            foreach (var candidateHit in hits)
            {
                var hitTransform = candidateHit.collider.transform;
                if (candidateHit.distance >= nearestDistance || IsPlayer(hitTransform) || (ignoredTransform != null && hitTransform.IsChildOf(ignoredTransform))) continue;
                nearestDistance = candidateHit.distance; rayHit = candidateHit; foundHit = true;
            }
            return foundHit;
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
            float solidHitDistance = float.MaxValue;
            if (Ray(out var rayHit, null) && !(rayHit.collider is TerrainCollider)) { hitTransform = rayHit.collider.transform; solidHitDistance = rayHit.distance; }

            // an instanced object nearer than anything solid (it may have no collider: the player walks through it)
            if (Time.time >= s_NextInstancePickTime)
            {
                s_NextInstancePickTime = Time.time + InstancePickIntervalInSeconds;
                var cameraTransform = Camera();
                s_CandidateInstance = null;
                if (cameraTransform != null)
                {
                    try { s_CandidateInstance = InstancedHider.Pick(new Ray(cameraTransform.position, cameraTransform.forward), Mathf.Min(ReachInMeters, solidHitDistance + InstancePickDistanceMarginInMeters)); }
                    catch (System.Exception exception) { SteamRailwayMod.Debug($"Instance pick: {exception.Message}"); }
                }
            }
            var pickedInstance = s_CandidateInstance;
            if (pickedInstance != null && solidHitDistance < float.MaxValue && pickedInstance.DistanceInMeters > solidHitDistance - SolidObjectSameDistanceToleranceInMeters) pickedInstance = null;      // the solid object is the same thing or nearer
            if (pickedInstance != null)
            {
                if (s_SelectedInstance != null && s_SelectedInstance.Key == pickedInstance.Key && s_HitTransform == null && s_Sel == null) { s_SelectedInstance = pickedInstance; return; }
                s_SelectedInstance = pickedInstance; s_HitTransform = null; s_Sel = null; s_Chain.Clear(); s_Level = 0;
                var boundsSize = pickedInstance.WorldBounds.size;
                s_InfoText = $"{pickedInstance.Prefix}   ({boundsSize.x:F1} x {boundsSize.y:F1} x {boundsSize.z:F1} m)  [GPU]";
                s_WarningText = Localizer.Translate("Toplu çizilen nesne: çarpışması olmayabilir", "Instanced object: it may have no collision");
                return;
            }
            if (s_SelectedInstance != null) { s_SelectedInstance = null; s_HitTransform = null; s_Sel = null; s_InfoText = ""; s_WarningText = ""; }
            if (hitTransform == s_HitTransform) return;
            s_HitTransform = hitTransform;
            s_Chain.Clear();
            s_Level = 0;
            if (hitTransform != null)
            {
                // from the hit part up to the whole object; region groups (many children) are never taken
                Transform root = null;
                for (var transform = hitTransform; transform != null && transform.parent != null; transform = transform.parent)
                {
                    if (transform.childCount > MaximumChainChildCount) break;
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
            s_WarningText = "";
            if (s_Sel == null) { s_InfoText = ""; return; }
            if (s_Sel.GetComponentsInChildren<LoadScene>(true).Length > 0)
            {
                s_InfoText = s_Sel.name;
                s_WarningText = Localizer.Translate("Bölge geçişi: düzenlenemez", "Region transition: cannot be edited");
                s_Sel = null;
                return;
            }
            var bounds = Bounds(s_Sel);
            s_InfoText = $"{s_Sel.name}   ({bounds.size.x:F1} x {bounds.size.y:F1} x {bounds.size.z:F1} m)";
            if (s_Sel.GetComponentsInChildren<Container>(true).Length > 0 || s_Sel.GetComponentsInChildren<GearItem>(true).Length > 0)
                s_WarningText = Localizer.Translate("İçinde eşya var: kaldırılırsa ulaşılamaz", "Holds items: they cannot be reached once it is removed");
            else if (s_Sel.GetComponentsInChildren<BaseInteraction>(true).Length > 0)
                s_WarningText = Localizer.Translate("Etkileşimli nesne", "Interactive object");
            if (ObjectEdits.IsStatic(s_Sel)) s_InfoText += Localizer.Translate("  [sabit model: taşınırken kopyası yapılır]", "  [static model: copied when moved]");
        }

        /// A part with nothing to see in it (and not an object the game draws by instancing).
        static bool NeedsParent(Transform transform)
        {
            if (transform.name.Contains("_Prefab") || transform.GetComponent<LODGroup>() != null) return false;
            return transform.GetComponentsInChildren<Renderer>(true).Length == 0;
        }

        static Bounds Bounds(Transform transform)
        {
            bool foundBounds = false;
            var combinedBounds = new Bounds(transform.position, Vector3.zero);
            foreach (var renderer in transform.GetComponentsInChildren<Renderer>(false))
            {
                if (!renderer.enabled) continue;
                if (!foundBounds) { combinedBounds = renderer.bounds; foundBounds = true; } else combinedBounds.Encapsulate(renderer.bounds);
            }
            if (!foundBounds)
                foreach (var collider in transform.GetComponentsInChildren<Collider>(false))
                {
                    if (!collider.enabled) continue;
                    if (!foundBounds) { combinedBounds = collider.bounds; foundBounds = true; } else combinedBounds.Encapsulate(collider.bounds);
                }
            return combinedBounds;
        }

        // ------------------------------------------------------------------ actions

        static void RemoveInst(InstancedHider.InstancedObjectPick instancePick)
        {
            s_Undo.Add(new UndoItem { PickedInstance = instancePick, Before = ObjectEdits.InstRecord(instancePick) });
            ObjectEdits.SetInstance(instancePick, ObjectEdits.InstHidden(instancePick));
            CabControls.Message(Localizer.Translate($"Kaldırıldı: {instancePick.Prefix}  (Ctrl+Z geri alır)", $"Removed: {instancePick.Prefix}  (Ctrl+Z undoes)"));
            s_SelectedInstance = null; s_CandidateInstance = null;
        }

        static void ResetInst(InstancedHider.InstancedObjectPick instancePick)
        {
            var editRecord = ObjectEdits.InstRecord(instancePick);
            if (editRecord == null) return;
            s_Undo.Add(new UndoItem { PickedInstance = instancePick, Before = editRecord });
            ObjectEdits.SetInstance(instancePick, null);
            CabControls.Message(Localizer.Translate($"Orijinal hâline döndü: {instancePick.Prefix}", $"Back as the region made it: {instancePick.Prefix}"));
        }

        static void PickUpInst(InstancedHider.InstancedObjectPick instancePick)
        {
            s_CarryInstance = instancePick;
            s_CarryBefore = ObjectEdits.InstRecord(instancePick);
            s_CarryStartRotation = instancePick.CurrentRotation;
            s_Yaw = 0f; s_Lift = 0f;
            s_PivotAbove = Mathf.Max(0f, instancePick.CurrentPosition.y - instancePick.WorldBounds.min.y);
            CabControls.Message(Localizer.Translate("Taşınıyor: G bırakır, X iptal, tekerlek döndürür, PageUp/PageDown yükseklik.",
                                    "Carrying: G puts down, X cancels, wheel turns, PageUp/PageDown height."));
        }

        static void Remove(Transform transform)
        {
            s_Undo.Add(new UndoItem { EditedTransform = transform, Before = ObjectEdits.RecordOf(transform) });
            ObjectEdits.Set(transform, ObjectEdits.Hidden(transform));
            CabControls.Message(Localizer.Translate($"Kaldırıldı: {transform.name}  (Ctrl+Z geri alır)", $"Removed: {transform.name}  (Ctrl+Z undoes)"));
            s_HitTransform = null;
        }

        static void Reset(Transform transform)
        {
            if (ObjectEdits.RecordOf(transform) == null) return;
            s_Undo.Add(new UndoItem { EditedTransform = transform, Before = ObjectEdits.RecordOf(transform) });
            ObjectEdits.Set(transform, null);
            CabControls.Message(Localizer.Translate($"Orijinal hâline döndü: {transform.name}", $"Back as the region made it: {transform.name}"));
        }

        static void Undo()
        {
            if (s_Undo.Count == 0) { CabControls.Message(Localizer.Translate("Geri alınacak bir şey yok.", "Nothing to undo.")); return; }
            var undoItem = s_Undo[s_Undo.Count - 1];
            s_Undo.RemoveAt(s_Undo.Count - 1);
            if (undoItem.PickedInstance != null)
            {
                ObjectEdits.SetInstance(undoItem.PickedInstance, undoItem.Before);
                CabControls.Message(Localizer.Translate($"Geri alındı: {undoItem.PickedInstance.Prefix}", $"Undone: {undoItem.PickedInstance.Prefix}"));
                s_SelectedInstance = null; s_CandidateInstance = null;
                return;
            }
            var transform = undoItem.EditedTransform;
            if (transform == null) return;
            ObjectEdits.Set(transform, undoItem.Before);
            CabControls.Message(Localizer.Translate($"Geri alındı: {transform.name}", $"Undone: {transform.name}"));
            s_HitTransform = null;
        }

        /// Taking hold of something to carry (no edit of the original object behind it).
        static void PickUpCommon(Transform transform)
        {
            s_Carry = transform;
            s_CarryStartPosition = transform.position;
            s_CarryStartRotation = transform.rotation;
            s_Yaw = 0f; s_Lift = 0f;
            s_PivotAbove = transform.position.y - Bounds(transform).min.y;
            s_CarryColliders.Clear();
            foreach (var collider in transform.GetComponentsInChildren<Collider>(false)) if (collider.enabled) { collider.enabled = false; s_CarryColliders.Add(collider); }
        }

        static void PickUp(Transform transform)
        {
            s_Carry = transform;
            s_CarryBefore = ObjectEdits.RecordOf(transform);
            s_CarryStartPosition = transform.position;
            s_CarryStartRotation = transform.rotation;
            s_Yaw = 0f; s_Lift = 0f;
            s_PivotAbove = transform.position.y - Bounds(transform).min.y;
            ObjectEdits.BeginMove(transform);
            // its own colliders must not catch the crosshair while it is carried
            s_CarryColliders.Clear();
            foreach (var collider in transform.GetComponentsInChildren<Collider>(false)) if (collider.enabled) { collider.enabled = false; s_CarryColliders.Add(collider); }
            CabControls.Message(Localizer.Translate("Taşınıyor: G bırakır, X iptal, tekerlek döndürür, PageUp/PageDown yükseklik.",
                                    "Carrying: G puts down, X cancels, wheel turns, PageUp/PageDown height."));
        }

        static void Carry()
        {
            bool isShiftHeld = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            bool isControlHeld = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            float wheel = Input.mouseScrollDelta.y;
            if (wheel != 0f) s_Yaw += Mathf.Sign(wheel) * (isShiftHeld ? YawStepCoarseInDegrees : isControlHeld ? YawStepFineInDegrees : YawStepNormalInDegrees);
            if (Input.GetKeyDown(KeyCode.PageUp)) s_Lift += isShiftHeld ? LiftStepCoarseInMeters : LiftStepNormalInMeters;
            if (Input.GetKeyDown(KeyCode.PageDown)) s_Lift -= isShiftHeld ? LiftStepCoarseInMeters : LiftStepNormalInMeters;
            if (Input.GetKeyDown(KeyCode.X)) { Drop(false); return; }
            if (Input.GetKeyDown(KeyCode.G)) { Drop(true); return; }

            var cameraTransform = Camera();
            if (cameraTransform == null) return;
            Vector3 point = Ray(out var rayHit, s_Carry) ? rayHit.point : cameraTransform.position + cameraTransform.forward * CarryFallbackDistanceInMeters;
            var targetPosition = new Vector3(point.x, point.y + s_PivotAbove + s_Lift, point.z);
            var rotation = Quaternion.Euler(0f, s_Yaw, 0f) * s_CarryStartRotation;
            string carriedName;
            if (s_CarryInstance != null) { ObjectEdits.PreviewInstance(s_CarryInstance, targetPosition, rotation); s_CarryPosition = targetPosition; s_CarryRotation = rotation; carriedName = s_CarryInstance.Prefix; }
            else if (s_CarryCopy != null) { s_Carry.SetPositionAndRotation(targetPosition, rotation); carriedName = s_Carry.name; }
            else { ObjectEdits.Preview(s_Carry, targetPosition, rotation); carriedName = s_Carry.name; }
            s_InfoText = $"{carriedName}   " + Localizer.Translate($"dönüş {s_Yaw:F0}°  yükseklik {s_Lift:+0.0;-0.0} m", $"turn {s_Yaw:F0}°  height {s_Lift:+0.0;-0.0} m");
        }

        static Vector3 s_CarryPosition;
        static Quaternion s_CarryRotation;

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
            CabControls.Message(isNew ? Localizer.Translate("Kopya oluşturuldu: G bırakır, X iptal, tekerlek döndürür, PageUp/PageDown yükseklik.", "Copy made: G puts down, X cancels, wheel turns, PageUp/PageDown height.")
                                      : Localizer.Translate("Kopya taşınıyor: G bırakır, X iptal.", "Carrying the copy: G puts down, X cancels."));
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
                if (keep) { ObjectEdits.MoveCopy(child); CabControls.Message(Localizer.Translate("Kopya yerleştirildi.", "Copy placed.")); }
                else if (s_CarryCopyNew) { ObjectEdits.RemoveCopy(child); CabControls.Message(Localizer.Translate("Kopya iptal edildi.", "Copy cancelled.")); }
                else { ObjectEdits.ResetCopy(child); CabControls.Message(Localizer.Translate("İptal edildi.", "Cancelled.")); }
                s_HitTransform = null;
                return;
            }
            if (s_CarryInstance != null)
            {
                var instancePick = s_CarryInstance;
                s_CarryInstance = null;
                if (keep)
                {
                    s_Undo.Add(new UndoItem { PickedInstance = instancePick, Before = s_CarryBefore });
                    ObjectEdits.SetInstance(instancePick, ObjectEdits.InstMoved(instancePick, s_CarryPosition, s_CarryRotation));
                    CabControls.Message(Localizer.Translate($"Yerleştirildi: {instancePick.Prefix}", $"Placed: {instancePick.Prefix}"));
                }
                else
                {
                    ObjectEdits.SetInstance(instancePick, s_CarryBefore);
                    CabControls.Message(Localizer.Translate("İptal edildi.", "Cancelled."));
                }
                s_SelectedInstance = null; s_CandidateInstance = null;
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
                s_Undo.Add(new UndoItem { EditedTransform = transform, Before = s_CarryBefore });
                ObjectEdits.Set(transform, ObjectEdits.Moved(transform, transform.position, transform.rotation));
                CabControls.Message(Localizer.Translate($"Yerleştirildi: {transform.name}", $"Placed: {transform.name}"));
            }
            else
            {
                ObjectEdits.Set(transform, s_CarryBefore);
                CabControls.Message(Localizer.Translate("İptal edildi.", "Cancelled."));
            }
            s_HitTransform = null;
        }

        // ------------------------------------------------------------------ on screen

        public static void OnGUI()
        {
            if (!s_IsEditorOn) return;
            if (s_Style == null)
            {
                s_Style = new GUIStyle(GUI.skin.label) { fontSize = TitleFontSize, alignment = TextAnchor.UpperCenter };
                s_Style.normal.textColor = Color.white;
                s_SmallStyle = new GUIStyle(GUI.skin.label) { fontSize = HintFontSize, alignment = TextAnchor.UpperCenter };
                s_SmallStyle.normal.textColor = new Color(1f, HintColorGreen, HintColorBlue);
            }
            float screenWidth = Screen.width;
            GUI.Label(new Rect(screenWidth * 0.5f - CrosshairWidthInPixels * 0.5f, Screen.height * 0.5f - CrosshairHeightInPixels * 0.5f, CrosshairWidthInPixels, CrosshairHeightInPixels), "+", s_Style);
            string head = s_Carry != null ? s_InfoText : (s_Sel != null || s_WarningText != "" ? s_InfoText : Localizer.Translate("(nesne yok)", "(no object)"));
            GUI.Label(new Rect(0f, TitleTopInPixels, screenWidth, TitleHeightInPixels), Localizer.Translate("NESNE DÜZENLEYİCİ  ", "OBJECT EDITOR  ") + head, s_Style);
            if (s_WarningText != "") GUI.Label(new Rect(0f, WarningTopInPixels, screenWidth, WarningHeightInPixels), s_WarningText, s_SmallStyle);
            string keys = s_Carry != null
                ? Localizer.Translate("G bırak   X iptal   tekerlek döndür (Shift 15°, Ctrl 1°)   PageUp/PageDown yükseklik",
                      "G put down   X cancel   wheel turn (Shift 15°, Ctrl 1°)   PageUp/PageDown height")
                : Localizer.Translate("Del kaldır   G taşı   C kopyala   V hareketli vagon   T devrik ağaç   [ ] büyük/küçük parça   Home orijinal   Ctrl+Z geri al   F11 sonraki mod",
                      "Del remove   G move   C copy   V movable wagon   T fallen tree   [ ] larger/smaller part   Home original   Ctrl+Z undo   F11 next mode");
            GUI.Label(new Rect(0f, Screen.height - KeysBottomMarginInPixels, screenWidth, KeysHeightInPixels), keys, s_SmallStyle);
        }
    }
}
