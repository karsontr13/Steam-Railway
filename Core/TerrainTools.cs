using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace SteamRailway
{
    internal enum BrushMode { Raise, Lower, Flatten, Fill, Smooth }

    internal sealed class TerrainStroke
    {
        public int Mode { get; set; }
        public List<float[]> Dabs { get; set; } = new List<float[]>();   // x, z, radius, amount, target height
    }

    internal sealed class TerrainEditFile
    {
        public int Version { get; set; } = 1;
        public List<TerrainStroke> Strokes { get; set; } = new List<TerrainStroke>();
    }

    /// Hand-made ground changes kept as brush strokes in Mods\SteamRailway\Terrain\<region>.json and played back
    /// each time the region loads, before the railway is laid. Only the loaded terrain in memory changes.
    internal static class TerrainEdits
    {
        const int DabComponentCount = 5;
        const int DabXIndex = 0;
        const int DabZIndex = 1;
        const int DabRadiusIndex = 2;
        const int DabAmountIndex = 3;
        const int DabTargetHeightIndex = 4;
        const float SmoothSampleCount = 5f;
        static readonly JsonSerializerOptions s_Json = new JsonSerializerOptions { WriteIndented = false };
        static string s_Scene;
        static TerrainEditFile s_File = new TerrainEditFile();
        static string s_AppliedScene;      // strokes are played back once per loaded region (rebuilds skip them)

        static string FilePath(string scene) => Path.Combine(RouteStore.FolderPath, "Terrain", scene + ".json");

        public static TerrainEditFile For(string scene)
        {
            if (scene == s_Scene) return s_File;
            s_Scene = scene;
            s_File = new TerrainEditFile();
            var path = FilePath(scene);
            if (File.Exists(path))
            {
                try { s_File = JsonSerializer.Deserialize<TerrainEditFile>(File.ReadAllText(path), s_Json) ?? new TerrainEditFile(); }
                catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"{path} could not be read: {exception.Message}"); }
            }
            if (s_File.Strokes == null) s_File.Strokes = new List<TerrainStroke>();
            return s_File;
        }

        public static void Save()
        {
            if (s_Scene == null) return;
            try
            {
                var path = FilePath(s_Scene);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonSerializer.Serialize(s_File, s_Json));
            }
            catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"Writing the terrain edits failed: {exception.Message}"); }
        }

        /// A new region was loaded: its strokes are played back on the next build.
        public static void SceneLoaded() => s_AppliedScene = null;

        public static void ApplyScene(string scene)
        {
            if (s_AppliedScene == scene) return;
            var strokeFile = HeldBackRemoved(For(scene));
            if (strokeFile.Strokes.Count == 0)
            {
                s_AppliedScene = scene;
                return;
            }
            var loadedTerrains = Terrain.activeTerrains;
            if (loadedTerrains.Length == 0)
            {
                SteamRailwayMod.Log.Warning($"{scene}: no terrain is loaded yet, the {strokeFile.Strokes.Count} terrain stroke(s) are not applied.");
                return;
            }
            s_AppliedScene = scene;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            int dabCount = 0;
            foreach (var stroke in strokeFile.Strokes) dabCount += stroke.Dabs != null ? stroke.Dabs.Count : 0;
            int terrainsChanged = 0;
            foreach (var terrain in loadedTerrains)
            {
                try { if (ReplayOn(terrain, strokeFile)) terrainsChanged++; }
                catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"Terrain edits on {terrain.name}: {exception}"); }
            }
            SteamRailwayMod.Log.Msg($"{scene}: {strokeFile.Strokes.Count} terrain stroke(s) ({dabCount} dabs) applied to {terrainsChanged} of {loadedTerrains.Length} terrain(s) in {stopwatch.ElapsedMilliseconds} ms.");
        }

        /// Hard mode: the strokes without the dabs that lie inside an obstacle still in the way. Ground smoothed elsewhere is
        /// always applied (unlike object edits, which wait for an always-applied circle).
        static TerrainEditFile HeldBackRemoved(TerrainEditFile source)
        {
            if (!Obstacles.HardMode) return source;
            var kept = new TerrainEditFile();
            foreach (var stroke in source.Strokes)
            {
                var dabs = new List<float[]>();
                if (stroke.Dabs != null)
                    foreach (var dab in stroke.Dabs)
                        if (dab != null && dab.Length >= DabComponentCount && Obstacles.BlockingAt(dab[DabXIndex], dab[DabZIndex]) == null) dabs.Add(dab);
                if (dabs.Count > 0) kept.Strokes.Add(new TerrainStroke { Mode = stroke.Mode, Dabs = dabs });
            }
            return kept;
        }

        /// The dabs of the region that lie inside the obstacle.
        static TerrainEditFile WithinObstacle(TerrainEditFile source, ObstacleData obstacle)
        {
            var kept = new TerrainEditFile();
            foreach (var stroke in source.Strokes)
            {
                var dabs = new List<float[]>();
                if (stroke.Dabs != null)
                    foreach (var dab in stroke.Dabs)
                        if (dab != null && dab.Length >= DabComponentCount && Obstacles.Contains(obstacle, dab[DabXIndex], dab[DabZIndex])) dabs.Add(dab);
                if (dabs.Count > 0) kept.Strokes.Add(new TerrainStroke { Mode = stroke.Mode, Dabs = dabs });
            }
            return kept;
        }

        public static int CountWithin(string scene, ObstacleData obstacle)
        {
            int count = 0;
            foreach (var stroke in WithinObstacle(For(scene), obstacle).Strokes) count += stroke.Dabs.Count;
            return count;
        }

        /// Hard mode: an obstacle was cleared, so its ground gets the region's terrain changes now.
        public static void ApplyWithin(string scene, ObstacleData obstacle)
        {
            var strokeFile = WithinObstacle(For(scene), obstacle);
            if (strokeFile.Strokes.Count == 0) return;
            int terrainsChanged = 0;
            foreach (var terrain in Terrain.activeTerrains)
            {
                try { if (ReplayOn(terrain, strokeFile)) terrainsChanged++; }
                catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"Terrain edits on {terrain.name}: {exception}"); }
            }
            SteamRailwayMod.Log.Msg($"{obstacle.Identifier}: terrain changes applied to {terrainsChanged} terrain(s) after clearing.");
        }

        static bool ReplayOn(Terrain terrain, TerrainEditFile editFile)
        {
            var terrainData = terrain.terrainData;
            var terrainOrigin = terrain.transform.position;
            var size = terrainData.size;
            int resolution = terrainData.heightmapResolution;
            float cellSizeX = size.x / (resolution - 1), cellSizeZ = size.z / (resolution - 1);
            // the area all the dabs touch on this terrain
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            foreach (var stroke in editFile.Strokes)
                if (stroke.Dabs != null)
                    foreach (var dab in stroke.Dabs)
                    {
                        if (dab == null || dab.Length < DabComponentCount) continue;
                        minX = Mathf.Min(minX, dab[DabXIndex] - dab[DabRadiusIndex]); maxX = Mathf.Max(maxX, dab[DabXIndex] + dab[DabRadiusIndex]);
                        minZ = Mathf.Min(minZ, dab[DabZIndex] - dab[DabRadiusIndex]); maxZ = Mathf.Max(maxZ, dab[DabZIndex] + dab[DabRadiusIndex]);
                    }
            if (maxX < terrainOrigin.x || minX > terrainOrigin.x + size.x || maxZ < terrainOrigin.z || minZ > terrainOrigin.z + size.z) return false;
            int startX = Mathf.Clamp(Mathf.FloorToInt((minX - terrainOrigin.x) / cellSizeX) - 1, 0, resolution - 1);
            int endX = Mathf.Clamp(Mathf.CeilToInt((maxX - terrainOrigin.x) / cellSizeX) + 1, 0, resolution - 1);
            int startZ = Mathf.Clamp(Mathf.FloorToInt((minZ - terrainOrigin.z) / cellSizeZ) - 1, 0, resolution - 1);
            int endZ = Mathf.Clamp(Mathf.CeilToInt((maxZ - terrainOrigin.z) / cellSizeZ) + 1, 0, resolution - 1);
            int width = endX - startX + 1, height = endZ - startZ + 1;
            if (width <= 2 || height <= 2) return false;
            var heights = HeightmapAccess.Read(terrainData, startX, startZ, width, height);
            if (heights.Length < width * height) return false;
            var heightBlock = new float[width * height];
            for (int index = 0; index < heightBlock.Length; index++) heightBlock[index] = heights[index];
            bool changed = false;
            foreach (var stroke in editFile.Strokes)
                if (stroke.Dabs != null)
                    foreach (var dab in stroke.Dabs)
                        if (dab != null && dab.Length >= DabComponentCount)
                            changed |= DabArray(heightBlock, width, height, startX, startZ, terrainOrigin, size, cellSizeX, cellSizeZ, (BrushMode)stroke.Mode, dab[DabXIndex], dab[DabZIndex], dab[DabRadiusIndex], dab[DabAmountIndex], dab[DabTargetHeightIndex]);
            if (!changed) return false;
            for (int index = 0; index < heightBlock.Length; index++) heights[index] = heightBlock[index];
            HeightmapAccess.Write(terrainData, startX, startZ, heights);
            return true;
        }

        /// One dab on heights held in memory (a block of width x height starting at heightmap column startX, row startZ).
        static bool DabArray(float[] heightBlock, int width, int height, int startX, int startZ, Vector3 terrainOrigin, Vector3 size, float cellSizeX, float cellSizeZ,
                             BrushMode mode, float worldX, float worldZ, float radius, float amount, float targetHeight)
        {
            int firstColumn = Mathf.Max(1, Mathf.FloorToInt((worldX - radius - terrainOrigin.x) / cellSizeX) - startX);
            int lastColumn = Mathf.Min(width - 2, Mathf.CeilToInt((worldX + radius - terrainOrigin.x) / cellSizeX) - startX);
            int firstRow = Mathf.Max(1, Mathf.FloorToInt((worldZ - radius - terrainOrigin.z) / cellSizeZ) - startZ);
            int lastRow = Mathf.Min(height - 2, Mathf.CeilToInt((worldZ + radius - terrainOrigin.z) / cellSizeZ) - startZ);
            if (lastColumn < firstColumn || lastRow < firstRow) return false;
            float normalizedTargetHeight = (targetHeight - terrainOrigin.y) / size.y;
            bool changed = false;
            for (int rowIndex = firstRow; rowIndex <= lastRow; rowIndex++)
            for (int columnIndex = firstColumn; columnIndex <= lastColumn; columnIndex++)
            {
                float cellWorldX = terrainOrigin.x + (startX + columnIndex) * cellSizeX, cellWorldZ = terrainOrigin.z + (startZ + rowIndex) * cellSizeZ;
                float distance = Mathf.Sqrt((cellWorldX - worldX) * (cellWorldX - worldX) + (cellWorldZ - worldZ) * (cellWorldZ - worldZ));
                if (distance >= radius) continue;
                float falloff = Mathf.SmoothStep(1f, 0f, distance / radius);
                int index = rowIndex * width + columnIndex;
                float current = heightBlock[index], newHeight = current;
                switch (mode)
                {
                    case BrushMode.Raise: newHeight = current + amount * falloff / size.y; break;
                    case BrushMode.Lower: newHeight = current - amount * falloff / size.y; break;
                    case BrushMode.Flatten: newHeight = Mathf.Lerp(current, normalizedTargetHeight, Mathf.Clamp01(amount * falloff)); break;
                    case BrushMode.Fill: if (current < normalizedTargetHeight) newHeight = Mathf.Lerp(current, normalizedTargetHeight, Mathf.Clamp01(amount * falloff)); break;
                    case BrushMode.Smooth:
                        float average = (heightBlock[index - 1] + heightBlock[index + 1] + heightBlock[index - width] + heightBlock[index + width] + current) / SmoothSampleCount;
                        newHeight = Mathf.Lerp(current, average, Mathf.Clamp01(amount * falloff));
                        break;
                }
                newHeight = Mathf.Clamp01(newHeight);
                if (newHeight != current) { heightBlock[index] = newHeight; changed = true; }
            }
            return changed;
        }

        public static void Add(string scene, TerrainStroke stroke)
        {
            For(scene).Strokes.Add(stroke);
            Save();
        }

        public static void RemoveLast(string scene, TerrainStroke stroke)
        {
            For(scene).Strokes.Remove(stroke);
            Save();
        }

        // ------------------------------------------------------------------ the brush

        /// The heights a dab changed (for undo during this session).
        internal sealed class Undo
        {
            public TerrainData EditedTerrainData;
            public int StartX, StartZ, Width, Height;
            public float[] HeightsBefore;
        }

        static readonly HashSet<System.IntPtr> s_Dirty = new HashSet<System.IntPtr>();

        /// One touch of the brush at (x, z). amount: metres (raise / lower) or 0..1 strength (others).
        public static void Dab(BrushMode mode, float worldX, float worldZ, float radius, float amount, float targetHeight, List<Undo> undo)
        {
            foreach (var terrain in Terrain.activeTerrains)
            {
                try { DabOn(terrain, mode, worldX, worldZ, radius, amount, targetHeight, undo); }
                catch (System.Exception exception) { SteamRailwayMod.Debug($"Terrain brush on {terrain.name}: {exception.Message}"); }
            }
        }

        static void DabOn(Terrain terrain, BrushMode mode, float worldX, float worldZ, float radius, float amount, float targetHeight, List<Undo> undo)
        {
            var terrainData = terrain.terrainData;
            var terrainOrigin = terrain.transform.position;
            var size = terrainData.size;
            int resolution = terrainData.heightmapResolution;
            float cellSizeX = size.x / (resolution - 1), cellSizeZ = size.z / (resolution - 1);
            int startX = Mathf.Clamp(Mathf.FloorToInt((worldX - radius - terrainOrigin.x) / cellSizeX) - 1, 0, resolution - 1);
            int endX = Mathf.Clamp(Mathf.CeilToInt((worldX + radius - terrainOrigin.x) / cellSizeX) + 1, 0, resolution - 1);
            int startZ = Mathf.Clamp(Mathf.FloorToInt((worldZ - radius - terrainOrigin.z) / cellSizeZ) - 1, 0, resolution - 1);
            int endZ = Mathf.Clamp(Mathf.CeilToInt((worldZ + radius - terrainOrigin.z) / cellSizeZ) + 1, 0, resolution - 1);
            int width = endX - startX + 1, height = endZ - startZ + 1;
            if (width <= 2 || height <= 2) return;
            // the brush must touch this terrain
            if (worldX + radius < terrainOrigin.x || worldX - radius > terrainOrigin.x + size.x || worldZ + radius < terrainOrigin.z || worldZ - radius > terrainOrigin.z + size.z) return;

            var heights = HeightmapAccess.Read(terrainData, startX, startZ, width, height);

            if (heights.Length < width * height) return;
            var sourceHeights = new float[width * height];
            for (int index = 0; index < sourceHeights.Length; index++) sourceHeights[index] = heights[index];
            if (undo != null) undo.Add(new Undo { EditedTerrainData = terrainData, StartX = startX, StartZ = startZ, Width = width, Height = height, HeightsBefore = (float[])sourceHeights.Clone() });

            float normalizedTargetHeight = (targetHeight - terrainOrigin.y) / size.y;
            bool changed = false;
            for (int rowIndex = 0; rowIndex < height; rowIndex++)
            for (int columnIndex = 0; columnIndex < width; columnIndex++)
            {
                float cellWorldX = terrainOrigin.x + (startX + columnIndex) * cellSizeX, cellWorldZ = terrainOrigin.z + (startZ + rowIndex) * cellSizeZ;
                float distance = Mathf.Sqrt((cellWorldX - worldX) * (cellWorldX - worldX) + (cellWorldZ - worldZ) * (cellWorldZ - worldZ));
                if (distance >= radius) continue;
                float falloff = Mathf.SmoothStep(1f, 0f, distance / radius);
                int index = rowIndex * width + columnIndex;
                float current = sourceHeights[index], newHeight = current;
                switch (mode)
                {
                    case BrushMode.Raise: newHeight = current + amount * falloff / size.y; break;
                    case BrushMode.Lower: newHeight = current - amount * falloff / size.y; break;
                    case BrushMode.Flatten: newHeight = Mathf.Lerp(current, normalizedTargetHeight, Mathf.Clamp01(amount * falloff)); break;
                    case BrushMode.Fill: if (current < normalizedTargetHeight) newHeight = Mathf.Lerp(current, normalizedTargetHeight, Mathf.Clamp01(amount * falloff)); break;
                    case BrushMode.Smooth:
                        if (columnIndex > 0 && rowIndex > 0 && columnIndex < width - 1 && rowIndex < height - 1)
                        {
                            float average = (sourceHeights[index - 1] + sourceHeights[index + 1] + sourceHeights[index - width] + sourceHeights[index + width] + current) / SmoothSampleCount;
                            newHeight = Mathf.Lerp(current, average, Mathf.Clamp01(amount * falloff));
                        }
                        break;
                }
                newHeight = Mathf.Clamp01(newHeight);
                if (newHeight != current) { heights[index] = newHeight; changed = true; }
            }
            if (!changed) return;
            HeightmapAccess.Write(terrainData, startX, startZ, heights, true);
            s_Dirty.Add(terrainData.Pointer);
        }

        /// The terrain's level of detail and collider catch up with the brush (at the end of a stroke).
        public static void Sync()
        {
            foreach (var terrain in Terrain.activeTerrains)
            {
                var terrainData = terrain.terrainData;
                if (!s_Dirty.Contains(terrainData.Pointer)) continue;
                try { terrainData.SyncHeightmap(); } catch (System.Exception exception) { SteamRailwayMod.Debug($"Terrain sync: {exception.Message}"); }
            }
            s_Dirty.Clear();
        }

        public static void Restore(List<Undo> undo)
        {
            for (int index = undo.Count - 1; index >= 0; index--)
            {
                var undoEntry = undo[index];
                if (undoEntry.EditedTerrainData == null) continue;
                var heights = HeightmapAccess.Read(undoEntry.EditedTerrainData, undoEntry.StartX, undoEntry.StartZ, undoEntry.Width, undoEntry.Height);
                for (int innerIndex = 0; innerIndex < undoEntry.HeightsBefore.Length && innerIndex < heights.Length; innerIndex++) heights[innerIndex] = undoEntry.HeightsBefore[innerIndex];
                HeightmapAccess.Write(undoEntry.EditedTerrainData, undoEntry.StartX, undoEntry.StartZ, heights, true);
                s_Dirty.Add(undoEntry.EditedTerrainData.Pointer);
            }
            Sync();
        }
    }

    /// The ground brush: hold the left mouse button to paint; the key list is shown on screen while it is on.
    internal static class TerrainEditor
    {
        static BrushMode s_Mode = BrushMode.Raise;
        static float s_Radius = DefaultBrushRadiusInMeters, s_Strength = 1f, s_Offset;
        static TerrainStroke s_Stroke;
        static List<TerrainEdits.Undo> s_StrokeUndo;
        static float s_Target, s_NextDab;
        static readonly List<(TerrainStroke stroke, List<TerrainEdits.Undo> undo)> s_Undo = new List<(TerrainStroke, List<TerrainEdits.Undo>)>();
        static readonly EditorHub.Line s_Ring = new EditorHub.Line("Brush", 0.1f);
        static readonly EditorHub.Line s_Inner = new EditorHub.Line("BrushInner", 0.06f);
        static string s_InfoText = "";

        const float DabIntervalInSeconds = 0.05f;
        const int BrushModeCount = 5;
        const float DefaultBrushRadiusInMeters = 6f;
        const float StrengthStep = 0.1f;
        const float MinimumStrength = 0.1f;
        const float MaximumStrength = 3f;
        const float RadiusStepNormalInMeters = 0.5f;
        const float RadiusStepFastInMeters = 3f;
        const float MinimumRadiusInMeters = 1f;
        const float MaximumRadiusInMeters = 40f;
        const float LevelStepNormalInMeters = 0.25f;
        const float LevelStepFastInMeters = 1f;
        const int OuterRingSegmentCount = 48;
        const int InnerRingSegmentCount = 32;
        const float InnerRingRadiusFraction = 0.5f;
        const float InnerRingColorScale = 0.8f;
        const float MaximumDabDeltaTimeInSeconds = 0.2f;
        const float RaiseLowerRateInMetersPerSecond = 1.5f;
        const float ShareRatePerSecond = 3f;
        const int MaximumUndoStrokes = 50;
        const int PositionDecimalPlaces = 3;
        const int AmountDecimalPlaces = 5;
        const float UnsetInfoValue = -1f;

        static readonly Color s_RaiseColor = new Color(0.4f, 1f, 0.4f);
        static readonly Color s_LowerColor = new Color(1f, 0.45f, 0.35f);
        static readonly Color s_FlattenColor = new Color(0.4f, 0.8f, 1f);
        static readonly Color s_FillColor = new Color(1f, 1f, 1f);
        static readonly Color s_SmoothColor = new Color(1f, 0.85f, 0.4f);

        static BrushMode s_InfoMode;
        static float s_InfoRadius = UnsetInfoValue, s_InfoStrength = UnsetInfoValue, s_InfoOffset = UnsetInfoValue;

        static void RefreshInfo()
        {
            if (s_InfoMode == s_Mode && s_InfoRadius == s_Radius && s_InfoStrength == s_Strength && s_InfoOffset == s_Offset) return;
            s_InfoMode = s_Mode; s_InfoRadius = s_Radius; s_InfoStrength = s_Strength; s_InfoOffset = s_Offset;
            bool levelled = s_Mode == BrushMode.Flatten || s_Mode == BrushMode.Fill;
            s_InfoText = $"{ModeName(s_Mode)}   " + Localizer.Translate($"boyut {s_Radius:F1} m   güç {s_Strength:F1}", $"size {s_Radius:F1} m   strength {s_Strength:F1}")
                   + (levelled ? Localizer.Translate($"   seviye {s_Offset:+0.00;-0.00} m", $"   level {s_Offset:+0.00;-0.00} m") : "");
        }

        static string ModeName(BrushMode brushMode) => brushMode switch
        {
            BrushMode.Raise => Localizer.Translate("Yükselt", "Raise"),
            BrushMode.Lower => Localizer.Translate("Alçalt", "Lower"),
            BrushMode.Flatten => Localizer.Translate("Düzle", "Flatten"),
            BrushMode.Fill => Localizer.Translate("Doldur (kar dolgusu)", "Fill (snow fill)"),
            _ => Localizer.Translate("Yumuşat", "Smooth"),
        };

        public static void Leave()
        {
            EndStroke();
            s_Ring.Hide(); s_Inner.Hide();
        }

        public static void Update()
        {
            string scene = RailwayManager.CurrentScene;
            if (Input.GetKeyDown(KeyCode.M)) { s_Mode = (BrushMode)(((int)s_Mode + 1) % BrushModeCount); CabControls.Message(Localizer.Translate("Fırça: ", "Brush: ") + ModeName(s_Mode)); }
            float wheel = Input.mouseScrollDelta.y;
            if (wheel != 0f)
            {
                if (EditorHub.Control) s_Strength = Mathf.Clamp(s_Strength + Mathf.Sign(wheel) * StrengthStep, MinimumStrength, MaximumStrength);
                else s_Radius = Mathf.Clamp(s_Radius + Mathf.Sign(wheel) * (EditorHub.Shift ? RadiusStepFastInMeters : RadiusStepNormalInMeters), MinimumRadiusInMeters, MaximumRadiusInMeters);
            }
            if (Input.GetKeyDown(KeyCode.PageUp)) s_Offset += EditorHub.Shift ? LevelStepFastInMeters : LevelStepNormalInMeters;
            if (Input.GetKeyDown(KeyCode.PageDown)) s_Offset -= EditorHub.Shift ? LevelStepFastInMeters : LevelStepNormalInMeters;
            if (Input.GetKeyDown(KeyCode.R) && s_Stroke == null)
            {
                if (RailwayManager.RebuildForEditor()) CabControls.Message(Localizer.Translate("Raylar yeni zemine göre yeniden kuruluyor...", "Laying the rails again on the new ground..."));
                else CabControls.Message(Localizer.Translate("Önce trenden in.", "Leave the train first."));
            }
            if (EditorHub.Control && Input.GetKeyDown(KeyCode.Z) && s_Stroke == null) UndoStroke(scene);

            bool aimed = EditorHub.Aim(out var aimPoint);
            if (aimed)
            {
                s_Ring.Set(EditorHub.Ring(aimPoint, s_Radius, OuterRingSegmentCount), ModeColor(), true);
                s_Inner.Set(EditorHub.Ring(aimPoint, s_Radius * InnerRingRadiusFraction, InnerRingSegmentCount), ModeColor() * InnerRingColorScale, true);
            }
            else { s_Ring.Hide(); s_Inner.Hide(); }

            RefreshInfo();

            if (Input.GetMouseButton(0) && aimed)
            {
                if (s_Stroke == null)
                {
                    s_Stroke = new TerrainStroke { Mode = (int)s_Mode };
                    s_StrokeUndo = new List<TerrainEdits.Undo>();
                    s_Target = Il2CppHelpers.TerrainHeight(aimPoint, aimPoint.y) + s_Offset;
                    s_NextDab = 0f;
                }
                if (Time.time >= s_NextDab)
                {
                    float deltaTime = s_NextDab == 0f ? DabIntervalInSeconds : Mathf.Min(Time.time - (s_NextDab - DabIntervalInSeconds), MaximumDabDeltaTimeInSeconds);
                    s_NextDab = Time.time + DabIntervalInSeconds;
                    // raise / lower in metres per second at the centre; the others as a share per second
                    float amount = (s_Mode == BrushMode.Raise || s_Mode == BrushMode.Lower) ? RaiseLowerRateInMetersPerSecond * s_Strength * deltaTime : Mathf.Clamp01(ShareRatePerSecond * s_Strength * deltaTime);
                    TerrainEdits.Dab(s_Mode, aimPoint.x, aimPoint.z, s_Radius, amount, s_Target, s_StrokeUndo);
                    s_Stroke.Dabs.Add(new[] { RoundPositionComponent(aimPoint.x), RoundPositionComponent(aimPoint.z), RoundPositionComponent(s_Radius), (float)System.Math.Round(amount, AmountDecimalPlaces), RoundPositionComponent(s_Target) });
                }
            }
            else if (s_Stroke != null) EndStroke();
        }

        static float RoundPositionComponent(float component) => (float)System.Math.Round(component, PositionDecimalPlaces);

        static void EndStroke()
        {
            if (s_Stroke == null) return;
            TerrainEdits.Sync();
            string scene = RailwayManager.CurrentScene;
            if (s_Stroke.Dabs.Count > 0 && scene != null)
            {
                TerrainEdits.Add(scene, s_Stroke);
                s_Undo.Add((s_Stroke, s_StrokeUndo));
                if (s_Undo.Count > MaximumUndoStrokes) s_Undo.RemoveAt(0);
            }
            s_Stroke = null; s_StrokeUndo = null;
        }

        static void UndoStroke(string scene)
        {
            if (s_Undo.Count == 0) { CabControls.Message(Localizer.Translate("Bu oturumda geri alınacak fırça darbesi yok.", "No brush stroke to undo in this session.")); return; }
            var (stroke, undo) = s_Undo[s_Undo.Count - 1];
            s_Undo.RemoveAt(s_Undo.Count - 1);
            TerrainEdits.Restore(undo);
            TerrainEdits.RemoveLast(scene, stroke);
            CabControls.Message(Localizer.Translate("Son fırça darbesi geri alındı.", "Last brush stroke undone."));
        }

        static Color ModeColor() => s_Mode switch
        {
            BrushMode.Raise => s_RaiseColor,
            BrushMode.Lower => s_LowerColor,
            BrushMode.Flatten => s_FlattenColor,
            BrushMode.Fill => s_FillColor,
            _ => s_SmoothColor,
        };

        public static void OnGUI()
        {
            EditorHub.Panel(Localizer.Translate("ARAZİ DÜZENLEYİCİ", "TERRAIN EDITOR"), s_InfoText,
                s_Mode == BrushMode.Fill ? Localizer.Translate("Doldur: zemin, darbenin başladığı yerin seviyesine (+ayar) kadar yükselir, asla kazılmaz",
                                               "Fill: the ground rises to the level where the stroke starts (+offset), it is never dug")
                                         : "",
                Localizer.Translate("Sol tık (basılı) fırçala   M fırça türü   tekerlek boyut (Shift hızlı)   Ctrl+tekerlek güç   PageUp/PageDown seviye\nR rayları yeniden kur   Ctrl+Z son darbeyi geri al   F11 sonraki mod",
                    "Left mouse (hold) paint   M brush   wheel size (Shift faster)   Ctrl+wheel strength   PageUp/PageDown level\nR lay the rails again   Ctrl+Z undo last stroke   F11 next mode"));
        }
    }
}
