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
        const float ScanInterval = 0.5f;
        const string HolderName = "CabItems";

        static readonly List<GearItem> s_Items = new List<GearItem>();
        static float s_NextScan;

        static Transform Holder(Transform train)
        {
            var h = train.Find(HolderName);
            if (h == null)
            {
                h = new GameObject(HolderName).transform;
                h.SetParent(train, false);
            }
            return h;
        }

        static bool InCab(Transform train, Vector3 world)
        {
            var p = train.InverseTransformPoint(world) - BoxCenter;
            return Mathf.Abs(p.x) <= BoxHalf.x && Mathf.Abs(p.y) <= BoxHalf.y && Mathf.Abs(p.z) <= BoxHalf.z;
        }

        static bool Loose(GearItem gi) =>
            gi != null && gi.gameObject.activeInHierarchy && !gi.m_InPlayerInventory && !gi.m_InsideContainer;

        static bool BeingPlaced(GearItem gi)
        {
            var pm = GameManager.GetPlayerManagerComponent();
            return pm != null && pm.m_ObjectToPlace != null && pm.m_ObjectToPlace == gi.gameObject;
        }

        static void Adopt(Transform train, GearItem gi)
        {
            gi.transform.SetParent(Holder(train), true);
            var rb = gi.GetComponent<Rigidbody>();
            if (rb != null) { rb.velocity = Vector3.zero; rb.isKinematic = true; }
            if (!s_Items.Contains(gi)) s_Items.Add(gi);
        }

        const float FloorTop = 1.63f;  

        /// Sets a dropped item down on the cab floor in front of the player's feet and keeps it with the train.
        public static void PutOnFloor(Transform train, GameObject go, Vector3 feetWorld)
        {
            var gi = go.GetComponent<GearItem>();
            if (gi == null || gi.m_InPlayerInventory) return;
            var p = train.InverseTransformPoint(feetWorld);
            p.x = Mathf.Clamp(p.x, -1.2f, 1.2f);
            p.z = Mathf.Clamp(p.z - 0.35f, -4.2f, -2.0f);       
            var rb = go.GetComponent<Rigidbody>();
            if (rb != null) { rb.velocity = Vector3.zero; rb.isKinematic = true; }
            float yaw = (Quaternion.Inverse(train.rotation) * go.transform.rotation).eulerAngles.y;
            go.transform.rotation = train.rotation * Quaternion.Euler(0f, yaw, 0f);
            go.transform.position = train.TransformPoint(new Vector3(p.x, FloorTop, p.z));
            float lowest = float.MaxValue;
            foreach (var r in go.GetComponentsInChildren<Renderer>()) lowest = Mathf.Min(lowest, r.bounds.min.y);
            if (lowest < float.MaxValue) go.transform.position += Vector3.up * (train.TransformPoint(new Vector3(0f, FloorTop, 0f)).y - lowest);
            Adopt(train, gi);
        }

        /// Finds items newly put down in the cab and lets go of items that left it (picked up, moved out).
        public static void Update(Transform train)
        {
            if (train == null || Time.time < s_NextScan) return;
            s_NextScan = Time.time + ScanInterval;

            for (int i = s_Items.Count - 1; i >= 0; i--)
            {
                var gi = s_Items[i];
                if (gi == null) { s_Items.RemoveAt(i); continue; }
                if (BeingPlaced(gi)) continue;
                bool attached = gi.transform.IsChildOf(train);
                if (!Loose(gi) || !attached || !InCab(train, gi.transform.position))
                {
                    // picked up, put in a container, or moved out of the cab: an ordinary item again
                    if (attached && Loose(gi)) gi.transform.SetParent(null, true);
                    s_Items.RemoveAt(i);
                }
            }

            var center = train.TransformPoint(BoxCenter);
            foreach (var col in Physics.OverlapBox(center, BoxHalf, train.rotation, -1, QueryTriggerInteraction.Collide))
            {
                if (col == null) continue;
                var gi = col.GetComponentInParent<GearItem>();
                if (gi == null || s_Items.Contains(gi) || !Loose(gi) || BeingPlaced(gi)) continue;
                if (!InCab(train, gi.transform.position)) continue;
                Adopt(train, gi);
                SteamRailwayMod.Debug($"Cab item: {gi.name}");
            }
        }

        public static List<CabItemState> Capture(Transform train)
        {
            var list = new List<CabItemState>();
            if (train == null) return list;
            foreach (var gi in s_Items)
            {
                if (gi == null || !Loose(gi) || !gi.transform.IsChildOf(train)) continue;
                try
                {
                    var lp = train.InverseTransformPoint(gi.transform.position);
                    var lr = Quaternion.Inverse(train.rotation) * gi.transform.rotation;
                    list.Add(new CabItemState
                    {
                        Prefab = gi.name.Replace("(Clone)", "").Trim(),
                        Data = gi.SerializeToString(),
                        Pos = new[] { lp.x, lp.y, lp.z },
                        Rot = new[] { lr.x, lr.y, lr.z, lr.w },
                    });
                }
                catch (System.Exception e) { SteamRailwayMod.Debug($"Cab item {gi.name} not saved: {e.Message}"); }
            }
            return list;
        }

        /// Puts the saved items back into a freshly built train.
        public static void Restore(Transform train, List<CabItemState> items)
        {
            if (train == null || items == null) return;
            foreach (var st in items)
            {
                if (string.IsNullOrEmpty(st.Prefab) || st.Pos == null || st.Rot == null) continue;
                try
                {
                    var gi = GearItem.InstantiateAndDeserializeGearItem(st.Prefab, st.Data, null, false, false);
                    if (gi == null) { SteamRailwayMod.Log.Warning($"Cab item {st.Prefab} could not be made."); continue; }
                    gi.transform.SetPositionAndRotation(train.TransformPoint(new Vector3(st.Pos[0], st.Pos[1], st.Pos[2])),
                        train.rotation * new Quaternion(st.Rot[0], st.Rot[1], st.Rot[2], st.Rot[3]));
                    Adopt(train, gi);
                }
                catch (System.Exception e) { SteamRailwayMod.Log.Warning($"Cab item {st.Prefab} not restored: {e.Message}"); }
            }
        }

        /// The cab items are left out of the region's gear save (they are saved with the train).
        public static List<GearItem> HideFromRegionSave()
        {
            var hidden = new List<GearItem>();
            foreach (var gi in s_Items)
            {
                if (gi == null) continue;
                GearManager.Remove(gi);
                hidden.Add(gi);
            }
            return hidden;
        }

        public static void Unhide(List<GearItem> hidden)
        {
            if (hidden == null) return;
            foreach (var gi in hidden) if (gi != null) GearManager.Add(gi);
        }

        /// A new game or a region change: the list belongs to the old train.
        public static void Clear() => s_Items.Clear();
    }
}
