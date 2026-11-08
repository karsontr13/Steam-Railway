using Il2Cpp;

namespace SteamRailway
{
    /// Texts in Turkish when the game runs in Turkish, English otherwise.
    internal static class L
    {
        static string s_Language;
        static bool s_Turkish;

        public static bool Turkish
        {
            get
            {
                string lang = null;
                try { lang = Localization.Language; } catch { }
                if (lang != s_Language) { s_Language = lang; s_Turkish = lang == "Turkish"; }
                return s_Turkish;
            }
        }

        public static string T(string tr, string en) => Turkish ? tr : en;
    }
}
