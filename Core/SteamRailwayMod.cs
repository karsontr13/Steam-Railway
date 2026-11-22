using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(SteamRailway.SteamRailwayMod), "SteamRailway", "0.1.0", "Hamsi Buglama")]
[assembly: MelonGame("Hinterland", "TheLongDark")]
// The in-game settings page needs ModSettings; without it the mod runs on its preferences file alone.
[assembly: MelonOptionalDependencies("ModSettings")]

namespace SteamRailway
{
    /// A coal-fired steam railway for Great Bear, built on the base game's train code (SteamTrain + Curvy splines).
    /// It uses no DLC assets: the firebox is the pot belly stove and the track mesh is generated at runtime.
    public class SteamRailwayMod : MelonMod
    {
        internal static MelonLogger.Instance Log;

        static MelonPreferences_Category s_Cat;
        static MelonPreferences_Entry<bool> s_Enabled;
        static MelonPreferences_Entry<bool> s_EditorEnabled;
        static MelonPreferences_Entry<bool> s_Verbose;
        static MelonPreferences_Entry<bool> s_ObjectEditor;
        static MelonPreferences_Entry<string> s_Realism;
        static MelonPreferences_Entry<string> s_Difficulty;
        static MelonPreferences_Entry<bool> s_DeveloperTools;

        internal const string SimpleRealismName = "Simple";
        internal const string MediumRealismName = "Medium";

        public static bool Enabled => s_Enabled == null || s_Enabled.Value;
        internal static Realism RealismMode =>
            s_Realism != null && s_Realism.Value.Trim().StartsWith("S", System.StringComparison.OrdinalIgnoreCase) ? Realism.Simple : Realism.Medium;
        /// Easy or Hard for the next new game (a started game keeps the mode it began with).
        internal static string DifficultySetting =>
            s_Difficulty != null && s_Difficulty.Value.Trim().StartsWith("H", System.StringComparison.OrdinalIgnoreCase) ? Obstacles.HardName : Obstacles.EasyName;
        /// Developer keys (F4: put the locomotive, ready to run, on the nearest line where you look). Turn off for normal play.
        public static bool DeveloperToolsEnabled => s_DeveloperTools != null && s_DeveloperTools.Value;
        public static bool EditorEnabled => s_EditorEnabled != null && s_EditorEnabled.Value;
        public static bool Verbose => s_Verbose != null && s_Verbose.Value;
        /// The F11 editors (object, track, terrain, obstacle). The saved edits are played back either way.
        public static bool ObjectEditorEnabled => s_ObjectEditor != null && s_ObjectEditor.Value;

        /// The settings page was confirmed: its values go into the preference entries.
        internal static void ApplySettings(bool enabled, string realism, string difficulty, bool editors, bool developerTools, bool verbose)
        {
            Set(s_Enabled, enabled);
            Set(s_Realism, realism);
            Set(s_Difficulty, difficulty);
            Set(s_ObjectEditor, editors);
            Set(s_DeveloperTools, developerTools);
            Set(s_Verbose, verbose);
        }

        static void Set<PreferenceValue>(MelonPreferences_Entry<PreferenceValue> entry, PreferenceValue value)
        {
            if (entry != null && !Equals(entry.Value, value)) entry.Value = value;
        }

        /// The blasting charge's item pack is written here, as the mod is registered: ModComponent reads the Mods folder
        /// for packs later (when the game starts up), so a pack written now is loaded on the very first start.
        public override void OnEarlyInitializeMelon()
        {
            Log = LoggerInstance;
            Dynamite.ExtractPackage();
        }

        public override void OnInitializeMelon()
        {
            Log = LoggerInstance;
            s_Cat = MelonPreferences.CreateCategory("SteamRailway");
            s_Enabled = s_Cat.CreateEntry("Enabled", true, "Enabled", "Build tracks and trains in Great Bear regions.");
            s_EditorEnabled = s_Cat.CreateEntry("RouteEditor", false, "Route editor",
                "F8 add point, F7 remove last point, F6 start a new route, F9 rebuild this scene, F10 save routes.json.");
            s_Verbose = s_Cat.CreateEntry("Verbose", false, "Verbose logging");
            s_ObjectEditor = s_Cat.CreateEntry("ObjectEditor", false, "Editors",
                "F11 goes round the object, track, terrain and obstacle editors. Edits are kept in Mods/SteamRailway. Leave off for normal play.");
            s_Realism = s_Cat.CreateEntry("Realism", MediumRealismName, "Realism",
                "Simple: automatic boiler feed, regulator/reverser/brake only. Medium: injector and blower by hand, boiler failures.");

            s_DeveloperTools = s_Cat.CreateEntry("DeveloperTools", false, "Developer tools",
                "F4 puts the locomotive, ready to run (boiler hot, water full, fire lit), on the nearest line where you look. Turn off for normal play.");
            s_Difficulty = s_Cat.CreateEntry("Difficulty", Obstacles.EasyName, "Difficulty (new games)",
                "Easy: every game starts with the changed regions. Hard: regions start as in the original game; rockfalls, stranded wagons and the broken bridge have to be cleared. Chosen when a game is started.");

            ShippedData.Extract();
            RouteStore.Load();
            WriteSoundCredits();
            SettingsRegistration.TryRegister();
            Log.Msg($"SteamRailway loaded. Routes file: {RouteStore.FilePath}");
        }

        /// The sound recordings are CC BY 4.0: their credits travel inside the DLL and are written next to the mod's data.
        static void WriteSoundCredits()
        {
            try
            {
                string path = System.IO.Path.Combine(RouteStore.FolderPath, "SOUND_CREDITS.txt");
                if (System.IO.File.Exists(path)) return;
                System.IO.Directory.CreateDirectory(RouteStore.FolderPath);
                using (var stream = typeof(SteamRailwayMod).Assembly.GetManifestResourceStream("SOUND_CREDITS.txt"))
                {
                    if (stream == null) return;
                    using (var file = System.IO.File.Create(path)) stream.CopyTo(file);
                }
            }
            catch (System.Exception exception) { Log.Warning($"Sound credits could not be written: {exception.Message}"); }
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            if (!Enabled) return;
            ArrivalGuard.SceneLoaded();
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
            if (DeveloperToolsEnabled) DeveloperTools.Update();
            if (EditorEnabled) RouteEditor.Update();
            // the saved object edits keep their loot in place and removed objects switched off, editors on or not
            try { ObjectEdits.Tick(); }
            catch (System.Exception exception) { LogUpdateFailure("Object edits", exception); }
            if (ObjectEditorEnabled)
            {
                try { EditorHub.Update(); }
                catch (System.Exception exception) { LogUpdateFailure("Editor", exception); }
            }
        }

        public override void OnGUI()
        {
            if (Enabled && ObjectEditorEnabled) EditorHub.OnGUI();
        }

        static void LogUpdateFailure(string label, System.Exception exception) => Log.Warning($"{label}: {exception.Message}");

        internal static void Debug(string message)
        {
            if (Verbose) Log.Msg(message);
        }
    }
}
