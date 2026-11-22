using System.IO;
using UnityEngine;

namespace SteamRailway
{
    /// Loads the locomotive model from the bundle (meshes, palette texture, empty sockets) and builds its materials
    /// from the game's own shaders: Glass_* transparent, Dials diffuse, Col_* invisible colliders, the rest palette.
    internal static class LocomotiveModel
    {
        const string BundleFile = "steamrailway.bundle";
        const string PrefabName = "SteamRailway_Locomotive";
        const string PaletteName = "Loco_Palette";
        const string CouplerPartnerPrefix = "CouplerPartner";
        const string CouplerRearLockedName = "CouplerRear_Locked";
        const string DialsName = "Cab_Dials";
        const string DiffuseShader = "Shader Forge/TLD_StandardDiffuse";
        const string TransparentShader = "Shader Forge/TLD_StandardTransparent";
        const float GlassRed = 0.55f;
        const float GlassGreen = 0.6f;
        const float GlassBlue = 0.65f;
        const float GlassAlpha = 0.22f;
        const float DialsDetailTiling = 4f;
        const float DialsDetailStrength = -0.08f;
        const string DetailOverlayProperty = "_DetailOverlay";
        static readonly Color GlassTint = new Color(GlassRed, GlassGreen, GlassBlue, GlassAlpha);

        static bool s_Tried;
        static AssetBundle s_Bundle;
        static readonly System.Collections.Generic.Dictionary<string, AudioClip> s_Sounds = new System.Collections.Generic.Dictionary<string, AudioClip>();
        static GameObject s_Prefab;
        static Texture2D s_Palette;
        static Texture2D s_DialsTexture;
        static Material s_Material;
        static Material s_Glass;
        static Material s_Dials;

        /// The game unloads unused assets when a region changes; anything taken from the bundle must be marked
        /// so it survives, and is loaded again below if it was lost anyway.
        static AssetType Keep<AssetType>(AssetType asset) where AssetType : Object
        {
            if (asset != null)
            {
                asset.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                Object.DontDestroyOnLoad(asset);
            }
            return asset;
        }

        /// The bundle is carried inside the DLL; Unity loads it from a file, so it is written to a cache folder
        /// in UserData whenever it differs from the copy in the DLL.
        static AssetBundle LoadBundle()
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            string path = null;
            try { path = ExtractBundle(assembly); }
            catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"{BundleFile} could not be unpacked from the DLL: {exception.Message}"); }
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
            s_DialsTexture = Keep(s_Bundle.LoadAsset<Texture2D>(DialsName));
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

        const float DetailTiling = 64f;
        const float DetailStrength = -0.05f;
        static Texture s_Detail;

        /// The game's painterly look comes from a tiled noise overlay (_DetailOverlay) on TLD_StandardDiffuse.
        /// Borrow that texture from any loaded game material.
        static void ApplyDetail(Material material, float tiling, float strength)
        {
            if (material == null || !material.HasProperty(DetailOverlayProperty)) return;
            if (s_Detail == null)
            {
                foreach (var renderer in Object.FindObjectsOfType<Renderer>())
                {
                    var glassMaterial = renderer.sharedMaterial;
                    if (glassMaterial == null || !glassMaterial.HasProperty(DetailOverlayProperty)) continue;
                    var texture = glassMaterial.GetTexture(DetailOverlayProperty);
                    if (texture != null) { s_Detail = texture; break; }
                }
            }
            if (s_Detail == null) return;
            material.SetTexture(DetailOverlayProperty, s_Detail);
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
                s_Dials.mainTexture = s_DialsTexture != null ? s_DialsTexture : s_Palette;
                if (s_Dials.HasProperty("_Color")) s_Dials.color = Color.white;
                ApplyDetail(s_Dials, DialsDetailTiling, DialsDetailStrength);
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
                string partName = meshRenderer.name;
                if (partName.StartsWith("Col_"))
                {
                    var meshFilter = meshRenderer.GetComponent<MeshFilter>();
                    var box = meshRenderer.gameObject.AddComponent<BoxCollider>();
                    if (meshFilter != null && meshFilter.sharedMesh != null) { box.center = meshFilter.sharedMesh.bounds.center; box.size = meshFilter.sharedMesh.bounds.size; }
                    meshRenderer.enabled = false;
                }
                else if (partName.StartsWith("Glass_"))
                {
                    if (s_Glass != null) { meshRenderer.sharedMaterial = s_Glass; meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; }
                    else meshRenderer.enabled = false;
                }
                else if (partName == "Dials") meshRenderer.sharedMaterial = s_Dials;
                else
                {
                    meshRenderer.sharedMaterial = s_Material;
                    // Wagons switches the locked coupler pose on when a wagon is locked on
                    if (partName.StartsWith(CouplerPartnerPrefix) || partName == CouplerRearLockedName) meshRenderer.gameObject.SetActive(false);
                }
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

        static GameObject s_SmokePrefab;
        static Texture2D s_SmokeTexture;

        /// The chimney smoke particle system and its sprite sheet (see SteamRailwayBundle.BuildSmoke).
        public static bool SmokeAssets(out GameObject prefab, out Texture2D sheet)
        {
            if (s_SmokePrefab == null && Load() && s_Bundle != null)
            {
                s_SmokePrefab = Keep(s_Bundle.LoadAsset<GameObject>("LocoSmoke"));
                s_SmokeTexture = Keep(s_Bundle.LoadAsset<Texture2D>("Smoke_Sheet"));
            }
            prefab = s_SmokePrefab;
            sheet = s_SmokeTexture;
            return prefab != null && sheet != null;
        }

        static GameObject s_BlastPrefab;

        /// The boiler explosion particle systems (SteamRailwayBundle.BuildBlast); null without the bundle.
        public static GameObject BlastPrefab()
        {
            if (s_BlastPrefab == null && Load() && s_Bundle != null) s_BlastPrefab = Keep(s_Bundle.LoadAsset<GameObject>("BoilerBlast"));
            return s_BlastPrefab;
        }

        /// A sound clip from the bundle (Sounds/*.wav); null if missing.
        public static AudioClip Sound(string name)
        {
            if (s_Sounds.TryGetValue(name, out var cachedClip) && (cachedClip != null || s_Bundle == null)) return cachedClip;
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
