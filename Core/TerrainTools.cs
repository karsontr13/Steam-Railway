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
            s_AppliedScene = scene;
            var f = For(scene);
            if (f.Strokes.Count == 0) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int dabs = 0;
            foreach (var st in f.Strokes) dabs += st.Dabs != null ? st.Dabs.Count : 0;
            // every terrain is read once, all the dabs are applied in memory, and it is written once
            foreach (var terrain in Terrain.activeTerrains)
            {
                try { ReplayOn(terrain, f); }
                catch (System.Exception e) { SteamRailwayMod.Log.Warning($"Terrain edits on {terrain.name}: {e.Message}"); }
            }
            SteamRailwayMod.Log.Msg($"{scene}: {f.Strokes.Count} terrain stroke(s) ({dabs} dabs) applied in {sw.ElapsedMilliseconds} ms.");
        }

        static void ReplayOn(Terrain terrain, TerrainEditFile f)
        {
            var td = terrain.terrainData;
            var tp = terrain.transform.position;
            var size = td.size;
            int res = td.heightmapResolution;
            float cx = size.x / (res - 1), cz = size.z / (res - 1);
            // the area all the dabs touch on this terrain
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            foreach (var st in f.Strokes)
                if (st.Dabs != null)
                    foreach (var d in st.Dabs)
                    {
                        if (d == null || d.Length < 5) continue;
                        minX = Mathf.Min(minX, d[0] - d[2]); maxX = Mathf.Max(maxX, d[0] + d[2]);
                        minZ = Mathf.Min(minZ, d[1] - d[2]); maxZ = Mathf.Max(maxZ, d[1] + d[2]);
                    }
            if (maxX < tp.x || minX > tp.x + size.x || maxZ < tp.z || minZ > tp.z + size.z) return;
            int x0 = Mathf.Clamp(Mathf.FloorToInt((minX - tp.x) / cx) - 1, 0, res - 1);
            int x1 = Mathf.Clamp(Mathf.CeilToInt((maxX - tp.x) / cx) + 1, 0, res - 1);
            int z0 = Mathf.Clamp(Mathf.FloorToInt((minZ - tp.z) / cz) - 1, 0, res - 1);
            int z1 = Mathf.Clamp(Mathf.CeilToInt((maxZ - tp.z) / cz) + 1, 0, res - 1);
            int w = x1 - x0 + 1, h = z1 - z0 + 1;
            if (w <= 2 || h <= 2) return;
            var obj = td.GetHeights(x0, z0, w, h);
            var hs = new Il2CppStructArray<float>(obj.Pointer);
            if (hs.Length < w * h) return;
            var a = new float[w * h];
            for (int i = 0; i < a.Length; i++) a[i] = hs[i];
            bool changed = false;
            foreach (var st in f.Strokes)
                if (st.Dabs != null)
                    foreach (var d in st.Dabs)
                        if (d != null && d.Length >= 5)
                            changed |= DabArray(a, w, h, x0, z0, tp, size, cx, cz, (BrushMode)st.Mode, d[0], d[1], d[2], d[3], d[4]);
            if (!changed) return;
            for (int i = 0; i < a.Length; i++) hs[i] = a[i];
            td.SetHeights(x0, z0, obj);
        }

        /// One dab on heights held in memory (a w x h block starting at heightmap x0, z0).
        static bool DabArray(float[] a, int w, int h, int x0, int z0, Vector3 tp, Vector3 size, float cx, float cz,
                             BrushMode mode, float x, float z, float radius, float amount, float target)
        {
            int ix0 = Mathf.Max(1, Mathf.FloorToInt((x - radius - tp.x) / cx) - x0);
            int ix1 = Mathf.Min(w - 2, Mathf.CeilToInt((x + radius - tp.x) / cx) - x0);
            int iz0 = Mathf.Max(1, Mathf.FloorToInt((z - radius - tp.z) / cz) - z0);
            int iz1 = Mathf.Min(h - 2, Mathf.CeilToInt((z + radius - tp.z) / cz) - z0);
            if (ix1 < ix0 || iz1 < iz0) return false;
            float tgt = (target - tp.y) / size.y;
            bool changed = false;
            for (int iz = iz0; iz <= iz1; iz++)
            for (int ix = ix0; ix <= ix1; ix++)
            {
                float wx = tp.x + (x0 + ix) * cx, wz = tp.z + (z0 + iz) * cz;
                float d = Mathf.Sqrt((wx - x) * (wx - x) + (wz - z) * (wz - z));
                if (d >= radius) continue;
                float fall = Mathf.SmoothStep(1f, 0f, d / radius);
                int idx = iz * w + ix;
                float cur = a[idx], nv = cur;
                switch (mode)
                {
                    case BrushMode.Raise: nv = cur + amount * fall / size.y; break;
                    case BrushMode.Lower: nv = cur - amount * fall / size.y; break;
                    case BrushMode.Flatten: nv = Mathf.Lerp(cur, tgt, Mathf.Clamp01(amount * fall)); break;
                    case BrushMode.Fill: if (cur < tgt) nv = Mathf.Lerp(cur, tgt, Mathf.Clamp01(amount * fall)); break;
                    case BrushMode.Smooth:
                        float avg = (a[idx - 1] + a[idx + 1] + a[idx - w] + a[idx + w] + cur) / 5f;
                        nv = Mathf.Lerp(cur, avg, Mathf.Clamp01(amount * fall));
                        break;
                }
                nv = Mathf.Clamp01(nv);
                if (nv != cur) { a[idx] = nv; changed = true; }
            }
            return changed;
        }

        public static void Add(string scene, TerrainStroke st)
        {
            For(scene).Strokes.Add(st);
            Save();
        }

        public static void RemoveLast(string scene, TerrainStroke st)
        {
            For(scene).Strokes.Remove(st);
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
            var tp = terrain.transform.position;
            var size = td.size;
            int res = td.heightmapResolution;
            float cx = size.x / (res - 1), cz = size.z / (res - 1);
            int x0 = Mathf.Clamp(Mathf.FloorToInt((x - radius - tp.x) / cx) - 1, 0, res - 1);
            int x1 = Mathf.Clamp(Mathf.CeilToInt((x + radius - tp.x) / cx) + 1, 0, res - 1);
            int z0 = Mathf.Clamp(Mathf.FloorToInt((z - radius - tp.z) / cz) - 1, 0, res - 1);
            int z1 = Mathf.Clamp(Mathf.CeilToInt((z + radius - tp.z) / cz) + 1, 0, res - 1);
            int w = x1 - x0 + 1, h = z1 - z0 + 1;
            if (w <= 2 || h <= 2) return;
            // the brush must touch this terrain
            if (x + radius < tp.x || x - radius > tp.x + size.x || z + radius < tp.z || z - radius > tp.z + size.z) return;

            var obj = td.GetHeights(x0, z0, w, h);
            var hs = new Il2CppStructArray<float>(obj.Pointer);
            if (hs.Length < w * h) return;
            var src = new float[w * h];
            for (int i = 0; i < src.Length; i++) src[i] = hs[i];
            if (undo != null) undo.Add(new Undo { Td = td, X0 = x0, Z0 = z0, W = w, H = h, Data = (float[])src.Clone() });

            float tgt = (target - tp.y) / size.y;
            bool changed = false;
            for (int iz = 0; iz < h; iz++)
            for (int ix = 0; ix < w; ix++)
            {
                float wx = tp.x + (x0 + ix) * cx, wz = tp.z + (z0 + iz) * cz;
                float d = Mathf.Sqrt((wx - x) * (wx - x) + (wz - z) * (wz - z));
                if (d >= radius) continue;
                float f = Mathf.SmoothStep(1f, 0f, d / radius);
                int idx = iz * w + ix;
                float cur = src[idx], nv = cur;
                switch (mode)
                {
                    case BrushMode.Raise: nv = cur + amount * f / size.y; break;
                    case BrushMode.Lower: nv = cur - amount * f / size.y; break;
                    case BrushMode.Flatten: nv = Mathf.Lerp(cur, tgt, Mathf.Clamp01(amount * f)); break;
                    case BrushMode.Fill: if (cur < tgt) nv = Mathf.Lerp(cur, tgt, Mathf.Clamp01(amount * f)); break;
                    case BrushMode.Smooth:
                        if (ix > 0 && iz > 0 && ix < w - 1 && iz < h - 1)
                        {
                            float avg = (src[idx - 1] + src[idx + 1] + src[idx - w] + src[idx + w] + cur) / 5f;
                            nv = Mathf.Lerp(cur, avg, Mathf.Clamp01(amount * f));
                        }
                        break;
                }
                nv = Mathf.Clamp01(nv);
                if (nv != cur) { hs[idx] = nv; changed = true; }
            }
            if (!changed) return;
            td.SetHeightsDelayLOD(x0, z0, obj);
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
            for (int i = undo.Count - 1; i >= 0; i--)
            {
                var u = undo[i];
                if (u.Td == null) continue;
                var obj = u.Td.GetHeights(u.X0, u.Z0, u.W, u.H);
                var hs = new Il2CppStructArray<float>(obj.Pointer);
                for (int k = 0; k < u.Data.Length && k < hs.Length; k++) hs[k] = u.Data[k];
                u.Td.SetHeightsDelayLOD(u.X0, u.Z0, obj);
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

        static string ModeName(BrushMode m) => m switch
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
                    float dt = s_NextDab == 0f ? DabRate : Mathf.Min(Time.time - (s_NextDab - DabRate), 0.2f);
                    s_NextDab = Time.time + DabRate;
                    // raise / lower in metres per second at the centre; the others as a share per second
                    float amount = (s_Mode == BrushMode.Raise || s_Mode == BrushMode.Lower) ? 1.5f * s_Strength * dt : Mathf.Clamp01(3f * s_Strength * dt);
                    TerrainEdits.Dab(s_Mode, hit.x, hit.z, s_Radius, amount, s_Target, s_StrokeUndo);
                    s_Stroke.Dabs.Add(new[] { R3(hit.x), R3(hit.z), R3(s_Radius), (float)System.Math.Round(amount, 5), R3(s_Target) });
                }
            }
            else if (s_Stroke != null) EndStroke();
        }

        static float R3(float f) => (float)System.Math.Round(f, 3);

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
            var (st, undo) = s_Undo[s_Undo.Count - 1];
            s_Undo.RemoveAt(s_Undo.Count - 1);
            TerrainEdits.Restore(undo);
            TerrainEdits.RemoveLast(scene, st);
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
