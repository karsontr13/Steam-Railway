using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace SteamRailway
{
    /// One object of a region changed with the object editor: hidden, or moved / turned.
    internal sealed class EditRecord
    {
        public string Name { get; set; }
        public string Parent { get; set; }       // for reading the file; the object is found by name and position
        [JsonPropertyName("Pos")] public float[] RegionPosition { get; set; }         // where the region placed it
        [JsonPropertyName("Rot")] public float[] RegionRotation { get; set; }         // quaternion x y z w
        public bool Hidden { get; set; }
        [JsonPropertyName("NewPos")] public float[] NewPosition { get; set; }
        [JsonPropertyName("NewRot")] public float[] NewRotation { get; set; }
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
        const float MatchDistanceInMeters = 0.05f;
        const float NamedMatchScale = 10f;
        const int PositionDecimalPlaces = 3;
        const int RotationDecimalPlaces = 5;
        const int Vector3ComponentCount = 3;
        const int QuaternionComponentCount = 4;
        const int QuaternionScalarIndex = 3;
        const int MaximumPrefabDescendantsCounted = 300;
        const float CopySourcePositionWeight = 100f;
        const float ReachMarginHorizontalInMeters = 0.6f;
        const float ReachMarginBelowInMeters = 0.3f;
        const float ReachMarginAboveInMeters = 1.2f;
        const float TickIntervalInSeconds = 1.5f;
        const float NewRegionDurationInSeconds = 60f;
        const float NewRegionScanIntervalInSeconds = 3f;
        const float SettledScanIntervalInSeconds = 12f;
        const float ScanRadiusInMeters = 150f;
        const int MaximumSixteenBitVertexCount = 65000;
        const int BoundsCornerCount = 8;
        const int CornerMaskX = 1;
        const int CornerMaskY = 2;
        const int CornerMaskZ = 4;
        const float BoundsCenterToleranceInMeters = 0.3f;
        const float BoundsSizeToleranceRelative = 0.1f;
        const float BoundsSizeToleranceAbsoluteInMeters = 0.2f;
        static readonly JsonSerializerOptions s_Json = new JsonSerializerOptions { WriteIndented = true };

        static string s_Scene;
        static EditFile s_File = new EditFile();

        /// What was changed on an object in this region, so it can be put back exactly.
        sealed class Live
        {
            public Transform EditedTransform;
            public Vector3 OriginalPosition;
            public Quaternion OriginalRotation;
            public EditRecord Record;
            public readonly List<(Renderer renderer, bool wasEnabled)> DisabledRenderers = new List<(Renderer, bool)>();
            public readonly List<(Collider collider, bool wasEnabled)> DisabledColliders = new List<(Collider, bool)>();
            public readonly List<(LODGroup lodGroup, bool wasEnabled)> DisabledLodGroups = new List<(LODGroup, bool)>();
            public readonly List<GameObject> Clones = new List<GameObject>();
            public readonly List<(string prefix, Vector3 originalPoint)> InstancedObjects = new List<(string, Vector3)>();   // drawn by instancing
            public Bounds OriginalBounds;                 // where the object stood (loot inside it follows it)
            public bool HasBounds;
            public readonly List<Loot> Loot = new List<Loot>();
        }

        /// A loose item or container lying in an edited object's area: it moves with the object, or is hidden with it.
        sealed class Loot
        {
            public Transform LootTransform;
            public Vector3 OriginalPosition;
            public Quaternion OriginalRotation;
            public readonly List<(Renderer renderer, bool wasEnabled)> Renderers = new List<(Renderer, bool)>();
            public readonly List<(Collider collider, bool wasEnabled)> Colliders = new List<(Collider, bool)>();
        }

        static readonly HashSet<System.IntPtr> s_Claimed = new HashSet<System.IntPtr>();

        static readonly Dictionary<System.IntPtr, Live> s_Live = new Dictionary<System.IntPtr, Live>();

        static string FilePath(string scene) => Path.Combine(RouteStore.FolderPath, "Edits", scene + ".json");
        public static int Count => s_File.Edits.Count;

        static float[] RoundedComponents(Vector3 vector) => new[] { RoundPositionComponent(vector.x), RoundPositionComponent(vector.y), RoundPositionComponent(vector.z) };
        static float[] RoundedComponents(Quaternion rotation) => new[] { RoundRotationComponent(rotation.x), RoundRotationComponent(rotation.y), RoundRotationComponent(rotation.z), RoundRotationComponent(rotation.w) };
        static float RoundPositionComponent(float component) => (float)System.Math.Round(component, PositionDecimalPlaces);
        static float RoundRotationComponent(float component) => (float)System.Math.Round(component, RotationDecimalPlaces);
        static Vector3 ToVector3(float[] components) => components != null && components.Length >= Vector3ComponentCount ? new Vector3(components[0], components[1], components[2]) : Vector3.zero;
        static Quaternion ToQuaternion(float[] components) => components != null && components.Length >= QuaternionComponentCount ? new Quaternion(components[0], components[1], components[2], components[QuaternionScalarIndex]) : Quaternion.identity;

        /// Reads the region's edits and applies them (call when the region is built, before the railway).
        public static void ApplyScene(string scene)
        {
            foreach (var copy in s_Copies) if (copy.CopiedObject != null) Object.Destroy(copy.CopiedObject);
            s_Copies.Clear();
            s_TreeObjects.Clear();
            s_WagonObjects.Clear();
            s_LooseLogs.Clear();
            LooseLogsKnown = false;
            s_HeldTargets.Clear();
            if (scene != s_Scene) { s_Live.Clear(); s_Claimed.Clear(); s_Scene = scene; }
            s_SceneStart = Time.time;
            s_File = new EditFile();
            var path = FilePath(scene);
            if (File.Exists(path))
            {
                try { s_File = JsonSerializer.Deserialize<EditFile>(File.ReadAllText(path), s_Json) ?? new EditFile(); }
                catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"{path} could not be read: {exception.Message}"); s_File = new EditFile(); }
            }
            if (s_File.Edits == null) s_File.Edits = new List<EditRecord>();
            if (s_File.Edits.Count == 0) return;

            var byName = NamesInScene();
            s_Locations.Clear();
            s_HeldTargets.Clear();
            FindMarkedTrees(byName);
            FindMovableWagons(byName);
            foreach (var editRecord in s_File.Edits) LocationOf(editRecord, byName);
            int applied = 0, missing = 0, held = 0;
            foreach (var editRecord in s_File.Edits)
            {
                if (IsHeldBack(editRecord))
                {
                    held++;
                    // found now, while the region's name table exists: clearing the obstacle later needs no scene search
                    if (editRecord.Parent != InstTag && editRecord.Parent != CopyTag) s_HeldTargets[editRecord] = Find(byName, editRecord);
                    continue;
                }
                ApplyRecord(editRecord, byName, ref applied, ref missing);
            }
            SteamRailwayMod.Log.Msg($"{scene}: {applied} object edit(s) applied" + (missing > 0 ? $", {missing} object(s) not found" : "") + (held > 0 ? $", {held} held back by obstacles." : "."));
        }

        /// Hard mode: an obstacle was cleared, so its area gets the region's object edits now.
        public static void ApplyWithin(string scene, ObstacleData obstacle)
        {
            if (scene != s_Scene || s_File.Edits.Count == 0) return;
            Dictionary<string, List<Transform>> byName = null;
            int applied = 0, missing = 0;
            foreach (var editRecord in s_File.Edits)
            {
                var location = KnownLocation(editRecord);
                if (!Obstacles.Contains(obstacle, location.x, location.z)) continue;
                if (IsHeldBack(editRecord)) continue;
                ApplyHeldRecord(editRecord, ref byName, ref applied, ref missing);
            }
            SteamRailwayMod.Log.Msg($"{obstacle.Identifier}: {applied} object edit(s) applied after clearing" + (missing > 0 ? $", {missing} object(s) not found." : "."));
        }

        /// The edit of one object (a fallen tree a train has run into) is applied now.
        public static void ApplyNamed(string scene, string name, float[] position)
        {
            if (scene != s_Scene || position == null || position.Length < Vector3ComponentCount) return;
            var point = new Vector3(position[0], position[1], position[2]);
            Dictionary<string, List<Transform>> byName = null;
            int applied = 0, missing = 0;
            foreach (var editRecord in s_File.Edits)
                if (editRecord.Name == name && Vector3.Distance(ToVector3(editRecord.RegionPosition), point) <= MatchDistanceInMeters * NamedMatchScale)
                    ApplyHeldRecord(editRecord, ref byName, ref applied, ref missing);
        }

        /// What clearing the obstacle will change: the objects of its held-back edits (found when the region was built)
        /// and, for instanced objects that have no transform of their own, where they stand. Movable wagons never change.
        public static void CollectHeldWithin(ObstacleData obstacle, List<Transform> objects, List<Vector3> instancedPoints)
        {
            foreach (var editRecord in s_File.Edits)
            {
                var location = KnownLocation(editRecord);
                if (!Obstacles.Contains(obstacle, location.x, location.z)) continue;
                if (Obstacles.IsMovableWagon(editRecord.Name, editRecord.RegionPosition)) continue;
                if (editRecord.Parent == InstTag) { instancedPoints.Add(ToVector3(editRecord.RegionPosition)); continue; }
                if (s_HeldTargets.TryGetValue(editRecord, out var target) && target != null) objects.Add(target);
            }
        }

        /// How many of the region's edits lie inside the obstacle (the editor shows it).
        public static int CountWithin(ObstacleData obstacle)
        {
            int count = 0;
            foreach (var editRecord in s_File.Edits)
            {
                var position = KnownLocation(editRecord);
                if (Obstacles.Contains(obstacle, position.x, position.z)) count++;
            }
            return count;
        }

        static readonly Dictionary<EditRecord, Vector3> s_Locations = new Dictionary<EditRecord, Vector3>();

        static Vector3 KnownLocation(EditRecord editRecord) => s_Locations.TryGetValue(editRecord, out var location) ? location : ToVector3(editRecord.RegionPosition);

        /// Where the record's object really stands: the middle of its parts as the region placed it (a copy: where
        /// its source's parts would be after the copy's move), because the pivot alone can lie far from the parts.
        static Vector3 LocationOf(EditRecord editRecord, Dictionary<string, List<Transform>> byName)
        {
            if (s_Locations.TryGetValue(editRecord, out var known)) return known;
            var location = ToVector3(editRecord.RegionPosition);
            try
            {
                if (editRecord.Parent == CopyTag)
                {
                    location = ToVector3(editRecord.NewPosition);
                    var source = CopySource(editRecord);
                    if (source != null)
                    {
                        var bounds = BoundsOf(source, out bool hasBounds);
                        if (hasBounds)
                        {
                            var offsetInSource = Quaternion.Inverse(source.rotation) * (bounds.center - source.position);
                            location = ToVector3(editRecord.NewPosition) + ToQuaternion(editRecord.NewRotation) * offsetInSource;
                        }
                    }
                }
                else if (editRecord.Parent != InstTag)
                {
                    var transform = Find(byName, editRecord);
                    if (transform != null)
                    {
                        var bounds = BoundsOf(transform, out bool hasBounds);
                        location = hasBounds ? bounds.center : transform.position;
                    }
                }
            }
            catch (System.Exception exception) { SteamRailwayMod.Debug($"Location of {editRecord.Name}: {exception.Message}"); }
            s_Locations[editRecord] = location;
            return location;
        }

        /// Movable wagons are never touched; other wagons and original rail pieces always keep the region's change.
        /// Anything else waits, in hard mode, for its obstacle to be cleared.
        static bool IsHeldBack(EditRecord editRecord)
        {
            if (Obstacles.IsMovableWagon(editRecord.Name, editRecord.RegionPosition)) return true;
            if (Obstacles.IsMarkedTree(editRecord.Name, editRecord.RegionPosition)) return Obstacles.HardMode && !Obstacles.IsTreeCleared(editRecord.Name, editRecord.RegionPosition);
            if (Obstacles.IsAlwaysEdited(editRecord.Name)) return false;
            return Obstacles.IsHeldBack(KnownLocation(editRecord));
        }

        static Dictionary<string, List<Transform>> NamesInScene()
        {
            var byName = new Dictionary<string, List<Transform>>();
            foreach (var transform in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (transform.gameObject.scene.name == null) continue;
                if (!byName.TryGetValue(transform.name, out var points)) byName[transform.name] = points = new List<Transform>();
                points.Add(transform);
            }
            return byName;
        }

        /// The objects of the marked fallen trees (by tree name and original position), found when the region was built:
        /// a train running into one hides it without searching the scene.
        static readonly Dictionary<string, List<Transform>> s_TreeObjects = new Dictionary<string, List<Transform>>();
        const float TreeMatchDistanceInMeters = 0.6f;

        static string TreeObjectKey(string name, float[] position) => $"{name}@{position[0]:F1},{position[1]:F1},{position[2]:F1}";

        static void FindMarkedTrees(Dictionary<string, List<Transform>> byName)
        {
            s_TreeObjects.Clear();
            var trees = Obstacles.Trees;
            for (int index = 0; index < trees.Count; index++)
            {
                var tree = trees[index];
                if (tree.Name == null || tree.Position == null || tree.Position.Length < Vector3ComponentCount || !byName.TryGetValue(tree.Name, out var candidates)) continue;
                var point = ToVector3(tree.Position);
                var found = new List<Transform>();
                foreach (var candidate in candidates)
                    if (Vector3.Distance(OriginalPosition(candidate), point) <= TreeMatchDistanceInMeters) found.Add(candidate);
                s_TreeObjects[TreeObjectKey(tree.Name, tree.Position)] = found;
            }
        }

        /// The objects of a marked tree found when the region was built; false when it was not looked up then
        /// (the caller searches the scene). Destroyed objects are left out.
        public static bool TryGetMarkedTreeObjects(string name, float[] position, List<Transform> result)
        {
            if (name == null || position == null || position.Length < Vector3ComponentCount) return false;
            if (!s_TreeObjects.TryGetValue(TreeObjectKey(name, position), out var found)) return false;
            foreach (var transform in found) if (transform != null) result.Add(transform);
            return true;
        }

        /// The scene objects of the movable wagons (by wagon name and original position), found when the region was built.
        static readonly Dictionary<string, Transform> s_WagonObjects = new Dictionary<string, Transform>();
        const float WagonMatchDistanceInMeters = 0.5f;

        /// Fallen logs and loose timber the region lays about (candidates for cargo on a wagon), found with the name table.
        static readonly List<Transform> s_LooseLogs = new List<Transform>();
        public static IReadOnlyList<Transform> LooseLogs => s_LooseLogs;
        public static bool LooseLogsKnown { get; private set; }

        static void FindMovableWagons(Dictionary<string, List<Transform>> byName)
        {
            s_WagonObjects.Clear();
            s_LooseLogs.Clear();
            LooseLogsKnown = true;
            foreach (var pair in byName)
            {
                if (!pair.Key.Contains("_Prefab") || !(pair.Key.StartsWith("OBJ_Log") || pair.Key.StartsWith("TRN_Log")) || pair.Key.Contains("Rail")) continue;
                foreach (var candidate in pair.Value) if (candidate != null) s_LooseLogs.Add(candidate);
            }
            var wagons = Obstacles.Wagons;
            for (int index = 0; index < wagons.Count; index++)
            {
                var wagon = wagons[index];
                if (wagon.Name == null || wagon.Position == null || wagon.Position.Length < Vector3ComponentCount || !byName.TryGetValue(wagon.Name, out var candidates)) continue;
                var point = ToVector3(wagon.Position);
                foreach (var candidate in candidates)
                    if (Vector3.Distance(OriginalPosition(candidate), point) <= WagonMatchDistanceInMeters) { s_WagonObjects[TreeObjectKey(wagon.Name, wagon.Position)] = candidate; break; }
            }
        }

        /// The scene object of a movable wagon found when the region was built, or null (not found, or destroyed since).
        public static Transform MovableWagonObject(string name, float[] position)
        {
            if (name == null || position == null || position.Length < Vector3ComponentCount) return null;
            return s_WagonObjects.TryGetValue(TreeObjectKey(name, position), out var found) && found != null ? found : null;
        }

        /// The objects of the edits held back by obstacles, found when the region was built (null: not found then).
        static readonly Dictionary<EditRecord, Transform> s_HeldTargets = new Dictionary<EditRecord, Transform>();

        /// Applies an edit that waited for its obstacle. Its object is normally known from the region's build; only when
        /// it is not (or was destroyed since) is the whole scene searched, once per call site (`byName` keeps that search).
        static void ApplyHeldRecord(EditRecord editRecord, ref Dictionary<string, List<Transform>> byName, ref int applied, ref int missing)
        {
            bool plainObject = editRecord.Parent != InstTag && editRecord.Parent != CopyTag;
            if (plainObject && s_HeldTargets.TryGetValue(editRecord, out var target) && target != null)
            {
                try { Put(target, editRecord); applied++; }
                catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"Edit of {editRecord.Name}: {exception.Message}"); }
                return;
            }
            if (plainObject && byName == null) byName = NamesInScene();
            ApplyRecord(editRecord, byName, ref applied, ref missing);
        }

        static void ApplyRecord(EditRecord editRecord, Dictionary<string, List<Transform>> byName, ref int applied, ref int missing)
        {
            if (editRecord.Parent == InstTag) { ApplyInstance(editRecord); applied++; return; }
            if (editRecord.Parent == CopyTag) { if (SpawnCopy(editRecord) != null) applied++; else missing++; return; }
            var transform = Find(byName, editRecord);
            if (transform == null) { missing++; return; }
            try { Put(transform, editRecord); applied++; }
            catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"Edit of {editRecord.Name}: {exception.Message}"); }
        }

        static Live NewLive(Transform transform)
        {
            var live = new Live { EditedTransform = transform, OriginalPosition = transform.position, OriginalRotation = transform.rotation };
            live.OriginalBounds = BoundsOf(transform, out live.HasBounds);
            live.InstancedObjects.Add((InstancedHider.Prefix(transform.name), transform.position));
            int count = 0;
            foreach (var descendant in transform.GetComponentsInChildren<Transform>(true))
            {
                if (descendant == transform || !descendant.name.Contains("_Prefab") || count++ > MaximumPrefabDescendantsCounted) continue;
                live.InstancedObjects.Add((InstancedHider.Prefix(descendant.name), descendant.position));
            }
            s_Live[transform.Pointer] = live;
            return live;
        }

        public static Bounds BoundsOf(Transform transform, out bool hasBounds)
        {
            hasBounds = false;
            var combinedBounds = new Bounds(transform.position, Vector3.zero);
            foreach (var renderer in transform.GetComponentsInChildren<Renderer>(false))
            {
                if (!renderer.enabled || renderer is ParticleSystemRenderer) continue;
                if (!hasBounds) { combinedBounds = renderer.bounds; hasBounds = true; } else combinedBounds.Encapsulate(renderer.bounds);
            }
            if (!hasBounds)
                foreach (var collider in transform.GetComponentsInChildren<Collider>(false))
                {
                    if (!collider.enabled) continue;
                    if (!hasBounds) { combinedBounds = collider.bounds; hasBounds = true; } else combinedBounds.Encapsulate(collider.bounds);
                }
            return combinedBounds;
        }

        // ---- copies of the region's objects (a missing bridge piece, another fence ...)

        const string CopyTag = "@copy";

        sealed class CopyLive { public GameObject CopiedObject; public EditRecord Record; }
        static readonly List<CopyLive> s_Copies = new List<CopyLive>();

        public static bool IsCopy(Transform transform) => transform != null && transform.name.StartsWith("SR_Copy_");

        /// The region object a copy is made from: same name, nearest place, then nearest turn (pieces of a curved
        /// bridge share one point and differ only in their turn).
        static Transform CopySource(EditRecord editRecord)
        {
            var point = ToVector3(editRecord.RegionPosition); var sourceRotation = ToQuaternion(editRecord.RegionRotation);
            Transform best = null; float bestDistance = float.MaxValue;
            foreach (var transform in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (transform.gameObject.scene.name == null || transform.name != editRecord.Name) continue;
                float distance = (transform.position - point).sqrMagnitude * CopySourcePositionWeight + Quaternion.Angle(transform.rotation, sourceRotation);
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
            targetObject.transform.SetPositionAndRotation(ToVector3(editRecord.NewPosition), ToQuaternion(editRecord.NewRotation));
            // the original may be one the railway hid (a replaced rail piece): the copy is shown
            if (!targetObject.activeSelf) targetObject.SetActive(true);
            foreach (var renderer in targetObject.GetComponentsInChildren<Renderer>(true)) renderer.enabled = true;
            foreach (var lodGroup in targetObject.GetComponentsInChildren<LODGroup>(true)) lodGroup.enabled = true;
            foreach (var collider in targetObject.GetComponentsInChildren<Collider>(true)) collider.enabled = true;
            s_Copies.Add(new CopyLive { CopiedObject = targetObject, Record = editRecord });
            return targetObject;
        }

        /// A copy of a region object, standing where the original stands (the editor then carries it away).
        public static Transform AddCopy(Transform source)
        {
            var editRecord = new EditRecord
            {
                Name = source.name, Parent = CopyTag, RegionPosition = RoundedComponents(OriginalPosition(source)), RegionRotation = RoundedComponents(OriginalRotation(source)),
                NewPosition = RoundedComponents(source.position), NewRotation = RoundedComponents(source.rotation),
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
            var copyLive = s_Copies.Find(candidateCopy => candidateCopy.CopiedObject != null && candidateCopy.CopiedObject.transform == copy);
            if (copyLive == null) return;
            copyLive.Record.NewPosition = RoundedComponents(copy.position);
            copyLive.Record.NewRotation = RoundedComponents(copy.rotation);
            Save();
        }

        /// Puts a copy back where it was before it was picked up.
        public static void ResetCopy(Transform copy)
        {
            var copyLive = s_Copies.Find(candidateCopy => candidateCopy.CopiedObject != null && candidateCopy.CopiedObject.transform == copy);
            if (copyLive != null) copy.SetPositionAndRotation(ToVector3(copyLive.Record.NewPosition), ToQuaternion(copyLive.Record.NewRotation));
        }

        public static bool RemoveCopy(Transform copy)
        {
            var copyLive = s_Copies.Find(candidateCopy => candidateCopy.CopiedObject != null && candidateCopy.CopiedObject.transform == copy);
            if (copyLive == null) return false;
            s_File.Edits.Remove(copyLive.Record);
            s_Copies.Remove(copyLive);
            Object.Destroy(copyLive.CopiedObject);
            Save();
            return true;
        }

        // ---- objects the game draws by GPU instancing and that have no scene object (many trees, logs, rocks)

        const string InstTag = "@instance";

        static void ApplyInstance(EditRecord editRecord)
        {
            var position = ToVector3(editRecord.RegionPosition);
            if (editRecord.Hidden) { InstancedHider.Set(editRecord.Name, position, false, Matrix4x4.identity); return; }
            var delta = Matrix4x4.TRS(ToVector3(editRecord.NewPosition), ToQuaternion(editRecord.NewRotation), Vector3.one) * Matrix4x4.TRS(position, ToQuaternion(editRecord.RegionRotation), Vector3.one).inverse;
            InstancedHider.Set(editRecord.Name, position, true, delta);
        }

        public static EditRecord InstRecord(InstancedHider.InstancedObjectPick instancePick)
        {
            foreach (var editRecord in s_File.Edits)
                if (editRecord.Parent == InstTag && editRecord.Name == instancePick.Prefix && (ToVector3(editRecord.RegionPosition) - instancePick.OriginalPosition).sqrMagnitude <= MatchDistanceInMeters * MatchDistanceInMeters) return editRecord;
            return null;
        }

        public static EditRecord InstHidden(InstancedHider.InstancedObjectPick instancePick) => new EditRecord
        { Name = instancePick.Prefix, Parent = InstTag, RegionPosition = RoundedComponents(instancePick.OriginalPosition), RegionRotation = RoundedComponents(instancePick.OriginalRotation), Hidden = true };

        public static EditRecord InstMoved(InstancedHider.InstancedObjectPick instancePick, Vector3 position, Quaternion rotation) => new EditRecord
        { Name = instancePick.Prefix, Parent = InstTag, RegionPosition = RoundedComponents(instancePick.OriginalPosition), RegionRotation = RoundedComponents(instancePick.OriginalRotation), NewPosition = RoundedComponents(position), NewRotation = RoundedComponents(rotation) };

        /// While the editor carries an instanced object (nothing is written yet).
        public static void PreviewInstance(InstancedHider.InstancedObjectPick instancePick, Vector3 position, Quaternion rotation)
        {
            var delta = Matrix4x4.TRS(position, rotation, Vector3.one) * Matrix4x4.TRS(instancePick.OriginalPosition, instancePick.OriginalRotation, Vector3.one).inverse;
            InstancedHider.Set(instancePick.Prefix, instancePick.OriginalPosition, true, delta);
        }

        /// Puts an instanced object into the state of `r` (null = as the region made it) and writes the file.
        public static void SetInstance(InstancedHider.InstancedObjectPick instancePick, EditRecord editRecord)
        {
            s_File.Edits.RemoveAll(candidateRecord => candidateRecord.Parent == InstTag && candidateRecord.Name == instancePick.Prefix && (ToVector3(candidateRecord.RegionPosition) - instancePick.OriginalPosition).sqrMagnitude <= MatchDistanceInMeters * MatchDistanceInMeters);
            if (editRecord == null) InstancedHider.Remove(instancePick.Prefix, instancePick.OriginalPosition);
            else { s_File.Edits.Add(editRecord); ApplyInstance(editRecord); }
            Save();
        }

        static Transform Find(Dictionary<string, List<Transform>> byName, EditRecord editRecord)
        {
            if (editRecord.Name == null || !byName.TryGetValue(editRecord.Name, out var candidates)) return null;
            var point = ToVector3(editRecord.RegionPosition);
            Transform best = null; float bestDistance = MatchDistanceInMeters * MatchDistanceInMeters;
            foreach (var child in candidates)
            {
                // an object already moved this session is known by where it was
                var live = s_Live.TryGetValue(child.Pointer, out var knownLive) ? knownLive : null;
                var origin = live != null ? live.OriginalPosition : child.position;
                float distance = (origin - point).sqrMagnitude;
                if (distance <= bestDistance) { bestDistance = distance; best = child; }
            }
            return best;
        }

        /// The record of an object (null: as the region made it).
        public static EditRecord RecordOf(Transform transform)
        {
            if (transform == null) return null;
            return s_Live.TryGetValue(transform.Pointer, out var live) ? live.Record : null;
        }

        public static Vector3 OriginalPosition(Transform transform) => s_Live.TryGetValue(transform.Pointer, out var live) ? live.OriginalPosition : transform.position;
        public static Quaternion OriginalRotation(Transform transform) => s_Live.TryGetValue(transform.Pointer, out var live) ? live.OriginalRotation : transform.rotation;

        public static EditRecord Hidden(Transform transform) => new EditRecord
        {
            Name = transform.name, Parent = transform.parent != null ? transform.parent.name : null,
            RegionPosition = RoundedComponents(OriginalPosition(transform)), RegionRotation = RoundedComponents(OriginalRotation(transform)), Hidden = true,
        };

        public static EditRecord Moved(Transform transform, Vector3 position, Quaternion rotation) => new EditRecord
        {
            Name = transform.name, Parent = transform.parent != null ? transform.parent.name : null,
            RegionPosition = RoundedComponents(OriginalPosition(transform)), RegionRotation = RoundedComponents(OriginalRotation(transform)), NewPosition = RoundedComponents(position), NewRotation = RoundedComponents(rotation),
        };

        /// Puts an object into the state of `r` (null = as the region made it) and writes the region's file.
        public static void Set(Transform transform, EditRecord editRecord)
        {
            if (transform == null) return;
            var originalPosition = OriginalPosition(transform);
            Put(transform, editRecord);
            s_File.Edits.RemoveAll(candidateRecord => candidateRecord.Name == transform.name && (ToVector3(candidateRecord.RegionPosition) - originalPosition).sqrMagnitude <= MatchDistanceInMeters * MatchDistanceInMeters);
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
            catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"Writing the object edits failed: {exception.Message}"); }
        }

        static void Put(Transform transform, EditRecord editRecord)
        {
            if (!s_Live.TryGetValue(transform.Pointer, out var live))
            {
                if (editRecord == null) return;
                live = NewLive(transform);
            }
            Restore(live);
            live.Record = editRecord;
            if (editRecord == null) { s_Live.Remove(transform.Pointer); return; }

            if (editRecord.Hidden)
            {
                HideAll(live);
                foreach (var (prefix, originalPoint) in live.InstancedObjects) InstancedHider.Set(prefix, originalPoint, false, Matrix4x4.identity);
                Claim(live);
                HideLoot(live, true);
                return;
            }

            // moved: statically batched meshes cannot move with their object; they get movable copies first
            Detach(live);
            var position = ToVector3(editRecord.NewPosition);
            var rotation = ToQuaternion(editRecord.NewRotation);
            transform.SetPositionAndRotation(position, rotation);
            SetInstanced(live);
            Claim(live);
            PoseLoot(live);
        }

        /// Everything that draws or collides in the object (every renderer, whatever its state, is held off).
        static void HideAll(Live live)
        {
            var transform = live.EditedTransform;
            foreach (var renderer in transform.GetComponentsInChildren<Renderer>(true)) { live.DisabledRenderers.Add((renderer, renderer.enabled)); renderer.enabled = false; }
            foreach (var lodGroup in transform.GetComponentsInChildren<LODGroup>(true)) { live.DisabledLodGroups.Add((lodGroup, lodGroup.enabled)); lodGroup.enabled = false; }
            foreach (var collider in transform.GetComponentsInChildren<Collider>(true)) { live.DisabledColliders.Add((collider, collider.enabled)); collider.enabled = false; }
        }

        static void SetInstanced(Live live)
        {
            var transform = live.EditedTransform;
            var delta = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one) * Matrix4x4.TRS(live.OriginalPosition, live.OriginalRotation, Vector3.one).inverse;
            foreach (var (prefix, originalPoint) in live.InstancedObjects) InstancedHider.Set(prefix, originalPoint, true, delta);
        }

        // ------------------------------------------------------------------ loot

        static Bounds ReachOf(Live live)
        {
            var bounds = live.OriginalBounds;
            var minimumCorner = bounds.min - new Vector3(ReachMarginHorizontalInMeters, ReachMarginBelowInMeters, ReachMarginHorizontalInMeters);
            var maximumCorner = bounds.max + new Vector3(ReachMarginHorizontalInMeters, ReachMarginAboveInMeters, ReachMarginHorizontalInMeters);
            bounds.SetMinMax(minimumCorner, maximumCorner);
            return bounds;
        }

        /// Loose items and containers lying in the object's original area (not in the player's hands, not inside a
        /// container, not dropped by the player) are taken along: they move with it and hide with it.
        static void Claim(Live live)
        {
            if (!live.HasBounds || live.EditedTransform == null) return;
            var reach = ReachOf(live);
            bool moved = live.Record != null && !live.Record.Hidden;
            bool hasCurrentBounds = false;
            var currentBounds = moved ? BoundsOf(live.EditedTransform, out hasCurrentBounds) : default(Bounds);
            // a physics query around the object (scanning every item of the scene costs tens of milliseconds)
            var hits = Physics.OverlapBox(reach.center, reach.extents, Quaternion.identity, ~0, QueryTriggerInteraction.Collide);
            bool isWagon = Obstacles.IsWagonName(live.EditedTransform.name);
            var originalPoint = live.OriginalPosition;
            bool isMovableWagon = isWagon && Obstacles.IsMovableWagon(live.EditedTransform.name, new[] { originalPoint.x, originalPoint.y, originalPoint.z });
            foreach (var collider in hits)
            {
                if (collider == null) continue;
                var gearItem = collider.GetComponentInParent<Il2Cpp.GearItem>();
                if (gearItem != null)
                {
                    // a movable wagon's loot rides with the wagon (see Wagons); claimed here it would be posed and hidden twice
                    if (isMovableWagon || Wagons.IsCargo(gearItem)) continue;
                    if (gearItem.m_InPlayerInventory || gearItem.m_BeenInPlayerInventory || gearItem.m_InsideContainer || gearItem.m_IsInSatchel) continue;
                    TryClaim(live, gearItem.transform, reach, moved && hasCurrentBounds, currentBounds);
                    continue;
                }
                var container = collider.GetComponentInParent<Il2Cpp.Container>();
                if (container != null) { TryClaim(live, container.transform, reach, moved && hasCurrentBounds, currentBounds); continue; }
                // fire barrels and the like are objects of their own, and so is the cargo lying on a wagon (logs):
                // they go where the object under them goes
                var carried = FireObjectRootOf(collider.transform);
                if (carried == null && isWagon) carried = CargoRootOf(collider.transform);
                if (carried != null) TryClaim(live, carried, reach, moved && hasCurrentBounds, currentBounds);
            }
        }

        /// The fire barrel, campfire, stove or lantern prefab a collider belongs to (doors and the like are not taken).
        static Transform FireObjectRootOf(Transform transform)
        {
            for (var node = transform; node != null; node = node.parent)
            {
                string name = node.name;
                if (!name.StartsWith("INTERACTIVE_")) continue;
                if (name.Contains("Fire") || name.Contains("Barrel") || name.Contains("Camp") || name.Contains("Stove") || name.Contains("Lantern")) return node;
            }
            return null;
        }

        /// The log prefab a collider belongs to (a log car's own load is a part of the wagon and is not taken here).
        static Transform CargoRootOf(Transform transform)
        {
            for (var node = transform; node != null; node = node.parent)
            {
                string name = node.name;
                if (name.StartsWith("OBJ_Log") && name.Contains("_Prefab") && !Obstacles.IsWagonName(name)) return node;
            }
            return null;
        }

        static void TryClaim(Live live, Transform item, Bounds reach, bool checkNow, Bounds currentBounds)
        {
            if (item == null || item == live.EditedTransform || item.IsChildOf(live.EditedTransform) || live.EditedTransform.IsChildOf(item) || s_Claimed.Contains(item.Pointer)) return;
            var point = item.position;
            if (!reach.Contains(point)) return;
            // already standing in the moved object (saved there): it has been placed before
            if (checkNow && currentBounds.Contains(point)) return;
            var loot = new Loot { LootTransform = item, OriginalPosition = point, OriginalRotation = item.rotation };
            foreach (var renderer in item.GetComponentsInChildren<Renderer>(true)) loot.Renderers.Add((renderer, renderer.enabled));
            foreach (var collider in item.GetComponentsInChildren<Collider>(true)) loot.Colliders.Add((collider, collider.enabled));
            live.Loot.Add(loot);
            s_Claimed.Add(item.Pointer);
        }

        static void PoseLoot(Live live)
        {
            if (live.EditedTransform == null || live.Loot.Count == 0) return;
            var matrix = Matrix4x4.TRS(live.EditedTransform.position, live.EditedTransform.rotation, Vector3.one) * Matrix4x4.TRS(live.OriginalPosition, live.OriginalRotation, Vector3.one).inverse;
            var rotationDelta = live.EditedTransform.rotation * Quaternion.Inverse(live.OriginalRotation);
            foreach (var loot in live.Loot)
            {
                if (loot.LootTransform == null) continue;
                loot.LootTransform.SetPositionAndRotation(matrix.MultiplyPoint3x4(loot.OriginalPosition), rotationDelta * loot.OriginalRotation);
                var body = loot.LootTransform.GetComponent<Rigidbody>();
                if (body != null && !body.isKinematic) { body.velocity = Vector3.zero; body.angularVelocity = Vector3.zero; }
            }
        }

        static void HideLoot(Live live, bool hide)
        {
            foreach (var loot in live.Loot)
            {
                if (loot.LootTransform == null) continue;
                foreach (var (renderer, wasEnabled) in loot.Renderers) if (renderer != null) renderer.enabled = hide ? false : wasEnabled;
                foreach (var (collider, wasEnabled) in loot.Colliders) if (collider != null) collider.enabled = hide ? false : wasEnabled;
            }
        }

        static float s_NextTick, s_NextScan, s_SceneStart;
        static bool s_Carrying;

        /// A few times a second: items the game spawns later are taken along, and anything the game switched back
        /// on in a removed object is switched off again.
        public static void Tick()
        {
            if (s_Live.Count == 0 || Time.time < s_NextTick) return;
            s_NextTick = Time.time + TickIntervalInSeconds;
            foreach (var live in s_Live.Values)
            {
                if (live.EditedTransform == null || live.Record == null || !live.Record.Hidden) continue;
                foreach (var (renderer, _) in live.DisabledRenderers) if (renderer != null && renderer.enabled) renderer.enabled = false;
                foreach (var (collider, _) in live.DisabledColliders) if (collider != null && collider.enabled) collider.enabled = false;
                foreach (var (lodGroup, _) in live.DisabledLodGroups) if (lodGroup != null && lodGroup.enabled) lodGroup.enabled = false;
            }
            if (s_Carrying || Time.time < s_NextScan) return;
            // the scene scan: often while the region is new (loot is still being spawned), seldom afterwards
            s_NextScan = Time.time + (Time.time - s_SceneStart < NewRegionDurationInSeconds ? NewRegionScanIntervalInSeconds : SettledScanIntervalInSeconds);
            var liveSnapshot = new List<Live>(s_Live.Values);
            var firstPersonCamera = Il2Cpp.GameManager.GetVpFPSCamera();
            var playerPosition = firstPersonCamera != null ? firstPersonCamera.transform.position : Vector3.zero;
            foreach (var live in liveSnapshot)
            {
                if (live.EditedTransform == null || live.Record == null) continue;
                // only edits around the player (the scan costs a little each time)
                if (firstPersonCamera != null && live.HasBounds && live.OriginalBounds.SqrDistance(playerPosition) > ScanRadiusInMeters * ScanRadiusInMeters) continue;
                int before = live.Loot.Count;
                Claim(live);
                if (live.Loot.Count != before && !live.Record.Hidden) PoseLoot(live);
                if (live.Record.Hidden) HideLoot(live, true);
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
            var transform = live.EditedTransform;
            foreach (var childObject in live.Clones) if (childObject != null) Object.Destroy(childObject);
            live.Clones.Clear();
            foreach (var (renderer, wasEnabled) in live.DisabledRenderers) if (renderer != null) renderer.enabled = wasEnabled;
            foreach (var (lodGroup, wasEnabled) in live.DisabledLodGroups) if (lodGroup != null) lodGroup.enabled = wasEnabled;
            foreach (var (collider, wasEnabled) in live.DisabledColliders) if (collider != null) collider.enabled = wasEnabled;
            live.DisabledRenderers.Clear(); live.DisabledLodGroups.Clear(); live.DisabledColliders.Clear();
            if (transform != null) transform.SetPositionAndRotation(live.OriginalPosition, live.OriginalRotation);
            // the loot goes back where it lay
            if (live.Loot.Count > 0)
            {
                HideLoot(live, false);
                PoseLoot(live);
                foreach (var loot in live.Loot) if (loot.LootTransform != null) s_Claimed.Remove(loot.LootTransform.Pointer);
                live.Loot.Clear();
            }
            foreach (var (prefix, originalPoint) in live.InstancedObjects) InstancedHider.Remove(prefix, originalPoint);
        }

        static bool HasRenderers(Transform transform) => transform.GetComponentsInChildren<Renderer>(true).Length > 0;

        /// Is part of the object statically batched (merged into a combined mesh)?
        public static bool IsStatic(Transform transform)
        {
            foreach (var meshRenderer in transform.GetComponentsInChildren<MeshRenderer>(true)) if (meshRenderer.isPartOfStaticBatch) return true;
            return false;
        }

        /// Replaces each statically batched renderer by a child copy with its own mesh so the object can move;
        /// objects with LODs keep only their most detailed level while moved.
        static void Detach(Live live)
        {
            var staticRoot = live.EditedTransform;
            if (!IsStatic(staticRoot)) return;
            var mostDetailedRendererPointers = new HashSet<System.IntPtr>();
            bool hasLods = false;
            foreach (var lodGroup in staticRoot.GetComponentsInChildren<LODGroup>(true))
            {
                var lods = lodGroup.GetLODs();
                if (lods == null || lods.Length == 0) continue;
                hasLods = true;
                foreach (var renderer in lods[0].renderers) if (renderer != null) mostDetailedRendererPointers.Add(renderer.Pointer);
                if (lodGroup.enabled) { lodGroup.enabled = false; live.DisabledLodGroups.Add((lodGroup, true)); }
            }
            int made = 0, failed = 0;
            foreach (var meshRenderer in staticRoot.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (!meshRenderer.enabled) continue;
                bool keep = !hasLods || mostDetailedRendererPointers.Contains(meshRenderer.Pointer);
                if (!keep) { meshRenderer.enabled = false; live.DisabledRenderers.Add((meshRenderer, true)); continue; }
                if (!meshRenderer.isPartOfStaticBatch) continue;
                var mesh = CopyMesh(meshRenderer);
                if (mesh == null) { failed++; continue; }
                var targetObject = new GameObject("SR_Moved_" + meshRenderer.gameObject.name);
                targetObject.layer = meshRenderer.gameObject.layer;
                targetObject.transform.SetParent(meshRenderer.transform, false);
                targetObject.AddComponent<MeshFilter>().sharedMesh = mesh;
                var movedRenderer = targetObject.AddComponent<MeshRenderer>();
                movedRenderer.sharedMaterials = meshRenderer.sharedMaterials;
                movedRenderer.shadowCastingMode = meshRenderer.shadowCastingMode;
                movedRenderer.receiveShadows = meshRenderer.receiveShadows;
                live.Clones.Add(targetObject);
                meshRenderer.enabled = false;
                live.DisabledRenderers.Add((meshRenderer, true));
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
                catch (System.Exception exception) { SteamRailwayMod.Debug($"Cutting {meshRenderer.name} out of the combined mesh: {exception.Message}"); }
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
            var sourceVertices = combined.vertices; var sourceNormals = combined.normals; var sourceTangents = combined.tangents; var sourceUvs = combined.uv;
            bool hasNormals = sourceNormals.Length == sourceVertices.Length, hasTangents = sourceTangents.Length == sourceVertices.Length, hasUvs = sourceUvs.Length == sourceVertices.Length;
            var vertexIndexMap = new Dictionary<int, int>();
            var remappedVertices = new List<Vector3>(); var remappedNormals = new List<Vector3>(); var remappedTangents = new List<Vector4>(); var remappedUvs = new List<Vector2>();
            var subMeshTriangles = new List<int[]>();
            for (int subMeshIndex = start; subMeshIndex < start + count; subMeshIndex++)
            {
                var sourceTriangles = combined.GetTriangles(subMeshIndex);
                var remappedIndices = new int[sourceTriangles.Length];
                for (int index = 0; index < sourceTriangles.Length; index++)
                {
                    int sourceIndex = sourceTriangles[index];
                    if (!vertexIndexMap.TryGetValue(sourceIndex, out int remappedIndex))
                    {
                        remappedIndex = remappedVertices.Count; vertexIndexMap[sourceIndex] = remappedIndex;
                        remappedVertices.Add(toLocal.MultiplyPoint3x4(sourceVertices[sourceIndex]));
                        if (hasNormals) remappedNormals.Add(toLocal.MultiplyVector(sourceNormals[sourceIndex]).normalized);
                        if (hasTangents) { var direction = toLocal.MultiplyVector(new Vector3(sourceTangents[sourceIndex].x, sourceTangents[sourceIndex].y, sourceTangents[sourceIndex].z)).normalized; remappedTangents.Add(new Vector4(direction.x, direction.y, direction.z, sourceTangents[sourceIndex].w)); }
                        if (hasUvs) remappedUvs.Add(sourceUvs[sourceIndex]);
                    }
                    remappedIndices[index] = remappedIndex;
                }
                subMeshTriangles.Add(remappedIndices);
            }
            if (remappedVertices.Count == 0) return null;
            var mesh = new Mesh { name = meshRenderer.name + "_Moved" };
            if (remappedVertices.Count > MaximumSixteenBitVertexCount) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(new Il2CppStructArray<Vector3>(remappedVertices.ToArray()));
            if (hasNormals) mesh.SetNormals(new Il2CppStructArray<Vector3>(remappedNormals.ToArray()));
            if (hasTangents) mesh.SetTangents(new Il2CppStructArray<Vector4>(remappedTangents.ToArray()));
            if (hasUvs) mesh.SetUVs(0, new Il2CppStructArray<Vector2>(remappedUvs.ToArray()));
            mesh.subMeshCount = subMeshTriangles.Count;
            for (int subMeshIndex = 0; subMeshIndex < subMeshTriangles.Count; subMeshIndex++) mesh.SetTriangles(new Il2CppStructArray<int>(subMeshTriangles[subMeshIndex]), subMeshIndex);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// The original mesh, if the game still has it loaded: same name as the object, same size and place.
        static Mesh ByName(MeshRenderer meshRenderer)
        {
            var rendererBounds = meshRenderer.bounds;
            foreach (var mesh in Resources.FindObjectsOfTypeAll<Mesh>())
            {
                if (mesh == null || mesh.name != meshRenderer.gameObject.name || mesh.vertexCount == 0) continue;
                // the mesh's box, placed like the object, must cover the renderer's box
                var meshBounds = mesh.bounds;
                var worldBounds = new Bounds(meshRenderer.transform.TransformPoint(meshBounds.center), Vector3.zero);
                for (int index = 0; index < BoundsCornerCount; index++)
                {
                    var corner = meshBounds.center + Vector3.Scale(meshBounds.extents, new Vector3((index & CornerMaskX) == 0 ? -1 : 1, (index & CornerMaskY) == 0 ? -1 : 1, (index & CornerMaskZ) == 0 ? -1 : 1));
                    worldBounds.Encapsulate(meshRenderer.transform.TransformPoint(corner));
                }
                if ((worldBounds.center - rendererBounds.center).magnitude < BoundsCenterToleranceInMeters && (worldBounds.size - rendererBounds.size).magnitude < BoundsSizeToleranceRelative * rendererBounds.size.magnitude + BoundsSizeToleranceAbsoluteInMeters) return mesh;
            }
            return null;
        }
    }
}
