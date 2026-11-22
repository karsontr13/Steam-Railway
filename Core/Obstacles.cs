using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using UnityEngine;

namespace SteamRailway
{
    internal enum ObstacleKind { Rockfall, BrokenBridge }

    /// One obstacle on a line, kept in Mods\SteamRailway\Obstacles\region.json; in hard mode the region's changes inside
    /// its circle are not applied until the player has cleared it.
    internal sealed class ObstacleData
    {
        const float DefaultRadiusInMeters = 20f;
        [JsonPropertyName("Id")] public string Identifier { get; set; }
        public string Kind { get; set; } = nameof(ObstacleKind.Rockfall);
        public float[] Center { get; set; }
        public float Radius { get; set; } = DefaultRadiusInMeters;
        /// The changes inside the circle are always applied (the transition tunnel to the ZoC, for example).
        public bool AlwaysApplied { get; set; }
        /// Points outside the circle whose changes belong to this obstacle too (a bridge piece whose pivot lies far from the gap).
        public List<float[]> AnchorPositions { get; set; } = new List<float[]>();
    }

    /// A wagon the player can push and pull: it stays where the original region put it, in both modes.
    internal sealed class MovableWagonData
    {
        public string Name { get; set; }
        [JsonPropertyName("Pos")] public float[] Position { get; set; }
        /// The wagon's own parts (the region edits them one by one: body, glass, snow): none of them is touched either.
        public List<MovableWagonPart> Parts { get; set; } = new List<MovableWagonPart>();
    }

    internal sealed class MovableWagonPart
    {
        public string Name { get; set; }
        [JsonPropertyName("Pos")] public float[] Position { get; set; }
    }

    /// A fallen tree lying on the line (no circle): in hard mode it stays until a train runs into it.
    internal sealed class MarkedTreeData
    {
        const float DefaultReachInMeters = 6f;
        public string Name { get; set; }
        [JsonPropertyName("Pos")] public float[] Position { get; set; }
        /// How close the train's nose must come (m, flat) to knock it away.
        public float Reach { get; set; } = DefaultReachInMeters;
    }

    internal sealed class ObstacleFile
    {
        public int Version { get; set; } = 1;
        public List<ObstacleData> Obstacles { get; set; } = new List<ObstacleData>();
        public List<MovableWagonData> Wagons { get; set; } = new List<MovableWagonData>();
        public List<MarkedTreeData> Trees { get; set; } = new List<MarkedTreeData>();
    }

    internal enum Difficulty { Easy, Hard }

    /// Easy mode applies all of the region's changes at the start; hard mode applies a change only inside an always-applied
    /// circle or inside a cleared obstacle (rails are never held back).
    internal static class Obstacles
    {
        const int Vector3ComponentCount = 3;
        public const string EasyName = "Easy";
        public const string HardName = "Hard";
        const float DefaultRadius = 20f;
        const float MinimumRadius = 4f;
        const float MaximumRadius = 120f;

        static readonly JsonSerializerOptions s_Json = new JsonSerializerOptions { WriteIndented = true };
        static string s_Scene;
        static ObstacleFile s_File = new ObstacleFile();

        public static IReadOnlyList<ObstacleData> All => s_File.Obstacles;
        public static IReadOnlyList<MarkedTreeData> Trees => s_File.Trees;
        public static IReadOnlyList<MovableWagonData> Wagons => s_File.Wagons;

        public static string FolderPath => Path.Combine(RouteStore.FolderPath, "Obstacles");
        static string FilePath(string scene) => Path.Combine(FolderPath, scene + ".json");

        public static bool HardMode => RailwayManager.Difficulty == Difficulty.Hard;

        /// Reads the region's obstacles (call before the region's changes are applied).
        public static void Load(string scene)
        {
            s_Scene = scene;
            s_File = new ObstacleFile();
            var path = FilePath(scene);
            if (File.Exists(path))
            {
                try { s_File = JsonSerializer.Deserialize<ObstacleFile>(File.ReadAllText(path), s_Json) ?? new ObstacleFile(); }
                catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"{path} could not be read: {exception.Message}"); s_File = new ObstacleFile(); }
            }
            if (s_File.Obstacles == null) s_File.Obstacles = new List<ObstacleData>();
            if (s_File.Wagons == null) s_File.Wagons = new List<MovableWagonData>();
            if (s_File.Trees == null) s_File.Trees = new List<MarkedTreeData>();
            RecordMissingWagonParts();
            if (HardMode)
                SteamRailwayMod.Log.Msg($"{scene}: hard mode, {CountHeld()} of {s_File.Obstacles.Count} obstacle(s) still in the way.");
        }

        static int CountHeld()
        {
            int held = 0;
            foreach (var obstacle in s_File.Obstacles) if (IsHolding(obstacle)) held++;
            return held;
        }

        public static void Save()
        {
            if (s_Scene == null) return;
            Directory.CreateDirectory(FolderPath);
            File.WriteAllText(FilePath(s_Scene), JsonSerializer.Serialize(s_File, s_Json));
        }

        public static Vector3 CenterOf(ObstacleData obstacle) =>
            obstacle.Center != null && obstacle.Center.Length >= Vector3ComponentCount ? new Vector3(obstacle.Center[0], obstacle.Center[1], obstacle.Center[2]) : Vector3.zero;

        public static ObstacleKind KindOf(ObstacleData obstacle) =>
            System.Enum.TryParse<ObstacleKind>(obstacle.Kind, out var kind) ? kind : ObstacleKind.Rockfall;

        /// The obstacle still blocks the line (hard mode, not cleared, not an always-applied one).
        public static bool IsHolding(ObstacleData obstacle) =>
            HardMode && !obstacle.AlwaysApplied && !RailwayManager.IsObstacleCleared(obstacle.Identifier);

        public static bool Contains(ObstacleData obstacle, float x, float z)
        {
            var center = CenterOf(obstacle);
            float deltaX = x - center.x, deltaZ = z - center.z;
            return deltaX * deltaX + deltaZ * deltaZ <= obstacle.Radius * obstacle.Radius || IsAtAnchor(obstacle, x, z);
        }

        const float AnchorMatchRadiusInMeters = 1.5f;

        static bool IsAtAnchor(ObstacleData obstacle, float x, float z)
        {
            if (obstacle.AnchorPositions == null) return false;
            foreach (var anchor in obstacle.AnchorPositions)
            {
                if (anchor == null || anchor.Length < Vector3ComponentCount) continue;
                float deltaX = x - anchor[0], deltaZ = z - anchor[2];
                if (deltaX * deltaX + deltaZ * deltaZ <= AnchorMatchRadiusInMeters * AnchorMatchRadiusInMeters) return true;
            }
            return false;
        }

        /// The obstacle that still blocks the line at this place, or null.
        public static ObstacleData BlockingAt(float x, float z)
        {
            foreach (var obstacle in s_File.Obstacles)
                if (IsHolding(obstacle) && Contains(obstacle, x, z)) return obstacle;
            return null;
        }

        /// The region's change at this place is applied: always in easy mode; in hard mode only inside an
        /// always-applied circle or a cleared obstacle.
        public static bool AppliesAt(float x, float z)
        {
            if (!HardMode) return true;
            foreach (var obstacle in s_File.Obstacles)
                if ((obstacle.AlwaysApplied || RailwayManager.IsObstacleCleared(obstacle.Identifier)) && Contains(obstacle, x, z)) return true;
            return false;
        }

        public static bool IsHeldBack(Vector3 position) => !AppliesAt(position.x, position.z);

        public static bool IsHeldBack(float[] position) => position != null && position.Length >= Vector3ComponentCount && !AppliesAt(position[0], position[2]);

        // ------------------------------------------------------------------ movable wagons

        const float WagonMatchDistance = 0.5f;

        /// Rail vehicles the region places along the line (freight cars, hoppers, trucks, locomotives).
        public static bool IsWagonName(string name) =>
            name != null && (name.Contains("RailCar") || name.Contains("RailHopper") || name.Contains("SiloTruck")
                             || name.Contains("RailwayTruck") || name.Contains("Locomotive") || name.Contains("RailBase") || name.Contains("CrewSpeeder"));

        /// Objects whose changes stay applied in hard mode wherever they lie: wagons and the original rail pieces taken away.
        /// The trestle bridge pieces are left out (the broken-bridge obstacle repairs them).
        public static bool IsAlwaysEdited(string name) =>
            IsWagonName(name) || (name != null && name.Contains("RailTrack") && !name.Contains("Trestle"));

        // ------------------------------------------------------------------ broken bridges

        const float BridgeSampleStep = 1f;

        /// Hard mode: the point lies in the circle of a broken bridge that is not repaired yet.
        public static bool IsInUnrepairedBridge(float x, float z)
        {
            foreach (var obstacle in s_File.Obstacles)
                if (IsHolding(obstacle) && KindOf(obstacle) == ObstacleKind.BrokenBridge && Contains(obstacle, x, z)) return true;
            return false;
        }

        /// The stretches of the line (path metres) inside unrepaired broken-bridge circles: no rail is laid there and
        /// the original pieces stay as the region has them.
        /// The position lies over a broken bridge (repaired or not): nobody gets off a train there.
        public static bool IsOverBridge(Vector3 position)
        {
            foreach (var obstacle in s_File.Obstacles)
                if (KindOf(obstacle) == ObstacleKind.BrokenBridge && Contains(obstacle, position.x, position.z)) return true;
            return false;
        }

        /// The stretches of the line (path metres) inside one bridge obstacle's circle.
        public static List<Vector2> RangesInsideObstacle(CurvePath path, ObstacleData obstacle)
        {
            var ranges = new List<Vector2>();
            float start = -1f;
            for (float arcLength = 0f; arcLength <= path.Length; arcLength += BridgeSampleStep)
            {
                path.Sample(arcLength, out var point, out _);
                bool inside = Contains(obstacle, point.x, point.z);
                if (inside && start < 0f) start = arcLength;
                if (!inside && start >= 0f) { ranges.Add(new Vector2(start, arcLength)); start = -1f; }
            }
            if (start >= 0f) ranges.Add(new Vector2(start, path.Length));
            return ranges;
        }

        public static List<Vector2> UnrepairedBridgeRanges(CurvePath path)
        {
            var ranges = new List<Vector2>();
            if (!HardMode) return ranges;
            bool any = false;
            foreach (var obstacle in s_File.Obstacles)
                if (IsHolding(obstacle) && KindOf(obstacle) == ObstacleKind.BrokenBridge) { any = true; break; }
            if (!any) return ranges;
            float start = -1f;
            for (float arcLength = 0f; arcLength <= path.Length; arcLength += BridgeSampleStep)
            {
                path.Sample(arcLength, out var point, out _);
                bool inside = IsInUnrepairedBridge(point.x, point.z);
                if (inside && start < 0f) start = arcLength;
                if (!inside && start >= 0f) { ranges.Add(new Vector2(start, arcLength)); start = -1f; }
            }
            if (start >= 0f) ranges.Add(new Vector2(start, path.Length));
            return ranges;
        }

        // ------------------------------------------------------------------ fallen trees

        const float TreeMatchDistance = 0.5f;
        const float MinimumTreeReach = 3f;
        const float MaximumTreeReach = 14f;

        static bool Matches(string name, float[] stored, string wantedName, Vector3 point) =>
            name == wantedName && stored != null && stored.Length >= Vector3ComponentCount
            && Vector3.Distance(new Vector3(stored[0], stored[1], stored[2]), point) <= TreeMatchDistance;

        static string TreeKey(MarkedTreeData tree) => $"tree:{tree.Name}@{tree.Position[0]:F1},{tree.Position[1]:F1},{tree.Position[2]:F1}";

        static MarkedTreeData FindTree(string name, float[] position)
        {
            if (position == null || position.Length < Vector3ComponentCount) return null;
            var point = new Vector3(position[0], position[1], position[2]);
            return s_File.Trees.Find(tree => Matches(tree.Name, tree.Position, name, point));
        }

        /// A marked fallen tree: in hard mode its change (it is gone) waits until a train has run into it.
        public static bool IsMarkedTree(string name, float[] position) => FindTree(name, position) != null;

        public static bool IsTreeCleared(string name, float[] position)
        {
            var tree = FindTree(name, position);
            return tree != null && RailwayManager.IsObstacleCleared(TreeKey(tree));
        }

        /// Marks the object as a fallen tree on the line, or takes the mark away. Returns whether it is marked now.
        public static bool ToggleTree(string name, Vector3 position, float reach)
        {
            var existing = s_File.Trees.Find(tree => Matches(tree.Name, tree.Position, name, position));
            bool marked = existing == null;
            if (marked) s_File.Trees.Add(new MarkedTreeData { Name = name, Position = new[] { Round(position.x), Round(position.y), Round(position.z) }, Reach = Mathf.Clamp(reach, MinimumTreeReach, MaximumTreeReach) });
            else s_File.Trees.Remove(existing);
            Save();
            return marked;
        }

        /// The marked tree (not yet knocked away) that a train's nose at this place runs into, or null.
        public static MarkedTreeData TreeAt(float x, float z)
        {
            if (!HardMode) return null;
            foreach (var tree in s_File.Trees)
            {
                if (tree.Position == null || tree.Position.Length < Vector3ComponentCount || RailwayManager.IsObstacleCleared(TreeKey(tree))) continue;
                float deltaX = x - tree.Position[0], deltaZ = z - tree.Position[2];
                if (deltaX * deltaX + deltaZ * deltaZ <= tree.Reach * tree.Reach) return tree;
            }
            return null;
        }

        /// A train has run into the tree: it is gone from now on (kept in the save).
        public static void ClearTree(MarkedTreeData tree)
        {
            RailwayManager.MarkObstacleCleared(TreeKey(tree));
            ObjectEdits.ApplyNamed(s_Scene, tree.Name, tree.Position);
            OriginalRails.HideObject(tree.Name, tree.Position);
        }

        /// Hard mode: a rock or log the region would clear from the line stays until its change applies: its obstacle is
        /// cleared, or (a marked fallen tree) a train has run into it.
        public static bool IsHeldBackObject(string name, Vector3 originalPosition)
        {
            if (!HardMode) return false;
            var position = new[] { originalPosition.x, originalPosition.y, originalPosition.z };
            if (IsMarkedTree(name, position)) return !IsTreeCleared(name, position);
            return !AppliesAt(originalPosition.x, originalPosition.z);
        }

        /// The object (by name and original position) is a wagon the player can move: the region's change to it is never applied.
        public static bool IsMovableWagon(string name, float[] position)
        {
            if (position == null || position.Length < Vector3ComponentCount) return false;
            var point = new Vector3(position[0], position[1], position[2]);
            foreach (var wagon in s_File.Wagons)
            {
                if (Matches(wagon.Name, wagon.Position, name, point)) return true;
                if (wagon.Parts != null)
                    foreach (var part in wagon.Parts)
                        if (Matches(part.Name, part.Position, name, point)) return true;
            }
            return false;
        }

        const int MaximumWagonParts = 400;
        const string EncampmentPrefix = "ENCAMPMENT_";

        /// The wagon and everything under it, by name and original position.
        static List<MovableWagonPart> PartsOf(Transform wagon)
        {
            var parts = new List<MovableWagonPart>();
            foreach (var transform in wagon.GetComponentsInChildren<Transform>(true))
            {
                if (parts.Count >= MaximumWagonParts) break;
                var position = ObjectEdits.OriginalPosition(transform);
                parts.Add(new MovableWagonPart { Name = transform.name, Position = new[] { Round(position.x), Round(position.y), Round(position.z) } });
            }
            return parts;
        }

        /// Wagons marked before their parts were recorded get them now (the region's objects are in the scene).
        static void RecordMissingWagonParts()
        {
            bool changed = false;
            foreach (var wagon in s_File.Wagons)
            {
                if (wagon.Parts != null && wagon.Parts.Count > 0) continue;
                if (wagon.Position == null || wagon.Position.Length < Vector3ComponentCount) continue;
                var point = new Vector3(wagon.Position[0], wagon.Position[1], wagon.Position[2]);
                foreach (var transform in Resources.FindObjectsOfTypeAll<Transform>())
                {
                    if (transform.name != wagon.Name || transform.gameObject.scene.name == null) continue;
                    if (Vector3.Distance(transform.position, point) > WagonMatchDistance) continue;
                    wagon.Parts = PartsOf(transform);
                    changed = true;
                    break;
                }
            }
            if (changed) Save();
        }

        /// Marks the object as a movable wagon, or takes the mark away. Returns whether it is marked now.
        public static bool ToggleMovableWagon(Transform wagonTransform)
        {
            // a vehicle's body clicked under its encampment root: the root is the wagon (its glass and snow move with it)
            while (wagonTransform.parent != null && wagonTransform.parent.name.StartsWith(EncampmentPrefix) && IsWagonName(wagonTransform.parent.name))
                wagonTransform = wagonTransform.parent;
            string name = wagonTransform.name;
            var position = ObjectEdits.OriginalPosition(wagonTransform);
            var existing = s_File.Wagons.Find(wagon => Matches(wagon.Name, wagon.Position, name, position));
            bool marked = existing == null;
            if (marked) s_File.Wagons.Add(new MovableWagonData { Name = name, Position = new[] { Round(position.x), Round(position.y), Round(position.z) }, Parts = PartsOf(wagonTransform) });
            else s_File.Wagons.Remove(existing);
            Save();
            return marked;
        }

        public static ObstacleData Find(string identifier)
        {
            foreach (var obstacle in s_File.Obstacles) if (obstacle.Identifier == identifier) return obstacle;
            return null;
        }

        /// The obstacle whose circle holds the point, whatever its state (the editor works on them all).
        public static ObstacleData NearestTo(Vector3 point, out float distance)
        {
            ObstacleData best = null;
            distance = float.MaxValue;
            foreach (var obstacle in s_File.Obstacles)
            {
                var center = CenterOf(obstacle);
                float flatDistance = Mathf.Sqrt((center.x - point.x) * (center.x - point.x) + (center.z - point.z) * (center.z - point.z));
                if (flatDistance < distance) { distance = flatDistance; best = obstacle; }
            }
            return best;
        }

        // ------------------------------------------------------------------ editing (the editor)

        public static ObstacleData Add(Vector3 center, ObstacleKind kind, float radius)
        {
            var obstacle = new ObstacleData
            {
                Identifier = NextId(kind),
                Kind = kind.ToString(),
                Center = new[] { Round(center.x), Round(center.y), Round(center.z) },
                Radius = Mathf.Clamp(radius, MinimumRadius, MaximumRadius),
            };
            s_File.Obstacles.Add(obstacle);
            Save();
            return obstacle;
        }

        public static void MoveTo(ObstacleData obstacle, Vector3 center)
        {
            obstacle.Center = new[] { Round(center.x), Round(center.y), Round(center.z) };
            Save();
        }

        public static void Remove(ObstacleData obstacle)
        {
            s_File.Obstacles.Remove(obstacle);
            Save();
        }

        public static float ClampRadius(float radius) => Mathf.Clamp(radius, MinimumRadius, MaximumRadius);
        public static float NewRadius => DefaultRadius;

        static float Round(float value) => (float)System.Math.Round(value, 2);

        static string NextId(ObstacleKind kind)
        {
            for (int number = 1; ; number++)
            {
                string identifier = $"{s_Scene}_{kind}{number}";
                if (Find(identifier) == null) return identifier;
            }
        }

        // ------------------------------------------------------------------ clearing

        /// The obstacle is cleared (dynamite, a wagon pulled away, a bridge built): its area gets the region's changes.
        public static void Clear(ObstacleData obstacle)
        {
            if (obstacle == null || RailwayManager.IsObstacleCleared(obstacle.Identifier)) return;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            RailwayManager.MarkObstacleCleared(obstacle.Identifier);
            ObjectEdits.ApplyWithin(s_Scene, obstacle);
            long objectEditsMilliseconds = stopwatch.ElapsedMilliseconds;
            TerrainEdits.ApplyWithin(s_Scene, obstacle);
            long terrainMilliseconds = stopwatch.ElapsedMilliseconds;
            Tunnels.ApplyScene(s_Scene);
            RailwayManager.ClearLinesAgain(obstacle);
            SteamRailwayMod.Debug($"{obstacle.Identifier}: clearing took objects {objectEditsMilliseconds} ms, terrain {terrainMilliseconds - objectEditsMilliseconds} ms, tunnels and lines {stopwatch.ElapsedMilliseconds - terrainMilliseconds} ms.");
            // only the repaired stretch is laid and the original pieces there hidden; a whole rebuild takes seconds
            if (KindOf(obstacle) == ObstacleKind.BrokenBridge && !RailwayManager.LayBridgeRails(obstacle) && !RailwayManager.RebuildForEditor())
                CabControls.Message(Localizer.Translate("Köprü onarıldı; raylar trenden inince kurulur (F9 ya da bölgeye yeniden gir).", "The bridge is repaired; the rails are laid once you leave the train (F9 or re-enter the region)."));
            SteamRailwayMod.Log.Msg($"{obstacle.Identifier} is cleared ({stopwatch.ElapsedMilliseconds} ms).");
        }

        /// Editor test: the obstacle blocks again from the next time the region is built.
        public static void Reinstate(ObstacleData obstacle)
        {
            if (obstacle != null) RailwayManager.MarkObstacleHeld(obstacle.Identifier);
        }
    }
}
