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

    /// Hand-made ground changes (raised or lowered snow, levelled beds, snow fills to lay rails on), kept as brush
    /// strokes in Mods\SteamRailway\Terrain\<region>.json and played back each time the region loads, before the
    /// railway is laid. Only the loaded terrain in memory changes; the game's files are untouched.
    internal static class TerrainEdits
    {
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
                catch (System.Exception e) { SteamRailwayMod.Log.Warning($"{path} could not be read: {e.Message}"); }
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
            catch (System.Exception e) { SteamRailwayMod.Log.Warning($"Writing the terrain edits failed: {e.Message}"); }
        }

        /// A new region was loaded: its strokes are played back on the next build.
        public static void SceneLoaded() => s_AppliedScene = null;

        public static void ApplyScene(string scene)
        {
            if (s_AppliedScene == scene) return;
            var strokeFile = For(scene);
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

        static bool ReplayOn(Terrain terrain, TerrainEditFile editFile)
        {
            var td = terrain.terrainData;
            var targetPoint = terrain.transform.position;
            var size = td.size;
            int resolution = td.heightmapResolution;
            float centerX = size.x / (resolution - 1), centerZ = size.z / (resolution - 1);
            // the area all the dabs touch on this terrain
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            foreach (var stroke in editFile.Strokes)
                if (stroke.Dabs != null)
                    foreach (var d in stroke.Dabs)
                    {
                        if (d == null || d.Length < 5) continue;
                        minX = Mathf.Min(minX, d[0] - d[2]); maxX = Mathf.Max(maxX, d[0] + d[2]);
                        minZ = Mathf.Min(minZ, d[1] - d[2]); maxZ = Mathf.Max(maxZ, d[1] + d[2]);
                    }
            if (maxX < targetPoint.x || minX > targetPoint.x + size.x || maxZ < targetPoint.z || minZ > targetPoint.z + size.z) return false;
            int startX = Mathf.Clamp(Mathf.FloorToInt((minX - targetPoint.x) / centerX) - 1, 0, resolution - 1);
            int endX = Mathf.Clamp(Mathf.CeilToInt((maxX - targetPoint.x) / centerX) + 1, 0, resolution - 1);
            int startZ = Mathf.Clamp(Mathf.FloorToInt((minZ - targetPoint.z) / centerZ) - 1, 0, resolution - 1);
            int endZ = Mathf.Clamp(Mathf.CeilToInt((maxZ - targetPoint.z) / centerZ) + 1, 0, resolution - 1);
            int width = endX - startX + 1, height = endZ - startZ + 1;
            if (width <= 2 || height <= 2) return false;
            var heights = HeightmapAccess.Read(td, startX, startZ, width, height);
            if (heights.Length < width * height) return false;
            var a = new float[width * height];
            for (int index = 0; index < a.Length; index++) a[index] = heights[index];
            bool changed = false;
            foreach (var stroke in editFile.Strokes)
                if (stroke.Dabs != null)
                    foreach (var d in stroke.Dabs)
                        if (d != null && d.Length >= 5)
                            changed |= DabArray(a, width, height, startX, startZ, targetPoint, size, centerX, centerZ, (BrushMode)stroke.Mode, d[0], d[1], d[2], d[3], d[4]);
            if (!changed) return false;
            for (int index = 0; index < a.Length; index++) heights[index] = a[index];
            HeightmapAccess.Write(td, startX, startZ, heights);
            return true;
        }

        /// One dab on heights held in memory (a w x h block starting at heightmap x0, z0).
        static bool DabArray(float[] a, int width, int height, int startX, int startZ, Vector3 targetPoint, Vector3 size, float centerX, float centerZ,
                             BrushMode mode, float x, float z, float radius, float amount, float target)
        {
            int firstColumn = Mathf.Max(1, Mathf.FloorToInt((x - radius - targetPoint.x) / centerX) - startX);
            int lastColumn = Mathf.Min(width - 2, Mathf.CeilToInt((x + radius - targetPoint.x) / centerX) - startX);
            int firstRow = Mathf.Max(1, Mathf.FloorToInt((z - radius - targetPoint.z) / centerZ) - startZ);
            int lastRow = Mathf.Min(height - 2, Mathf.CeilToInt((z + radius - targetPoint.z) / centerZ) - startZ);
            if (lastColumn < firstColumn || lastRow < firstRow) return false;
            float tgt = (target - targetPoint.y) / size.y;
            bool changed = false;
            for (int rowIndex = firstRow; rowIndex <= lastRow; rowIndex++)
            for (int columnIndex = firstColumn; columnIndex <= lastColumn; columnIndex++)
            {
                float wx = targetPoint.x + (startX + columnIndex) * centerX, wz = targetPoint.z + (startZ + rowIndex) * centerZ;
                float distance = Mathf.Sqrt((wx - x) * (wx - x) + (wz - z) * (wz - z));
                if (distance >= radius) continue;
                float fall = Mathf.SmoothStep(1f, 0f, distance / radius);
                int index = rowIndex * width + columnIndex;
                float current = a[index], nv = current;
                switch (mode)
                {
                    case BrushMode.Raise: nv = current + amount * fall / size.y; break;
                    case BrushMode.Lower: nv = current - amount * fall / size.y; break;
                    case BrushMode.Flatten: nv = Mathf.Lerp(current, tgt, Mathf.Clamp01(amount * fall)); break;
                    case BrushMode.Fill: if (current < tgt) nv = Mathf.Lerp(current, tgt, Mathf.Clamp01(amount * fall)); break;
                    case BrushMode.Smooth:
                        float average = (a[index - 1] + a[index + 1] + a[index - width] + a[index + width] + current) / 5f;
                        nv = Mathf.Lerp(current, average, Mathf.Clamp01(amount * fall));
                        break;
                }
                nv = Mathf.Clamp01(nv);
                if (nv != current) { a[index] = nv; changed = true; }
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
            public TerrainData Td;
            public int X0, Z0, W, H;
            public float[] Data;
        }

        static readonly HashSet<System.IntPtr> s_Dirty = new HashSet<System.IntPtr>();

        /// One touch of the brush at (x, z). amount: metres (raise / lower) or 0..1 strength (others).
        public static void Dab(BrushMode mode, float x, float z, float radius, float amount, float target, List<Undo> undo)
        {
            foreach (var terrain in Terrain.activeTerrains)
            {
                try { DabOn(terrain, mode, x, z, radius, amount, target, undo); }
                catch (System.Exception e) { SteamRailwayMod.Debug($"Terrain brush on {terrain.name}: {e.Message}"); }
            }
        }

        static void DabOn(Terrain terrain, BrushMode mode, float x, float z, float radius, float amount, float target, List<Undo> undo)
        {
            var td = terrain.terrainData;
            var targetPoint = terrain.transform.position;
            var size = td.size;
            int resolution = td.heightmapResolution;
            float centerX = size.x / (resolution - 1), centerZ = size.z / (resolution - 1);
            int startX = Mathf.Clamp(Mathf.FloorToInt((x - radius - targetPoint.x) / centerX) - 1, 0, resolution - 1);
            int endX = Mathf.Clamp(Mathf.CeilToInt((x + radius - targetPoint.x) / centerX) + 1, 0, resolution - 1);
            int startZ = Mathf.Clamp(Mathf.FloorToInt((z - radius - targetPoint.z) / centerZ) - 1, 0, resolution - 1);
            int endZ = Mathf.Clamp(Mathf.CeilToInt((z + radius - targetPoint.z) / centerZ) + 1, 0, resolution - 1);
            int width = endX - startX + 1, height = endZ - startZ + 1;
            if (width <= 2 || height <= 2) return;
            // the brush must touch this terrain
            if (x + radius < targetPoint.x || x - radius > targetPoint.x + size.x || z + radius < targetPoint.z || z - radius > targetPoint.z + size.z) return;

            var heights = HeightmapAccess.Read(td, startX, startZ, width, height);

            if (heights.Length < width * height) return;
            var src = new float[width * height];
            for (int index = 0; index < src.Length; index++) src[index] = heights[index];
            if (undo != null) undo.Add(new Undo { Td = td, X0 = startX, Z0 = startZ, W = width, H = height, Data = (float[])src.Clone() });

            float tgt = (target - targetPoint.y) / size.y;
            bool changed = false;
            for (int rowIndex = 0; rowIndex < height; rowIndex++)
            for (int columnIndex = 0; columnIndex < width; columnIndex++)
            {
                float wx = targetPoint.x + (startX + columnIndex) * centerX, wz = targetPoint.z + (startZ + rowIndex) * centerZ;
                float distance = Mathf.Sqrt((wx - x) * (wx - x) + (wz - z) * (wz - z));
                if (distance >= radius) continue;
                float factor = Mathf.SmoothStep(1f, 0f, distance / radius);
                int index = rowIndex * width + columnIndex;
                float current = src[index], nv = current;
                switch (mode)
                {
                    case BrushMode.Raise: nv = current + amount * factor / size.y; break;
                    case BrushMode.Lower: nv = current - amount * factor / size.y; break;
                    case BrushMode.Flatten: nv = Mathf.Lerp(current, tgt, Mathf.Clamp01(amount * factor)); break;
                    case BrushMode.Fill: if (current < tgt) nv = Mathf.Lerp(current, tgt, Mathf.Clamp01(amount * factor)); break;
                    case BrushMode.Smooth:
                        if (columnIndex > 0 && rowIndex > 0 && columnIndex < width - 1 && rowIndex < height - 1)
                        {
                            float average = (src[index - 1] + src[index + 1] + src[index - width] + src[index + width] + current) / 5f;
                            nv = Mathf.Lerp(current, average, Mathf.Clamp01(amount * factor));
                        }
                        break;
                }
                nv = Mathf.Clamp01(nv);
                if (nv != current) { heights[index] = nv; changed = true; }
            }
            if (!changed) return;
            HeightmapAccess.Write(td, startX, startZ, heights, true);
            s_Dirty.Add(td.Pointer);
        }

        /// The terrain's level of detail and collider catch up with the brush (at the end of a stroke).
        public static void Sync()
        {
            foreach (var terrain in Terrain.activeTerrains)
            {
                var td = terrain.terrainData;
                if (!s_Dirty.Contains(td.Pointer)) continue;
                try { td.SyncHeightmap(); } catch (System.Exception e) { SteamRailwayMod.Debug($"Terrain sync: {e.Message}"); }
            }
            s_Dirty.Clear();
        }

        public static void Restore(List<Undo> undo)
        {
            for (int index = undo.Count - 1; index >= 0; index--)
            {
                var u = undo[index];
                if (u.Td == null) continue;
                var heights = HeightmapAccess.Read(u.Td, u.X0, u.Z0, u.W, u.H);
                for (int deepIndex = 0; deepIndex < u.Data.Length && deepIndex < heights.Length; deepIndex++) heights[deepIndex] = u.Data[deepIndex];
                HeightmapAccess.Write(u.Td, u.X0, u.Z0, heights, true);
                s_Dirty.Add(u.Td.Pointer);
            }
            Sync();
        }
    }

    /// The ground brush.
    ///   left mouse (hold)  paint          M  next brush (raise, lower, flatten, fill, smooth)
    ///   wheel  size (Shift: faster)       Ctrl + wheel  strength
    ///   PageUp / PageDown  level for flatten / fill, above the ground where the stroke starts
    ///   R  lay the rails again on the new ground       Ctrl+Z  undo the last stroke
    internal static class TerrainEditor
    {
        static BrushMode s_Mode = BrushMode.Raise;
        static float s_Radius = 6f, s_Strength = 1f, s_Offset;
        static TerrainStroke s_Stroke;
        static List<TerrainEdits.Undo> s_StrokeUndo;
        static float s_Target, s_NextDab;
        static readonly List<(TerrainStroke st, List<TerrainEdits.Undo> undo)> s_Undo = new List<(TerrainStroke, List<TerrainEdits.Undo>)>();
        static readonly EditorHub.Line s_Ring = new EditorHub.Line("Brush", 0.1f);
        static readonly EditorHub.Line s_Inner = new EditorHub.Line("BrushInner", 0.06f);
        static string s_Info = "";

        const float DabRate = 0.05f;   // s between dabs while the mouse is held

        static string ModeName(BrushMode brushMode) => brushMode switch
        {
            BrushMode.Raise => L.T("Yükselt", "Raise"),
            BrushMode.Lower => L.T("Alçalt", "Lower"),
            BrushMode.Flatten => L.T("Düzle", "Flatten"),
            BrushMode.Fill => L.T("Doldur (kar dolgusu)", "Fill (snow fill)"),
            _ => L.T("Yumuşat", "Smooth"),
        };

        public static void Leave()
        {
            EndStroke();
            s_Ring.Hide(); s_Inner.Hide();
        }

        public static void Update()
        {
            string scene = RailwayManager.CurrentScene;
            if (Input.GetKeyDown(KeyCode.M)) { s_Mode = (BrushMode)(((int)s_Mode + 1) % 5); CabControls.Message(L.T("Fırça: ", "Brush: ") + ModeName(s_Mode)); }
            float wheel = Input.mouseScrollDelta.y;
            if (wheel != 0f)
            {
                if (EditorHub.Ctrl) s_Strength = Mathf.Clamp(s_Strength + Mathf.Sign(wheel) * 0.1f, 0.1f, 3f);
                else s_Radius = Mathf.Clamp(s_Radius + Mathf.Sign(wheel) * (EditorHub.Shift ? 3f : 0.5f), 1f, 40f);
            }
            if (Input.GetKeyDown(KeyCode.PageUp)) s_Offset += EditorHub.Shift ? 1f : 0.25f;
            if (Input.GetKeyDown(KeyCode.PageDown)) s_Offset -= EditorHub.Shift ? 1f : 0.25f;
            if (Input.GetKeyDown(KeyCode.R) && s_Stroke == null)
            {
                if (RailwayManager.RebuildForEditor()) CabControls.Message(L.T("Raylar yeni zemine göre yeniden kuruluyor...", "Laying the rails again on the new ground..."));
                else CabControls.Message(L.T("Önce trenden in.", "Leave the train first."));
            }
            if (EditorHub.Ctrl && Input.GetKeyDown(KeyCode.Z) && s_Stroke == null) UndoStroke(scene);

            bool aimed = EditorHub.Aim(out var hit);
            if (aimed)
            {
                s_Ring.Set(EditorHub.Ring(hit, s_Radius, 48), ModeColor(), true);
                s_Inner.Set(EditorHub.Ring(hit, s_Radius * 0.5f, 32), ModeColor() * 0.8f, true);
            }
            else { s_Ring.Hide(); s_Inner.Hide(); }

            bool levelled = s_Mode == BrushMode.Flatten || s_Mode == BrushMode.Fill;
            s_Info = $"{ModeName(s_Mode)}   " + L.T($"boyut {s_Radius:F1} m   güç {s_Strength:F1}", $"size {s_Radius:F1} m   strength {s_Strength:F1}")
                   + (levelled ? L.T($"   seviye {s_Offset:+0.00;-0.00} m", $"   level {s_Offset:+0.00;-0.00} m") : "");

            if (Input.GetMouseButton(0) && aimed)
            {
                if (s_Stroke == null)
                {
                    s_Stroke = new TerrainStroke { Mode = (int)s_Mode };
                    s_StrokeUndo = new List<TerrainEdits.Undo>();
                    s_Target = H.TerrainHeight(hit, hit.y) + s_Offset;
                    s_NextDab = 0f;
                }
                if (Time.time >= s_NextDab)
                {
                    float deltaTime = s_NextDab == 0f ? DabRate : Mathf.Min(Time.time - (s_NextDab - DabRate), 0.2f);
                    s_NextDab = Time.time + DabRate;
                    // raise / lower in metres per second at the centre; the others as a share per second
                    float amount = (s_Mode == BrushMode.Raise || s_Mode == BrushMode.Lower) ? 1.5f * s_Strength * deltaTime : Mathf.Clamp01(3f * s_Strength * deltaTime);
                    TerrainEdits.Dab(s_Mode, hit.x, hit.z, s_Radius, amount, s_Target, s_StrokeUndo);
                    s_Stroke.Dabs.Add(new[] { R3(hit.x), R3(hit.z), R3(s_Radius), (float)System.Math.Round(amount, 5), R3(s_Target) });
                }
            }
            else if (s_Stroke != null) EndStroke();
        }

        static float R3(float factor) => (float)System.Math.Round(factor, 3);

        static void EndStroke()
        {
            if (s_Stroke == null) return;
            TerrainEdits.Sync();
            string scene = RailwayManager.CurrentScene;
            if (s_Stroke.Dabs.Count > 0 && scene != null)
            {
                TerrainEdits.Add(scene, s_Stroke);
                s_Undo.Add((s_Stroke, s_StrokeUndo));
                if (s_Undo.Count > 50) s_Undo.RemoveAt(0);
            }
            s_Stroke = null; s_StrokeUndo = null;
        }

        static void UndoStroke(string scene)
        {
            if (s_Undo.Count == 0) { CabControls.Message(L.T("Bu oturumda geri alınacak fırça darbesi yok.", "No brush stroke to undo in this session.")); return; }
            var (stroke, undo) = s_Undo[s_Undo.Count - 1];
            s_Undo.RemoveAt(s_Undo.Count - 1);
            TerrainEdits.Restore(undo);
            TerrainEdits.RemoveLast(scene, stroke);
            CabControls.Message(L.T("Son fırça darbesi geri alındı.", "Last brush stroke undone."));
        }

        static Color ModeColor() => s_Mode switch
        {
            BrushMode.Raise => new Color(0.4f, 1f, 0.4f),
            BrushMode.Lower => new Color(1f, 0.45f, 0.35f),
            BrushMode.Flatten => new Color(0.4f, 0.8f, 1f),
            BrushMode.Fill => new Color(1f, 1f, 1f),
            _ => new Color(1f, 0.85f, 0.4f),
        };

        public static void OnGUI()
        {
            EditorHub.Panel(L.T("ARAZİ DÜZENLEYİCİ", "TERRAIN EDITOR"), s_Info,
                s_Mode == BrushMode.Fill ? L.T("Doldur: zemin, darbenin başladığı yerin seviyesine (+ayar) kadar yükselir, asla kazılmaz",
                                               "Fill: the ground rises to the level where the stroke starts (+offset), it is never dug")
                                         : "",
                L.T("Sol tık (basılı) fırçala   M fırça türü   tekerlek boyut (Shift hızlı)   Ctrl+tekerlek güç   PageUp/PageDown seviye\nR rayları yeniden kur   Ctrl+Z son darbeyi geri al   F11 sonraki mod",
                    "Left mouse (hold) paint   M brush   wheel size (Shift faster)   Ctrl+wheel strength   PageUp/PageDown level\nR lay the rails again   Ctrl+Z undo last stroke   F11 next mode"));
        }
    }
}
