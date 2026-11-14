using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace SteamRailway
{
    /// One object of a region changed with the object editor: hidden, or moved / turned.
    internal sealed class EditRecord
    {
        public string Name { get; set; }
        public string Parent { get; set; }       // for reading the file; the object is found by name and position
        public float[] Pos { get; set; }         // where the region placed it
        public float[] Rot { get; set; }         // quaternion x y z w
        public bool Hidden { get; set; }
        public float[] NewPos { get; set; }
        public float[] NewRot { get; set; }
    }

    internal sealed class EditFile
    {
        public int Version { get; set; } = 1;
        public List<EditRecord> Edits { get; set; } = new List<EditRecord>();
    }

    /// The object edits of every region, kept in Mods\SteamRailway\Edits\<region>.json. They belong to the mod,
    /// not to a save: every game the mod runs in gets the same changed regions. Applied when a region is built.
    internal static class ObjectEdits
    {
        const float MatchDist = 0.05f;
        static readonly JsonSerializerOptions s_Json = new JsonSerializerOptions { WriteIndented = true };

        static string s_Scene;
        static EditFile s_File = new EditFile();

        /// What was changed on an object in this region, so it can be put back exactly.
        sealed class Live
        {
            public Transform T;
            public Vector3 OrigPos;
            public Quaternion OrigRot;
            public EditRecord R;
            public readonly List<(Renderer r, bool was)> OffR = new List<(Renderer, bool)>();
            public readonly List<(Collider c, bool was)> OffC = new List<(Collider, bool)>();
            public readonly List<(LODGroup l, bool was)> OffL = new List<(LODGroup, bool)>();
            public readonly List<GameObject> Clones = new List<GameObject>();
            public readonly List<(string prefix, Vector3 pos)> Inst = new List<(string, Vector3)>();   // drawn by instancing
            public Bounds OrigBounds;                 // where the object stood (loot inside it follows it)
            public bool HasBounds;
            public readonly List<Loot> Loot = new List<Loot>();
        }

        /// A loose item or container lying in an edited object's area: it moves with the object, or is hidden with it.
        sealed class Loot
        {
            public Transform T;
            public Vector3 Pos;
            public Quaternion Rot;
            public readonly List<(Renderer r, bool was)> R = new List<(Renderer, bool)>();
            public readonly List<(Collider c, bool was)> C = new List<(Collider, bool)>();
        }

        static readonly HashSet<System.IntPtr> s_Claimed = new HashSet<System.IntPtr>();

        static readonly Dictionary<System.IntPtr, Live> s_Live = new Dictionary<System.IntPtr, Live>();

        static string FilePath(string scene) => Path.Combine(RouteStore.FolderPath, "Edits", scene + ".json");
        public static int Count => s_File.Edits.Count;

        static float[] F(Vector3 vector) => new[] { R3(vector.x), R3(vector.y), R3(vector.z) };
        static float[] F(Quaternion q) => new[] { R5(q.x), R5(q.y), R5(q.z), R5(q.w) };
        static float R3(float factor) => (float)System.Math.Round(factor, 3);
        static float R5(float factor) => (float)System.Math.Round(factor, 5);
        static Vector3 V(float[] a) => a != null && a.Length >= 3 ? new Vector3(a[0], a[1], a[2]) : Vector3.zero;
        static Quaternion Q(float[] a) => a != null && a.Length >= 4 ? new Quaternion(a[0], a[1], a[2], a[3]) : Quaternion.identity;

        /// Reads the region's edits and applies them (call when the region is built, before the railway).
        public static void ApplyScene(string scene)
        {
            foreach (var copy in s_Copies) if (copy.Go != null) Object.Destroy(copy.Go);
            s_Copies.Clear();
            if (scene != s_Scene) { s_Live.Clear(); s_Claimed.Clear(); s_Scene = scene; }
            s_SceneStart = Time.time;
            s_File = new EditFile();
            var path = FilePath(scene);
            if (File.Exists(path))
            {
                try { s_File = JsonSerializer.Deserialize<EditFile>(File.ReadAllText(path), s_Json) ?? new EditFile(); }
                catch (System.Exception e) { SteamRailwayMod.Log.Warning($"{path} could not be read: {e.Message}"); s_File = new EditFile(); }
            }
            if (s_File.Edits == null) s_File.Edits = new List<EditRecord>();
            if (s_File.Edits.Count == 0) return;

            var byName = new Dictionary<string, List<Transform>>();
            foreach (var transform in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (transform.gameObject.scene.name == null) continue;
                if (!byName.TryGetValue(transform.name, out var points)) byName[transform.name] = points = new List<Transform>();
                points.Add(transform);
            }
            int applied = 0, missing = 0;
            foreach (var editRecord in s_File.Edits)
            {
                if (editRecord.Parent == InstTag) { ApplyInstance(editRecord); applied++; continue; }
                if (editRecord.Parent == CopyTag) { if (SpawnCopy(editRecord) != null) applied++; else missing++; continue; }
                var transform = Find(byName, editRecord);
                if (transform == null) { missing++; continue; }
                try { Put(transform, editRecord); applied++; }
                catch (System.Exception e) { SteamRailwayMod.Log.Warning($"Edit of {editRecord.Name}: {e.Message}"); }
            }
            SteamRailwayMod.Log.Msg($"{scene}: {applied} object edit(s) applied" + (missing > 0 ? $", {missing} object(s) not found." : "."));
        }

        static Live NewLive(Transform transform)
        {
            var live = new Live { T = transform, OrigPos = transform.position, OrigRot = transform.rotation };
            live.OrigBounds = BoundsOf(transform, out live.HasBounds);
            live.Inst.Add((InstancedHider.Prefix(transform.name), transform.position));
            int count = 0;
            foreach (var d in transform.GetComponentsInChildren<Transform>(true))
            {
                if (d == transform || !d.name.Contains("_Prefab") || count++ > 300) continue;
                live.Inst.Add((InstancedHider.Prefix(d.name), d.position));
            }
            s_Live[transform.Pointer] = live;
            return live;
        }

        public static Bounds BoundsOf(Transform transform, out bool any)
        {
            any = false;
            var otherBounds = new Bounds(transform.position, Vector3.zero);
            foreach (var renderer in transform.GetComponentsInChildren<Renderer>(false))
            {
                if (!renderer.enabled || renderer is ParticleSystemRenderer) continue;
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

        // ---- copies of the region's objects (a missing bridge piece, another fence ...)

        const string CopyTag = "@copy";

        sealed class CopyLive { public GameObject Go; public EditRecord R; }
        static readonly List<CopyLive> s_Copies = new List<CopyLive>();

        public static bool IsCopy(Transform transform) => transform != null && transform.name.StartsWith("SR_Copy_");

        /// The region object a copy is made from: same name, nearest place, then nearest turn (pieces of a curved
        /// bridge share one point and differ only in their turn).
        static Transform CopySource(EditRecord editRecord)
        {
            var point = V(editRecord.Pos); var q = Q(editRecord.Rot);
            Transform best = null; float bestDistance = float.MaxValue;
            foreach (var transform in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (transform.gameObject.scene.name == null || transform.name != editRecord.Name) continue;
                float distance = (transform.position - point).sqrMagnitude * 100f + Quaternion.Angle(transform.rotation, q);
                if (distance < bestDistance) { bestDistance = distance; best = transform; }
            }
            return best;
        }

        static GameObject SpawnCopy(EditRecord editRecord)
        {
            var source = CopySource(editRecord);
            if (source == null) return null;
            var targetObject = Object.Instantiate(source.gameObject);
            targetObject.name = "SR_Copy_" + editRecord.Name;
            targetObject.transform.SetPositionAndRotation(V(editRecord.NewPos), Q(editRecord.NewRot));
            // the original may be one the railway hid (a replaced rail piece): the copy is shown
            if (!targetObject.activeSelf) targetObject.SetActive(true);
            foreach (var renderer in targetObject.GetComponentsInChildren<Renderer>(true)) renderer.enabled = true;
            foreach (var lodGroup in targetObject.GetComponentsInChildren<LODGroup>(true)) lodGroup.enabled = true;
            foreach (var collider in targetObject.GetComponentsInChildren<Collider>(true)) collider.enabled = true;
            s_Copies.Add(new CopyLive { Go = targetObject, R = editRecord });
            return targetObject;
        }

        /// A copy of a region object, standing where the original stands (the editor then carries it away).
        public static Transform AddCopy(Transform source)
        {
            var editRecord = new EditRecord
            {
                Name = source.name, Parent = CopyTag, Pos = F(OriginalPosition(source)), Rot = F(OriginalRotation(source)),
                NewPos = F(source.position), NewRot = F(source.rotation),
            };
            var targetObject = SpawnCopy(editRecord);
            if (targetObject == null) return null;
            s_File.Edits.Add(editRecord);
            Save();
            return targetObject.transform;
        }

        /// A copy was put down somewhere new.
        public static void MoveCopy(Transform copy)
        {
            var c = s_Copies.Find(x => x.Go != null && x.Go.transform == copy);
            if (c == null) return;
            c.R.NewPos = F(copy.position);
            c.R.NewRot = F(copy.rotation);
            Save();
        }

        /// Puts a copy back where it was before it was picked up.
        public static void ResetCopy(Transform copy)
        {
            var c = s_Copies.Find(x => x.Go != null && x.Go.transform == copy);
            if (c != null) copy.SetPositionAndRotation(V(c.R.NewPos), Q(c.R.NewRot));
        }

        public static bool RemoveCopy(Transform copy)
        {
            var c = s_Copies.Find(x => x.Go != null && x.Go.transform == copy);
            if (c == null) return false;
            s_File.Edits.Remove(c.R);
            s_Copies.Remove(c);
            Object.Destroy(c.Go);
            Save();
            return true;
        }

        // ---- objects the game draws by GPU instancing and that have no scene object (many trees, logs, rocks)

        const string InstTag = "@instance";

        static void ApplyInstance(EditRecord editRecord)
        {
            var position = V(editRecord.Pos);
            if (editRecord.Hidden) { InstancedHider.Set(editRecord.Name, position, false, Matrix4x4.identity); return; }
            var delta = Matrix4x4.TRS(V(editRecord.NewPos), Q(editRecord.NewRot), Vector3.one) * Matrix4x4.TRS(position, Q(editRecord.Rot), Vector3.one).inverse;
            InstancedHider.Set(editRecord.Name, position, true, delta);
        }

        public static EditRecord InstRecord(InstancedHider.InstPick instancePick)
        {
            foreach (var editRecord in s_File.Edits)
                if (editRecord.Parent == InstTag && editRecord.Name == instancePick.Prefix && (V(editRecord.Pos) - instancePick.OrigPos).sqrMagnitude <= MatchDist * MatchDist) return editRecord;
            return null;
        }

        public static EditRecord InstHidden(InstancedHider.InstPick instancePick) => new EditRecord
        { Name = instancePick.Prefix, Parent = InstTag, Pos = F(instancePick.OrigPos), Rot = F(instancePick.OrigRot), Hidden = true };

        public static EditRecord InstMoved(InstancedHider.InstPick instancePick, Vector3 position, Quaternion rotation) => new EditRecord
        { Name = instancePick.Prefix, Parent = InstTag, Pos = F(instancePick.OrigPos), Rot = F(instancePick.OrigRot), NewPos = F(position), NewRot = F(rotation) };

        /// While the editor carries an instanced object (nothing is written yet).
        public static void PreviewInstance(InstancedHider.InstPick instancePick, Vector3 position, Quaternion rotation)
        {
            var delta = Matrix4x4.TRS(position, rotation, Vector3.one) * Matrix4x4.TRS(instancePick.OrigPos, instancePick.OrigRot, Vector3.one).inverse;
            InstancedHider.Set(instancePick.Prefix, instancePick.OrigPos, true, delta);
        }

        /// Puts an instanced object into the state of `r` (null = as the region made it) and writes the file.
        public static void SetInstance(InstancedHider.InstPick instancePick, EditRecord editRecord)
        {
            s_File.Edits.RemoveAll(x => x.Parent == InstTag && x.Name == instancePick.Prefix && (V(x.Pos) - instancePick.OrigPos).sqrMagnitude <= MatchDist * MatchDist);
            if (editRecord == null) InstancedHider.Remove(instancePick.Prefix, instancePick.OrigPos);
            else { s_File.Edits.Add(editRecord); ApplyInstance(editRecord); }
            Save();
        }

        static Transform Find(Dictionary<string, List<Transform>> byName, EditRecord editRecord)
        {
            if (editRecord.Name == null || !byName.TryGetValue(editRecord.Name, out var cands)) return null;
            var point = V(editRecord.Pos);
            Transform best = null; float bestDistance = MatchDist * MatchDist;
            foreach (var child in cands)
            {
                // an object already moved this session is known by where it was
                var live = s_Live.TryGetValue(child.Pointer, out var lv) ? lv : null;
                var origin = live != null ? live.OrigPos : child.position;
                float distance = (origin - point).sqrMagnitude;
                if (distance <= bestDistance) { bestDistance = distance; best = child; }
            }
            return best;
        }

        /// The record of an object (null: as the region made it).
        public static EditRecord RecordOf(Transform transform)
        {
            if (transform == null) return null;
            return s_Live.TryGetValue(transform.Pointer, out var live) ? live.R : null;
        }

        public static Vector3 OriginalPosition(Transform transform) => s_Live.TryGetValue(transform.Pointer, out var live) ? live.OrigPos : transform.position;
        public static Quaternion OriginalRotation(Transform transform) => s_Live.TryGetValue(transform.Pointer, out var live) ? live.OrigRot : transform.rotation;

        public static EditRecord Hidden(Transform transform) => new EditRecord
        {
            Name = transform.name, Parent = transform.parent != null ? transform.parent.name : null,
            Pos = F(OriginalPosition(transform)), Rot = F(OriginalRotation(transform)), Hidden = true,
        };

        public static EditRecord Moved(Transform transform, Vector3 position, Quaternion rotation) => new EditRecord
        {
            Name = transform.name, Parent = transform.parent != null ? transform.parent.name : null,
            Pos = F(OriginalPosition(transform)), Rot = F(OriginalRotation(transform)), NewPos = F(position), NewRot = F(rotation),
        };

        /// Puts an object into the state of `r` (null = as the region made it) and writes the region's file.
        public static void Set(Transform transform, EditRecord editRecord)
        {
            if (transform == null) return;
            var orig = OriginalPosition(transform);
            Put(transform, editRecord);
            s_File.Edits.RemoveAll(x => x.Name == transform.name && (V(x.Pos) - orig).sqrMagnitude <= MatchDist * MatchDist);
            if (editRecord != null) s_File.Edits.Add(editRecord);
            Save();
        }

        static void Save()
        {
            if (s_Scene == null) return;
            try
            {
                var path = FilePath(s_Scene);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonSerializer.Serialize(s_File, s_Json));
            }
            catch (System.Exception e) { SteamRailwayMod.Log.Warning($"Writing the object edits failed: {e.Message}"); }
        }

        static void Put(Transform transform, EditRecord editRecord)
        {
            if (!s_Live.TryGetValue(transform.Pointer, out var live))
            {
                if (editRecord == null) return;
                live = NewLive(transform);
            }
            Restore(live);
            live.R = editRecord;
            if (editRecord == null) { s_Live.Remove(transform.Pointer); return; }

            if (editRecord.Hidden)
            {
                HideAll(live);
                foreach (var (prefix, originalPoint) in live.Inst) InstancedHider.Set(prefix, originalPoint, false, Matrix4x4.identity);
                Claim(live);
                HideLoot(live, true);
                return;
            }

            // moved: statically batched meshes cannot move with their object; they get movable copies first
            Detach(live);
            var position = V(editRecord.NewPos);
            var rotation = Q(editRecord.NewRot);
            transform.SetPositionAndRotation(position, rotation);
            SetInstanced(live);
            Claim(live);
            PoseLoot(live);
        }

        /// Everything that draws or collides in the object (every renderer, whatever its state, is held off).
        static void HideAll(Live live)
        {
            var transform = live.T;
            foreach (var renderer in transform.GetComponentsInChildren<Renderer>(true)) { live.OffR.Add((renderer, renderer.enabled)); renderer.enabled = false; }
            foreach (var lodGroup in transform.GetComponentsInChildren<LODGroup>(true)) { live.OffL.Add((lodGroup, lodGroup.enabled)); lodGroup.enabled = false; }
            foreach (var collider in transform.GetComponentsInChildren<Collider>(true)) { live.OffC.Add((collider, collider.enabled)); collider.enabled = false; }
        }

        static void SetInstanced(Live live)
        {
            var transform = live.T;
            var delta = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one) * Matrix4x4.TRS(live.OrigPos, live.OrigRot, Vector3.one).inverse;
            foreach (var (prefix, originalPoint) in live.Inst) InstancedHider.Set(prefix, originalPoint, true, delta);
        }

        // ------------------------------------------------------------------ loot

        static Bounds ReachOf(Live live)
        {
            var bounds = live.OrigBounds;
            var lo = bounds.min - new Vector3(0.6f, 0.3f, 0.6f);
            var hi = bounds.max + new Vector3(0.6f, 1.2f, 0.6f);
            bounds.SetMinMax(lo, hi);
            return bounds;
        }

        /// Loose items and containers lying in the object's original area (not in the player's hands, not inside a
        /// container, not dropped by the player) are taken along: they move with it and hide with it.
        static void Claim(Live live)
        {
            if (!live.HasBounds || live.T == null) return;
            var reach = ReachOf(live);
            bool moved = live.R != null && !live.R.Hidden;
            bool now = false;
            var currentBounds = moved ? BoundsOf(live.T, out now) : default(Bounds);
            // a physics query around the object (scanning every item of the scene costs tens of milliseconds)
            var hits = Physics.OverlapBox(reach.center, reach.extents, Quaternion.identity, ~0, QueryTriggerInteraction.Collide);
            foreach (var collider in hits)
            {
                if (collider == null) continue;
                var gearItem = collider.GetComponentInParent<Il2Cpp.GearItem>();
                if (gearItem != null)
                {
                    if (gearItem.m_InPlayerInventory || gearItem.m_BeenInPlayerInventory || gearItem.m_InsideContainer || gearItem.m_IsInSatchel) continue;
                    TryClaim(live, gearItem.transform, reach, moved && now, currentBounds);
                    continue;
                }
                var container = collider.GetComponentInParent<Il2Cpp.Container>();
                if (container != null) TryClaim(live, container.transform, reach, moved && now, currentBounds);
            }
        }

        static void TryClaim(Live live, Transform item, Bounds reach, bool checkNow, Bounds currentBounds)
        {
            if (item == null || item == live.T || item.IsChildOf(live.T) || live.T.IsChildOf(item) || s_Claimed.Contains(item.Pointer)) return;
            var point = item.position;
            if (!reach.Contains(point)) return;
            // already standing in the moved object (saved there): it has been placed before
            if (checkNow && currentBounds.Contains(point)) return;
            var loot = new Loot { T = item, Pos = point, Rot = item.rotation };
            foreach (var renderer in item.GetComponentsInChildren<Renderer>(true)) loot.R.Add((renderer, renderer.enabled));
            foreach (var collider in item.GetComponentsInChildren<Collider>(true)) loot.C.Add((collider, collider.enabled));
            live.Loot.Add(loot);
            s_Claimed.Add(item.Pointer);
        }

        static void PoseLoot(Live live)
        {
            if (live.T == null || live.Loot.Count == 0) return;
            var matrix = Matrix4x4.TRS(live.T.position, live.T.rotation, Vector3.one) * Matrix4x4.TRS(live.OrigPos, live.OrigRot, Vector3.one).inverse;
            var dq = live.T.rotation * Quaternion.Inverse(live.OrigRot);
            foreach (var loot in live.Loot)
            {
                if (loot.T == null) continue;
                loot.T.SetPositionAndRotation(matrix.MultiplyPoint3x4(loot.Pos), dq * loot.Rot);
                var body = loot.T.GetComponent<Rigidbody>();
                if (body != null && !body.isKinematic) { body.velocity = Vector3.zero; body.angularVelocity = Vector3.zero; }
            }
        }

        static void HideLoot(Live live, bool hide)
        {
            foreach (var loot in live.Loot)
            {
                if (loot.T == null) continue;
                foreach (var (renderer, wasEnabled) in loot.R) if (renderer != null) renderer.enabled = hide ? false : wasEnabled;
                foreach (var (collider, was) in loot.C) if (collider != null) collider.enabled = hide ? false : was;
            }
        }

        static float s_NextTick, s_NextScan, s_SceneStart;
        static bool s_Carrying;

        /// A few times a second: items the game spawns later are taken along, and anything the game switched back
        /// on in a removed object is switched off again.
        public static void Tick()
        {
            if (s_Live.Count == 0 || Time.time < s_NextTick) return;
            s_NextTick = Time.time + 1.5f;
            foreach (var live in s_Live.Values)
            {
                if (live.T == null || live.R == null || !live.R.Hidden) continue;
                foreach (var (renderer, _) in live.OffR) if (renderer != null && renderer.enabled) renderer.enabled = false;
                foreach (var (collider, _) in live.OffC) if (collider != null && collider.enabled) collider.enabled = false;
                foreach (var (lodGroup, _) in live.OffL) if (lodGroup != null && lodGroup.enabled) lodGroup.enabled = false;
            }
            if (s_Carrying || Time.time < s_NextScan) return;
            // the scene scan: often while the region is new (loot is still being spawned), seldom afterwards
            s_NextScan = Time.time + (Time.time - s_SceneStart < 60f ? 3f : 12f);
            var all = new List<Live>(s_Live.Values);
            var firstPersonCamera = Il2Cpp.GameManager.GetVpFPSCamera();
            var from = firstPersonCamera != null ? firstPersonCamera.transform.position : Vector3.zero;
            foreach (var live in all)
            {
                if (live.T == null || live.R == null) continue;
                // only edits around the player (the scan costs a little each time)
                if (firstPersonCamera != null && live.HasBounds && live.OrigBounds.SqrDistance(from) > 150f * 150f) continue;
                int before = live.Loot.Count;
                Claim(live);
                if (live.Loot.Count != before && !live.R.Hidden) PoseLoot(live);
                if (live.R.Hidden) HideLoot(live, true);
            }
        }

        /// The editor picks an object up: its picture is made movable (nothing is written yet).
        public static void BeginMove(Transform transform)
        {
            if (!s_Live.TryGetValue(transform.Pointer, out var live)) live = NewLive(transform);
            s_Carrying = true;
            Detach(live);
            Claim(live);
        }

        public static void EndMove() => s_Carrying = false;

        /// While the editor carries an object: where it is now (no file written).
        public static void Preview(Transform transform, Vector3 position, Quaternion rotation)
        {
            if (!s_Live.TryGetValue(transform.Pointer, out var live)) return;
            transform.SetPositionAndRotation(position, rotation);
            SetInstanced(live);
            PoseLoot(live);
        }

        static void Restore(Live live)
        {
            var transform = live.T;
            foreach (var childObject in live.Clones) if (childObject != null) Object.Destroy(childObject);
            live.Clones.Clear();
            foreach (var (renderer, wasEnabled) in live.OffR) if (renderer != null) renderer.enabled = wasEnabled;
            foreach (var (lodGroup, was) in live.OffL) if (lodGroup != null) lodGroup.enabled = was;
            foreach (var (collider, was) in live.OffC) if (collider != null) collider.enabled = was;
            live.OffR.Clear(); live.OffL.Clear(); live.OffC.Clear();
            if (transform != null) transform.SetPositionAndRotation(live.OrigPos, live.OrigRot);
            // the loot goes back where it lay
            if (live.Loot.Count > 0)
            {
                HideLoot(live, false);
                PoseLoot(live);
                foreach (var loot in live.Loot) if (loot.T != null) s_Claimed.Remove(loot.T.Pointer);
                live.Loot.Clear();
            }
            foreach (var (prefix, originalPoint) in live.Inst) InstancedHider.Remove(prefix, originalPoint);
        }

        static bool HasRenderers(Transform transform) => transform.GetComponentsInChildren<Renderer>(true).Length > 0;

        /// Is part of the object statically batched (its picture fixed in a combined mesh)?
        public static bool IsStatic(Transform transform)
        {
            foreach (var meshRenderer in transform.GetComponentsInChildren<MeshRenderer>(true)) if (meshRenderer.isPartOfStaticBatch) return true;
            return false;
        }

        /// Makes the object's picture movable: a statically batched renderer is switched off and replaced by a
        /// child copy with its own mesh (cut out of the combined mesh, or the original mesh found by name).
        /// Objects with LODs keep only their most detailed level while they are moved.
        static void Detach(Live live)
        {
            var staticRoot = live.T;
            if (!IsStatic(staticRoot)) return;
            var lod0 = new HashSet<System.IntPtr>();
            bool hasLods = false;
            foreach (var lodGroup in staticRoot.GetComponentsInChildren<LODGroup>(true))
            {
                var lods = lodGroup.GetLODs();
                if (lods == null || lods.Length == 0) continue;
                hasLods = true;
                foreach (var renderer in lods[0].renderers) if (renderer != null) lod0.Add(renderer.Pointer);
                if (lodGroup.enabled) { lodGroup.enabled = false; live.OffL.Add((lodGroup, true)); }
            }
            int made = 0, failed = 0;
            foreach (var meshRenderer in staticRoot.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (!meshRenderer.enabled) continue;
                bool keep = !hasLods || lod0.Contains(meshRenderer.Pointer);
                if (!keep) { meshRenderer.enabled = false; live.OffR.Add((meshRenderer, true)); continue; }
                if (!meshRenderer.isPartOfStaticBatch) continue;
                var mesh = CopyMesh(meshRenderer);
                if (mesh == null) { failed++; continue; }
                var targetObject = new GameObject("SR_Moved_" + meshRenderer.gameObject.name);
                targetObject.layer = meshRenderer.gameObject.layer;
                targetObject.transform.SetParent(meshRenderer.transform, false);
                targetObject.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = targetObject.AddComponent<MeshRenderer>();
                mr.sharedMaterials = meshRenderer.sharedMaterials;
                mr.shadowCastingMode = meshRenderer.shadowCastingMode;
                mr.receiveShadows = meshRenderer.receiveShadows;
                live.Clones.Add(targetObject);
                meshRenderer.enabled = false;
                live.OffR.Add((meshRenderer, true));
                made++;
            }
            if (failed > 0)
                SteamRailwayMod.Log.Warning($"{staticRoot.name}: {failed} part(s) are fixed in the region's combined mesh and could not be copied; they stay where they were.");
            else if (made > 0) SteamRailwayMod.Debug($"{staticRoot.name}: {made} static part(s) copied so they can move.");
        }

        /// The renderer's own mesh in its local space.
        static Mesh CopyMesh(MeshRenderer meshRenderer)
        {
            var meshFilter = meshRenderer.GetComponent<MeshFilter>();
            var combined = meshFilter != null ? meshFilter.sharedMesh : null;
            if (combined != null && combined.isReadable)
            {
                try { var mesh = Cut(meshRenderer, combined); if (mesh != null) return mesh; }
                catch (System.Exception e) { SteamRailwayMod.Debug($"Cutting {meshRenderer.name} out of the combined mesh: {e.Message}"); }
            }
            return ByName(meshRenderer);
        }

        static Mesh Cut(MeshRenderer meshRenderer, Mesh combined)
        {
            int start = meshRenderer.subMeshStartIndex;
            int count = meshRenderer.sharedMaterials.Length;
            if (start < 0 || start + count > combined.subMeshCount) return null;
            var root = meshRenderer.staticBatchRootTransform;
            var toWorld = root != null ? root.localToWorldMatrix : Matrix4x4.identity;
            var toLocal = meshRenderer.transform.worldToLocalMatrix * toWorld;
            var sv = combined.vertices; var sn = combined.normals; var st = combined.tangents; var uvs = combined.uv;
            bool hasN = sn.Length == sv.Length, hasT = st.Length == sv.Length, hasU = uvs.Length == sv.Length;
            var map = new Dictionary<int, int>();
            var v = new List<Vector3>(); var n = new List<Vector3>(); var tg = new List<Vector4>(); var uv = new List<Vector2>();
            var subs = new List<int[]>();
            for (int s = start; s < start + count; s++)
            {
                var tris = combined.GetTriangles(s);
                var outT = new int[tris.Length];
                for (int index = 0; index < tris.Length; index++)
                {
                    int deepIndex = tris[index];
                    if (!map.TryGetValue(deepIndex, out int ni))
                    {
                        ni = v.Count; map[deepIndex] = ni;
                        v.Add(toLocal.MultiplyPoint3x4(sv[deepIndex]));
                        if (hasN) n.Add(toLocal.MultiplyVector(sn[deepIndex]).normalized);
                        if (hasT) { var direction = toLocal.MultiplyVector(new Vector3(st[deepIndex].x, st[deepIndex].y, st[deepIndex].z)).normalized; tg.Add(new Vector4(direction.x, direction.y, direction.z, st[deepIndex].w)); }
                        if (hasU) uv.Add(uvs[deepIndex]);
                    }
                    outT[index] = ni;
                }
                subs.Add(outT);
            }
            if (v.Count == 0) return null;
            var mesh = new Mesh { name = meshRenderer.name + "_Moved" };
            if (v.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(new Il2CppStructArray<Vector3>(v.ToArray()));
            if (hasN) mesh.SetNormals(new Il2CppStructArray<Vector3>(n.ToArray()));
            if (hasT) mesh.SetTangents(new Il2CppStructArray<Vector4>(tg.ToArray()));
            if (hasU) mesh.SetUVs(0, new Il2CppStructArray<Vector2>(uv.ToArray()));
            mesh.subMeshCount = subs.Count;
            for (int s = 0; s < subs.Count; s++) mesh.SetTriangles(new Il2CppStructArray<int>(subs[s]), s);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// The original mesh, if the game still has it loaded: same name as the object, same size and place.
        static Mesh ByName(MeshRenderer meshRenderer)
        {
            var want = meshRenderer.bounds;
            foreach (var mesh in Resources.FindObjectsOfTypeAll<Mesh>())
            {
                if (mesh == null || mesh.name != meshRenderer.gameObject.name || mesh.vertexCount == 0) continue;
                // the mesh's box, placed like the object, must cover the renderer's box
                var otherBounds = mesh.bounds;
                var w = new Bounds(meshRenderer.transform.TransformPoint(otherBounds.center), Vector3.zero);
                for (int index = 0; index < 8; index++)
                {
                    var c = otherBounds.center + Vector3.Scale(otherBounds.extents, new Vector3((index & 1) == 0 ? -1 : 1, (index & 2) == 0 ? -1 : 1, (index & 4) == 0 ? -1 : 1));
                    w.Encapsulate(meshRenderer.transform.TransformPoint(c));
                }
                if ((w.center - want.center).magnitude < 0.3f && (w.size - want.size).magnitude < 0.1f * want.size.magnitude + 0.2f) return mesh;
            }
            return null;
        }
    }
}
