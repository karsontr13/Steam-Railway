using Il2Cpp;
using Il2CppTLD.SaveState;
using UnityEngine;

namespace SteamRailway
{
    /// The master and sound-effect volume of the game's audio options, read from the options as the game applies them
    /// (the mod's own sounds do not go through the game's sound engine): the mod's sounds are scaled by them.
    internal static class GameVolume
    {
        static float s_Master = 1f;
        static float s_Effects = 1f;
        static bool s_PausedByMod;

        /// What the mod's own sounds are multiplied by: 0 (sliders down) to 1.
        public static float Scale => s_Master * s_Effects;

        /// Takes the sliders from the options (0 to 1).
        public static void Read(SettingsState state)
        {
            if (state == null) return;
            s_Master = Mathf.Clamp01(state.m_MasterVolume);
            s_Effects = Mathf.Clamp01(state.m_SoundVolume);
        }

        /// The game holds its own sounds while paused (the pause menu); the mod's sounds are held with them.
        public static void Update()
        {
            bool paused = GameManager.m_IsPaused;
            if (paused == s_PausedByMod) return;
            s_PausedByMod = paused;
            AudioListener.pause = paused;
        }
    }
}
