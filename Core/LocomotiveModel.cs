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

        static bool Load()
        {
            if (s_Prefab != null && s_Palette != null) return true;
            if (s_Tried && s_Bundle == null) return false;      // the file is missing or broken: nothing to retry
            s_Tried = true;
            if (s_Bundle == null)
            {
                string path = Path.Combine(RouteStore.FolderPath, BundleFile);
                if (!File.Exists(path))
                {
                    SteamRailwayMod.Log.Warning($"{BundleFile} not found, using placeholder boxes.");
                    return false;
                }
                var bundle = AssetBundle.LoadFromFile(path);
                if (bundle == null) { SteamRailwayMod.Log.Error($"{BundleFile} could not be loaded."); return false; }
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
            foreach (var r in Object.FindObjectsOfType<Renderer>())
            {
                var m = r.sharedMaterial;
                if (m != null && m.shader != null && m.shader.name == shaderName) return new Material(m);
            }
            return null;
        }

        const float DetailTiling = 64f, DetailStrength = -0.05f;
        static Texture s_Detail;

        /// The game's painterly look comes from a tiled noise overlay (_DetailOverlay) on TLD_StandardDiffuse.
        /// Borrow that texture from any loaded game material.
        static void ApplyDetail(Material m, float tiling, float strength)
        {
            if (m == null || !m.HasProperty("_DetailOverlay")) return;
            if (s_Detail == null)
            {
                foreach (var r in Object.FindObjectsOfType<Renderer>())
                {
                    var gm = r.sharedMaterial;
                    if (gm == null || !gm.HasProperty("_DetailOverlay")) continue;
                    var t = gm.GetTexture("_DetailOverlay");
                    if (t != null) { s_Detail = t; break; }
                }
            }
            if (s_Detail == null) return;
            m.SetTexture("_DetailOverlay", s_Detail);
            m.SetFloat("_DetailTiling", tiling);
            m.SetFloat("_DetailStrength", strength);
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
            var go = Object.Instantiate(s_Prefab);
            go.name = "Model";
            go.transform.SetParent(trainRoot, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            // door windows ride on their doors (kept apart in the FBX: the export misplaces child objects)
            foreach (var side in new[] { "L", "R" })
            {
                var door = FindDeep(go.transform, "CabDoor_" + side);
                var glass = FindDeep(go.transform, "Glass_Door" + side);
                if (door != null && glass != null) glass.SetParent(door, true);
            }
            foreach (var r in go.GetComponentsInChildren<MeshRenderer>(true))
            {
                string n = r.name;
                if (n.StartsWith("Col_"))
                {
                    var mf = r.GetComponent<MeshFilter>();
                    var box = r.gameObject.AddComponent<BoxCollider>();
                    if (mf != null && mf.sharedMesh != null) { box.center = mf.sharedMesh.bounds.center; box.size = mf.sharedMesh.bounds.size; }
                    r.enabled = false;
                }
                else if (n.StartsWith("Glass_"))
                {
                    if (s_Glass != null) { r.sharedMaterial = s_Glass; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; }
                    else r.enabled = false;
                }
                else if (n == "Dials") r.sharedMaterial = s_Dials;
                else r.sharedMaterial = s_Material;
            }
            return go;
        }

        static GameObject s_SwitchPrefab;

        /// The switch stand (Blender/build_switch.py) under `parent`, on the locomotive's material; null without the bundle.
        public static GameObject SpawnSwitch(Transform parent)
        {
            if (!Load() || s_Bundle == null) return null;
            if (s_SwitchPrefab == null) s_SwitchPrefab = Keep(s_Bundle.LoadAsset<GameObject>("SteamRailway_Switch"));
            if (s_SwitchPrefab == null) return null;
            EnsureMaterials();
            var go = Object.Instantiate(s_SwitchPrefab);
            go.name = "Model";
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            foreach (var r in go.GetComponentsInChildren<MeshRenderer>(true)) r.sharedMaterial = s_Material;
            return go;
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
            for (int i = 0; i < root.childCount; i++)
            {
                var t = FindDeep(root.GetChild(i), name);
                if (t != null) return t;
            }
            return null;
        }
    }
}
