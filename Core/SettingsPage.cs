using System.Runtime.CompilerServices;
using MelonLoader;

namespace SteamRailway
{
    /// The in-game settings page (Options > Mod Settings). MelonPreferences stays the only store; the page shows and
    /// writes the same entries. Without ModSettings installed the mod runs on its preferences file alone.
    internal static class SettingsRegistration
    {
        const string ModSettingsAssemblyName = "ModSettings";
        const string PageTitle = "Steam Railway";

        public static void TryRegister()
        {
            bool installed = false;
            foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
                if (assembly.GetName().Name == ModSettingsAssemblyName) { installed = true; break; }
            if (!installed)
            {
                SteamRailwayMod.Log.Msg("ModSettings is not installed: the settings stay in MelonPreferences.cfg.");
                return;
            }
            try { Register(); }
            catch (System.Exception exception) { SteamRailwayMod.Log.Warning($"The settings page could not be added: {exception.Message}"); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Register()
        {
            var page = new SettingsPage();
            page.CopyFromPreferences();
            page.AddToModSettings(PageTitle);
            SteamRailwayMod.Log.Msg("Settings page added (Options > Mod Settings).");
        }
    }

    // Choice labels match an enum by position.
    internal enum RealismChoice { Simple = 0, Medium = 1 }
    internal enum DifficultyChoice { Easy = 0, Hard = 1 }

    internal class SettingsPage : ModSettings.ModSettingsBase
    {
        [ModSettings.Section("General")]
        [ModSettings.Name("Enabled")]
        [ModSettings.Description("Builds the tracks and trains in the Great Bear regions. Takes effect at the next region load.")]
        public bool Enabled = true;

        [ModSettings.Name("Realism")]
        [ModSettings.Description("Simple: the boiler is fed by itself; you drive with the regulator, reverser and brake. Medium: injector and blower by hand, and the boiler can fail.")]
        [ModSettings.Choice(new string[] { "Simple", "Medium" })]
        public RealismChoice Realism = RealismChoice.Medium;

        [ModSettings.Name("Difficulty (new games)")]
        [ModSettings.Description("Easy: the regions start with the railway open. Hard: they start as in the original game; rockfalls, stranded wagons and the broken bridge have to be cleared. A game keeps the difficulty it was started with.")]
        [ModSettings.Choice(new string[] { "Easy", "Hard" })]
        public DifficultyChoice Difficulty = DifficultyChoice.Easy;

        [ModSettings.Name("Ear ringing after a blast")]
        [ModSettings.Description("A blast of the blasting charge close by makes the ears ring. Turn off if the sound bothers you.")]
        public bool Tinnitus = true;

        [ModSettings.Section("Map making")]
        [ModSettings.Name("Editors (F11)")]
        [ModSettings.Description("F11 goes round the object, track, terrain and obstacle editors. Edits are kept in Mods/SteamRailway. Leave off for normal play.")]
        public bool Editors = false;

        [ModSettings.Name("Developer tools (F4)")]
        [ModSettings.Description("F4 puts the locomotive, ready to run (boiler hot, water full, fire lit), on the nearest line where you look. Leave off for normal play.")]
        public bool DeveloperTools = false;

        [ModSettings.Section("Troubleshooting")]
        [ModSettings.Name("Verbose logging")]
        [ModSettings.Description("Writes extra lines to the log. Turn on only when reporting a problem.")]
        public bool VerboseLogging = false;

        internal void CopyFromPreferences()
        {
            Enabled = SteamRailwayMod.Enabled;
            Realism = SteamRailwayMod.RealismMode == SteamRailway.Realism.Simple ? RealismChoice.Simple : RealismChoice.Medium;
            Difficulty = SteamRailwayMod.DifficultySetting == Obstacles.HardName ? DifficultyChoice.Hard : DifficultyChoice.Easy;
            Tinnitus = SteamRailwayMod.TinnitusEnabled;
            Editors = SteamRailwayMod.ObjectEditorEnabled;
            DeveloperTools = SteamRailwayMod.DeveloperToolsEnabled;
            VerboseLogging = SteamRailwayMod.Verbose;
            RefreshGUI();
        }

        protected override void OnConfirm()
        {
            SteamRailwayMod.ApplySettings(Enabled, Realism == RealismChoice.Simple ? SteamRailwayMod.SimpleRealismName : SteamRailwayMod.MediumRealismName,
                Difficulty == DifficultyChoice.Hard ? Obstacles.HardName : Obstacles.EasyName, Editors, DeveloperTools, VerboseLogging, Tinnitus);
            MelonPreferences.Save();
            SteamRailwayMod.Log.Msg("Saved from the settings page.");
        }
    }
}
