using System.Collections.Generic;
using Il2Cpp;
using UnityEngine;

namespace SteamRailway
{
    /// One item lying in the cab, saved with the train (TrainState.CabItems).
    internal sealed class CabItemState
    {
        public string Prefab { get; set; }
        public string Data { get; set; }      
        public float[] Pos { get; set; }    
        public float[] Rot { get; set; }
    }

    /// Items placed or dropped in the cab ride with the train: they are parented to the train, saved with it and put
    /// back when it is built again (after a region change or a reload). They are left out of the region's own gear
    /// save, so a region never keeps a copy where the train once stood.
    internal static class CabItems
    {
        static readonly Vector3 BoxCenter = new Vector3(0f, 2.9f, -3.0f);
        static readonly Vector3 BoxHalf = new Vector3(1.45f, 1.35f, 1.38f);
        const float ScanInterval = 0.2f;
        const string HolderName = "CabItems";

        static readonly List<GearItem> s_Items = new List<GearItem>();
        static readonly Dictionary<GearItem, KeyValuePair<Vector3, Quaternion>> s_HeldPoses = new Dictionary<GearItem, KeyValuePair<Vector3, Quaternion>>();
        static float s_NextScan;

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

        static bool InCab(Transform train, Vector3 world)
        {
            var point = train.InverseTransformPoint(world) - BoxCenter;
            return Mathf.Abs(point.x) <= BoxHalf.x && Mathf.Abs(point.y) <= BoxHalf.y && Mathf.Abs(point.z) <= BoxHalf.z;
        }

        static bool Loose(GearItem gearItem) =>
            gearItem != null && gearItem.gameObject.activeInHierarchy && !gearItem.m_InPlayerInventory && !gearItem.m_InsideContainer;

        static bool BeingPlaced(GearItem gearItem)
        {
            var playerManager = GameManager.GetPlayerManagerComponent();
            return playerManager != null && playerManager.m_ObjectToPlace != null && playerManager.m_ObjectToPlace == gearItem.gameObject;
        }

        static void Adopt(Transform train, GearItem gearItem)
        {
            gearItem.transform.SetParent(Holder(train), true);
            var body = gearItem.GetComponent<Rigidbody>();
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

        /// Where the game aimed the item being placed, relative to the cab. The train moves after the game has aimed
        /// it, so the item is put back at that place in the cab (CarryPlacement) instead of staying behind.
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
            point.x = Mathf.Clamp(point.x, -1.2f, 1.2f);
            point.z = Mathf.Clamp(point.z - 0.35f, -4.2f, -2.0f);       
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

        /// Every frame: an item that the game detached or made physical again (a lantern waking up, an item put down
        /// after being moved with the place control) is taken back at once, or the moving train leaves it behind.
        static void KeepAttached(Transform train)
        {
            for (int index = 0; index < s_Items.Count; index++)
            {
                var gearItem = s_Items[index];
                if (gearItem == null || !Loose(gearItem)) continue;
                if (BeingPlaced(gearItem))
                {
                    s_HeldPoses[gearItem] = PoseInTrain(train, gearItem.transform);
                    continue;
                }
                if (!InCab(train, gearItem.transform.position)) continue;
                if (!gearItem.transform.IsChildOf(train)) gearItem.transform.SetParent(Holder(train), true);
                if (s_HeldPoses.TryGetValue(gearItem, out var heldPose))
                {
                    // something in the game (a lantern settling after being put down) keeps moving the item: hold it
                    if ((train.InverseTransformPoint(gearItem.transform.position) - heldPose.Key).sqrMagnitude > 0.000001f) ApplyPose(train, gearItem.transform, heldPose);
                }
                var body = gearItem.GetComponent<Rigidbody>();
                if (body != null && !body.isKinematic) { body.velocity = Vector3.zero; body.isKinematic = true; }
            }
        }

        /// Finds items newly put down in the cab and lets go of items that left it (picked up, moved out).
        public static void Update(Transform train)
        {
            if (train == null) return;
            CarryPlacement(train);
            KeepAttached(train);
            if (Time.time < s_NextScan) return;
            s_NextScan = Time.time + ScanInterval;

            for (int index = s_Items.Count - 1; index >= 0; index--)
            {
                var gearItem = s_Items[index];
                if (gearItem == null) { s_Items.RemoveAt(index); continue; }
                if (BeingPlaced(gearItem)) continue;
                bool attached = gearItem.transform.IsChildOf(train);
                if (!Loose(gearItem) || !attached || !InCab(train, gearItem.transform.position))
                {
                    // picked up, put in a container, or moved out of the cab: an ordinary item again
                    if (attached && Loose(gearItem)) gearItem.transform.SetParent(null, true);
                    s_Items.RemoveAt(index);
                }
            }

            AdoptNewItems(train);
        }

        /// Takes in what was just put down in the cab, without waiting for the next scan: at speed the train would
        /// have carried its cab away from the item by then.
        public static void ScanNow(Transform train)
        {
            if (train == null) return;
            AdoptNewItems(train);
            s_NextScan = Time.time + ScanInterval;
        }

        static void AdoptNewItems(Transform train)
        {
            var center = train.TransformPoint(BoxCenter);
            foreach (var collider in Physics.OverlapBox(center, BoxHalf, train.rotation, -1, QueryTriggerInteraction.Collide))
            {
                if (collider == null) continue;
                var gearItem = collider.GetComponentInParent<GearItem>();
                if (gearItem == null || s_Items.Contains(gearItem) || !Loose(gearItem) || BeingPlaced(gearItem)) continue;
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
                    var lp = train.InverseTransformPoint(gearItem.transform.position);
                    var lr = Quaternion.Inverse(train.rotation) * gearItem.transform.rotation;
                    list.Add(new CabItemState
                    {
                        Prefab = gearItem.name.Replace("(Clone)", "").Trim(),
                        Data = gearItem.SerializeToString(),
                        Pos = new[] { lp.x, lp.y, lp.z },
                        Rot = new[] { lr.x, lr.y, lr.z, lr.w },
                    });
                }
                catch (System.Exception e) { SteamRailwayMod.Debug($"Cab item {gearItem.name} not saved: {e.Message}"); }
            }
            return list;
        }

        /// Puts the saved items back into a freshly built train.
        public static void Restore(Transform train, List<CabItemState> items)
        {
            if (train == null || items == null) return;
            foreach (var itemState in items)
            {
                if (string.IsNullOrEmpty(itemState.Prefab) || itemState.Pos == null || itemState.Rot == null) continue;
                try
                {
                    var gearItem = GearItem.InstantiateAndDeserializeGearItem(itemState.Prefab, itemState.Data, null, false, false);
                    if (gearItem == null) { SteamRailwayMod.Log.Warning($"Cab item {itemState.Prefab} could not be made."); continue; }
                    gearItem.transform.SetPositionAndRotation(train.TransformPoint(new Vector3(itemState.Pos[0], itemState.Pos[1], itemState.Pos[2])),
                        train.rotation * new Quaternion(itemState.Rot[0], itemState.Rot[1], itemState.Rot[2], itemState.Rot[3]));
                    Adopt(train, gearItem);
                }
                catch (System.Exception e) { SteamRailwayMod.Log.Warning($"Cab item {itemState.Prefab} not restored: {e.Message}"); }
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
        public static void Clear() { s_Items.Clear(); s_HeldPoses.Clear(); }
    }
}
