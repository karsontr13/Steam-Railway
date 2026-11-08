using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(SteamRailway.SteamRailwayMod), "SteamRailway", "0.1.0", "Hamsi Buglama")]
[assembly: MelonGame("Hinterland", "TheLongDark")]

namespace SteamRailway
{
    /// A coal-fired steam railway for Great Bear, built on the base game's own train code
    /// (TLD.SteamTrain + Curvy splines). Uses no DLC assets: the firebox is the base game's
    /// pot belly stove and the track mesh is generated from the route splines at runtime.
    public class SteamRailwayMod : MelonMod
    {
        internal static MelonLogger.Instance Log;

        static MelonPreferences_Category s_Cat;
        static MelonPreferences_Entry<bool> s_Enabled;
        static MelonPreferences_Entry<bool> s_EditorEnabled;
        static MelonPreferences_Entry<bool> s_Verbose;
        static MelonPreferences_Entry<bool> s_ObjectEditor;
        static MelonPreferences_Entry<string> s_Realism;

        public static bool Enabled => s_Enabled == null || s_Enabled.Value;
        internal static Realism RealismMode =>
            s_Realism != null && s_Realism.Value.Trim().StartsWith("S", System.StringComparison.OrdinalIgnoreCase) ? Realism.Simple : Realism.Medium;
        public static bool EditorEnabled => s_EditorEnabled != null && s_EditorEnabled.Value;
        public static bool Verbose => s_Verbose != null && s_Verbose.Value;
        public static bool ObjectEditorEnabled => s_ObjectEditor == null || s_ObjectEditor.Value;

        public override void OnInitializeMelon()
        {
            Log = LoggerInstance;
            s_Cat = MelonPreferences.CreateCategory("SteamRailway");
            s_Enabled = s_Cat.CreateEntry("Enabled", true, "Enabled", "Build tracks and trains in Great Bear regions.");
            s_EditorEnabled = s_Cat.CreateEntry("RouteEditor", false, "Route editor",
                "F8 add point, F7 remove last point, F6 start a new route, F9 rebuild this scene, F10 save routes.json.");
            s_Verbose = s_Cat.CreateEntry("Verbose", false, "Verbose logging");
            s_ObjectEditor = s_Cat.CreateEntry("ObjectEditor", true, "Object editor",
                "F11 turns the object editor on: Del removes, G moves, Ctrl+Z undoes. Edits are kept in Mods/SteamRailway/Edits.");
            s_Realism = s_Cat.CreateEntry("Realism", "Medium", "Realism",
                "Simple: automatic boiler feed, regulator/reverser/brake only. Medium: injector and blower by hand, boiler failures.");

            RouteStore.Load();
            Log.Msg($"SteamRailway loaded. Routes file: {RouteStore.FilePath}");
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            if (!Enabled) return;
            RailwayManager.OnSceneInitialized(sceneName);
        }

        public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
        {
            RailwayManager.OnSceneUnloaded(sceneName);
        }

        public override void OnUpdate()
        {
            if (!Enabled) return;
            RailwayManager.Update();
            if (EditorEnabled) RouteEditor.Update();
            if (ObjectEditorEnabled)
            {
                try { ObjectEdits.Tick(); }
                catch (System.Exception e) { Log.Warning($"Object edits: {e.Message}"); }
                try { EditorHub.Update(); }
                catch (System.Exception e) { Log.Warning($"Editor: {e.Message}"); }
            }
        }

        public override void OnGUI()
        {
            if (Enabled && ObjectEditorEnabled) EditorHub.OnGUI();
        }

        internal static void Debug(string msg)
        {
            if (Verbose) Log.Msg(msg);
        }
    }
}
