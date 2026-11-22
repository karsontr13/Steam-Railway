using System.Collections.Generic;
using System.Text.Json.Serialization;
using Il2Cpp;
using UnityEngine;

namespace SteamRailway
{
    /// One item lying in the cab, saved with the train (TrainState.CabItems).
    internal sealed class CabItemState
    {
        public string Prefab { get; set; }
        public string Data { get; set; }
        [JsonPropertyName("Pos")] public float[] Position { get; set; }
        [JsonPropertyName("Rot")] public float[] Rotation { get; set; }
    }

    /// Items placed or dropped in the cab are parented to the train, saved with it and put back when it is rebuilt.
    /// They are left out of the region's own gear save.
    internal static class CabItems
    {
        const float BoxCenterHeightInMeters = 2.9f;
        const float BoxCenterDepthInMeters = -3.2f;
        const float BoxHalfWidthInMeters = 1.4f;
        const float BoxHalfHeightInMeters = 1.35f;
        const float BoxHalfLengthInMeters = 1.5f;
        /// An item is let go only when it is this far outside the cab volume: things lying at the edge (on the cupboard) stay.
        const float ReleaseMarginInMeters = 0.9f;
        const float FloorMarginWidthInMeters = 1.2f;
        const float FloorNearestDepthInMeters = -4.2f;
        const float FloorFarthestDepthInMeters = -2.0f;
        const float FloorFeetOffsetInMeters = 0.35f;
        const float HeldPoseToleranceSquaredInMeters = 0.000001f;
        static readonly Vector3 BoxCenter = new Vector3(0f, BoxCenterHeightInMeters, BoxCenterDepthInMeters);
        static readonly Vector3 BoxHalf = new Vector3(BoxHalfWidthInMeters, BoxHalfHeightInMeters, BoxHalfLengthInMeters);
        const float ScanIntervalInSeconds = 0.2f;
        const string HolderName = "CabItems";

        static readonly List<GearItem> s_Items = new List<GearItem>();
        static readonly Dictionary<GearItem, KeyValuePair<Vector3, Quaternion>> s_HeldPoses = new Dictionary<GearItem, KeyValuePair<Vector3, Quaternion>>();
        const float RestoreGraceInSeconds = 3f;
        static readonly Dictionary<GearItem, float> s_RestoreGraceEnds = new Dictionary<GearItem, float>();
        static float s_NextScan;
        static readonly Dictionary<GearItem, Rigidbody> s_Bodies = new Dictionary<GearItem, Rigidbody>();

        static Transform Holder(Transform train)
        {
            var hitTransform = train.Find(HolderName);
            if (hitTransform == null)
            {
                hitTransform = new GameObject(HolderName).transform;
                hitTransform.SetParent(train, false);
            }
            return hitTransform;
        }

        static bool InCab(Transform train, Vector3 world, float margin = 0f)
        {
            var point = train.InverseTransformPoint(world) - BoxCenter;
            return Mathf.Abs(point.x) <= BoxHalf.x + margin && Mathf.Abs(point.y) <= BoxHalf.y + margin && Mathf.Abs(point.z) <= BoxHalf.z + margin;
        }

        static bool Loose(GearItem gearItem) =>
            gearItem != null && gearItem.gameObject.activeInHierarchy && !gearItem.m_InPlayerInventory && !gearItem.m_InsideContainer;

        /// The item is one the cab carries.
        public static bool IsTracked(GearItem gearItem) => s_Items.Contains(gearItem);

        static bool BeingPlaced(GearItem gearItem)
        {
            var playerManager = GameManager.GetPlayerManagerComponent();
            return playerManager != null && playerManager.m_ObjectToPlace != null && playerManager.m_ObjectToPlace == gearItem.gameObject;
        }

        static void Adopt(Transform train, GearItem gearItem)
        {
            gearItem.transform.SetParent(Holder(train), true);
            var body = gearItem.GetComponent<Rigidbody>();
            s_Bodies[gearItem] = body;
            if (body != null) { body.velocity = Vector3.zero; body.isKinematic = true; }
            if (!s_Items.Contains(gearItem)) s_Items.Add(gearItem);
            s_HeldPoses[gearItem] = PoseInTrain(train, gearItem.transform);
        }

        static KeyValuePair<Vector3, Quaternion> PoseInTrain(Transform train, Transform item) =>
            new KeyValuePair<Vector3, Quaternion>(train.InverseTransformPoint(item.position), Quaternion.Inverse(train.rotation) * item.rotation);

        static void ApplyPose(Transform train, Transform item, KeyValuePair<Vector3, Quaternion> pose) =>
            item.SetPositionAndRotation(train.TransformPoint(pose.Key), train.rotation * pose.Value);

        static GameObject s_PlacedObject;
        static KeyValuePair<Vector3, Quaternion> s_PlacedPose;

        /// Where the game aimed the item being placed, relative to the cab; CarryPlacement puts it back there
        /// after the train has moved on.
        public static void RememberPlacement(Transform train, GameObject placed)
        {
            s_PlacedObject = placed;
            if (placed != null) s_PlacedPose = PoseInTrain(train, placed.transform);
        }

        static void CarryPlacement(Transform train)
        {
            var playerManager = GameManager.GetPlayerManagerComponent();
            if (s_PlacedObject == null || playerManager == null || playerManager.m_ObjectToPlace != s_PlacedObject) { s_PlacedObject = null; return; }
            ApplyPose(train, s_PlacedObject.transform, s_PlacedPose);
        }

        const float FloorTop = 1.63f;

        /// Sets a dropped item down on the cab floor in front of the player's feet and keeps it with the train.
        public static void PutOnFloor(Transform train, GameObject targetObject, Vector3 feetWorld)
        {
            var gearItem = targetObject.GetComponent<GearItem>();
            if (gearItem == null || gearItem.m_InPlayerInventory) return;
            var point = train.InverseTransformPoint(feetWorld);
            point.x = Mathf.Clamp(point.x, -FloorMarginWidthInMeters, FloorMarginWidthInMeters);
            point.z = Mathf.Clamp(point.z - FloorFeetOffsetInMeters, FloorNearestDepthInMeters, FloorFarthestDepthInMeters);
            var body = targetObject.GetComponent<Rigidbody>();
            if (body != null) { body.velocity = Vector3.zero; body.isKinematic = true; }
            float yaw = (Quaternion.Inverse(train.rotation) * targetObject.transform.rotation).eulerAngles.y;
            targetObject.transform.rotation = train.rotation * Quaternion.Euler(0f, yaw, 0f);
            targetObject.transform.position = train.TransformPoint(new Vector3(point.x, FloorTop, point.z));
            float lowest = float.MaxValue;
            foreach (var renderer in targetObject.GetComponentsInChildren<Renderer>()) lowest = Mathf.Min(lowest, renderer.bounds.min.y);
            if (lowest < float.MaxValue) targetObject.transform.position += Vector3.up * (train.TransformPoint(new Vector3(0f, FloorTop, 0f)).y - lowest);
            Adopt(train, gearItem);
        }

        /// Every frame: an item the game detached or made physical again is taken back at once.
        static void KeepAttached(Transform train)
        {
            for (int index = 0; index < s_Items.Count; index++)
            {
                var gearItem = s_Items[index];
                if (gearItem == null || !Loose(gearItem)) continue;
                if (HoldRestored(train, gearItem)) continue;
                if (BeingPlaced(gearItem))
                {
                    s_HeldPoses[gearItem] = PoseInTrain(train, gearItem.transform);
                    continue;
                }
                if (!InCab(train, gearItem.transform.position, ReleaseMarginInMeters)) continue;
                if (!gearItem.transform.IsChildOf(train)) gearItem.transform.SetParent(Holder(train), true);
                if (s_HeldPoses.TryGetValue(gearItem, out var heldPose))
                {
                    // something in the game keeps moving the item: hold it
                    if ((train.InverseTransformPoint(gearItem.transform.position) - heldPose.Key).sqrMagnitude > HeldPoseToleranceSquaredInMeters) ApplyPose(train, gearItem.transform, heldPose);
                }
                s_Bodies.TryGetValue(gearItem, out var body);
                if (body != null && !body.isKinematic) { body.velocity = Vector3.zero; body.isKinematic = true; }
            }
        }

        /// A just restored item is kept at its saved place in the cab for a few seconds: the game may still move it
        /// (it places the item where it lay when it was saved) while the train is being put on its line.
        static bool HoldRestored(Transform train, GearItem gearItem)
        {
            if (!s_RestoreGraceEnds.TryGetValue(gearItem, out float graceEnd)) return false;
            if (Time.time >= graceEnd) { s_RestoreGraceEnds.Remove(gearItem); return false; }
            if (!gearItem.transform.IsChildOf(train)) gearItem.transform.SetParent(Holder(train), true);
            if (s_HeldPoses.TryGetValue(gearItem, out var heldPose)) ApplyPose(train, gearItem.transform, heldPose);
            return true;
        }

        /// Finds items newly put down in the cab and lets go of items that left it (picked up, moved out).
        public static void Update(Transform train)
        {
            if (train == null) return;
            CarryPlacement(train);
            KeepAttached(train);
            if (Time.time < s_NextScan) return;
            s_NextScan = Time.time + ScanIntervalInSeconds;

            for (int index = s_Items.Count - 1; index >= 0; index--)
            {
                var gearItem = s_Items[index];
                if (gearItem == null) { s_Items.RemoveAt(index); continue; }
                if (BeingPlaced(gearItem)) continue;
                bool attached = gearItem.transform.IsChildOf(train);
                if (s_RestoreGraceEnds.ContainsKey(gearItem)) continue;
                if (!Loose(gearItem) || !attached || !InCab(train, gearItem.transform.position, ReleaseMarginInMeters))
                {
                    // picked up, put in a container, or moved out of the cab: an ordinary item again
                    if (attached && Loose(gearItem)) gearItem.transform.SetParent(null, true);
                    s_Items.RemoveAt(index);
                }
            }

            AdoptNewItems(train);
        }

        /// Takes in what was just put down in the cab, without waiting for the next scan.
        public static void ScanNow(Transform train)
        {
            if (train == null) return;
            AdoptNewItems(train);
            s_NextScan = Time.time + ScanIntervalInSeconds;
        }

        static void AdoptNewItems(Transform train)
        {
            var center = train.TransformPoint(BoxCenter);
            foreach (var collider in Physics.OverlapBox(center, BoxHalf, train.rotation, -1, QueryTriggerInteraction.Collide))
            {
                if (collider == null) continue;
                var gearItem = collider.GetComponentInParent<GearItem>();
                if (gearItem == null || s_Items.Contains(gearItem) || !Loose(gearItem) || BeingPlaced(gearItem) || Wagons.IsCargo(gearItem)) continue;
                if (!InCab(train, gearItem.transform.position)) continue;
                Adopt(train, gearItem);
                SteamRailwayMod.Debug($"Cab item: {gearItem.name}");
            }
        }

        public static List<CabItemState> Capture(Transform train)
        {
            var list = new List<CabItemState>();
            if (train == null) return list;
            foreach (var gearItem in s_Items)
            {
                if (gearItem == null || !Loose(gearItem) || !gearItem.transform.IsChildOf(train)) continue;
                try
                {
                    var localPosition = train.InverseTransformPoint(gearItem.transform.position);
                    var localRotation = Quaternion.Inverse(train.rotation) * gearItem.transform.rotation;
                    list.Add(new CabItemState
                    {
                        Prefab = gearItem.name.Replace("(Clone)", "").Trim(),
                        Data = gearItem.SerializeToString(),
                        Position = new[] { localPosition.x, localPosition.y, localPosition.z },
                        Rotation = new[] { localRotation.x, localRotation.y, localRotation.z, localRotation.w },
                    });
                }
                catch (System.Exception exception) { SteamRailwayMod.Debug($"Cab item {gearItem.name} not saved: {exception.Message}"); }
            }
            return list;
        }

        /// Puts the saved items back into a freshly built train.
        public static void Restore(Transform train, List<CabItemState> items)
        {
            if (train == null || items == null) return;
            foreach (var itemState in items)
            {
                if (string.IsNullOrEmpty(itemState.Prefab) || itemState.Position == null || itemState.Rotation == null) continue;
                try
                {
                    var gearItem = GearItem.InstantiateAndDeserializeGearItem(itemState.Prefab, itemState.Data, null, false, false);
                    if (gearItem == null) { SteamRailwayMod.Log.Warning($"Cab item {itemState.Prefab} could not be made."); continue; }
                    var savedPosition = new Vector3(itemState.Position[0], itemState.Position[1], itemState.Position[2]);
                    var savedRotation = new Quaternion(itemState.Rotation[0], itemState.Rotation[1], itemState.Rotation[2], itemState.Rotation[3]);
                    gearItem.transform.SetPositionAndRotation(train.TransformPoint(savedPosition), train.rotation * savedRotation);
                    Adopt(train, gearItem);
                    s_HeldPoses[gearItem] = new KeyValuePair<Vector3, Quaternion>(savedPosition, savedRotation);
                    s_RestoreGraceEnds[gearItem] = Time.time + RestoreGraceInSeconds;
                    SteamRailwayMod.Debug($"Cab item restored: {itemState.Prefab} at {train.TransformPoint(savedPosition)} (train at {train.position}).");
                }
                catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"Cab item {itemState.Prefab} not restored: {exception.Message}"); }
            }
        }

        /// The cab items are left out of the region's gear save (they are saved with the train).
        public static List<GearItem> HideFromRegionSave()
        {
            var hidden = new List<GearItem>();
            foreach (var gearItem in s_Items)
            {
                if (gearItem == null) continue;
                GearManager.Remove(gearItem);
                hidden.Add(gearItem);
            }
            return hidden;
        }

        public static void Unhide(List<GearItem> hidden)
        {
            if (hidden == null) return;
            foreach (var gearItem in hidden) if (gearItem != null) GearManager.Add(gearItem);
        }

        /// A new game or a region change: the list belongs to the old train.
        public static void Clear() { s_Items.Clear(); s_HeldPoses.Clear(); s_Bodies.Clear(); s_RestoreGraceEnds.Clear(); }
    }
}
