using System.IO;
using UnityEngine;

namespace SteamRailway
{
    /// Loads the locomotive model from Mods\SteamRailway\steamrailway.bundle (built by Unity\Editor\SteamRailwayBundle.cs).
    /// The bundle holds meshes, the palette texture and empty Socket_ transforms only. Materials are built here with
    /// the game's own shaders so the model is lit like the rest of the world:
    ///   Glass_*  -> TLD_StandardTransparent (see-through windows / gauge glasses)
    ///   Dials    -> TLD_StandardDiffuse with Cab_Dials (dial faces and instrument plates, Tools/DialPainter)
    ///   Col_*    -> invisible box colliders (the solid hull)
    ///   others   -> TLD_StandardDiffuse with the palette texture
    internal static class LocomotiveModel
    {
        const string BundleFile = "steamrailway.bundle";
        const string PrefabName = "SteamRailway_Locomotive";
        const string PaletteName = "Loco_Palette";
        const string DialsName = "Cab_Dials";
        const string DiffuseShader = "Shader Forge/TLD_StandardDiffuse";
        const string TransparentShader = "Shader Forge/TLD_StandardTransparent";
        static readonly Color GlassTint = new Color(0.55f, 0.6f, 0.65f, 0.22f);

        static bool s_Tried;
        static AssetBundle s_Bundle;
        static readonly System.Collections.Generic.Dictionary<string, AudioClip> s_Sounds = new System.Collections.Generic.Dictionary<string, AudioClip>();
        static GameObject s_Prefab;
        static Texture2D s_Palette, s_DialsTex;
        static Material s_Material, s_Glass, s_Dials;

        /// The game unloads unused assets when a region changes; anything taken from the bundle must be marked
        /// so it survives, and is loaded again below if it was lost anyway.
        static T Keep<T>(T o) where T : Object
        {
            if (o != null)
            {
                o.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                Object.DontDestroyOnLoad(o);
            }
            return o;
        }

        /// The bundle is carried inside the DLL. Unity loads it from a file, so it is written to a cache folder in
        /// UserData (again whenever it differs from the copy in the DLL). Handing the 12 MB to the game as an Il2Cpp
        /// array did not work: the array was collected before the game had read it.
        static AssetBundle LoadBundle()
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            string path = null;
            try { path = ExtractBundle(assembly); }
            catch (System.Exception e) { SteamRailwayMod.Log.Warning($"{BundleFile} could not be unpacked from the DLL: {e.Message}"); }
            if (path == null) path = Path.Combine(RouteStore.FolderPath, BundleFile);
            if (!File.Exists(path))
            {
                SteamRailwayMod.Log.Warning($"{BundleFile} not found, using placeholder boxes.");
                return null;
            }
            var bundle = AssetBundle.LoadFromFile(path);
            if (bundle == null) SteamRailwayMod.Log.Error($"{BundleFile} could not be loaded.");
            return bundle;
        }

        static string ExtractBundle(System.Reflection.Assembly assembly)
        {
            using (var stream = assembly.GetManifestResourceStream(BundleFile))
            {
                if (stream == null) return null;
                var bytes = new byte[stream.Length];
                int read = 0;
                while (read < bytes.Length) read += stream.Read(bytes, read, bytes.Length - read);
                string direction = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "SteamRailway");
                string path = Path.Combine(direction, BundleFile);
                using (var hasher = System.Security.Cryptography.SHA1.Create())
                {
                    if (File.Exists(path))
                    {
                        var onDisk = File.ReadAllBytes(path);
                        if (onDisk.Length == bytes.Length && System.Linq.Enumerable.SequenceEqual(hasher.ComputeHash(onDisk), hasher.ComputeHash(bytes))) return path;
                    }
                }
                Directory.CreateDirectory(direction);
                File.WriteAllBytes(path, bytes);
                return path;
            }
        }

        static bool Load()
        {
            if (s_Prefab != null && s_Palette != null) return true;
            if (s_Tried && s_Bundle == null) return false;      // the file is missing or broken: nothing to retry
            s_Tried = true;
            if (s_Bundle == null)
            {
                var bundle = LoadBundle();
                if (bundle == null) return false;
                s_Bundle = bundle;
            }
            bool again = s_Prefab != null || s_Material != null;
            s_Prefab = Keep(s_Bundle.LoadAsset<GameObject>(PrefabName));
            s_Palette = Keep(s_Bundle.LoadAsset<Texture2D>(PaletteName));
            s_DialsTex = Keep(s_Bundle.LoadAsset<Texture2D>(DialsName));
            if (s_Prefab == null) { SteamRailwayMod.Log.Error($"{PrefabName} missing in {BundleFile}."); return false; }
            SteamRailwayMod.Log.Msg(again ? $"Locomotive model reloaded ({BundleFile})." : $"Locomotive model loaded ({BundleFile}).");
            return true;
        }

        /// A material using one of the game's shaders: Shader.Find first, otherwise a copy of a scene material.
        static Material GameMaterial(string shaderName)
        {
            var shader = Shader.Find(shaderName);
            if (shader != null) return new Material(shader);
            foreach (var renderer in Object.FindObjectsOfType<Renderer>())
            {
                var material = renderer.sharedMaterial;
                if (material != null && material.shader != null && material.shader.name == shaderName) return new Material(material);
            }
            return null;
        }

        const float DetailTiling = 64f, DetailStrength = -0.05f;
        static Texture s_Detail;

        /// The game's painterly look comes from a tiled noise overlay (_DetailOverlay) on TLD_StandardDiffuse.
        /// Borrow that texture from any loaded game material.
        static void ApplyDetail(Material material, float tiling, float strength)
        {
            if (material == null || !material.HasProperty("_DetailOverlay")) return;
            if (s_Detail == null)
            {
                foreach (var renderer in Object.FindObjectsOfType<Renderer>())
                {
                    var glassMaterial = renderer.sharedMaterial;
                    if (glassMaterial == null || !glassMaterial.HasProperty("_DetailOverlay")) continue;
                    var texture = glassMaterial.GetTexture("_DetailOverlay");
                    if (texture != null) { s_Detail = texture; break; }
                }
            }
            if (s_Detail == null) return;
            material.SetTexture("_DetailOverlay", s_Detail);
            material.SetFloat("_DetailTiling", tiling);
            material.SetFloat("_DetailStrength", strength);
        }

        static void EnsureMaterials()
        {
            if (s_Material == null || (s_Palette != null && s_Material.mainTexture == null))
            {
                s_Material = GameMaterial(DiffuseShader) ?? new Material(Shader.Find("Standard"));
                s_Material.name = "SteamRailway_Palette";
                if (s_Palette != null) s_Material.mainTexture = s_Palette;
                if (s_Material.HasProperty("_Color")) s_Material.color = Color.white;
                ApplyDetail(s_Material, DetailTiling, DetailStrength);
                Keep(s_Material);
            }
            if (s_Dials == null || s_Dials.mainTexture == null)
            {
                s_Dials = GameMaterial(DiffuseShader) ?? new Material(s_Material);
                s_Dials.name = "SteamRailway_Dials";
                s_Dials.mainTexture = s_DialsTex != null ? s_DialsTex : s_Palette;
                if (s_Dials.HasProperty("_Color")) s_Dials.color = Color.white;
                ApplyDetail(s_Dials, 4f, -0.08f);
                Keep(s_Dials);
            }
            if (s_Glass == null)
            {
                s_Glass = GameMaterial(TransparentShader);
                if (s_Glass != null)
                {
                    s_Glass.name = "SteamRailway_Glass";
                    s_Glass.mainTexture = Texture2D.whiteTexture;
                    if (s_Glass.HasProperty("_Color")) s_Glass.color = GlassTint;
                    Keep(s_Glass);
                }
                else SteamRailwayMod.Log.Warning("No transparent game shader found: windows are left open.");
            }
        }

        /// Instantiates the model under the train root (local origin = rails' centre, +Z forward).
        public static GameObject Spawn(Transform trainRoot)
        {
            if (!Load()) return null;
            EnsureMaterials();
            var targetObject = Object.Instantiate(s_Prefab);
            targetObject.name = "Model";
            targetObject.transform.SetParent(trainRoot, false);
            targetObject.transform.localPosition = Vector3.zero;
            targetObject.transform.localRotation = Quaternion.identity;
            // door windows ride on their doors (kept apart in the FBX: the export misplaces child objects)
            foreach (var side in new[] { "L", "R" })
            {
                var door = FindDeep(targetObject.transform, "CabDoor_" + side);
                var glass = FindDeep(targetObject.transform, "Glass_Door" + side);
                if (door != null && glass != null) glass.SetParent(door, true);
            }
            foreach (var meshRenderer in targetObject.GetComponentsInChildren<MeshRenderer>(true))
            {
                string n = meshRenderer.name;
                if (n.StartsWith("Col_"))
                {
                    var meshFilter = meshRenderer.GetComponent<MeshFilter>();
                    var box = meshRenderer.gameObject.AddComponent<BoxCollider>();
                    if (meshFilter != null && meshFilter.sharedMesh != null) { box.center = meshFilter.sharedMesh.bounds.center; box.size = meshFilter.sharedMesh.bounds.size; }
                    meshRenderer.enabled = false;
                }
                else if (n.StartsWith("Glass_"))
                {
                    if (s_Glass != null) { meshRenderer.sharedMaterial = s_Glass; meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; }
                    else meshRenderer.enabled = false;
                }
                else if (n == "Dials") meshRenderer.sharedMaterial = s_Dials;
                else meshRenderer.sharedMaterial = s_Material;
            }
            return targetObject;
        }

        static GameObject s_SwitchPrefab;

        /// The switch stand (Blender/build_switch.py) under `parent`, on the locomotive's material; null without the bundle.
        public static GameObject SpawnSwitch(Transform parent)
        {
            if (!Load() || s_Bundle == null) return null;
            if (s_SwitchPrefab == null) s_SwitchPrefab = Keep(s_Bundle.LoadAsset<GameObject>("SteamRailway_Switch"));
            if (s_SwitchPrefab == null) return null;
            EnsureMaterials();
            var targetObject = Object.Instantiate(s_SwitchPrefab);
            targetObject.name = "Model";
            targetObject.transform.SetParent(parent, false);
            targetObject.transform.localPosition = Vector3.zero;
            targetObject.transform.localRotation = Quaternion.identity;
            foreach (var meshRenderer in targetObject.GetComponentsInChildren<MeshRenderer>(true)) meshRenderer.sharedMaterial = s_Material;
            return targetObject;
        }

        /// A sound clip from the bundle (Sounds/*.wav, see Sounds/CREDITS.txt); null if missing.
        static GameObject s_SmokePrefab;
        static Texture2D s_SmokeTex;

        /// The chimney smoke particle system and its sprite sheet (see SteamRailwayBundle.BuildSmoke).
        public static bool SmokeAssets(out GameObject prefab, out Texture2D sheet)
        {
            if (s_SmokePrefab == null && Load() && s_Bundle != null)
            {
                s_SmokePrefab = Keep(s_Bundle.LoadAsset<GameObject>("LocoSmoke"));
                s_SmokeTex = Keep(s_Bundle.LoadAsset<Texture2D>("Smoke_Sheet"));
            }
            prefab = s_SmokePrefab;
            sheet = s_SmokeTex;
            return prefab != null && sheet != null;
        }

        static GameObject s_BlastPrefab;

        /// The boiler explosion particle systems (SteamRailwayBundle.BuildBlast); null without the bundle.
        public static GameObject BlastPrefab()
        {
            if (s_BlastPrefab == null && Load() && s_Bundle != null) s_BlastPrefab = Keep(s_Bundle.LoadAsset<GameObject>("BoilerBlast"));
            return s_BlastPrefab;
        }

        public static AudioClip Sound(string name)
        {
            if (s_Sounds.TryGetValue(name, out var c) && (c != null || s_Bundle == null)) return c;
            AudioClip clip = null;
            if (Load() && s_Bundle != null)
            {
                clip = s_Bundle.LoadAsset<AudioClip>(name);
                if (clip != null) Keep(clip);
                else SteamRailwayMod.Log.Warning($"Sound {name} missing in {BundleFile}.");
            }
            s_Sounds[name] = clip;
            return clip;
        }

        public static Transform FindDeep(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            for (int index = 0; index < root.childCount; index++)
            {
                var transform = FindDeep(root.GetChild(index), name);
                if (transform != null) return transform;
            }
            return null;
        }
    }
}
